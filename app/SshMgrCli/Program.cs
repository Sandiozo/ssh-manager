// sshmgr: lets scripts and AI agents use the PCs the SSH Manager app is connected to. It asks the app
// (over a local named pipe only this Windows user can open) for a PC's connection port and access token,
// then calls that PC's server through the app's own SSH port forward: no SSH connection or token of its
// own, nothing stored on disk. Run `sshmgr help` for the commands.
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SshMgrCli;

/// <summary>An error to report and the exit code to end with. <see cref="Lost"/>: the request may have
/// reached the PC before the connection failed (so something may have started there).</summary>
sealed class Failure(string message, int code = 1, HttpStatusCode? status = null, bool lost = false) : Exception(message)
{
    public int Code { get; } = code;
    public HttpStatusCode? Status { get; } = status;
    public bool Lost { get; } = lost;
}

static partial class Program
{
    const int Usage = 2, Unreachable = 3, ConnectionLost = 4, TimedOut = 124, Interrupted = 130;

    const string Help = """
        sshmgr - use the PCs the SSH Manager app is connected to (through the app's own connections)

        Usage:
          sshmgr mcp [--as <name>]                            an MCP server (stdio) for AI agents: see the README
          sshmgr pcs [--json]                                 the PCs in the app and whether they are connected
          sshmgr run [options] <pc> <script...>               run PowerShell on the PC (as administrator) and
          sshmgr run [options] <pc> -f <file.ps1|->             stream its output; exits with the script's exit code
              --cwd <dir>        working directory (environment variables work, e.g. %USERPROFILE%)
              --env <K=V>        extra environment variable (repeatable)
              --title <text>     title in the PC's activity log
              --timeout <s>      stop the run after this many seconds (exit code 124)
            Options go before the script: everything after the script's first word is part of the script
            (`sshmgr run pc tar -h` runs `tar -h`). Several words are joined with spaces; a word that contains
            spaces is passed on as one quoted argument. For pipes, $variables or quotes, give the whole script
            as one quoted argument, or use -f.
          sshmgr runs <pc> [--json]                           recent runs on the PC (and the ones still running)
          sshmgr actions <pc> [--json]                        list the PC's quick actions
          sshmgr action <pc> <id|name> [--timeout <s>]        run a quick action and stream its output
          sshmgr shot <pc> <out.png|out.jpg> [options]        save a screenshot (full size; PNG is lossless)
              --window <app|title|id>   one app window instead of a whole monitor
              --monitor <n>             which monitor (default 1)
              --width <px>              scale to this width; --quality <20-100> for JPEG
          sshmgr windows <pc> [--json]                        open app windows that can be captured
          sshmgr get <pc> <remote> <local>                    download a file (a folder comes as a .zip);
                                                                <local> may be a folder
          sshmgr put <pc> <local> <remote> [--overwrite]      upload a file; a <remote> ending in \ (or an
                                                                existing folder) is the folder to put it in
          sshmgr wait <pc> --file <path> [--changed-after now|<time>] [--timeout <s>]
          sshmgr wait <pc> --process <name> [--exited] [--timeout <s>]
                                                              wait until a file appears or changes, or a program
                                                              starts (or exits); exit code 1 if it timed out
                                                              (default timeout 60 s)

        <pc> is a PC's name, the name or address it was added with, or its id (see `sshmgr pcs`).
        --as <name> (any command) marks the calls as an AI agent's, e.g. --as Claude: the PC labels them
        "Claude (<this computer>)" and applies its AI agent settings. The SSHMGR_AGENT environment variable
        does the same; inside Claude Code, Codex, Cursor or Gemini CLI, calls count as that agent's.
        Ctrl+C stops a run (or withdraws a quick action's request for approval) and waits for it to end;
        press it again to quit at once.
        Exit codes: the script's own for run/action; 1 = failed; 2 = wrong usage; 3 = SSH Manager isn't
        running, or the PC is unknown or not connected; 4 = the connection was lost while a run was going
        (it may still be running: see `sshmgr runs <pc>`); 124 = --timeout stopped the run; 130 = Ctrl+C.
        """;

    static readonly HashSet<string> ValueOptions =
        ["-f", "--file", "--cwd", "--env", "--title", "--timeout", "--window", "--monitor", "--width", "--quality",
         "--process", "--changed-after", "--as"];
    static readonly HashSet<string> FlagOptions = ["--json", "--overwrite", "--exited", "-h", "--help", "--version"];

    /// <summary>Standard output as UTF-8, flushed after each batch rather than each line.</summary>
    static readonly StreamWriter Out = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

