// sshmgr: lets scripts and AI agents use the PCs the SSH Manager app is connected to. It asks the app
// (over a local named pipe only this Windows user can open) for a PC's connection port and access token,
// then calls that PC's server through the app's own SSH port forward: no SSH connection or token of its
// own, nothing stored on disk. Run `sshmgr help` for the commands.
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SshMgrCli;

/// <summary>An error to report and the exit code to end with.</summary>
sealed class Failure(string message, int code = 1, HttpStatusCode? status = null) : Exception(message)
{
    public int Code { get; } = code;
    public HttpStatusCode? Status { get; } = status;
}

static class Program
{
    const int Usage = 2, Unreachable = 3;

    const string Help = """
        sshmgr - use the PCs the SSH Manager app is connected to (through the app's own connections)

        Usage:
          sshmgr pcs [--json]                                 the PCs in the app and whether they are connected
          sshmgr run <pc> <script> [options]                  run PowerShell on the PC and stream its output;
          sshmgr run <pc> -f <file.ps1|-> [options]             exits with the script's exit code
              --cwd <dir>        working directory (environment variables work, e.g. %USERPROFILE%)
              --env <K=V>        extra environment variable (repeatable)
              --title <text>     title in the PC's activity log
              --timeout <s>      stop the run after this many seconds (exit code 124)
          sshmgr actions <pc> [--json]                        list the PC's quick actions
          sshmgr action <pc> <id|name>                        run a quick action and stream its output
          sshmgr shot <pc> <out.png|out.jpg> [options]        save a screenshot (full size; PNG is lossless)
              --window <app|title|id>   one app window instead of a whole monitor
              --monitor <n>             which monitor (default 1)
              --width <px>              scale to this width; --quality <20-100> for JPEG
          sshmgr windows <pc> [--json]                        open app windows that can be captured
          sshmgr get <pc> <remote> <local>                    download a file (a folder comes as a .zip);
                                                                <local> may be a folder
          sshmgr put <pc> <local> <remote> [--overwrite]      upload a file; a <remote> ending in \ is a folder
          sshmgr wait <pc> --file <path> [--changed-after now|<time>] [--timeout <s>]
          sshmgr wait <pc> --process <name> [--exited] [--timeout <s>]
                                                              wait until a file appears or changes, or a program
                                                              starts (or exits); exit code 1 if it timed out
                                                              (default timeout 60 s)

        <pc> is a PC's name, the name or address it was added with, or its id (see `sshmgr pcs`).
        Exit codes: the script's own for run/action; 1 = failed; 2 = wrong usage; 3 = SSH Manager isn't
        running, or the PC is unknown or not connected.
        """;

    static readonly HashSet<string> ValueOptions =
        ["-f", "--file", "--cwd", "--env", "--title", "--timeout", "--window", "--monitor", "--width", "--quality",
         "--process", "--changed-after"];
    static readonly HashSet<string> FlagOptions = ["--json", "--overwrite", "--exited", "-h", "--help", "--version"];

    /// <summary>Standard output as UTF-8, flushed after each batch rather than each line.</summary>
    static readonly StreamWriter Out = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

    static async Task<int> Main(string[] argv)
    {
        try
        {
            var a = Parse(argv);
            var cmd = a.Pos.Count > 0 ? a.Pos[0].ToLowerInvariant() : "help";
            if (a.Flag("--version")) cmd = "version";
            if (a.Flag("-h") || a.Flag("--help")) cmd = "help";
            return cmd switch
            {
                "help" => Print(Help),
                "version" => Print(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? ""),
                "pcs" => await Pcs(a),
                "run" => await Run(a),
                "actions" => await Actions(a),
                "action" => await RunAction(a),
                "shot" => await Shot(a),
                "windows" => await Windows(a),
                "get" => await Get(a),
                "put" => await Put(a),
                "wait" => await Wait(a),
                _ => throw new Failure($"Unknown command \"{a.Pos[0]}\". Run `sshmgr help` for the commands.", Usage),
            };
        }
        catch (Failure f)
        {
            Out.Flush();
            Console.Error.WriteLine(f.Message);
            return f.Code;
        }
        finally { Out.Flush(); }
    }

