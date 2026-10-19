// `sshmgr mcp`: an MCP server (Model Context Protocol, JSON-RPC 2.0 over stdin/stdout, one message per
// line) that gives AI agents the PCs the SSH Manager app is connected to as tools. Every call goes through
// the same app connections as the other commands and carries the agent's name, so each PC labels it
// "<agent> (<this computer>)" and applies its AI agent settings (off / read-only / full; actions that
// ask first wait for a person's approval).
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SshMgrCli;

static partial class Program
{
    static readonly string[] ProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>Text handed back in one tool result stays under this (about 8,000 tokens).</summary>
    const int MaxText = 30_000;
    const int InlineFileLimit = 100 * 1024, InlineImageLimit = 3_500_000;

    const string Instructions = """
        These tools work on Windows PCs that the SSH Manager app on this computer is connected to (including,
        usually, this computer itself). Start with list_pcs to see their names. Scripts are Windows PowerShell 5.1
        and run with administrator rights in the signed-in user's session on that PC, so be careful: prefer
        looking (pc_status, list_files, read_log, screenshot, download_file) before changing things, and use the
        PC's quick actions (list_actions / run_action) for routine jobs its owner set up. Each PC decides what
        agents may do there (off, read-only or full); a refused call says why, so don't try to work around it.
        Everything you do is recorded in that PC's activity log under your name.
        """;

    sealed class ToolError(string message) : Exception(message);

    sealed record Tool(string Name, string Title, string Description, JsonObject Schema, bool ReadOnly,
                       Func<JsonObject, ToolContext, Task<JsonArray>> Run, bool Destructive = false);

    sealed class ToolContext(JsonNode? progressToken, Func<JsonObject, Task> notify, CancellationToken ct)
    {
        public CancellationToken Cancel => ct;
        DateTime lastProgress = DateTime.MinValue;