    /// <summary>The first Ctrl+C: commands stop what they started and end; a second one quits at once.</summary>
    static readonly CancellationTokenSource Interrupt = new();

    static Encoding? consoleEncoding;

    static async Task<int> Main(string[] argv)
    {
        try
        {
            var a = Parse(argv);
            var cmd = a.Pos.Count > 0 ? a.Pos[0].ToLowerInvariant() : "help";
            if (a.Flag("--version")) cmd = "version";
            if (a.Flag("-h") || a.Flag("--help")) cmd = "help";
            if (cmd != "mcp")
            {
                // a console shows what we write as UTF-8 only while its code page is UTF-8 (restored at the end;
                // `mcp` talks to a program over pipes and leaves the console alone)
                if (!Console.IsOutputRedirected || !Console.IsErrorRedirected)
                {
                    try
                    {
                        consoleEncoding = Console.OutputEncoding;
                        Console.OutputEncoding = new UTF8Encoding(false);
                    }
                    catch (Exception) { consoleEncoding = null; }
                }
                Console.CancelKeyPress += OnCtrlC;
                var name = a.Value("--as") ?? Environment.GetEnvironmentVariable("SSHMGR_AGENT") ?? DetectAgent();
                Agent = name is null ? null : AgentName(name) is { Length: > 0 } n ? n : "AI agent";
            }
            return cmd switch
            {
                "help" => Print(Help),
                "version" => Print(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? ""),
                "mcp" => await Mcp(a),
                "pcs" => await Pcs(a),
                "run" => await Run(a),
                "runs" => await Runs(a),
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
            Complain(f.Message);
            return f.Code;
        }
        catch (OperationCanceledException) when (Interrupt.IsCancellationRequested)
        {
            Complain("Interrupted.");
            return Interrupted;
        }
        catch (Exception e)
        {
            Complain(OneLine(e));
            return 1;
        }
        finally
        {
            try { Out.Flush(); } catch (Exception) { }
            RestoreConsole();
        }
    }

    static void OnCtrlC(object? sender, ConsoleCancelEventArgs e)
    {
        if (!Interrupt.IsCancellationRequested)
        {
            e.Cancel = true;
            try { Interrupt.Cancel(); } catch (Exception) { }
            return;
        }
        RestoreConsole();   // the second Ctrl+C ends the process right away
    }

    static void RestoreConsole()
    {
        if (consoleEncoding is not { } e) return;
        consoleEncoding = null;
        try { Console.OutputEncoding = e; } catch (Exception) { }
    }

    static void Complain(string message)
    {
        try { Out.Flush(); } catch (Exception) { }
        try { Console.Error.WriteLine(message); } catch (Exception) { }
    }

    /// <summary>An unexpected error as one line (never a stack trace).</summary>
    static string OneLine(Exception e)
    {
        var text = e switch
        {
            HttpRequestException => $"Network error: {e.Message}",
            JsonException => $"Got an answer that couldn't be read: {e.Message}",
            IOException or UnauthorizedAccessException => e.Message,
            OperationCanceledException => "Cancelled.",
            _ => $"Unexpected error ({e.GetType().Name}): {e.Message}",
        };
        return text.Replace("\r", "").Split('\n')[0].Trim();
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
        /// <summary>`run`: the script's words, from its first one on (they are never read as options).</summary>
        public readonly List<string> Script = [];
        public readonly Dictionary<string, List<string>> Values = [];
        public readonly HashSet<string> Flags = [];

        public bool Flag(string name) => Flags.Contains(name);
        public string? Value(params string[] names) => names.Select(n => Values.GetValueOrDefault(n)?.LastOrDefault()).FirstOrDefault(v => v is not null);
        public List<string> All(string name) => Values.GetValueOrDefault(name) ?? [];

        public double? Number(string name)
        {
            if (Value(name) is not { } v) return null;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && !double.IsInfinity(d)
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
        bool IsRun() => a.Pos.Count > 0 && a.Pos[0].Equals("run", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < argv.Length; i++)
        {
            var t = argv[i];
            // `run <pc> <script...>`: from the script's first word on, everything is the script's
            if (a.Script.Count > 0) { a.Script.Add(t); continue; }
            if (rest || !t.StartsWith('-') || t == "-")
            {
                if (IsRun() && a.Pos.Count == 2) a.Script.Add(t);
                else a.Pos.Add(t);
                continue;
            }
            if (t == "--") { rest = true; continue; }
            if (ValueOptions.Contains(t))
            {
                if (i + 1 >= argv.Length) throw new Failure($"{t} needs a value.", Usage);
                if (!a.Values.TryGetValue(t, out var list)) a.Values[t] = list = [];
                list.Add(argv[++i]);
            }
            else if (FlagOptions.Contains(t)) a.Flags.Add(t);
            else throw new Failure($"Unknown option {t}. Run `sshmgr help` for the options" +
                                   (IsRun() ? " (sshmgr's options go before the script)." : "."), Usage);
        }
        return a;
    }

    /// <summary>The script from several words: the first as given (it may be a whole script), the others too
    /// unless they contain spaces or quotes (they were one argument, so they stay one: quoted for PowerShell).</summary>
    static string JoinScript(List<string> words)
    {
        if (words.Count <= 1) return words.FirstOrDefault() ?? "";
        static bool NeedsQuotes(string w) => w == "" || w.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '‘' or '’');
        static string Quote(string w) => "'" + w.Replace("'", "''").Replace("‘", "‘‘").Replace("’", "’’") + "'";
        // the first word is script text ("Get-Service sshd" -Verbose), the others are its arguments
        return string.Join(" ", words.Skip(1).Select(w => NeedsQuotes(w) ? Quote(w) : w).Prepend(words[0]));
    }

    // ---- the app ----

    static async Task<JsonNode> AskApp(object request, CancellationToken ct)
    {
        var name = "SSHManager.Cli." + WindowsIdentity.GetCurrent().User!.Value;
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var appRunning = System.Diagnostics.Process.GetProcessesByName("SSHManager").Length > 0;
        if (!appRunning)
            throw new Failure("SSH Manager isn't running. Start the SSH Manager app (it can stay in the tray) and try again.", Unreachable);
        try
        {
            // a few seconds: several calls at once may have to take turns
            await pipe.ConnectAsync(5000, ct);
            // only talk to a pipe owned by this user account: no other account can make that so it can't
            // pose as the app. (Not PipeOptions.CurrentUserOnly: it compares with the token's owner, which in
            // an elevated shell is the Administrators group, and would refuse the user's own app there.)
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner is null || !owner.Equals(WindowsIdentity.GetCurrent().User)) throw new UnauthorizedAccessException();
        }
        catch (TimeoutException)
        {
            throw new Failure("The SSH Manager app that is running doesn't answer the command-line tool; it needs version 1.5.0 or later. Update it and try again.", Unreachable);
        }
        catch (UnauthorizedAccessException)
        {
            throw new Failure("The SSH Manager connection point on this PC belongs to another account; not using it.", Unreachable);
        }
        catch (IOException e)
        {
            throw new Failure($"Couldn't talk to the SSH Manager app ({e.Message}).", Unreachable);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var line = JsonSerializer.Serialize(request) + "\n";
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var answer = await reader.ReadLineAsync(timeout.Token) ?? throw new Failure("SSH Manager didn't answer.", Unreachable);
            return JsonNode.Parse(answer) ?? throw new Failure("SSH Manager gave an unreadable answer.", Unreachable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new Failure("The SSH Manager app didn't answer in time. Is it busy or stuck?", Unreachable);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            throw new Failure($"Couldn't talk to the SSH Manager app ({e.Message}).", Unreachable);
        }
    }

    static Task<Pc> Connect(string query, CancellationToken ct) => Pc.Open(query, ct);

    /// <summary>The AI agent these calls are made for (null for a person: then no agent header is sent).</summary>
    static string? Agent;

    /// <summary>An agent running this tool, from the environment its tools give to the programs they start.</summary>
    static string? DetectAgent()
    {
        var names = Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList();
        bool Has(string n) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n));
        bool Prefix(string p) => names.Any(k => k.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (Has("CLAUDECODE") || Prefix("CLAUDE_CODE_")) return "Claude";
        if (Has("CODEX_SANDBOX") || Prefix("CODEX_")) return "Codex";
        if (Has("CURSOR_TRACE_ID") || Has("CURSOR_AGENT")) return "Cursor";
        if (Has("GEMINI_CLI")) return "Gemini";
        return null;
    }