    static int Print(string text)
    {
        Out.WriteLine(text.TrimEnd());
        return 0;
    }

    // ---- arguments ----

    sealed class Args
    {
        public readonly List<string> Pos = [];
        public readonly Dictionary<string, List<string>> Values = [];
        public readonly HashSet<string> Flags = [];

        public bool Flag(string name) => Flags.Contains(name);
        public string? Value(params string[] names) => names.Select(n => Values.GetValueOrDefault(n)?.LastOrDefault()).FirstOrDefault(v => v is not null);
        public List<string> All(string name) => Values.GetValueOrDefault(name) ?? [];

        public double? Number(string name)
        {
            if (Value(name) is not { } v) return null;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0
                ? d : throw new Failure($"{name} needs a number, not \"{v}\".", Usage);
        }

        /// <summary>Positional argument i (after the command), or a usage error naming what is missing.</summary>
        public string At(int i, string what) =>
            Pos.Count > i + 1 ? Pos[i + 1] : throw new Failure($"Missing {what}. Usage: see `sshmgr help`.", Usage);
    }

    static Args Parse(string[] argv)
    {
        var a = new Args();
        var rest = false;
        for (var i = 0; i < argv.Length; i++)
        {
            var t = argv[i];
            if (rest || !t.StartsWith('-') || t == "-") { a.Pos.Add(t); continue; }
            if (t == "--") { rest = true; continue; }
            if (ValueOptions.Contains(t))
            {
                if (i + 1 >= argv.Length) throw new Failure($"{t} needs a value.", Usage);
                if (!a.Values.TryGetValue(t, out var list)) a.Values[t] = list = [];
                list.Add(argv[++i]);
            }
            else if (FlagOptions.Contains(t)) a.Flags.Add(t);
            // a script's own parameters (`sshmgr run pc Get-ChildItem -Recurse`) are part of the script
            else if (a.Pos.Count > 0 && a.Pos[0].Equals("run", StringComparison.OrdinalIgnoreCase) && a.Pos.Count >= 3) a.Pos.Add(t);
            else throw new Failure($"Unknown option {t}. Run `sshmgr help` for the options.", Usage);
        }
        return a;
    }

    // ---- the app ----