        /// <summary>Progress for a client that asked for it (at most every 2 s).</summary>
        public async Task Progress(double done, string message)
        {
            if (progressToken is null || DateTime.UtcNow - lastProgress < TimeSpan.FromSeconds(2)) return;
            lastProgress = DateTime.UtcNow;
            await notify(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["method"] = "notifications/progress",
                ["params"] = new JsonObject { ["progressToken"] = progressToken.DeepClone(), ["progress"] = done, ["message"] = message },
            });
        }
    }

    // ---- the protocol ----

    static async Task<int> Mcp(Args a)
    {
        var fixedName = a.Value("--as") ?? Environment.GetEnvironmentVariable("SSHMGR_AGENT");
        Agent = AgentName(fixedName ?? "AI agent");
        var tools = Tools().ToDictionary(t => t.Name);
        var write = new SemaphoreSlim(1, 1);
        var running = new ConcurrentDictionary<string, CancellationTokenSource>();
        var inflight = new List<Task>();
        Console.InputEncoding = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        async Task Send(JsonObject message)
        {
            var line = message.ToJsonString();
            await write.WaitAsync();
            try
            {
                await Out.WriteLineAsync(line);
                await Out.FlushAsync();
            }
            finally { write.Release(); }
        }

        Task Reply(JsonNode? id, JsonNode result) =>
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

        Task Error(JsonNode? id, int code, string message) =>
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

        async Task Handle(JsonObject msg)
        {
            var method = msg["method"]?.GetValue<string>();
            var id = msg["id"];
            var p = msg["params"] as JsonObject ?? [];
            if (method is null) return;   // a response to something we never ask: ignore
            if (id is null)
            {
                if (method == "notifications/cancelled" && Str(p["requestId"]) is { Length: > 0 } rid && running.TryGetValue(rid, out var c)) c.Cancel();
                return;
            }
            try
            {
                switch (method)
                {
                    case "initialize":
                        var asked = Str(p["protocolVersion"]);
                        if (fixedName is null)
                            Agent = AgentName(ClientAgentName(Str(p["clientInfo"]?["name"])));
                        await Reply(id, new JsonObject
                        {
                            ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JsonObject
                            {
                                ["name"] = "sshmgr", ["title"] = "SSH Manager",
                                ["version"] = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "",
                            },
                            ["instructions"] = Instructions.Trim(),
                        });
                        break;
                    case "ping":
                        await Reply(id, new JsonObject());
                        break;
                    case "tools/list":
                        await Reply(id, new JsonObject { ["tools"] = new JsonArray(tools.Values.Select(DescribeTool).ToArray<JsonNode?>()) });
                        break;
                    case "tools/call":
                        var name = Str(p["name"]);
                        if (!tools.TryGetValue(name, out var tool)) { await Error(id, -32602, $"Unknown tool: {name}"); break; }
                        using (var cts = new CancellationTokenSource())
                        {
                            var key = id.ToJsonString().Trim('"');
                            running[key] = cts;
                            try
                            {
                                var ctx = new ToolContext(p["_meta"]?["progressToken"], Send, cts.Token);
                                var result = await CallTool(tool, p["arguments"] as JsonObject ?? [], ctx);
                                if (!cts.IsCancellationRequested) await Reply(id, result);   // a cancelled call gets no answer
                            }
                            finally { running.TryRemove(key, out _); }
                        }
                        break;
                    default:
                        await Error(id, -32601, $"Method not found: {method}");
                        break;
                }
            }
            catch (Exception e) { await Error(id, -32603, e.Message); }
        }

        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Trim() == "") continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { await Error(null, -32700, "Parse error"); continue; }
            var messages = node is JsonArray batch ? batch.OfType<JsonObject>().ToList() : node is JsonObject o ? [o] : [];
            foreach (var m in messages)
            {
                inflight.RemoveAll(t => t.IsCompleted);
                inflight.Add(Task.Run(() => Handle(m)));   // calls run side by side; a slow one doesn't hold up the rest
            }
        }
        foreach (var c in running.Values) c.Cancel();
        await Task.WhenAll(inflight.Select(t => t.ContinueWith(_ => { })));
        return 0;
    }

    /// <summary>The agent's name from the MCP client's name (claude-code → Claude, codex-mcp-client → Codex).</summary>
    static string ClientAgentName(string client)
    {
        var c = client.ToLowerInvariant();
        foreach (var (key, name) in new[] { ("claude", "Claude"), ("codex", "Codex"), ("cursor", "Cursor"), ("gemini", "Gemini"),
                                             ("copilot", "Copilot"), ("windsurf", "Windsurf"), ("cline", "Cline"), ("goose", "Goose") })
            if (c.Contains(key)) return name;
        return client.Trim() == "" ? "AI agent" : client.Trim();
    }

    static JsonObject DescribeTool(Tool t) => new()
    {
        ["name"] = t.Name, ["title"] = t.Title, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = t.Title, ["readOnlyHint"] = t.ReadOnly, ["destructiveHint"] = t.Destructive, ["openWorldHint"] = false,
        },
    };

    static async Task<JsonNode> CallTool(Tool tool, JsonObject args, ToolContext ctx)
    {
        JsonArray content;
        var error = false;
        try { content = await tool.Run(args, ctx); }
        catch (Exception e)
        {
            error = true;
            content = [TextBlock(e is OperationCanceledException ? "Cancelled." : e.Message)];
        }
        var result = new JsonObject { ["content"] = content };
        if (error) result["isError"] = true;
        return result;
    }

    static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    // ---- arguments ----

    static string Need(JsonObject a, string name) =>
        Str(a[name]).Trim() is { Length: > 0 } v ? v : throw new ToolError($"Missing \"{name}\".");

    static string? Opt(JsonObject a, string name) => Str(a[name]).Trim() is { Length: > 0 } v ? v : null;

    static double? Num(JsonObject a, string name)
    {
        var n = a[name];
        if (n is null || n.GetValueKind() == JsonValueKind.Null) return null;
        if (n.GetValueKind() == JsonValueKind.Number) return n.GetValue<double>();
        return double.TryParse(Str(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new ToolError($"\"{name}\" needs a number.");
    }

    static bool Bool(JsonObject a, string name) =>
        a[name] is { } n && (n.GetValueKind() == JsonValueKind.True || Str(n).Equals("true", StringComparison.OrdinalIgnoreCase));

    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
        {
            var s = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Type == "object") s["additionalProperties"] = new JsonObject { ["type"] = "string" };
            properties[p.Name] = s;
        }
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(p => p.Required).Select(p => (JsonNode?)p.Name).ToArray()),
        };
    }

    const string PcArg = "The PC: its name as list_pcs shows it (or its id).";

    // ---- the tools ----

    static IEnumerable<Tool> Tools()
    {
        yield return new("list_pcs", "List PCs",
            "Lists the PCs the SSH Manager app is connected to: name, id, state, version and what AI agents may do there " +
            "(full, read-only or off). Use a PC's name as `pc` in the other tools.",
            Schema(), true, async (_, _) => [TextBlock(await ListPcs())]);

        yield return new("pc_status", "PC status",
            "A PC's status: computer name, uptime, CPU, memory, disks, the SSH server, who is connected right now " +
            "(people and agents), failed logins in the last 24 h and its SSH Manager version.",
            Schema(("pc", "string", PcArg, true)), true, async (a, _) => [TextBlock(await PcStatus(await Connect(Need(a, "pc"))))]);

        yield return new("run", "Run PowerShell",
            "Runs a Windows PowerShell 5.1 script on the PC with administrator rights (no window, in the signed-in user's " +
            "session) and returns its output and exit code. Output is UTF-8; long output is shortened to its start and " +
            "end, with the path of the complete output on the PC. The run is stopped after `timeout` seconds " +
            "(default 600). With background=true it returns at once with a run id: check on it with run_status. " +
            "Use cwd/env instead of cd / $env: lines. Don't use it to restart SSH Manager itself.",
            Schema(("pc", "string", PcArg, true), ("script", "string", "The PowerShell script (several lines are fine).", true),
                   ("cwd", "string", "Working directory on the PC; environment variables work, e.g. %USERPROFILE%\\project.", false),
                   ("env", "object", "Extra environment variables for this run, e.g. {\"CONFIG\": \"Release\"}.", false),
                   ("timeout", "number", "Seconds after which the run is stopped (default 600, at most 86400).", false),
                   ("title", "string", "A short title for the PC's activity log (default: the first line).", false),
                   ("background", "boolean", "Start it and return right away with its run id (for long jobs).", false)),
            false, RunTool, Destructive: true);

        yield return new("run_status", "Run status",
            "Status, exit code and output of a run started by run or run_action (for example with background=true). " +
            "Output is given from line `from_line` (0 = the start; use the returned next_line to continue). " +
            "`wait` waits up to that many seconds for the run to finish first. stop=true stops it.",
            Schema(("pc", "string", PcArg, true), ("run_id", "string", "The run id.", true),
                   ("from_line", "number", "First output line to return (default 0).", false),
                   ("wait", "number", "Seconds to wait for the run to finish (default 0, at most 3600).", false),
                   ("stop", "boolean", "Stop the run.", false)),
            false, RunStatusTool);

        yield return new("list_actions", "List quick actions",
            "The PC's quick actions: ready-made jobs its owner set up (id, group, name, and whether it asks first). " +
            "Prefer them over writing your own script for the same job.",
            Schema(("pc", "string", PcArg, true)), true, async (a, _) => [TextBlock(await ListActions(await Connect(Need(a, "pc"))))]);

        yield return new("run_action", "Run a quick action",
            "Runs one of the PC's quick actions (by id or name) and returns its output and exit code. An action that " +
            "\"asks first\" waits until a person clicks Allow in the SSH Manager app (up to 2 minutes) and is not run " +
            "if they deny it or nobody answers.",
            Schema(("pc", "string", PcArg, true), ("action", "string", "The action's id or name (see list_actions).", true),
                   ("timeout", "number", "Seconds after which the run is stopped (default: the action's own limit, or 600).", false),
                   ("background", "boolean", "Start it and return right away with its run id.", false)),
            false, RunActionTool, Destructive: true);

        yield return new("screenshot", "Screenshot",
            "A screenshot of one app window or a whole monitor on the PC, as an image. Without `window` it shows monitor 1. " +
            "The PC may allow only some apps, or no whole screens. Scaled to 1568 px wide by default; width=0 keeps " +
            "the full size. save_to also saves it on this computer.",
            Schema(("pc", "string", PcArg, true),
                   ("window", "string", "An app window: program name (e.g. notepad, chrome), part of its title, or an id from list_windows.", false),
                   ("monitor", "number", "Monitor number (1 = main) when no window is given.", false),
                   ("width", "number", "Scale to this width in pixels (default 1568; 0 = full size).", false),
                   ("format", "string", "png (lossless, default) or jpeg.", false),
                   ("save_to", "string", "Also save the image to this path on this computer.", false)),
            true, ScreenshotTool);

        yield return new("list_windows", "List windows",
            "Open app windows on the PC that can be captured with screenshot (id, program, title).",
            Schema(("pc", "string", PcArg, true)), true, async (a, _) => [TextBlock(await ListWindows(await Connect(Need(a, "pc"))))]);

        yield return new("list_files", "List files",
            "Lists a folder on the PC (folders first, with sizes and modification times). Without `path` it lists the " +
            "drives and the PC's folder shortcuts.",
            Schema(("pc", "string", PcArg, true), ("path", "string", "An absolute folder path; environment variables work.", false)),
            true, ListFilesTool);

        yield return new("read_log", "Read a log",
            "The last lines of a log on the PC: one of its configured logs (by name; omit `log` to list them) or any text " +
            "file by path. A pattern like C:\\logs\\app-*.log reads the newest match, a folder its newest file. Only the " +
            "last 256 KB are searched; for more use download_file.",
            Schema(("pc", "string", PcArg, true), ("log", "string", "A configured log's name, or a file path or pattern.", false),
                   ("lines", "number", "How many lines from the end (default 100, at most 2000).", false),
                   ("contains", "string", "Only lines containing this text (not case-sensitive).", false)),
            true, ReadLogTool);

        yield return new("download_file", "Download a file",
            "Gets a file from the PC. A small text file (up to 100 KB) is returned as text and a small image as an image; " +
            "anything else is saved on this computer (to save_to, or a temporary folder) and its path returned. A folder " +
            "comes as a .zip.",
            Schema(("pc", "string", PcArg, true), ("path", "string", "Absolute path of the file or folder on the PC.", true),
                   ("save_to", "string", "Where to save it on this computer (a file path, or a folder ending in \\).", false)),
            true, DownloadTool);

        yield return new("upload_file", "Upload a file",
            "Puts a file on the PC: either a file from this computer (local_path) or the given text (content, saved as UTF-8). " +
            "A `path` ending in \\ is a folder to put it in. Won't replace an existing file unless overwrite=true.",
            Schema(("pc", "string", PcArg, true), ("path", "string", "Absolute destination path on the PC.", true),
                   ("local_path", "string", "A file on this computer to upload.", false),
                   ("content", "string", "Text to write instead of a local file.", false),
                   ("overwrite", "boolean", "Replace an existing file.", false)),
            false, UploadTool, Destructive: true);

        yield return new("wait_for", "Wait for a file or program",
            "Waits on the PC until a file exists (optionally: was changed after a time), or a program has started or exited. " +
            "Better than polling with run. Returns whether it happened before `timeout`.",
            Schema(("pc", "string", PcArg, true), ("file", "string", "A file path to wait for.", false),
                   ("changed_after", "string", "With file: only count a change after this time: \"now\", an ISO time or Unix seconds.", false),
                   ("process", "string", "A program name to wait for, e.g. setup or setup.exe.", false),
                   ("exited", "boolean", "With process: wait until no such program runs (default: until one runs).", false),
                   ("timeout", "number", "Seconds to wait (default 60, at most 3600).", false)),
            true, WaitTool);
    }

    // ---- tool implementations ----

    static async Task<string> ListPcs()
    {
        var r = await AskApp(new { cmd = "pcs" });
        var pcs = r["pcs"]!.AsArray().Select(p => p!).ToList();
        if (pcs.Count == 0) return "The SSH Manager app has no PCs yet.";
        var sb = new StringBuilder();
        foreach (var p in pcs)
        {
            var state = Str(p["state"]);
            var line = $"{Str(p["name"])}  (id {Str(p["id"])}{(p["local"]?.GetValue<bool>() == true ? ", this computer" : "")})  " +
                       (state == "Connected" ? $"connected, SSH Manager {Str(p["version"])}" : $"{state.ToLowerInvariant()}: {Str(p["message"]).Split('\n')[0]}");
            if (state == "Connected")
            {
                try
                {
                    var pc = await Connect(Str(p["id"]));
                    var ai = await pc.Get("/api/ai");
                    line += Str(ai["access"]) switch { "read" => ", AI access: read-only", "off" => ", AI access: off", _ => ", AI access: full" };
                }
                catch (Failure f) when (f.Status == HttpStatusCode.NotFound) { line += ", AI access: full (older version)"; }
                catch (Exception) { }
            }
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    static async Task<string> PcStatus(Pc pc)
    {
        var s = await pc.Get("/api/status");
        var h = s["host"]!;
        var ram = h["ram"]!;
        var sb = new StringBuilder();
        sb.AppendLine($"{Str(h["name"])} (computer name {Str(h["computer_name"])}), SSH Manager {Str(s["version"])}, AI access: {(Str(s["ai_access"]) is { Length: > 0 } acc ? acc : "full")}");
        sb.AppendLine($"Up {Duration(h["uptime_s"]?.GetValue<double>() ?? 0)}; CPU {h["cpu"]?.GetValue<double>():0}%; memory {Size(ram["used"]!.GetValue<long>())} of {Size(ram["total"]!.GetValue<long>())}");
        foreach (var d in h["disks"]!.AsArray())
            sb.AppendLine($"Disk {Str(d!["mount"])}: {Size(d["free"]!.GetValue<long>())} free of {Size(d["total"]!.GetValue<long>())}");
        var sshd = s["sshd"]!;
        sb.AppendLine($"SSH server: {Str(sshd["status"])}{(sshd["listening"]?.GetValue<bool>() == true ? ", listening on port 22" : ", not listening")}");
        var sessions = s["sessions"]!.AsArray();
        sb.AppendLine(sessions.Count == 0 ? "SSH sessions: none" : "SSH sessions:");
        foreach (var x in sessions)
        {
            var agents = x!["agents"] is JsonArray ag && ag.Count > 0 ? $" (AI agents: {string.Join(", ", ag.Select(Str))})" : "";
            var cmds = x["commands"]!.AsArray().Where(c => Str(c!["kind"]) != "dashboard").Select(c => Str(c!["summary"])).ToList();
            sb.AppendLine($"  {Str(x["client"])} as {Str(x["user"])} since {Str(x["start"])}{agents}{(cmds.Count > 0 ? ": " + string.Join("; ", cmds) : "")}");
        }
        if (s["agents"] is JsonArray active && active.Count > 0)
            sb.AppendLine("AI agents active in the last 2 minutes: " + string.Join(", ", active.Select(a => Str(a!["label"]))));
        var fails = s["failures_24h"]!.AsArray().Count;
        sb.AppendLine($"Logins in the last 24 h: {Str(s["logins_24h"])}; failed: {fails}");
        return sb.ToString().TrimEnd();
    }

    // runs

    static async Task<JsonArray> RunTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"));
        var script = Need(a, "script");
        var env = new JsonObject();
        if (a["env"] is JsonObject e)
            foreach (var (k, v) in e) env[k] = Str(v);
        var run = await pc.Json(HttpMethod.Post, "/api/run", new
        {
            script, title = Opt(a, "title") ?? "", cwd = Opt(a, "cwd") ?? "",
            env = env.ToDictionary(kv => kv.Key, kv => Str(kv.Value)),
        });
        return [TextBlock(await Finish(pc, run, a, 600, ctx))];
    }

    static async Task<JsonArray> RunActionTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"));
        var want = Need(a, "action");
        var actions = (await pc.Get("/api/actions"))["actions"]!.AsArray().Select(x => x!).ToList();
        var match = actions.Where(x => Str(x["id"]) == want).ToList();
        if (match.Count == 0) match = actions.Where(x => Str(x["name"]).Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count != 1)
            throw new ToolError(match.Count == 0
                ? $"{pc.Name} has no quick action \"{want}\". Its actions: {string.Join(", ", actions.Select(x => $"{Str(x["id"])} ({Str(x["name"])})"))}"
                : $"More than one action is called \"{want}\"; use its id (see list_actions).");
        var action = match[0];
        if (action["confirm"]?.GetValue<bool>() == true)
            await ctx.Progress(0, $"Waiting for a person to allow \"{Str(action["name"])}\" in SSH Manager...");
        var run = await pc.Json(HttpMethod.Post, $"/api/actions/{Q(Str(action["id"]))}/run");
        var own = action["timeout"]?.GetValue<double>() ?? 0;
        return [TextBlock(await Finish(pc, run, a, own > 0 ? own : 600, ctx))];
    }

    /// <summary>Waits for a run to end (or returns at once with background=true) and describes the result.</summary>
    static async Task<string> Finish(Pc pc, JsonNode run, JsonObject a, double defaultTimeout, ToolContext ctx)
    {
        var id = Str(run["id"]);
        if (Bool(a, "background"))
            return $"Started run {id} on {pc.Name} (\"{Str(run["title"])}\"). Check on it with run_status (pc \"{pc.Name}\", run_id \"{id}\").";
        var timeout = Math.Clamp(Num(a, "timeout") ?? defaultTimeout, 1, 86400);
        var (view, output) = await Collect(pc, id, 0, timeout, stopAtDeadline: true, ctx);
        return Describe(pc, view, output, from: 0);
    }

    static async Task<JsonArray> RunStatusTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"));
        var id = Need(a, "run_id");
        if (Bool(a, "stop")) await pc.Json(HttpMethod.Post, $"/api/runs/{Q(id)}/stop");
        var from = (long)Math.Max(0, Num(a, "from_line") ?? 0);
        var wait = Math.Clamp(Num(a, "wait") ?? 0, 0, 3600);
        var (view, output) = await Collect(pc, id, from, wait, stopAtDeadline: false, ctx);
        return [TextBlock(Describe(pc, view, output, from))];
    }

    /// <summary>Output kept while following a run: the first lines and a rolling window of the last ones.</summary>
    sealed class Output
    {
        public readonly List<string> Head = [];
        public readonly LinkedList<string> Tail = new();
        int headChars, tailChars;
        bool headDone;
        public long Total, Dropped;   // lines seen; lines left out between Head and Tail

        public void Add(string line)
        {
            Total++;
            if (!headDone && headChars + line.Length + 1 <= MaxText / 5) { Head.Add(line); headChars += line.Length + 1; return; }
            headDone = true;
            Tail.AddLast(line);
            tailChars += line.Length + 1;
            while (tailChars > MaxText * 4 / 5 && Tail.Count > 1)
            {
                tailChars -= Tail.First!.Value.Length + 1;
                Tail.RemoveFirst();
                Dropped++;
            }
        }

        /// <summary>Lines the server no longer had in memory: what follows doesn't continue the head.</summary>
        public void Skip(long lines)
        {
            Total += lines;
            Dropped += lines;
            HeadCut |= !headDone;
            headDone = true;
        }

        /// <summary>The first lines went by before they were read: take them from the complete output.</summary>
        public bool HeadCut;

        public async Task FillHead(Pc pc, string id)
        {
            using var r = await pc.Send(HttpMethod.Get, $"/api/runs/{Q(id)}/output", stream: true);
            using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(), new UTF8Encoding(false));
            var lines = new List<string>();
            var chars = 0;
            while (lines.Count < Total - Tail.Count && await reader.ReadLineAsync() is { } line && chars + line.Length + 1 <= MaxText / 5)
            {
                lines.Add(line);
                chars += line.Length + 1;
            }
            Head.Clear();
            Head.AddRange(lines);
            Dropped = Total - Head.Count - Tail.Count;
            HeadCut = false;
        }

        public string Text()
        {
            var sb = new StringBuilder();
            foreach (var l in Head) sb.Append(l).Append('\n');
            if (Dropped > 0) sb.Append(Head.Count > 0 ? "\n" : "").Append($"[... {Dropped} lines left out here ...]\n\n");
            foreach (var l in Tail) sb.Append(Clip(l)).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        static string Clip(string line) => line.Length > MaxText / 2 ? line[..(MaxText / 2)] + " [line shortened]" : line;
    }

    static async Task<(JsonNode View, Output Output)> Collect(Pc pc, string id, long from, double seconds, bool stopAtDeadline, ToolContext ctx)
    {
        var output = new Output();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var after = from;
        var stopped = false;
        while (true)
        {
            JsonNode v;
            try { v = await pc.Get($"/api/runs/{Q(id)}?after={after}"); }
            catch (Failure f) when (f.Status == HttpStatusCode.NotFound) { throw new ToolError($"{pc.Name} no longer has run {id} (it keeps the last 50 runs, until it restarts)."); }
            if (v["skipped"]?.GetValue<long>() is > 0 and var skipped) output.Skip(skipped);
            var lines = v["lines"]!.AsArray();
            foreach (var line in lines) output.Add(Str(line));
            after = v["next"]!.GetValue<long>();
            var end = Str(v["status"]) != "running" || (!stopAtDeadline && (ctx.Cancel.IsCancellationRequested || DateTime.UtcNow > deadline));
            if (end)
            {
                if (output.HeadCut && from == 0)
                {
                    try { await output.FillHead(pc, id); }
                    catch (Failure) { }   // no longer kept: the start stays left out
                }
                return (v, output);
            }
            if (ctx.Cancel.IsCancellationRequested || (!stopped && DateTime.UtcNow > deadline))
            {
                if (stopped) { await Task.Delay(500); continue; }
                stopped = true;
                await pc.Json(HttpMethod.Post, $"/api/runs/{Q(id)}/stop");
                output.Add(ctx.Cancel.IsCancellationRequested ? "[cancelled: the run was stopped]" : $"[timed out after {seconds:0} s: the run was stopped]");
                continue;
            }
            if (lines.Count > 0) await ctx.Progress(output.Total, Str(lines[^1]));
            await Task.Delay(lines.Count > 0 ? 200 : 500);
        }
    }

    static string Describe(Pc pc, JsonNode view, Output output, long from)
    {
        var status = Str(view["status"]);
        var id = Str(view["id"]);
        var sb = new StringBuilder();
        sb.Append(status switch
        {
            "running" => $"Run {id} on {pc.Name} is still running ({Duration(view["duration_s"]?.GetValue<double>() ?? 0)} so far).",
            "stopped" => $"Run {id} on {pc.Name} was stopped after {Duration(view["duration_s"]?.GetValue<double>() ?? 0)}.",
            _ => $"Exit code {Str(view["exit_code"])} ({Duration(view["duration_s"]?.GetValue<double>() ?? 0)}, run {id} on {pc.Name}).",
        });
        sb.Append('\n');
        var text = output.Text();
        sb.Append(text == "" ? (from > 0 ? "(no new output)" : "(no output)") : "Output" + (from > 0 ? $" from line {from}" : "") + ":\n" + text);
        if (output.Dropped > 0)
        {
            var file = Str(view["output_file"]);
            sb.Append($"\n\nThe output had {from + output.Total} lines; the middle was left out above. The complete output is on {pc.Name}" +
                      (file != "" ? $" at {file} (read it with read_log: log = that path, lines, contains; or download_file)." : $" (GET {Str(view["full_output"])})."));
        }
        if (status == "running") sb.Append($"\n\nnext_line: {Str(view["next"])} (run_status with from_line = that, and wait = seconds to wait for it to finish)");
        return sb.ToString();
    }

    // quick actions, windows

    static async Task<string> ListActions(Pc pc)
    {
        var actions = (await pc.Get("/api/actions"))["actions"]!.AsArray();
        if (actions.Count == 0) return $"{pc.Name} has no quick actions.";
        var sb = new StringBuilder($"Quick actions on {pc.Name}:\n");
        foreach (var x in actions)
        {
            var script = Str(x!["script"]).Trim().Split('\n')[0].Trim();
            sb.AppendLine($"- {Str(x["id"])}: {Str(x["name"])}{(Str(x["group"]) != "" ? $" [{Str(x["group"])}]" : "")}" +
                          (x["confirm"]?.GetValue<bool>() == true ? " (asks first: needs a person's approval)" : "") +
                          (script != "" ? $"\n    {(script.Length > 120 ? script[..117] + "..." : script)}" : ""));
        }
        return sb.ToString().TrimEnd();
    }

    static async Task<string> ListWindows(Pc pc)
    {
        var w = await pc.Get("/api/screen/windows");
        var windows = w["windows"]!.AsArray();
        var note = w["filtered"]?.GetValue<bool>() == true ? " (this PC allows only some apps)" : "";
        if (windows.Count == 0) return $"No app windows on {pc.Name} that can be captured{note}.";
        var sb = new StringBuilder($"Windows on {pc.Name}{note}:\n");
        foreach (var x in windows)
            sb.AppendLine($"- {Str(x!["id"])}  {Str(x["app"])}: {Str(x["title"])}{(x["minimized"]?.GetValue<bool>() == true ? " (minimized)" : "")}");
        return sb.ToString().TrimEnd();
    }

    static async Task<JsonArray> ScreenshotTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"));
        var width = (int)Math.Clamp(Num(a, "width") ?? 1568, 0, 7680);
        var jpeg = (Opt(a, "format") ?? "png").ToLowerInvariant() is "jpg" or "jpeg";
        var what = "";
        var target = "";
        if (Opt(a, "window") is { } want)
        {
            var w = PickWindow((await pc.Get("/api/screen/windows"))["windows"]!.AsArray(), want, pc.Name);
            target = $"window={Str(w["id"])}";
            what = $"{Str(w["app"])}: {Str(w["title"])}";
        }
        else
        {
            var m = (int)(Num(a, "monitor") ?? 1);
            target = $"monitor={m}";
            what = $"monitor {m}";
        }

        async Task<byte[]> Get(bool asJpeg)
        {
            using var r = await pc.Send(HttpMethod.Get, $"/api/screen?{target}&width={width}&format={(asJpeg ? "jpeg" : "png")}&quality=85");
            return await r.Content.ReadAsByteArrayAsync();
        }

        var bytes = await Get(jpeg);
        var note = "";
        if (Opt(a, "save_to") is { } save)
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(save));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            note = $" Saved to {path}.";
        }
        if (!jpeg && bytes.Length > InlineImageLimit)
        {
            bytes = await Get(true);   // too big to hand over as PNG
            jpeg = true;
            note += " Shown as JPEG because the PNG is large.";
        }
        var size = ImageSize(bytes);
        return
        [
            TextBlock($"Screenshot of {what} on {pc.Name}{(size is { } s ? $", {s.W}x{s.H} px" : "")}, {Size(bytes.Length)}.{note}"),
            new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = jpeg ? "image/jpeg" : "image/png" },
        ];
    }

    /// <summary>Width and height of a PNG or JPEG.</summary>
    static (int W, int H)? ImageSize(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P')
            return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
        for (var i = 2; i + 9 < b.Length && b[0] == 0xFF && b[1] == 0xD8;)
        {
            if (b[i] != 0xFF) return null;
            var marker = b[i + 1];
            var len = (b[i + 2] << 8) | b[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            i += 2 + len;
        }
        return null;
    }

    // files

    static async Task<JsonArray> ListFilesTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"));
        var sb = new StringBuilder();
        if (Opt(a, "path") is not { } path)
        {
            var roots = await pc.Get("/api/fs/roots");
            sb.AppendLine($"Drives on {pc.Name}:");
            foreach (var d in roots["drives"]!.AsArray())
                sb.AppendLine($"  {Str(d!["path"])}  {Size(d["free"]!.GetValue<long>())} free of {Size(d["total"]!.GetValue<long>())}");
            var bookmarks = roots["bookmarks"]!.AsArray();
            if (bookmarks.Count > 0)
            {
                sb.AppendLine("Folder shortcuts:");
                foreach (var b in bookmarks) sb.AppendLine($"  {Str(b!["name"])}: {Str(b["path"])}");
            }
            return [TextBlock(sb.ToString().TrimEnd())];
        }
        var list = await pc.Get("/api/fs/list?path=" + Q(path));
        var items = list["items"]!.AsArray();
        sb.AppendLine($"{Str(list["path"])} on {pc.Name}: {items.Count(i => i!["dir"]!.GetValue<bool>())} folders, {items.Count(i => !i!["dir"]!.GetValue<bool>())} files");
        var shown = 0;
        foreach (var i in items)
        {
            if (++shown > 500) { sb.AppendLine($"... and {items.Count - 500} more"); break; }
            var dir = i!["dir"]!.GetValue<bool>();
            var mtime = Str(i["mtime"]).Replace("T", " ").Replace("+00:00", "Z");
            sb.AppendLine(dir ? $"  {Str(i["name"])}\\  {mtime}" : $"  {Str(i["name"])}  {Size(i["size"]!.GetValue<long>())}  {mtime}");
        }
        return [TextBlock(sb.ToString().TrimEnd())];
    }

    static async Task<JsonArray> ReadLogTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"));
        var logs = (await pc.Get("/api/logs"))["logs"]!.AsArray().Select(l => l!).ToList();
        if (Opt(a, "log") is not { } want)
            return [TextBlock(logs.Count == 0
                ? $"{pc.Name} has no configured logs. Give a file path as `log` to read any text file."
                : $"Logs on {pc.Name}:\n" + string.Join("\n", logs.Select(l => $"- {Str(l["name"])}: {Str(l["pattern"])}" +
                    (Str(l["file"]) is { Length: > 0 } f ? $" (now {f}, {Size(l["size"]?.GetValue<long>() ?? 0)})" : " (no file yet)"))))];
        var named = logs.FirstOrDefault(l => Str(l["name"]).Equals(want, StringComparison.OrdinalIgnoreCase));
        var r = await pc.Get(named is not null ? $"/api/logs/read?index={Str(named["index"])}" : "/api/logs/read?path=" + Q(want));
        if (Str(r["error"]) is { Length: > 0 } err) throw new ToolError($"{(named is not null ? Str(named["name"]) : want)} on {pc.Name}: {err}");
        var count = (int)Math.Clamp(Num(a, "lines") ?? 100, 1, 2000);
        IEnumerable<string> lines = Str(r["text"]).Replace("\r\n", "\n").Split('\n');
        if (Opt(a, "contains") is { } filter) lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var all = lines.ToList();
        if (all.Count > 0 && all[^1] == "") all.RemoveAt(all.Count - 1);
        var picked = all.TakeLast(count).ToList();
        var text = string.Join("\n", picked);
        if (text.Length > MaxText) text = "[...]\n" + text[^MaxText..];
        var header = $"{Str(r["file"])} on {pc.Name} ({Size(r["size"]?.GetValue<long>() ?? 0)}): last {picked.Count} line(s)" +
                     (Opt(a, "contains") is { } c ? $" containing \"{c}\"" : "") + ":";
        return [TextBlock(header + "\n" + (text == "" ? "(nothing)" : text))];
    }

    static async Task<JsonArray> DownloadTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"));
        var remote = Need(a, "path");
        using var r = await pc.Send(HttpMethod.Get, "/api/fs/download?path=" + Q(remote), stream: true);
        var cd = r.Content.Headers.ContentDisposition;
        var name = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"') is { Length: > 0 } n ? n : Path.GetFileName(remote.TrimEnd('\\', '/'));
        var length = r.Content.Headers.ContentLength;
        var saveTo = Opt(a, "save_to");
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var image = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => null };

        // small enough to hand over directly
        if (length is { } len && (len <= InlineFileLimit || (image is not null && len <= InlineImageLimit)) && ext != ".zip")
        {
            var bytes = await r.Content.ReadAsByteArrayAsync();
            var saved = saveTo is not null ? await Save(bytes) : null;
            var where = saved is not null ? $" Saved to {saved}." : "";
            if (image is not null)
                return [TextBlock($"{remote} on {pc.Name} ({Size(bytes.Length)}).{where}"),
                        new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = image }];
            if (TextOf(bytes) is { } text)
                return [TextBlock($"{remote} on {pc.Name} ({Size(bytes.Length)}).{where}\n\n{text}")];
            saved ??= await Save(bytes);
            return [TextBlock($"{remote} on {pc.Name} isn't text ({Size(bytes.Length)}); saved to {saved}.")];
        }

        var dest = Target();
        var part = dest + ".part";
        long size;
        try
        {
            await using (var file = File.Create(part))
            {
                await (await r.Content.ReadAsStreamAsync(ctx.Cancel)).CopyToAsync(file, ctx.Cancel);
                size = file.Length;
            }
            File.Move(part, dest, overwrite: true);
        }
        catch (Exception)
        {
            File.Delete(part);
            throw;
        }
        return [TextBlock($"Saved {remote} from {pc.Name} to {dest} ({Size(size)}){(ext == ".zip" && !remote.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "; the folder came as a .zip" : "")}.")];

        string Target()
        {
            var folder = Path.Combine(Path.GetTempPath(), "sshmgr", Safe(pc.Name));
            var path = saveTo is null ? Path.Combine(folder, name)
                : Environment.ExpandEnvironmentVariables(saveTo) is var s && (Directory.Exists(s) || s.EndsWith('\\') || s.EndsWith('/')) ? Path.Combine(s, name) : Environment.ExpandEnvironmentVariables(saveTo);
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        async Task<string> Save(byte[] bytes)
        {
            var path = Target();
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
    }

    static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>The text of a file that is UTF-8 (or UTF-16 with a byte order mark), else null.</summary>
    static string? TextOf(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (Array.IndexOf(b, (byte)0) >= 0) return null;
        try { return new UTF8Encoding(false, true).GetString(b).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return null; }
    }

    static async Task<JsonArray> UploadTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"));
        var remote = Need(a, "path");
        var local = Opt(a, "local_path");
        var content = a["content"] is { } c && c.GetValueKind() == JsonValueKind.String ? c.GetValue<string>() : null;
        if ((local is null) == (content is null)) throw new ToolError("Give either local_path or content.");
        byte[]? data = null;
        string name;
        if (local is not null)
        {
            local = Path.GetFullPath(Environment.ExpandEnvironmentVariables(local));
            if (!File.Exists(local)) throw new ToolError(Directory.Exists(local) ? $"{local} is a folder; upload single files (zip a folder first)." : $"Not found on this computer: {local}");
            name = Path.GetFileName(local);
        }
        else
        {
            data = new UTF8Encoding(false).GetBytes(content!);
            name = Path.GetFileName(remote.TrimEnd('\\', '/'));
            if (remote.EndsWith('\\') || remote.EndsWith('/')) throw new ToolError("With content, `path` must name the file, not a folder.");
        }
        if (remote.EndsWith('\\') || remote.EndsWith('/')) remote += name;
        var query = Bool(a, "overwrite") ? "&overwrite=1" : "";
        if (local is not null)
            query += "&mtime=" + (new DateTimeOffset(File.GetLastWriteTimeUtc(local)).ToUnixTimeMilliseconds() / 1000.0).ToString(CultureInfo.InvariantCulture);

        async Task<JsonNode> Upload(string path)
        {
            HttpContent body;
            if (data is not null) body = new ByteArrayContent(data);
            else body = new StreamContent(File.OpenRead(local!));
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using (body)
            using (var r = await pc.Send(HttpMethod.Put, $"/api/fs/upload?path={Q(path)}{query}", body))
                return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        }

        JsonNode done;
        try { done = await Upload(remote); }
        catch (Failure f) when (f.Status == HttpStatusCode.Conflict && f.Message.Contains("A folder with that name exists"))
        {
            done = await Upload(remote.TrimEnd('\\', '/') + "\\" + name);
        }
        catch (Failure f) when (f.Status == HttpStatusCode.Conflict && f.Message.Contains("Already exists"))
        {
            throw new ToolError(f.Message + " (set overwrite=true to replace it)");
        }
        var size = data?.Length ?? new FileInfo(local!).Length;
        return [TextBlock($"Uploaded {(local ?? "the text")} to {(Str(done["path"]) is { Length: > 0 } p ? p : remote)} on {pc.Name} ({Size(size)}).")];
    }

    static async Task<JsonArray> WaitTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"));
        var file = Opt(a, "file");
        var process = Opt(a, "process");
        if ((file is null) == (process is null)) throw new ToolError("Give either file or process.");
        var timeout = Math.Clamp(Num(a, "timeout") ?? 60, 0, 3600);
        var changedAfter = Opt(a, "changed_after");
        var start = DateTime.UtcNow;
        while (true)
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            var left = Math.Max(0, timeout - (DateTime.UtcNow - start).TotalSeconds);
            var chunk = Math.Min(left, 30);   // short calls, so a cancelled wait ends soon
            var query = file is not null
                ? $"file={Q(file)}" + (changedAfter is not null ? $"&changed_after={Q(changedAfter)}" : "")
                : $"process={Q(process!)}&state={(Bool(a, "exited") ? "exited" : "started")}";
            using var r = await pc.Send(HttpMethod.Get, $"/api/wait?{query}&timeout={chunk.ToString("0.#", CultureInfo.InvariantCulture)}");
            var v = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
            var met = v["met"]?.GetValue<bool>() == true;
            var waited = Math.Round((DateTime.UtcNow - start).TotalSeconds);
            if (met && file is not null)
                return [TextBlock($"Yes after {waited} s: {Str(v["file"])} ({Size(v["size"]?.GetValue<long>() ?? 0)}, modified {Str(v["mtime"])}).")];
            if (met)
                return [TextBlock($"Yes after {waited} s: {Str(v["process"])} {Str(v["state"])}" +
                                  (v["pids"] is JsonArray { Count: > 0 } pids ? $" (PID {string.Join(", ", pids.Select(Str))})." : "."))];
            if (left <= 30)
                return [TextBlock($"Not yet: timed out after {waited} s waiting for " +
                                  (file is not null ? $"{file}{(changedAfter is not null ? " to change" : "")}." : $"{process} to {(Bool(a, "exited") ? "exit" : "start")}."))];
            // "now" must keep meaning when the wait began, by the PC's clock
            if (changedAfter == "now" && r.Headers.Date is { } date)
                changedAfter = (date.ToUnixTimeSeconds() - (v["waited_s"]?.GetValue<double>() ?? 0)).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    static string Duration(double s) => s switch
    {
        < 60 => $"{s:0.#} s",
        < 3600 => $"{(int)s / 60} min {(int)s % 60} s",
        < 86400 => $"{(int)s / 3600} h {(int)s % 3600 / 60} min",
        _ => $"{(int)s / 86400} d {(int)s % 86400 / 3600} h",
    };
}