    /// <summary>A name for the agent header: letters and digits (any language), spaces and . @ + - _ only,
    /// at most 40 characters.</summary>
    static string AgentName(string? name)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var r in (name ?? "").EnumerateRunes())
        {
            var keep = Rune.IsLetterOrDigit(r) || (r.IsAscii && " .@+-_".Contains((char)r.Value))
                       || Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            if (!keep || r.Value < 32) continue;
            sb.Append(r.ToString());
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var clean = new StringBuilder();
        foreach (var r in string.Join(' ', words).EnumerateRunes())
        {
            if (++count > 40) break;
            clean.Append(r.ToString());
        }
        return clean.ToString().TrimEnd();
    }

    /// <summary>The agent's name as the header carries it: ASCII as is, anything else as percent-encoded UTF-8
    /// (the cleaned name never contains %, so the server can always decode it).</summary>
    static string HeaderValue(string name)
    {
        var sb = new StringBuilder();
        Span<byte> buf = stackalloc byte[4];
        foreach (var r in name.EnumerateRunes())
        {
            if (r.IsAscii) { sb.Append((char)r.Value); continue; }
            var n = r.EncodeToUtf8(buf);
            for (var i = 0; i < n; i++) sb.Append('%').Append(buf[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>One PC's server, reached through the app's port forward.</summary>
    sealed class Pc
    {
        static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
            { Timeout = Timeout.InfiniteTimeSpan };

        public readonly string Query;
        public string Name { get; private set; }
        int port;
        string token = "";

        Pc(string query) => Query = Name = query;

        public static async Task<Pc> Open(string query, CancellationToken ct)
        {
            var pc = new Pc(query);
            await pc.Attach(DateTime.UtcNow.AddSeconds(30), waitIfDisconnected: false, ct);
            return pc;
        }

        /// <summary>Asks the app for the PC's port and token; waits while the app is (re)connecting to it.</summary>
        async Task Attach(DateTime deadline, bool waitIfDisconnected, CancellationToken ct)
        {
            var said = false;
            while (true)
            {
                var r = await AskApp(new { cmd = "pc", pc = Query }, ct);
                if (r["error"] is JsonValue error) throw new Failure(Str(error), Unreachable);
                Name = Str(r["name"]) is { Length: > 0 } n ? n : Query;
                var state = Str(r["state"]);
                if (state == "Connected" && r["port"] is JsonValue pv && pv.TryGetValue<int>(out var p) && p > 0)
                {
                    port = p;
                    token = Str(r["token"]);
                    return;
                }
                // between attempts the app shows the PC as disconnected for a few seconds
                if ((state == "Connecting" || (waitIfDisconnected && state == "Disconnected")) && DateTime.UtcNow < deadline)
                {
                    if (!said && Console.IsErrorRedirected == false) Console.Error.WriteLine($"Waiting for SSH Manager to connect to {Name}...");
                    said = true;
                    await Task.Delay(1000, ct);
                    continue;
                }
                var message = Str(r["message"]);
                throw new Failure($"{Name} isn't connected in SSH Manager{(message != "" ? ": " + message.Trim() : ".")}", Unreachable);
            }
        }

        /// <summary>Sends a request; `dog` limits how long it may take (the caller keeps it alive while a body
        /// streams). An error answer becomes a Failure with the server's explanation.</summary>
        public async Task<HttpResponseMessage> Send(HttpMethod method, string path, Watchdog dog, HttpContent? body = null, bool stream = false)
        {
            HttpResponseMessage r;
            DateTime? deadline = null;
            try
            {
                while (true)
                {
                    var req = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}") { Content = body };
                    req.Headers.Add("X-SSHMgr-Token", token);
                    req.Headers.Add("X-SSHMgr", "1");
                    // an AI agent says who it is (the PC labels and limits its calls; any value, even empty,
                    // marks an agent); where its app can approve
                    if (Agent is not null) req.Headers.TryAddWithoutValidation("X-SSHMgr-Agent", HeaderValue(Agent));
                    req.Headers.Add("X-SSHMgr-From", Environment.MachineName);
                    try
                    {
                        r = await Http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, dog.Token);
                        break;
                    }
                    // the connection dropped and the app hasn't noticed yet: nothing was sent, so wait for it to
                    // reconnect (it may get another local port) and try again
                    catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                                                         && (deadline ??= DateTime.UtcNow.AddSeconds(30)) > DateTime.UtcNow)
                    {
                        await Task.Delay(1000, dog.Token);
                        await Attach(deadline.Value, waitIfDisconnected: true, dog.Token);
                    }
                    catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                    {
                        throw new Failure($"Couldn't reach {Name} through SSH Manager's connection ({e.Message}). Check its state in the app.", Unreachable);
                    }
                }
                if (r.IsSuccessStatusCode) return r;
                using (r)
                {
                    var text = await r.Content.ReadAsStringAsync(dog.Token);
                    string detail;
                    try { detail = JsonNode.Parse(text)?["detail"] is { } d ? (d is JsonValue ? Str(d) : d.ToJsonString()) : text; }
                    catch (Exception) { detail = text; }
                    if (detail.Trim() == "") detail = $"{(int)r.StatusCode} {r.ReasonPhrase}";
                    throw new Failure($"{Name}: {detail.Trim()}", 1, r.StatusCode);
                }
            }
            catch (Exception e) when (Lost(e, dog) is { } f) { throw f; }
        }

        /// <summary>A failed connection or a time limit as a Failure (null for other errors, which pass on).</summary>
        public Failure? Lost(Exception e, Watchdog dog) => e switch
        {
            Failure => null,
            OperationCanceledException when dog.Expired =>
                new Failure($"{Name} didn't answer for {dog.Seconds:0} s; the connection may be down (check it in SSH Manager).", Unreachable, lost: true),
            HttpRequestException or IOException =>
                new Failure($"Lost the connection to {Name} ({e.Message.Split('\n')[0].Trim()}). Check its state in SSH Manager.", Unreachable, lost: true),
            _ => null,
        };

        /// <summary>A JSON call that must be answered within `seconds`.</summary>
        public async Task<JsonNode> Json(HttpMethod method, string path, CancellationToken ct, object? body = null, double seconds = 20)
        {
            using var dog = new Watchdog(seconds, ct);
            try
            {
                using var r = await Send(method, path, dog, body is null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
                var text = await r.Content.ReadAsStringAsync(dog.Token);
                try { return JsonNode.Parse(text) ?? throw new JsonException("empty"); }
                catch (JsonException) { throw new Failure($"{Name} gave an answer that couldn't be read ({path}).", 1); }
            }
            catch (Exception e) when (Lost(e, dog) is { } f) { throw f; }
        }

        public Task<JsonNode> Get(string path, CancellationToken ct, double seconds = 20) => Json(HttpMethod.Get, path, ct, null, seconds);
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

    static bool IsTrue(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l
                                     : n is JsonValue d && d.TryGetValue<double>(out var x) ? (long)x : 0;

    static double Dbl(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    static JsonArray Arr(JsonNode? n) => n as JsonArray ?? [];

    static async Task Nap(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }

    // ---- commands ----

    static async Task<int> Pcs(Args a)
    {
        var r = await AskApp(new { cmd = "pcs" }, Interrupt.Token);
        if (a.Flag("--json")) { PrintJson(r["pcs"]); return 0; }
        var pcs = Arr(r["pcs"]);
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
            if (a.Script.Count > 0) throw new Failure("Give either a script or -f <file>, not both.", Usage);
            byte[] bytes;
            try
            {
                if (file == "-")
                {
                    using var ms = new MemoryStream();
                    await Console.OpenStandardInput().CopyToAsync(ms, Interrupt.Token);
                    bytes = ms.ToArray();
                }
                else bytes = await File.ReadAllBytesAsync(file, Interrupt.Token);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new Failure($"Couldn't read {file}: {e.Message}", Usage); }
            script = DecodeText(bytes) ?? throw new Failure($"{file} isn't a text file.", Usage);
        }
        else script = JoinScript(a.Script);
        if (script.Trim() == "") throw new Failure("Missing the script to run. Usage: sshmgr run [options] <pc> <script> | -f <file>", Usage);
        var env = new Dictionary<string, string>();
        foreach (var kv in a.All("--env"))
        {
            var i = kv.IndexOf('=');
            if (i <= 0) throw new Failure($"--env needs NAME=value, not \"{kv}\".", Usage);
            env[kv[..i]] = kv[(i + 1)..];
        }
        var timeout = a.Number("--timeout");
        var pc = await Connect(pcName, Interrupt.Token);
        JsonNode run;
        try
        {
            run = await pc.Json(HttpMethod.Post, "/api/run", Interrupt.Token,
                new { script, title = a.Value("--title") ?? "", cwd = a.Value("--cwd") ?? "", env });
        }
        catch (Failure f) when (f.Lost)
        {
            throw new Failure($"{f.Message} The script may have started on {pc.Name} anyway: see `sshmgr runs {pc.Query}`.", ConnectionLost);
        }
        return await Follow(pc, Str(run["id"]), timeout);
    }

    /// <summary>Streams a run's output until it ends and returns its exit code. Ctrl+C stops the run (and
    /// waits for it to end); a second Ctrl+C quits at once.</summary>
    static async Task<int> Follow(Pc pc, string id, double? timeout)
    {
        var deadline = timeout is > 0 and var t ? DateTime.UtcNow.AddSeconds(t) : DateTime.MaxValue;
        long after = 0;
        string? stopping = null;
        Failure Lost(Failure f) =>
            new($"{f.Message}\nRun {id} may still be running on {pc.Name}: see `sshmgr runs {pc.Query}`.", ConnectionLost);
        while (true)
        {
            JsonNode v;
            try { v = await pc.Get($"/api/runs/{Q(id)}?after={after}", CancellationToken.None); }
            catch (Failure f) when (f.Code == Unreachable) { throw Lost(f); }
            if (Long(v["skipped"]) is > 0 and var skipped)
            {
                Out.Flush();
                Console.Error.WriteLine($"[{skipped} lines not shown; the complete output is in {Str(v["output_file"])} on {pc.Name}]");
            }
            var lines = Arr(v["lines"]);
            foreach (var line in lines) Out.WriteLine(Str(line));
            Out.Flush();
            after = Long(v["next"]);
            var status = Str(v["status"]);
            if (status != "running")
            {
                if (stopping is not null) return stopping == "timeout" ? TimedOut : Interrupted;
                if (status == "stopped") Console.Error.WriteLine("The run was stopped.");
                return ExitCode(v["exit_code"]);
            }
            if (stopping is null && (Interrupt.IsCancellationRequested || DateTime.UtcNow > deadline))
            {
                stopping = Interrupt.IsCancellationRequested ? "interrupted" : "timeout";
                Console.Error.WriteLine((stopping == "interrupted" ? "Stopping the run..." : $"Timed out after {timeout} s; stopping the run...")
                                        + " (Ctrl+C again quits without waiting)");
                try { await pc.Json(HttpMethod.Post, $"/api/runs/{Q(id)}/stop", CancellationToken.None); }
                catch (Failure f) when (f.Code == Unreachable) { throw Lost(f); }
            }
            await Nap(lines.Count > 0 ? 200 : 500, stopping is null ? Interrupt.Token : CancellationToken.None);
        }
    }

    static async Task<int> Runs(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"), Interrupt.Token);
        var runs = Arr((await pc.Get("/api/runs", Interrupt.Token))["runs"]);
        if (a.Flag("--json")) { PrintJson(runs); return 0; }
        if (runs.Count == 0) return Print($"No runs on {pc.Name} since its server started.");
        Table(new[] { new[] { "ID", "STATUS", "EXIT", "STARTED", "BY", "TITLE" } }.Concat(runs.Select(r => new[]
        {
            Str(r!["id"]), Str(r["status"]), Str(r["status"]) == "running" ? "" : ExitText(r["exit_code"]),
            Str(r["started"]).Replace("T", " ").Replace("+00:00", "Z"), Str(r["client"]), Str(r["title"]),
        })));
        return 0;
    }

    static async Task<int> Actions(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"), Interrupt.Token);
        var actions = Arr((await pc.Get("/api/actions", Interrupt.Token))["actions"]);
        if (a.Flag("--json")) { PrintJson(actions); return 0; }
        if (actions.Count == 0) return Print($"{pc.Name} has no quick actions.");
        Table(new[] { new[] { "ID", "GROUP", "NAME" } }.Concat(actions.Select(x => new[] { Str(x!["id"]), Str(x["group"]), Str(x["name"]) })));
        return 0;
    }

    static async Task<int> RunAction(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"), Interrupt.Token);
        var want = a.At(1, "the action's id or name");
        var actions = Arr((await pc.Get("/api/actions", Interrupt.Token))["actions"]).Select(x => x!).ToList();
        var match = actions.Where(x => Str(x["id"]) == want).ToList();
        if (match.Count == 0) match = actions.Where(x => Str(x["name"]).Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count != 1)
            throw new Failure(match.Count == 0
                ? $"{pc.Name} has no quick action \"{want}\". See `sshmgr actions {a.Pos[1]}`."
                : $"More than one action is called \"{want}\"; use its id (see `sshmgr actions {a.Pos[1]}`).");
        var action = match[0];
        // an agent's request for an action that asks first waits until a person allows it
        var asks = IsTrue(action["confirm"]) && Agent is not null;
        if (asks)
            Console.Error.WriteLine($"\"{Str(action["name"])}\" asks first: waiting for a person to allow it in SSH Manager " +
                                    "(up to 2 minutes; Ctrl+C withdraws the request)...");
        JsonNode run;
        try { run = await pc.Json(HttpMethod.Post, $"/api/actions/{Q(Str(action["id"]))}/run", Interrupt.Token, seconds: asks ? 300 : 20); }
        catch (OperationCanceledException) when (Interrupt.IsCancellationRequested)
        {
            throw new Failure(asks ? "Withdrew the request for approval; the action won't run." : "Interrupted.", Interrupted);
        }
        catch (Failure f) when (f.Lost)
        {
            throw new Failure($"{f.Message} The action may have started on {pc.Name} anyway: see `sshmgr runs {pc.Query}`.", ConnectionLost);
        }
        if (asks) Console.Error.WriteLine("Allowed.");
        return await Follow(pc, Str(run["id"]), a.Number("--timeout"));
    }

    static async Task<int> Windows(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"), Interrupt.Token);
        var windows = Arr((await pc.Get("/api/screen/windows", Interrupt.Token))["windows"]);
        if (a.Flag("--json")) { PrintJson(windows); return 0; }
        if (windows.Count == 0) return Print($"No app windows on {pc.Name} that can be captured.");
        Table(new[] { new[] { "ID", "APP", "TITLE" } }.Concat(windows.Select(w => new[]
            { Str(w!["id"]), Str(w["app"]), Str(w["title"]) + (IsTrue(w["minimized"]) ? "  (minimized)" : "") })));
        return 0;
    }

    static async Task<int> Shot(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"), Interrupt.Token);
        var outPath = Path.GetFullPath(a.At(1, "the file to save the screenshot to (.png or .jpg)"));
        var ext = Path.GetExtension(outPath).ToLowerInvariant();
        var png = ext is not (".jpg" or ".jpeg");
        var query = $"format={(png ? "png" : "jpeg")}&width={(int)(a.Number("--width") ?? 0)}&quality={(int)(a.Number("--quality") ?? 90)}";
        var what = "";
        if (a.Value("--window") is { } want)
        {
            var w = PickWindow(Arr((await pc.Get("/api/screen/windows", Interrupt.Token))["windows"]), want, pc.Name);
            query += $"&window={Q(Str(w["id"]))}";
            what = $" ({Str(w["app"])}: {Str(w["title"])})";
        }
        else query += $"&monitor={(int)(a.Number("--monitor") ?? 1)}";
        var size = await Download(pc, "/api/screen?" + query, outPath, Interrupt.Token);
        return Print($"Saved {outPath} ({Size(size)}){what}");
    }

    /// <summary>Saves a response body to a file (through a .part file, so a broken transfer leaves nothing).</summary>
    static async Task<long> Download(Pc pc, string path, string dest, CancellationToken ct)
    {
        using var dog = new Watchdog(60, ct);
        try
        {
            using var r = await pc.Send(HttpMethod.Get, path, dog, stream: true);
            return await SaveBody(r, dest, dog);
        }
        catch (Exception e) when (pc.Lost(e, dog) is { } f) { throw f; }
    }

    static async Task<long> SaveBody(HttpResponseMessage r, string dest, Watchdog dog)
    {
        var part = dest + ".part";
        FileStream file;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            file = File.Create(part);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new Failure($"Couldn't write {dest}: {e.Message}"); }
        try
        {
            long size;
            await using (file)
                size = await CopyAsync(await r.Content.ReadAsStreamAsync(dog.Token), file, dog);
            try { File.Move(part, dest, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new Failure($"Couldn't write {dest}: {e.Message}"); }
            return size;
        }
        catch (Exception)
        {
            try { File.Delete(part); } catch (Exception) { }
            throw;
        }
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
        return found.FirstOrDefault(w => !IsTrue(w["minimized"])) ?? found[0];
    }

    /// <summary>The file name the server gives a download (made safe), else the remote path's last part.</summary>
    static string DownloadName(HttpResponseMessage r, string remote)
    {
        var cd = r.Content.Headers.ContentDisposition;
        var given = cd?.FileNameStar ?? cd?.FileName;
        return SafeFileName(string.IsNullOrWhiteSpace(given) ? Path.GetFileName(remote.TrimEnd('\\', '/')) : given);
    }

    static async Task<int> Get(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"), Interrupt.Token);
        var remote = a.At(1, "the file on the PC");
        var local = a.At(2, "where to save it");
        using var dog = new Watchdog(60, Interrupt.Token);
        string dest;
        long size;
        try
        {
            using var r = await pc.Send(HttpMethod.Get, "/api/fs/download?path=" + Q(remote), dog, stream: true);
            var name = DownloadName(r, remote);
            dest = Path.GetFullPath(Directory.Exists(local) || local.EndsWith('\\') || local.EndsWith('/') ? Path.Combine(local, name) : local);
            size = await SaveBody(r, dest, dog);
        }
        catch (Exception e) when (pc.Lost(e, dog) is { } f) { throw new Failure($"Downloading {remote} failed: {f.Message}", f.Code); }
        return Print($"Saved {dest} ({Size(size)})");
    }

    /// <summary>Whether a path on the PC is an existing folder.</summary>
    static async Task<bool> IsFolder(Pc pc, string path, CancellationToken ct)
    {
        try
        {
            await pc.Get("/api/fs/list?path=" + Q(path), ct);
            return true;
        }
        catch (Failure f) when (f.Status is not null) { return false; }
    }

    /// <summary>Uploads to `remote`; one that turns out to be an existing folder gets the file inside it.
    /// Returns the server's answer.</summary>
    static async Task<JsonNode> Upload(Pc pc, string remote, string name, Func<Stream> open, long length, string query,
                                       bool overwrite, string overwriteHint, CancellationToken ct)
    {
        async Task<JsonNode> PutTo(string path)
        {
            using var dog = new Watchdog(60, ct);
            try
            {
                using var body = new UploadContent(open, length, dog);
                using var r = await pc.Send(HttpMethod.Put, $"/api/fs/upload?path={Q(path)}{query}{(overwrite ? "&overwrite=1" : "")}", dog, body);
                return JsonNode.Parse(await r.Content.ReadAsStringAsync(dog.Token)) ?? new JsonObject();
            }
            catch (Exception e) when (pc.Lost(e, dog) is { } f) { throw f; }
        }

        try { return await PutTo(remote); }
        catch (Failure f) when (f.Status == HttpStatusCode.Conflict)
        {
            var inside = remote.TrimEnd('\\', '/') + "\\" + name;
            if (await IsFolder(pc, remote, ct))
            {
                if (name == "") throw new Failure($"{remote} is a folder on {pc.Name}; give the full path of the file to write.");
                try { return await PutTo(inside); }   // <remote> is a folder: put it inside
                catch (Failure g) when (g.Status == HttpStatusCode.Conflict)
                {
                    throw new Failure(g.Message.Contains("folder", StringComparison.OrdinalIgnoreCase) ? g.Message : $"{g.Message} ({overwriteHint})");
                }
            }
            if (f.Message.Contains("folder", StringComparison.OrdinalIgnoreCase)) throw;
            throw new Failure($"{f.Message} ({overwriteHint})");
        }
    }

    static async Task<int> Put(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"), Interrupt.Token);
        var local = a.At(1, "the file to upload");
        var remote = a.At(2, "where to put it on the PC");
        if (!File.Exists(local))
            throw new Failure(Directory.Exists(local) ? $"{local} is a folder; put uploads single files (zip a folder first)." : $"Not found: {local}", Usage);
        var name = Path.GetFileName(local);
        if (remote.EndsWith('\\') || remote.EndsWith('/')) remote += name;
        var mtime = (new DateTimeOffset(File.GetLastWriteTimeUtc(local)).ToUnixTimeMilliseconds() / 1000.0).ToString(CultureInfo.InvariantCulture);
        var length = new FileInfo(local).Length;
        var done = await Upload(pc, remote, name, () => File.OpenRead(local), length, $"&mtime={mtime}", a.Flag("--overwrite"),
                                "add --overwrite to replace it", Interrupt.Token);
        var path = Str(done["path"]);
        return Print($"Uploaded {Path.GetFullPath(local)} to {(path != "" ? path : remote)} on {pc.Name} ({Size(length)})");
    }

    static async Task<int> Wait(Args a)
    {
        var pc = await Connect(a.At(0, "the PC"), Interrupt.Token);
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
            var (v, date) = await WaitCall(pc, $"/api/wait?{query}&timeout={chunk.ToString("0.#", CultureInfo.InvariantCulture)}", chunk, Interrupt.Token);
            var met = IsTrue(v["met"]);
            if (met || left <= 300)
            {
                if (a.Flag("--json")) PrintJson(v);
                else if (met && file is not null) Print($"{Str(v["file"])} ({Size(Long(v["size"]))}, modified {Str(v["mtime"])})");
                else if (met) Print($"{Str(v["process"])} {Str(v["state"])}" + (v["pids"] is JsonArray { Count: > 0 } pids ? $" (PID {string.Join(", ", pids.Select(Str))})" : ""));
                if (!met) Console.Error.WriteLine($"Timed out after {Math.Round((DateTime.UtcNow - start).TotalSeconds)} s.");
                return met ? 0 : 1;
            }
            // "now" must keep meaning when the wait began, by the PC's clock
            if (changedAfter == "now" && date is { } d)
                changedAfter = (d.ToUnixTimeSeconds() - Dbl(v["waited_s"])).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One /api/wait call (the server answers within `seconds`) and the PC's clock from its answer.</summary>
    static async Task<(JsonNode Answer, DateTimeOffset? Date)> WaitCall(Pc pc, string path, double seconds, CancellationToken ct)
    {
        using var dog = new Watchdog(seconds + 20, ct);
        try
        {
            using var r = await pc.Send(HttpMethod.Get, path, dog);
            var v = JsonNode.Parse(await r.Content.ReadAsStringAsync(dog.Token)) ?? new JsonObject();
            return (v, r.Headers.Date);
        }
        catch (Exception e) when (pc.Lost(e, dog) is { } f) { throw f; }
    }

    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