    static async Task<JsonNode> AskApp(object request)
    {
        var name = "SSHManager.Cli." + WindowsIdentity.GetCurrent().User!.Value;
        // CurrentUserOnly: only talk to a pipe owned by this user (no other account can pose as the app)
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(1500); }
        catch (TimeoutException)
        {
            throw new Failure(System.Diagnostics.Process.GetProcessesByName("SSHManager").Length > 0
                ? "The SSH Manager app that is running doesn't answer the command-line tool; it needs version 1.5.0 or later. Update it and try again."
                : "SSH Manager isn't running. Start the SSH Manager app (it can stay in the tray) and try again.", Unreachable);
        }
        catch (UnauthorizedAccessException)
        {
            throw new Failure("The SSH Manager connection point on this PC belongs to another account; not using it.", Unreachable);
        }
        var line = JsonSerializer.Serialize(request) + "\n";
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line));
        await pipe.FlushAsync();
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        var answer = await reader.ReadLineAsync() ?? throw new Failure("SSH Manager didn't answer.", Unreachable);
        return JsonNode.Parse(answer) ?? throw new Failure("SSH Manager gave an unreadable answer.", Unreachable);
    }

    static Task<Pc> Connect(string query) => Pc.Open(query);

    /// <summary>One PC's server, reached through the app's port forward.</summary>
    sealed class Pc
    {
        static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };

        readonly string query;
        public string Name { get; private set; }
        int port;
        string token = "";

        Pc(string query) => this.query = Name = query;

        public static async Task<Pc> Open(string query)
        {
            var pc = new Pc(query);
            await pc.Attach(DateTime.UtcNow.AddSeconds(30), waitIfDisconnected: false);
            return pc;
        }

        /// <summary>Asks the app for the PC's port and token; waits while the app is (re)connecting to it.</summary>
        async Task Attach(DateTime deadline, bool waitIfDisconnected)
        {
            var said = false;
            while (true)
            {
                var r = await AskApp(new { cmd = "pc", pc = query });
                if (r["error"]?.GetValue<string>() is { } error) throw new Failure(error, Unreachable);
                Name = r["name"]!.GetValue<string>();
                var state = r["state"]!.GetValue<string>();
                if (state == "Connected" && r["port"]?.GetValue<int>() is > 0 and var p)
                {
                    port = p;
                    token = r["token"]!.GetValue<string>();
                    return;
                }
                // between attempts the app shows the PC as disconnected for a few seconds
                if ((state == "Connecting" || (waitIfDisconnected && state == "Disconnected")) && DateTime.UtcNow < deadline)
                {
                    if (!said) Console.Error.WriteLine($"Waiting for SSH Manager to connect to {Name}...");
                    said = true;
                    await Task.Delay(1000);
                    continue;
                }
                var message = r["message"]?.GetValue<string>() ?? "";
                throw new Failure($"{Name} isn't connected in SSH Manager{(message != "" ? ": " + message.Trim() : ".")}", Unreachable);
            }
        }

        public async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? body = null, bool stream = false)
        {
            HttpResponseMessage r;
            DateTime? deadline = null;
            while (true)
            {
                var req = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}") { Content = body };
                req.Headers.Add("X-SSHMgr-Token", token);
                req.Headers.Add("X-SSHMgr", "1");
                try
                {
                    r = await Http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
                    break;
                }
                // the connection dropped and the app hasn't noticed yet: nothing was sent, so wait for it to
                // reconnect (it may get another local port) and try again
                catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                                                     && (deadline ??= DateTime.UtcNow.AddSeconds(30)) > DateTime.UtcNow)
                {
                    await Task.Delay(1000);
                    await Attach(deadline.Value, waitIfDisconnected: true);
                }
                catch (HttpRequestException e)
                {
                    throw new Failure($"Couldn't reach {Name} through SSH Manager's connection ({e.Message}). Check its state in the app.", Unreachable);
                }
            }
            if (r.IsSuccessStatusCode) return r;
            var text = await r.Content.ReadAsStringAsync();
            string detail;
            try { detail = JsonNode.Parse(text)?["detail"]?.ToString() ?? text; }
            catch (JsonException) { detail = text; }
            if (detail.Trim() == "") detail = $"{(int)r.StatusCode} {r.ReasonPhrase}";
            throw new Failure($"{Name}: {detail.Trim()}", 1, r.StatusCode);
        }

        public async Task<JsonNode> Json(HttpMethod method, string path, object? body = null)
        {
            using var r = await Send(method, path, body is null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        }

        public Task<JsonNode> Get(string path) => Json(HttpMethod.Get, path);
    }

    static string Q(string s) => Uri.EscapeDataString(s);

    static void PrintJson(JsonNode? node) => Out.WriteLine(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");

    static void Table(IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var widths = Enumerable.Range(0, list[0].Length).Select(c => list.Max(r => r[c].Length)).ToArray();
        foreach (var r in list)
            Out.WriteLine(string.Join("  ", r.Select((v, c) => c == r.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd());
    }

    static string Str(JsonNode? n) => n?.GetValueKind() switch
    {
        null or JsonValueKind.Null => "",
        JsonValueKind.String => n.GetValue<string>(),
        _ => n.ToJsonString(),
    };

    // ---- commands ----

    static async Task<int> Pcs(Args a)
    {
        var r = await AskApp(new { cmd = "pcs" });
        if (a.Flag("--json")) { PrintJson(r["pcs"]); return 0; }
        var pcs = r["pcs"]!.AsArray();
        if (pcs.Count == 0) return Print("No PCs in SSH Manager yet. Add one in the app.");
        Table(new[] { new[] { "NAME", "ID", "STATE", "VERSION", "" } }.Concat(pcs.Select(p => new[]
        {
            Str(p!["name"]), Str(p["id"]), Str(p["state"]) == "Connected" ? "connected" : Str(p["state"]).ToLowerInvariant(),
            Str(p["version"]), Str(p["state"]) == "Connected" ? "" : Str(p["message"]).Split('\n')[0],
        })));
        return 0;
    }

    static async Task<int> Run(Args a)
    {
        var pcName = a.At(0, "the PC");
        string script;
        if (a.Value("-f", "--file") is { } file)
        {
            if (a.Pos.Count > 2) throw new Failure("Give either a script or -f <file>, not both.", Usage);
            try { script = file == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(file); }
            catch (IOException e) { throw new Failure($"Couldn't read {file}: {e.Message}", Usage); }
        }
        else script = string.Join(" ", a.Pos.Skip(2));
        if (script.Trim() == "") throw new Failure("Missing the script to run. Usage: sshmgr run <pc> <script> | -f <file>", Usage);
        var env = new Dictionary<string, string>();
        foreach (var kv in a.All("--env"))
        {
            var i = kv.IndexOf('=');
            if (i <= 0) throw new Failure($"--env needs NAME=value, not \"{kv}\".", Usage);
            env[kv[..i]] = kv[(i + 1)..];
        }
        var timeout = a.Number("--timeout");
        var pc = await Connect(pcName);
        var run = await pc.Json(HttpMethod.Post, "/api/run",
            new { script, title = a.Value("--title") ?? "", cwd = a.Value("--cwd") ?? "", env });
        return await Follow(pc, Str(run["id"]), timeout);
    }

    /// <summary>Streams a run's output until it ends and returns its exit code. Ctrl+C stops the run.</summary>
    static async Task<int> Follow(Pc pc, string id, double? timeout)
    {
        var interrupted = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; interrupted = true; };
        var deadline = timeout is { } t ? DateTime.UtcNow.AddSeconds(t) : DateTime.MaxValue;
        long after = 0;
        string? stopping = null;
        while (true)
        {
            var v = await pc.Get($"/api/runs/{id}?after={after}");
            if (v["skipped"]?.GetValue<long>() is > 0 and var skipped)
            {
                Out.Flush();
                Console.Error.WriteLine($"[{skipped} lines not shown; the complete output is at /api/runs/{id}/output]");
            }
            var lines = v["lines"]!.AsArray();
            foreach (var line in lines) Out.WriteLine(Str(line));
            Out.Flush();
            after = v["next"]!.GetValue<long>();
            var status = Str(v["status"]);
            if (status != "running")
            {
                var code = v["exit_code"]?.GetValue<int>() ?? 1;
                if (stopping is not null) return stopping == "timeout" ? 124 : 130;
                if (status == "stopped") Console.Error.WriteLine("The run was stopped.");
                return code;
            }
            if (stopping is null && (interrupted || DateTime.UtcNow > deadline))
            {
                stopping = interrupted ? "interrupted" : "timeout";
                Console.Error.WriteLine(interrupted ? "Stopping the run..." : $"Timed out after {timeout} s; stopping the run...");
                await pc.Json(HttpMethod.Post, $"/api/runs/{id}/stop");
            }
            await Task.Delay(lines.Count > 0 ? 200 : 500);
        }
    }

    static async Task<int> Actions(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"));
        var actions = (await pc.Get("/api/actions"))["actions"]!.AsArray();
        if (a.Flag("--json")) { PrintJson(actions); return 0; }
        if (actions.Count == 0) return Print($"{pc.Name} has no quick actions.");
        Table(new[] { new[] { "ID", "GROUP", "NAME" } }.Concat(actions.Select(x => new[] { Str(x!["id"]), Str(x["group"]), Str(x["name"]) })));
        return 0;
    }

    static async Task<int> RunAction(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"));
        var want = a.At(1, "the action's id or name");
        var actions = (await pc.Get("/api/actions"))["actions"]!.AsArray().Select(x => x!).ToList();
        var match = actions.Where(x => Str(x["id"]) == want).ToList();
        if (match.Count == 0) match = actions.Where(x => Str(x["name"]).Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count != 1)
            throw new Failure(match.Count == 0
                ? $"{pc.Name} has no quick action \"{want}\". See `sshmgr actions {a.Pos[1]}`."
                : $"More than one action is called \"{want}\"; use its id (see `sshmgr actions {a.Pos[1]}`).");
        var run = await pc.Json(HttpMethod.Post, $"/api/actions/{Q(Str(match[0]["id"]))}/run");
        return await Follow(pc, Str(run["id"]), a.Number("--timeout"));
    }

    static async Task<int> Windows(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"));
        var windows = (await pc.Get("/api/screen/windows"))["windows"]!.AsArray();
        if (a.Flag("--json")) { PrintJson(windows); return 0; }
        if (windows.Count == 0) return Print($"No app windows on {pc.Name} that can be captured.");
        Table(new[] { new[] { "ID", "APP", "TITLE" } }.Concat(windows.Select(w => new[]
            { Str(w!["id"]), Str(w["app"]), Str(w["title"]) + (w["minimized"]?.GetValue<bool>() == true ? "  (minimized)" : "") })));
        return 0;
    }

    static async Task<int> Shot(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"));
        var outPath = Path.GetFullPath(a.At(1, "the file to save the screenshot to (.png or .jpg)"));
        var ext = Path.GetExtension(outPath).ToLowerInvariant();
        var png = ext is not (".jpg" or ".jpeg");
        var query = $"format={(png ? "png" : "jpeg")}&width={(int)(a.Number("--width") ?? 0)}&quality={(int)(a.Number("--quality") ?? 90)}";
        var what = "";
        if (a.Value("--window") is { } want)
        {
            var w = PickWindow((await pc.Get("/api/screen/windows"))["windows"]!.AsArray(), want, pc.Name);
            query += $"&window={Str(w["id"])}";
            what = $" ({Str(w["app"])}: {Str(w["title"])})";
        }
        else query += $"&monitor={(int)(a.Number("--monitor") ?? 1)}";
        using var r = await pc.Send(HttpMethod.Get, "/api/screen?" + query);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllBytesAsync(outPath, bytes);
        return Print($"Saved {outPath} ({Size(bytes.Length)}){what}");
    }

    /// <summary>A window by id, else by program name (notepad, Code.exe), else by part of its title.</summary>
    static JsonNode PickWindow(JsonArray windows, string want, string pcName)
    {
        var all = windows.Select(w => w!).ToList();
        var key = want.ToLowerInvariant();
        if (key.EndsWith(".exe")) key = key[..^4];
        var found = all.Where(w => Str(w["id"]) == want).ToList();
        if (found.Count == 0) found = all.Where(w => Str(w["app"]).ToLowerInvariant() == key).ToList();
        if (found.Count == 0) found = all.Where(w => Str(w["title"]).Contains(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count == 0)
            throw new Failure($"No window of \"{want}\" on {pcName} that can be captured. Open ones: " +
                              (all.Count == 0 ? "none" : string.Join(", ", all.Select(w => Str(w["app"])).Distinct())));
        return found.FirstOrDefault(w => w["minimized"]?.GetValue<bool>() != true) ?? found[0];
    }

    static async Task<int> Get(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"));
        var remote = a.At(1, "the file on the PC");
        var local = a.At(2, "where to save it");
        using var r = await pc.Send(HttpMethod.Get, "/api/fs/download?path=" + Q(remote), stream: true);
        var cd = r.Content.Headers.ContentDisposition;
        var name = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"') is { Length: > 0 } n ? n : Path.GetFileName(remote.TrimEnd('\\', '/'));
        var dest = Directory.Exists(local) || local.EndsWith('\\') || local.EndsWith('/') ? Path.Combine(local, name) : local;
        dest = Path.GetFullPath(dest);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        long size;
        try
        {
            await using (var file = File.Create(part))
            {
                await (await r.Content.ReadAsStreamAsync()).CopyToAsync(file);
                size = file.Length;
            }
            File.Move(part, dest, overwrite: true);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            File.Delete(part);
            throw new Failure($"Downloading {remote} failed: {e.Message}");
        }
        return Print($"Saved {dest} ({Size(size)})");
    }

    static async Task<int> Put(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"));
        var local = a.At(1, "the file to upload");
        var remote = a.At(2, "where to put it on the PC");
        if (!File.Exists(local))
            throw new Failure(Directory.Exists(local) ? $"{local} is a folder; put uploads single files (zip a folder first)." : $"Not found: {local}", Usage);
        var name = Path.GetFileName(local);
        if (remote.EndsWith('\\') || remote.EndsWith('/')) remote += name;
        var mtime = (new DateTimeOffset(File.GetLastWriteTimeUtc(local)).ToUnixTimeMilliseconds() / 1000.0).ToString(CultureInfo.InvariantCulture);
        var overwrite = a.Flag("--overwrite") ? "&overwrite=1" : "";

        async Task<JsonNode> Upload(string path)
        {
            await using var file = File.OpenRead(local);
            var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var r = await pc.Send(HttpMethod.Put, $"/api/fs/upload?path={Q(path)}&mtime={mtime}{overwrite}", content);
            return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        }

        JsonNode done;
        try { done = await Upload(remote); }
        catch (Failure f) when (f.Status == HttpStatusCode.Conflict && f.Message.Contains("A folder with that name exists"))
        {
            done = await Upload(remote.TrimEnd('\\', '/') + "\\" + name);   // <remote> is a folder: put it inside
        }
        catch (Failure f) when (f.Status == HttpStatusCode.Conflict && f.Message.Contains("Already exists"))
        {
            throw new Failure(f.Message + " (add --overwrite to replace it)");
        }
        var path = Str(done["path"]);
        return Print($"Uploaded {Path.GetFullPath(local)} to {(path != "" ? path : remote)} on {pc.Name} ({Size(new FileInfo(local).Length)})");
    }

    static async Task<int> Wait(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"));
        var file = a.Value("--file");
        var process = a.Value("--process");
        if ((file is null) == (process is null)) throw new Failure("Give either --file <path> or --process <name>.", Usage);
        var timeout = a.Number("--timeout") ?? 60;
        var changedAfter = a.Value("--changed-after");
        var start = DateTime.UtcNow;
        while (true)
        {
            // the server waits up to 300 s per call; longer waits take several calls
            var left = Math.Max(0, timeout - (DateTime.UtcNow - start).TotalSeconds);
            var chunk = Math.Min(left, 300);
            var query = file is not null
                ? $"file={Q(file)}" + (changedAfter is not null ? $"&changed_after={Q(changedAfter)}" : "")
                : $"process={Q(process!)}&state={(a.Flag("--exited") ? "exited" : "started")}";
            using var r = await pc.Send(HttpMethod.Get, $"/api/wait?{query}&timeout={chunk.ToString("0.#", CultureInfo.InvariantCulture)}");
            var v = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
            var met = v["met"]?.GetValue<bool>() == true;
            if (met || left <= 300)
            {
                if (a.Flag("--json")) PrintJson(v);
                else if (met && file is not null) Print($"{Str(v["file"])} ({Size(v["size"]?.GetValue<long>() ?? 0)}, modified {Str(v["mtime"])})");
                else if (met) Print($"{Str(v["process"])} {Str(v["state"])}" + (v["pids"] is JsonArray { Count: > 0 } pids ? $" (PID {string.Join(", ", pids.Select(Str))})" : ""));
                if (!met) Console.Error.WriteLine($"Timed out after {Math.Round((DateTime.UtcNow - start).TotalSeconds)} s.");
                return met ? 0 : 1;
            }
            // "now" must keep meaning when the wait began, by the PC's clock
            if (changedAfter == "now" && r.Headers.Date is { } date)
                changedAfter = (date.ToUnixTimeSeconds() - (v["waited_s"]?.GetValue<double>() ?? 0)).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
