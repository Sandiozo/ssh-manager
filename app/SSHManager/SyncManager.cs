using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SSHManager;

/// <summary>A folder on this computer kept in step with a folder on a PC.</summary>
internal sealed class SyncPair
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string PcId { get; set; } = "";
    public string Local { get; set; } = "";
    public string Remote { get; set; } = "";
    /// <summary>"to-pc", "from-pc" or "two-way".</summary>
    public string Mode { get; set; } = "to-pc";
    /// <summary>Folder or file names to skip, separated by ';'.</summary>
    public string Ignore { get; set; } = ".git;node_modules;bin;obj";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Runs folder sync while the app is open: on a timer, when files change in a local folder, and on
/// demand. It remembers, per pair, each file's size and modification time on both sides after the last
/// sync (%LOCALAPPDATA%\SSHManager\sync\&lt;pair&gt;.json) and decides per file from what changed since:
/// a change on one side is copied over; changes on both sides keep both (the other version is saved as
/// "name (conflict PC date).ext"); a file deleted on one side and unchanged on the other is moved to the
/// Recycle Bin here (when deleted on the PC) or not fetched again (when deleted here; nothing is deleted
/// on the PC). Nothing is ever overwritten without its content being kept somewhere.
/// </summary>
internal sealed class SyncManager(AppSettings settings, Func<string, Link?> linkFor, Action changed)
{
    public static readonly string[] Modes = ["to-pc", "from-pc", "two-way"];
    const double Slack = 2.0;   // seconds of modification-time difference that count as "the same"
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(2) };
    static readonly string StateFolder = Path.Combine(AppSettings.Folder, "sync");

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
    long lastProgressPost;

    public void Start()
    {
        foreach (var p in Pairs()) Watch(p);
        timer = new System.Threading.Timer(_ => AppLog.Guard("Folder sync timer", () => SyncAll()), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
    }

    List<SyncPair> Pairs()
    {
        lock (settings.Gate) return settings.Syncs.ToList();
    }

    SyncPair? Find(string id)
    {
        lock (settings.Gate) return settings.Syncs.FirstOrDefault(p => p.Id == id);
    }

    State StateOf(SyncPair p)
    {
        lock (states) return states.TryGetValue(p.Id, out var s) ? s : states[p.Id] = new State();
    }

    /// <summary>The pairs for the page: all of them, or only those of one PC (a PC's dashboard sees only its own).</summary>
    public object Snapshot(string? pcId = null)
    {
        List<PcEntry> pcs;
        lock (settings.Gate) pcs = settings.Pcs.ToList();
        return new
        {
            type = "sync-state",
            pairs = Pairs().Where(p => pcId is null || p.PcId == pcId).Select(p => new
            {
                id = p.Id, local = p.Local, remote = p.Remote, mode = p.Mode, enabled = p.Enabled, pcId = p.PcId,
                pcName = pcs.FirstOrDefault(x => x.Id == p.PcId)?.DisplayName ?? "(removed PC)",
                removed = pcs.All(x => x.Id != p.PcId) && p.PcId != PcEntry.LocalId,
                state = StateOf(p).Kind, message = StateOf(p).Message,
            }).ToList(),
        };
    }

    public bool Owns(string pairId, string pcId) => Find(pairId) is { } p && p.PcId == pcId;

    // ---- checks for new pairs ----

    /// <summary>Folders sync must never write into or read from: Windows, programs, start-up programs, SSH keys,
    /// app data. A folder that is inside one of them or contains one (e.g. the whole user folder) is refused.</summary>
    public static string? SensitiveLocal(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\') + "\\"; }
        catch (Exception) { return "That isn't a valid folder."; }
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var risky = new[]
        {
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.Startup), F(Environment.SpecialFolder.CommonStartup),
            F(Environment.SpecialFolder.ApplicationData), F(Environment.SpecialFolder.LocalApplicationData),
            profile == "" ? "" : Path.Combine(profile, "AppData"), profile == "" ? "" : Path.Combine(profile, ".ssh"),
        }.Where(r => r != "").Select(r => r.TrimEnd('\\') + "\\");
        foreach (var r in risky)
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase) || r.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                return $"For safety, folder sync can't use {folder.TrimEnd('\\')}: it is or contains a system or private folder ({r.TrimEnd('\\')}). Choose a folder of your own documents.";
        return null;
    }

    static bool Inside(string a, string b) =>
        (a.TrimEnd('\\', '/') + "\\").StartsWith(b.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the pair can be added; otherwise why not. Fixes up the paths' form.</summary>
    public string? Validate(SyncPair pair, bool checkOverlap = true)
    {
        if (!Modes.Contains(pair.Mode)) return "Choose a sync direction.";
        if (pair.Local.Trim() == "" || !Path.IsPathFullyQualified(pair.Local.Trim())) return "Choose a folder on this computer.";
        pair.Local = Path.GetFullPath(pair.Local.Trim());
        if (pair.Local.Length > 3) pair.Local = pair.Local.TrimEnd('\\');
        if (SensitiveLocal(pair.Local) is { } why) return why;
        pair.Remote = pair.Remote.Trim().Replace('/', '\\');
        if (pair.Remote == "" || !(System.Text.RegularExpressions.Regex.IsMatch(pair.Remote, @"^[A-Za-z]:\\") || pair.Remote.StartsWith(@"\\")))
            return "Enter the full path of the folder on the PC, e.g. C:\\Users\\you\\Documents\\Project.";
        if (pair.Remote.Length <= 3) return "Syncing a whole drive isn't supported. Choose a folder on the PC.";
        if (pair.Remote.Split('\\').Any(s => s is ".." or ".")) return "The folder on the PC can't contain \"..\".";
        pair.Remote = pair.Remote.TrimEnd('\\');
        pair.Ignore ??= "";
        foreach (var other in checkOverlap ? Pairs().Where(o => o.Id != pair.Id) : [])
        {
            if (Inside(pair.Local, other.Local) || Inside(other.Local, pair.Local))
                return $"{other.Local} is already synced (or overlaps this folder). Each local folder can be in one sync only.";
            if (other.PcId == pair.PcId && (Inside(pair.Remote, other.Remote) || Inside(other.Remote, pair.Remote)))
                return $"{other.Remote} on that PC is already synced (or overlaps this folder).";
        }
        return null;
    }

    // ---- changing the list ----

    public void Add(SyncPair pair)
    {
        lock (settings.Gate) settings.Syncs.Add(pair);
        settings.Save();
        Watch(pair);
        Run(pair);
        changed();
    }

    public void Remove(string id)
    {
        if (Find(id) is not { } pair) return;
        lock (settings.Gate) settings.Syncs.Remove(pair);
        settings.Save();
        State? s;
        lock (states) states.Remove(id, out s);
        s?.Watcher?.Dispose();
        s?.Debounce?.Dispose();
        try { File.Delete(StatePath(id)); } catch (Exception) { }
        changed();
    }

    /// <summary>The PC was removed from the app: its pairs go too.</summary>
    public void RemoveForPc(string pcId)
    {
        foreach (var p in Pairs().Where(p => p.PcId == pcId)) Remove(p.Id);
    }

    /// <summary>Pairs of a PC that was merged into another entry follow it.</summary>
    public void MovePc(string fromPcId, string toPcId)
    {
        lock (settings.Gate)
            foreach (var p in settings.Syncs.Where(p => p.PcId == fromPcId)) p.PcId = toPcId;
        settings.Save();
        changed();
    }

    public void Toggle(string id)
    {
        if (Find(id) is not { } pair) return;
        pair.Enabled = !pair.Enabled;
        settings.Save();
        if (pair.Enabled) Run(pair);
        changed();
    }

    public void SyncNow(string id)
    {
        if (Find(id) is { } pair) Run(pair, force: true);
    }

    void SyncAll()
    {
        foreach (var p in Pairs()) Run(p, again: false);
    }

    void Run(SyncPair pair, bool force = false, bool again = true) =>
        _ = Task.Run(() => SyncAsync(pair, force, again));

    void Watch(SyncPair pair)
    {
        var s = StateOf(pair);
        s.Watcher?.Dispose();
        s.Watcher = null;
        if (!Directory.Exists(pair.Local) || pair.Mode == "from-pc") return;
        try
        {
            var w = new FileSystemWatcher(pair.Local) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Soon()
            {
                lock (s)
                {
                    s.Debounce?.Dispose();   // wait until changes settle
                    s.Debounce = new System.Threading.Timer(_ => Run(pair), null, 3000, Timeout.Infinite);
                }
            }
            FileSystemEventHandler onChange = (_, e) =>
            {
                if (!Ignored(pair, Path.GetRelativePath(pair.Local, e.FullPath))) Soon();
            };
            w.Changed += onChange;
            w.Created += onChange;
            w.Deleted += onChange;
            w.Renamed += (o, e) => onChange(o, e);
            w.Error += (_, _) => Soon();   // too many changes at once: just look at everything
            w.EnableRaisingEvents = true;
            s.Watcher = w;
        }
        catch (Exception e) { AppLog.Write($"Watching {pair.Local} failed", e); }
    }

    static bool TempName(string name) =>
        name.EndsWith(".sshmgr-part", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sshmgr-upload", StringComparison.OrdinalIgnoreCase);

    static bool Ignored(SyncPair pair, string rel)
    {
        var skip = pair.Ignore.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return rel.Split('/', '\\').Any(part => skip.Any(i => string.Equals(i, part, StringComparison.OrdinalIgnoreCase)) || TempName(part));
    }

    void Set(SyncPair pair, string kind, string message)
    {
        var s = StateOf(pair);
        var progress = kind == "syncing" && s.Kind == "syncing";
        s.Kind = kind;
        s.Message = message;
        if (progress)
        {
            // progress: at most a few updates a second to the pages
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastProgressPost) < 300) return;
            Interlocked.Exchange(ref lastProgressPost, now);
        }
        try { changed(); } catch (Exception e) { AppLog.Write("Sync state post", e); }
    }

    async Task SyncAsync(SyncPair pair, bool force, bool again)
    {
        if (!pair.Enabled && !force) return;
        var s = StateOf(pair);
        lock (s)
        {
            if (s.Running)
            {
                if (again) s.Again = true;
                return;
            }
            s.Running = true;
        }
        try
        {
            while (true)
            {
                lock (s) s.Again = false;
                try { await SyncOnceAsync(pair).ConfigureAwait(false); }
                catch (Exception e)
                {
                    Set(pair, "error", e.Message);
                    AppLog.Write($"Sync {pair.Id} failed", e);
                }
                lock (s)
                {
                    if (!s.Again || Find(pair.Id) is null) { s.Running = false; return; }
                }
            }
        }
        catch (Exception) { lock (s) s.Running = false; }
    }

    // ---- what was where after the last sync ----

    sealed class Entry
    {
        /// <summary>The file's relative path with its letter case as last seen.</summary>
        public string Name { get; set; } = "";
        public bool HasL { get; set; }
        public long Ls { get; set; }
        public double Lm { get; set; }
        public bool HasR { get; set; }
        public long Rs { get; set; }
        public double Rm { get; set; }
        /// <summary>Deleted here while unchanged on the PC: not fetched again until it changes there.</summary>
        public bool LocalGone { get; set; }
    }

    sealed class SyncState
    {
        public int Version { get; set; } = 1;
        public string Local { get; set; } = "";
        public string Remote { get; set; } = "";
        public Dictionary<string, Entry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    static string StatePath(string id) => Path.Combine(StateFolder, id + ".json");

    static SyncState? LoadState(SyncPair pair)
    {
        try
        {
            var path = StatePath(pair.Id);
            if (!File.Exists(path)) return null;
            var st = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(path));
            if (st is null || !st.Local.Equals(pair.Local, StringComparison.OrdinalIgnoreCase) || !st.Remote.Equals(pair.Remote, StringComparison.OrdinalIgnoreCase))
                return null;   // the pair now points elsewhere: start over as on a first run
            st.Files = new Dictionary<string, Entry>(st.Files, StringComparer.OrdinalIgnoreCase);
            return st;
        }
        catch (Exception e)
        {
            AppLog.Write($"Sync state of {pair.Id} could not be read; treating it as a first run", e);
            return null;
        }
    }

    static void SaveState(SyncPair pair, SyncState st)
    {
        try
        {
            Directory.CreateDirectory(StateFolder);
            var path = StatePath(pair.Id);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) { AppLog.Write($"Saving sync state of {pair.Id} failed", e); }
    }

    // ---- paths: nothing the PC reports may lead outside the synced folder ----

    static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>The relative path in "a/b/c.txt" form, or null when it is not a plain path below the folder:
    /// rooted, "..", ":" (drives, alternate streams), device names, invalid characters, names Windows would
    /// silently change (trailing dot or space).</summary>
    public static string? SafeRel(string rel)
    {
        if (string.IsNullOrEmpty(rel) || rel.Length > 4000) return null;
        rel = rel.Replace('\\', '/');
        if (rel.StartsWith('/') || rel.Contains(':') || Path.IsPathRooted(rel)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var part in rel.Split('/'))
        {
            if (part is "" or "." or ".." || part.IndexOfAny(invalid) >= 0 || part.EndsWith('.') || part.EndsWith(' ')) return null;
            var stem = part.Split('.')[0].TrimEnd();
            if (Devices.Contains(stem)) return null;
        }
        return rel;
    }

    /// <summary>Full local path of a relative path, checked to stay inside the pair's folder and not to pass
    /// through a junction or link inside it.</summary>
    static string LocalPath(SyncPair pair, string rel)
    {
        if (SafeRel(rel) is not { } safe) throw new InvalidOperationException($"Unsafe file name: {rel}");
        var root = Path.GetFullPath(pair.Local).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(root, safe.Replace('/', '\\')));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length <= root.Length)
            throw new InvalidOperationException($"Unsafe file name: {rel}");
        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length >= root.Length; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"{rel} is inside a linked folder; it is skipped.");
        return full;
    }

    static string RemotePath(SyncPair pair, string rel)
    {
        if (SafeRel(rel) is not { } safe) throw new InvalidOperationException($"Unsafe file name: {rel}");
        return pair.Remote.TrimEnd('\\', '/') + "\\" + safe.Replace('/', '\\');
    }

    /// <summary>"report (conflict LAPTOP 2026-10-28 1432).docx" next to the file, not taken yet.</summary>
    static string ConflictRel(string rel, string pcName, Func<string, bool> exists)
    {
        var slash = rel.LastIndexOf('/');
        var dir = slash >= 0 ? rel[..(slash + 1)] : "";
        var name = rel[(slash + 1)..];
        var ext = Path.GetExtension(name);
        var stem = ext == "" ? name : name[..^ext.Length];
        var pc = new string(pcName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        var label = $"conflict {pc} {DateTime.Now:yyyy-MM-dd HHmm}";
        for (var i = 1; ; i++)
        {
            var candidate = $"{dir}{stem} ({label}{(i > 1 ? $" {i}" : "")}){ext}";
            if (!exists(candidate)) return candidate;
        }
    }

    // ---- one run ----

    readonly record struct FileInfoLite(string Name, long Size, double Mtime);

    static bool Same(long s1, double m1, long s2, double m2)
    {
        if (s1 != s2) return false;
        var d = Math.Abs(m1 - m2);
        return d <= Slack || Math.Abs(d - 3600) <= Slack;   // an hour exactly: daylight saving time on FAT drives
    }

    static bool Same(FileInfoLite a, FileInfoLite b) => Same(a.Size, a.Mtime, b.Size, b.Mtime);

    static Dictionary<string, FileInfoLite> ScanLocal(SyncPair pair)
    {
        var local = new Dictionary<string, FileInfoLite>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(pair.Local);
        var files = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        // linked folders (junctions, symbolic links) are not followed: they can loop or lead anywhere
        var dirs = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
        var todo = new Stack<string>();
        todo.Push(root);
        while (todo.Count > 0)
        {
            var dir = todo.Pop();
            try
            {
                foreach (var full in Directory.EnumerateFiles(dir, "*", files))
                {
                    var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
                    if (Ignored(pair, rel) || SafeRel(rel) is null) continue;
                    try
                    {
                        var fi = new FileInfo(full);
                        local[rel] = new(rel, fi.Length, (fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
                    }
                    catch (Exception) { }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", dirs))
                    if (!Ignored(pair, Path.GetRelativePath(root, sub))) todo.Push(sub);
            }
            catch (Exception) { }   // a folder that went away or can't be read
        }
        return local;
    }

    FileInfoLite? LocalNow(SyncPair pair, string rel)
    {
        try
        {
            var fi = new FileInfo(LocalPath(pair, rel));
            return fi.Exists ? new(rel, fi.Length, (fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds) : null;
        }
        catch (Exception) { return null; }
    }

    async Task SyncOnceAsync(SyncPair pair)
    {
        var link = linkFor(pair.PcId);
        if (link is null || link.State != LinkState.Connected)
        {
            Set(pair, "waiting", link is null ? "the PC was removed from SSH Manager" : "waiting for the PC to connect");
            return;
        }
        if (Validate(Clone(pair), checkOverlap: false) is { } invalid)
        {
            Set(pair, "error", invalid);
            return;
        }
        if (!Directory.Exists(pair.Local))
        {
            if (pair.Mode != "from-pc") { Set(pair, "error", "The local folder doesn't exist."); return; }
            Directory.CreateDirectory(pair.Local);
        }
        var pcName = link.Pc.DisplayName;

        // what's on the PC
        var remote = new Dictionary<string, FileInfoLite>(StringComparer.OrdinalIgnoreCase);
        var unsafeNames = 0;
        using (var r = await Http.SendAsync(link.Request(HttpMethod.Get, "/api/fs/tree?path=" + Uri.EscapeDataString(pair.Remote))).ConfigureAwait(false))
        {
            if (r.StatusCode == HttpStatusCode.NotFound && pair.Mode != "from-pc")
            {
                using var mk = await Http.SendAsync(link.Request(HttpMethod.Post, "/api/fs/mkdir", JsonContent(new { path = pair.Remote }))).ConfigureAwait(false);
            }
            else if (!r.IsSuccessStatusCode)
                throw new InvalidOperationException($"Couldn't list {pair.Remote} on the PC: {await r.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            else
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var f in list.EnumerateArray())
                    {
                        var raw = f.TryGetProperty("rel", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString() ?? "" : "";
                        if (SafeRel(raw) is not { } rel) { unsafeNames++; continue; }
                        if (Ignored(pair, rel)) continue;
                        var size = f.TryGetProperty("size", out var sv) && sv.TryGetInt64(out var sz) ? sz : -1;
                        var mtime = f.TryGetProperty("mtime", out var mv) && mv.TryGetDouble(out var mt) ? mt : 0;
                        if (size >= 0) remote[rel] = new(rel, size, mtime);
                    }
            }
        }

        var local = ScanLocal(pair);
        var state = LoadState(pair) ?? new SyncState { Local = pair.Local, Remote = pair.Remote };
        var files = state.Files;

        var keys = local.Keys.Union(remote.Keys, StringComparer.OrdinalIgnoreCase).Union(files.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        int sent = 0, received = 0, conflicts = 0, recycled = 0, failed = 0, done = 0;
        string? firstError = null;
        var work = keys.Count(k => !(local.TryGetValue(k, out var a) && remote.TryGetValue(k, out var b) && Same(a, b)));
        var lastSave = Environment.TickCount64;

        void Record(string key, FileInfoLite? l, FileInfoLite? r, bool localGone = false)
        {
            if (l is null && r is null) { files.Remove(key); return; }
            files[key] = new Entry
            {
                Name = (l ?? r)!.Value.Name, HasL = l is not null, Ls = l?.Size ?? 0, Lm = l?.Mtime ?? 0,
                HasR = r is not null, Rs = r?.Size ?? 0, Rm = r?.Mtime ?? 0, LocalGone = localGone,
            };
        }

        foreach (var key in keys)
        {
            FileInfoLite? L = local.TryGetValue(key, out var lv) ? lv : null;
            FileInfoLite? R = remote.TryGetValue(key, out var rv2) ? rv2 : null;
            var S = files.GetValueOrDefault(key);
            bool LocalChanged() => S is not { HasL: true } || L is null || !Same(L.Value.Size, L.Value.Mtime, S.Ls, S.Lm);
            bool RemoteChanged() => S is not { HasR: true } || R is null || !Same(R.Value.Size, R.Value.Mtime, S.Rs, S.Rm);
            try
            {
                if (L is { } l && R is { } r)
                {
                    if (Same(l, r))
                    {
                        // same content; follow a rename that only changed upper/lower case
                        if (l.Name != r.Name && S is not null)
                        {
                            if (S.Name == l.Name && pair.Mode != "to-pc") { RenameLocalCase(pair, l.Name, r.Name); l = l with { Name = r.Name }; }
                            else if (S.Name == r.Name && pair.Mode != "from-pc") { await RenameRemoteAsync(link, pair, r.Name, l.Name).ConfigureAwait(false); r = r with { Name = l.Name }; }
                        }
                        Record(key, l, r);
                        continue;
                    }
                    Progress(pair, ++done, work, l.Name);
                    var noHistory = S is not { HasL: true, HasR: true };
                    switch (pair.Mode)
                    {
                        case "to-pc":
                        {
                            // a copy changed on the PC since the last sync is kept there under a conflict name
                            var dstChanged = noHistory || RemoteChanged();   // no history: both exist and differ, keep both
                            if (dstChanged)
                            {
                                var keep = ConflictRel(r.Name, pcName, x => remote.ContainsKey(x));
                                await RenameRemoteAsync(link, pair, r.Name, keep).ConfigureAwait(false);
                                conflicts++;
                            }
                            Record(key, l, await UploadAsync(link, pair, l).ConfigureAwait(false));
                            sent++;
                            break;
                        }
                        case "from-pc":
                        {
                            var dstChanged = noHistory || LocalChanged();
                            if (dstChanged)
                            {
                                var keep = ConflictRel(l.Name, Environment.MachineName, x => local.ContainsKey(x) || LocalNow(pair, x) is not null);
                                File.Move(LocalPath(pair, l.Name), LocalPath(pair, keep));
                                conflicts++;
                                L = null;
                            }
                            var got = await DownloadAsync(link, pair, r, L, pcName).ConfigureAwait(false);
                            if (got.conflict) conflicts++;
                            Record(key, got.local, r);
                            received++;
                            break;
                        }
                        default:   // two-way
                        {
                            var lc = noHistory || LocalChanged();
                            var rc = noHistory || RemoteChanged();
                            if (lc && !rc)
                            {
                                Record(key, l, await UploadAsync(link, pair, l).ConfigureAwait(false));
                                sent++;
                            }
                            else if (rc && !lc)
                            {
                                var got = await DownloadAsync(link, pair, r, l, pcName).ConfigureAwait(false);
                                if (got.conflict) conflicts++;
                                Record(key, got.local, r);
                                received++;
                            }
                            else
                            {
                                // changed on both sides (or no history): the newer stays under its name, the other
                                // is kept next to it as a conflict copy
                                conflicts++;
                                if (r.Mtime > l.Mtime + Slack)
                                {
                                    var keep = ConflictRel(l.Name, Environment.MachineName, x => local.ContainsKey(x) || LocalNow(pair, x) is not null);
                                    File.Move(LocalPath(pair, l.Name), LocalPath(pair, keep));
                                    var got = await DownloadAsync(link, pair, r, null, pcName).ConfigureAwait(false);
                                    Record(key, got.local, r);
                                    received++;
                                }
                                else
                                {
                                    var keep = ConflictRel(r.Name, pcName, x => local.ContainsKey(x) || remote.ContainsKey(x) || LocalNow(pair, x) is not null);
                                    await DownloadAsync(link, pair, r with { Name = keep }, null, pcName, from: r.Name).ConfigureAwait(false);
                                    Record(key, l, await UploadAsync(link, pair, l).ConfigureAwait(false));
                                    received++;
                                    sent++;
                                }
                            }
                            break;
                        }
                    }
                }
                else if (L is { } onlyL)
                {
                    var deletedOnPc = S is { HasR: true, HasL: true } && !LocalChanged();
                    if (pair.Mode == "from-pc" || (pair.Mode == "two-way" && deletedOnPc))
                    {
                        if (deletedOnPc)
                        {
                            // deleted on the PC, unchanged here: to the Recycle Bin (never deleted for good)
                            Progress(pair, ++done, work, onlyL.Name);
                            if (!Recycle(LocalPath(pair, onlyL.Name))) throw new IOException($"Couldn't move {onlyL.Name} to the Recycle Bin.");
                            recycled++;
                            files.Remove(key);
                        }
                        else files.Remove(key);   // only here; from-pc leaves it alone
                        continue;
                    }
                    Progress(pair, ++done, work, onlyL.Name);
                    Record(key, onlyL, await UploadAsync(link, pair, onlyL).ConfigureAwait(false));
                    sent++;
                }
                else if (R is { } onlyR)
                {
                    if (pair.Mode == "to-pc")
                    {
                        files.Remove(key);   // only on the PC (or deleted here): nothing is deleted on the PC
                        continue;
                    }
                    var unchangedThere = S is { HasR: true } && !RemoteChanged();
                    if (unchangedThere && (S!.HasL || S.LocalGone))
                    {
                        // deleted here, unchanged on the PC: don't bring it back
                        Record(key, null, onlyR, localGone: true);
                        continue;
                    }
                    Progress(pair, ++done, work, onlyR.Name);
                    var got = await DownloadAsync(link, pair, onlyR, null, pcName).ConfigureAwait(false);
                    if (got.conflict) conflicts++;
                    Record(key, got.local, onlyR);
                    received++;
                }
                else files.Remove(key);
            }
            catch (Exception e) when (e is not OperationCanceledException || link.State == LinkState.Connected)
            {
                failed++;
                firstError ??= e.Message;
                if (link.State != LinkState.Connected) throw new InvalidOperationException("The connection to the PC was lost during the sync.");
            }
            if (Environment.TickCount64 - lastSave > 10000 && Find(pair.Id) is not null)
            {
                SaveState(pair, state);
                lastSave = Environment.TickCount64;
            }
        }
        if (Find(pair.Id) is null) return;   // removed while running
        SaveState(pair, state);

        var parts = new List<string>();
        if (sent > 0) parts.Add($"{sent} sent");
        if (received > 0) parts.Add($"{received} received");
        if (conflicts > 0) parts.Add($"{conflicts} conflict{(conflicts == 1 ? "" : "s")} kept as copies");
        if (recycled > 0) parts.Add($"{recycled} moved to the Recycle Bin");
        if (unsafeNames > 0) parts.Add($"{unsafeNames} unsafe name{(unsafeNames == 1 ? "" : "s")} skipped");
        var summary = parts.Count == 0 ? $"up to date · {DateTime.Now:HH:mm}" : $"synced {DateTime.Now:HH:mm} · {string.Join(", ", parts)}";
        if (failed > 0) Set(pair, "error", $"{summary} · {failed} failed, retried later ({firstError})");
        else Set(pair, "idle", summary);
    }

    static SyncPair Clone(SyncPair p) => new() { Id = p.Id, PcId = p.PcId, Local = p.Local, Remote = p.Remote, Mode = p.Mode, Ignore = p.Ignore, Enabled = p.Enabled };

    void Progress(SyncPair pair, int n, int total, string rel) =>
        Set(pair, "syncing", $"{Math.Min(n, Math.Max(total, n))}/{Math.Max(total, n)}: {rel}");

    static StringContent JsonContent(object o) =>
        new(JsonSerializer.Serialize(o), System.Text.Encoding.UTF8, "application/json");

    /// <summary>Sends a local file; returns how the PC's copy looks now.</summary>
    static async Task<FileInfoLite> UploadAsync(Link link, SyncPair pair, FileInfoLite l)
    {
        var full = LocalPath(pair, l.Name);
        long size;
        await using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            size = stream.Length;
            var url = $"/api/fs/upload?path={Uri.EscapeDataString(RemotePath(pair, l.Name))}&overwrite=1&mtime={l.Mtime.ToString(CultureInfo.InvariantCulture)}";
            using var r = await Http.SendAsync(link.Request(HttpMethod.Put, url, new StreamContent(stream))).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Sending {l.Name} failed: {await r.Content.ReadAsStringAsync().ConfigureAwait(false)}");
        }
        return new(l.Name, size, l.Mtime);
    }

    /// <summary>Fetches a PC's file into place. The local file is checked again right before it is replaced: if it
    /// changed since the scan (<paramref name="expected"/>), the incoming copy is kept under a conflict name instead.</summary>
    async Task<(FileInfoLite? local, bool conflict)> DownloadAsync(Link link, SyncPair pair, FileInfoLite r, FileInfoLite? expected, string pcName, string? from = null)
    {
        var full = LocalPath(pair, r.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var part = full + ".sshmgr-part";
        try
        {
            using (var resp = await Http.SendAsync(link.Request(HttpMethod.Get, "/api/fs/download?path=" + Uri.EscapeDataString(RemotePath(pair, from ?? r.Name))),
                       HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Receiving {r.Name} failed: {await resp.Content.ReadAsStringAsync().ConfigureAwait(false)}");
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(dst).ConfigureAwait(false);
            }
            File.SetLastWriteTimeUtc(part, DateTime.UnixEpoch.AddSeconds(r.Mtime));
            var now = LocalNow(pair, r.Name);
            var conflict = expected is null ? now is not null : now is null || !Same(now.Value, expected.Value);
            var target = full;
            if (conflict)
            {
                // the file here changed while this one was coming: keep both
                var keep = ConflictRel(r.Name, pcName, x => LocalNow(pair, x) is not null);
                target = LocalPath(pair, keep);
            }
            File.Move(part, target, overwrite: !conflict);
            var fi = new FileInfo(target);
            FileInfoLite got = new(r.Name, fi.Length, (fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
            // after a conflict the file here counts as changed, so the next run sends it (the PC's version is kept here)
            return (conflict ? expected : got, conflict);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (Exception) { }
        }
    }

    static async Task RenameRemoteAsync(Link link, SyncPair pair, string rel, string newRel)
    {
        var newName = newRel[(newRel.LastIndexOf('/') + 1)..];
        using var r = await Http.SendAsync(link.Request(HttpMethod.Post, "/api/fs/rename",
            JsonContent(new { path = RemotePath(pair, rel), new_name = newName }))).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Renaming {rel} on the PC failed: {await r.Content.ReadAsStringAsync().ConfigureAwait(false)}");
    }

    static void RenameLocalCase(SyncPair pair, string rel, string newRel)
    {
        // only the file name; folders that differ only in case are left as they are
        if (!string.Equals(Path.GetDirectoryName(rel), Path.GetDirectoryName(newRel), StringComparison.Ordinal)) return;
        File.Move(LocalPath(pair, rel), LocalPath(pair, newRel));
    }

    // ---- Recycle Bin ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public bool Aborted;
        public IntPtr NameMappings;
        public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    static extern int SHFileOperation(ref ShFileOp op);

    static bool Recycle(string path)
    {
        // FO_DELETE with FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR
        var op = new ShFileOp { Func = 3, From = path + "\0", Flags = 0x40 | 0x10 | 0x4 | 0x400 | 0x200 };
        return SHFileOperation(ref op) == 0 && !op.Aborted && !File.Exists(path);
    }
}
