using System.Net;
using System.Text.Json;

namespace SSHManager;

/// <summary>A folder on this computer kept in step with a folder on a PC.</summary>
internal sealed class SyncPair
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string PcId { get; set; } = "";
    public string Local { get; set; } = "";
    public string Remote { get; set; } = "";
    /// <summary>"to-pc", "from-pc" or "two-way" (newest copy wins; nothing is ever deleted).</summary>
    public string Mode { get; set; } = "to-pc";
    /// <summary>Folder or file names to skip, separated by ';'.</summary>
    public string Ignore { get; set; } = ".git;node_modules;bin;obj";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Runs folder sync while the app is open: on a timer, when files change in a local folder, and on
/// demand. Compares names, sizes and modification times, and copies changed files with the server's
/// file API (uploads keep the original modification time, downloads get the PC's). Never deletes.
/// </summary>
internal sealed class SyncManager(AppSettings settings, Func<string, Link?> linkFor, Action changed)
{
    const double Slack = 2.0;   // seconds of modification-time difference that count as "the same"
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(2) };

    sealed class State
    {
        public string Kind = "idle";   // idle | syncing | error | waiting
        public string Message = "";
        public bool Running, Again;
        public FileSystemWatcher? Watcher;
        public System.Threading.Timer? Debounce;
    }

    readonly Dictionary<string, State> states = [];
    System.Threading.Timer? timer;

    public void Start()
    {
        foreach (var p in settings.Syncs) Watch(p);
        timer = new System.Threading.Timer(_ => SyncAll(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
    }

    State StateOf(SyncPair p) => states.TryGetValue(p.Id, out var s) ? s : states[p.Id] = new State();

    public object Snapshot() => new
    {
        type = "sync-state",
        pairs = settings.Syncs.Select(p => new
        {
            id = p.Id, local = p.Local, remote = p.Remote, mode = p.Mode, enabled = p.Enabled, pcId = p.PcId,
            pcName = settings.Pcs.FirstOrDefault(x => x.Id == p.PcId)?.DisplayName ?? "(removed PC)",
            state = StateOf(p).Kind, message = StateOf(p).Message,
        }),
    };

    public void Add(SyncPair pair)
    {
        settings.Syncs.Add(pair);
        settings.Save();
        Watch(pair);
        _ = SyncAsync(pair);
        changed();
    }

    public void Remove(string id)
    {
        if (settings.Syncs.FirstOrDefault(p => p.Id == id) is not { } pair) return;
        settings.Syncs.Remove(pair);
        settings.Save();
        if (states.Remove(id, out var s)) s.Watcher?.Dispose();
        changed();
    }

    public void Toggle(string id)
    {
        if (settings.Syncs.FirstOrDefault(p => p.Id == id) is not { } pair) return;
        pair.Enabled = !pair.Enabled;
        settings.Save();
        if (pair.Enabled) _ = SyncAsync(pair);
        changed();
    }

    public void SyncNow(string id)
    {
        if (settings.Syncs.FirstOrDefault(p => p.Id == id) is { } pair) _ = SyncAsync(pair, force: true);
    }

    void SyncAll()
    {
        foreach (var p in settings.Syncs.ToList()) _ = SyncAsync(p);
    }

    void Watch(SyncPair pair)
    {
        var s = StateOf(pair);
        s.Watcher?.Dispose();
        if (!Directory.Exists(pair.Local) || pair.Mode == "from-pc") return;
        try
        {
            var w = new FileSystemWatcher(pair.Local) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler onChange = (_, e) =>
            {
                if (Ignored(pair, Path.GetRelativePath(pair.Local, e.FullPath))) return;
                s.Debounce?.Dispose();   // wait until changes settle
                s.Debounce = new System.Threading.Timer(_ => _ = SyncAsync(pair), null, 3000, Timeout.Infinite);
            };
            w.Changed += onChange;
            w.Created += onChange;
            w.Renamed += (o, e) => onChange(o, e);
            w.EnableRaisingEvents = true;
            s.Watcher = w;
        }
        catch (Exception) { }
    }

    static bool Ignored(SyncPair pair, string rel)
    {
        var skip = pair.Ignore.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return rel.Split('/', '\\').Any(part => skip.Any(i => string.Equals(i, part, StringComparison.OrdinalIgnoreCase))
                                                || part.EndsWith(".sshmgr-part", StringComparison.OrdinalIgnoreCase));
    }

    void Set(SyncPair pair, string kind, string message)
    {
        var s = StateOf(pair);
        s.Kind = kind;
        s.Message = message;
        changed();
    }

    async Task SyncAsync(SyncPair pair, bool force = false)
    {
        if (!pair.Enabled && !force) return;
        var s = StateOf(pair);
        if (s.Running) { s.Again = true; return; }
        s.Running = true;
        try
        {
            do
            {
                s.Again = false;
                await SyncOnceAsync(pair);
            } while (s.Again);
        }
        catch (Exception e) { Set(pair, "error", e.Message); }
        finally { s.Running = false; }
    }

    async Task SyncOnceAsync(SyncPair pair)
    {
        var link = linkFor(pair.PcId);
        if (link is null || link.State != LinkState.Connected)
        {
            Set(pair, "waiting", "waiting for the PC to connect");
            return;
        }
        if (!Directory.Exists(pair.Local))
        {
            if (pair.Mode == "to-pc") { Set(pair, "error", "The local folder doesn't exist."); return; }
            Directory.CreateDirectory(pair.Local);
        }

        // what's on the PC
        var remote = new Dictionary<string, (long size, double mtime)>(StringComparer.OrdinalIgnoreCase);
        using (var r = await Http.SendAsync(link.Request(HttpMethod.Get, "/api/fs/tree?path=" + Uri.EscapeDataString(pair.Remote))))
        {
            if (r.StatusCode == HttpStatusCode.NotFound && pair.Mode != "from-pc")
            {
                using var mk = await Http.SendAsync(link.Request(HttpMethod.Post, "/api/fs/mkdir",
                    JsonContent(new { path = pair.Remote })));
            }
            else if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Couldn't list {pair.Remote} on the PC: {await r.Content.ReadAsStringAsync()}");
            else
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
                {
                    var rel = f.GetProperty("rel").GetString()!;
                    if (!Ignored(pair, rel)) remote[rel] = (f.GetProperty("size").GetInt64(), f.GetProperty("mtime").GetDouble());
                }
            }
        }

        // what's here
        var local = new Dictionary<string, (long size, double mtime)>(StringComparer.OrdinalIgnoreCase);
        foreach (var full in Directory.EnumerateFiles(pair.Local, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(pair.Local, full).Replace('\\', '/');
            if (Ignored(pair, rel)) continue;
            var fi = new FileInfo(full);
            local[rel] = (fi.Length, (fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
        }

        var up = new List<string>();
        var down = new List<string>();
        foreach (var (rel, l) in local)
        {
            if (!remote.TryGetValue(rel, out var r))
            {
                if (pair.Mode != "from-pc") up.Add(rel);
                continue;
            }
            var same = l.size == r.size && Math.Abs(l.mtime - r.mtime) <= Slack;
            if (same) continue;
            var localNewer = l.mtime > r.mtime + Slack || (Math.Abs(l.mtime - r.mtime) <= Slack && pair.Mode == "to-pc");
            if (pair.Mode == "to-pc" || (pair.Mode == "two-way" && localNewer)) up.Add(rel);
            else if (pair.Mode == "from-pc" || pair.Mode == "two-way") down.Add(rel);
        }
        if (pair.Mode != "to-pc")
            down.AddRange(remote.Keys.Where(rel => !local.ContainsKey(rel)));

        var total = up.Count + down.Count;
        var done = 0;
        foreach (var rel in up)
        {
            Set(pair, "syncing", $"sending {++done}/{total}: {rel}");
            await UploadAsync(link, pair, rel, local[rel].mtime);
        }
        foreach (var rel in down)
        {
            Set(pair, "syncing", $"receiving {++done}/{total}: {rel}");
            await DownloadAsync(link, pair, rel, remote[rel].mtime);
        }
        Set(pair, "idle", total == 0
            ? $"up to date · {DateTime.Now:HH:mm}"
            : $"synced {DateTime.Now:HH:mm} · {up.Count} sent, {down.Count} received");
    }

    static StringContent JsonContent(object o) =>
        new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    static string RemotePath(SyncPair pair, string rel) => pair.Remote.TrimEnd('\\', '/') + "\\" + rel.Replace('/', '\\');

    static async Task UploadAsync(Link link, SyncPair pair, string rel, double mtime)
    {
        var full = Path.Combine(pair.Local, rel.Replace('/', Path.DirectorySeparatorChar));
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var url = $"/api/fs/upload?path={Uri.EscapeDataString(RemotePath(pair, rel))}&overwrite=1&mtime={mtime.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        using var r = await Http.SendAsync(link.Request(HttpMethod.Put, url, new StreamContent(stream)));
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Sending {rel} failed: {await r.Content.ReadAsStringAsync()}");
    }

    static async Task DownloadAsync(Link link, SyncPair pair, string rel, double mtime)
    {
        var full = Path.Combine(pair.Local, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var part = full + ".sshmgr-part";
        using (var r = await Http.SendAsync(link.Request(HttpMethod.Get, "/api/fs/download?path=" + Uri.EscapeDataString(RemotePath(pair, rel))),
                   HttpCompletionOption.ResponseHeadersRead))
        {
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Receiving {rel} failed: {await r.Content.ReadAsStringAsync()}");
            await using var dst = File.Create(part);
            await r.Content.CopyToAsync(dst);
        }
        File.Move(part, full, overwrite: true);
        File.SetLastWriteTimeUtc(full, DateTime.UnixEpoch.AddSeconds(mtime));
    }
}
