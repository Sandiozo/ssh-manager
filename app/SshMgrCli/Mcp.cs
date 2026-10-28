// `sshmgr mcp`: an MCP server (Model Context Protocol, JSON-RPC 2.0 over stdin/stdout, one message per
// line) that gives AI agents the PCs the SSH Manager app is connected to as tools. Every call goes through
// the same app connections as the other commands and always carries the agent's name, so each PC labels it
// "<agent> (<this computer>)" and applies its AI agent settings (off / read-only / full; actions that
// ask first wait for a person's approval).
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SshMgrCli;

static partial class Program
{
    /// <summary>Protocol versions this server speaks, newest first.</summary>
    static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>Text handed back in one tool result stays under this (about 8,000 tokens).</summary>
    const int MaxText = 30_000;
    const int InlineReadLimit = 128 * 1024, InlineImageLimit = 3_500_000;

    /// <summary>How long one tool call may wait (for a run, an approval, a file): MCP clients give up on calls
    /// after a minute or so. SSHMGR_MCP_MAX_WAIT changes it for clients that wait longer.</summary>
    static readonly double WaitLimit =
        double.TryParse(Environment.GetEnvironmentVariable("SSHMGR_MCP_MAX_WAIT"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0
            ? Math.Clamp(w, 1, 3600) : 40;

    static string Instructions() => $"""
        Tools for the Windows PCs that the SSH Manager app on this computer is connected to (usually including
        this computer). Start with list_pcs. Scripts (run, run_action) are Windows PowerShell 5.1 and run AS
        ADMINISTRATOR in the signed-in user's session on that PC, so look first (pc_status, list_files,
        read_file, read_log, screenshot) and prefer the PC's quick actions for routine jobs.
        Each PC sets what agents may do there: full (everything a person can do from its dashboard), read-only
        (only look: status, files, logs, screenshots, run output) or off (nothing). No agent can change
        settings, SSH keys or pairing, or answer approvals. A refused call says why; don't work around it.
        A call waits at most {WaitLimit:0} s: a longer run keeps going and you get its run id; follow it with
        run_status. Files are saved or read on this computer only in the working folder, the temp folder or
        SSHMGR_MCP_ROOT. Everything you do is recorded in that PC's activity log under your name.
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
            try
            {
                await notify(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["method"] = "notifications/progress",
                    ["params"] = new JsonObject { ["progressToken"] = progressToken.DeepClone(), ["progress"] = done, ["message"] = message },
                });
            }
            catch (Exception) { }
        }
    }

    /// <summary>Ends with the MCP server: background jobs (time limits of runs) stop then.</summary>
    static readonly CancellationTokenSource Shutdown = new();

    // ---- the protocol ----

    static async Task<int> Mcp(Args a)
    {
        // always an agent: the name from --as, SSHMGR_AGENT or (on initialize) the client
        var fixedName = a.Value("--as") ?? Environment.GetEnvironmentVariable("SSHMGR_AGENT");
        Agent = AgentName(fixedName) is { Length: > 0 } fixedClean ? fixedClean : "AI agent";
        var tools = Tools().ToDictionary(t => t.Name);
        var write = new SemaphoreSlim(1, 1);
        var running = new ConcurrentDictionary<string, CancellationTokenSource>();
        var inflight = new List<Task>();
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

        async Task Call(JsonNode id, JsonObject p, Tool tool, CancellationTokenSource cts, string key)
        {
            try
            {
                var ctx = new ToolContext((p["_meta"] as JsonObject)?["progressToken"], Send, cts.Token);
                var result = await CallTool(tool, p["arguments"] as JsonObject ?? [], ctx);
                if (!cts.IsCancellationRequested) await Reply(id, result);   // a cancelled call gets no answer
            }
            catch (Exception e)
            {
                try { if (!cts.IsCancellationRequested) await Error(id, -32603, OneLine(e)); } catch (Exception) { }
            }
            finally
            {
                running.TryRemove(key, out _);
                cts.Dispose();
            }
        }

        try
        {
            while (await input.ReadLineAsync() is { } line)
            {
                if (line.Trim() == "") continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { await Error(null, -32700, "Parse error"); continue; }
                if (node is JsonArray)
                {
                    await Error(null, -32600, "Batches aren't supported: send one message per line.");
                    continue;
                }
                if (node is not JsonObject msg) { await Error(null, -32600, "Invalid request: not an object."); continue; }
                var hasId = msg.TryGetPropertyValue("id", out var id);
                if (hasId && id?.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
                {
                    await Error(null, -32600, "Invalid request: the id must be a string or a number.");
                    continue;
                }
                if (!msg.TryGetPropertyValue("method", out var m) || m is null)
                {
                    if (msg.ContainsKey("result") || msg.ContainsKey("error")) continue;   // a response: we never ask, so ignore it
                    await Error(hasId ? id : null, -32600, "Invalid request: no method.");
                    continue;
                }
                if (m.GetValueKind() != JsonValueKind.String)
                {
                    await Error(hasId ? id : null, -32600, "Invalid request: the method must be a string.");
                    continue;
                }
                var method = m.GetValue<string>();
                var p = msg["params"] as JsonObject ?? [];

                if (!hasId)
                {
                    // notifications: cancel a call (here in the read loop, so it can't overtake the call it
                    // cancels); everything else (initialized, roots changed, ...) needs nothing
                    if (method == "notifications/cancelled" && p["requestId"] is { } rid && running.TryGetValue(rid.ToJsonString(), out var c))
                    {
                        try { c.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    continue;
                }

                try
                {
                    switch (method)
                    {
                        case "initialize":
                            var asked = Str(p["protocolVersion"]);
                            if (fixedName is null)
                                Agent = AgentName(ClientAgentName(Str((p["clientInfo"] as JsonObject)?["name"]))) is { Length: > 0 } n ? n : "AI agent";
                            await Reply(id, new JsonObject
                            {
                                ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
                                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                                ["serverInfo"] = new JsonObject
                                {
                                    ["name"] = "sshmgr", ["title"] = "SSH Manager",
                                    ["version"] = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "",
                                },
                                ["instructions"] = Instructions().Trim(),
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
                            // registered before it starts, so a cancellation that follows at once finds it
                            var key = id!.ToJsonString();
                            var cts = new CancellationTokenSource();
                            if (!running.TryAdd(key, cts))
                            {
                                cts.Dispose();
                                await Error(id, -32600, "A request with this id is still running.");
                                break;
                            }
                            inflight.RemoveAll(t => t.IsCompleted);
                            inflight.Add(Task.Run(() => Call(id, p, tool, cts, key)));   // calls run side by side
                            break;
                        default:
                            await Error(id, -32601, $"Method not found: {method}");
                            break;
                    }
                }
                catch (Exception e) when (e is not IOException) { await Error(id, -32603, OneLine(e)); }
            }
        }
        catch (IOException) { }   // the client went away
        finally
        {
            Shutdown.Cancel();
            foreach (var c in running.Values)
            {
                try { c.Cancel(); } catch (ObjectDisposedException) { }
            }
            foreach (var q in Approvals.Values)
            {
                try { q.Cts.Cancel(); } catch (ObjectDisposedException) { }   // withdraw requests nobody will follow
            }
            await Task.WhenAny(Task.WhenAll(inflight.Select(t => t.ContinueWith(_ => { }))), Task.Delay(10_000));
        }
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
            content = [TextBlock(e switch
            {
                OperationCanceledException => "Cancelled.",
                ToolError or Failure => e.Message,
                _ => OneLine(e),
            })];
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
        if (Str(n).Trim() == "") return null;
        return double.TryParse(Str(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new ToolError($"\"{name}\" needs a number.");
    }

    static bool Bool(JsonObject a, string name) =>
        a[name] is { } n && (n.GetValueKind() == JsonValueKind.True || Str(n).Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>How long this call waits for a result: `wait_seconds`, at most (and by default) the wait limit.</summary>
    static double WaitSeconds(JsonObject a) => Num(a, "wait_seconds") is > 0 and var s ? Math.Min(s, WaitLimit) : WaitLimit;

    /// <summary>A run's time limit: `timeout` in seconds; missing or 0 means the default.</summary>
    static double? Limit(JsonObject a) => Num(a, "timeout") is > 0 and var t ? Math.Clamp(t, 1, 86400) : null;

    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
        {
            var s = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Type == "object") s["additionalProperties"] = new JsonObject { ["type"] = "string" };
            if (p.Type == "array") s["items"] = new JsonObject { ["type"] = "string" };
            properties[p.Name] = s;
        }
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(props.Where(p => p.Required).Select(p => (JsonNode?)p.Name).ToArray()),
        };
    }

    const string PcArg = "The PC: its name as list_pcs shows it (or its id).";
    const string Full = " Needs full AI access on the PC.";

    // ---- the tools ----

    static IEnumerable<Tool> Tools()
    {
        var wait = $"{WaitLimit:0}";
        var local = "the working folder, the temp folder or a folder in SSHMGR_MCP_ROOT";

        yield return new("list_pcs", "List PCs",
            "Lists the PCs in the SSH Manager app: name, id, connection state, version and what AI agents may do on " +
            "each (full, read-only or off). Use a PC's name as `pc` in the other tools. Changes nothing.",
            Schema(), true, async (_, ctx) => [TextBlock(await ListPcs(ctx.Cancel))]);

        yield return new("pc_status", "PC status",
            "A PC's status: uptime, CPU, memory, disks, the SSH server, who is connected now (people and agents), " +
            "failed logins in 24 h, SSH Manager version and AI access. Without `pc`: every connected PC. Changes nothing.",
            Schema(("pc", "string", PcArg + " Omit for all connected PCs.", false)), true, PcStatusTool);

        yield return new("run", "Run PowerShell",
            "Runs a Windows PowerShell 5.1 script on the PC as administrator (hidden, in the signed-in user's session): " +
            $"it can change anything there. Returns the output and exit code; if it takes longer than wait_seconds (default {wait}), " +
            "it returns \"still running\" with the run id and next_line while the run goes on, until it ends or `timeout` " +
            "(default 600 s) stops it; follow it with run_status. Long output keeps its start and end and points to the " +
            "complete output on the PC. Use cwd/env instead of cd / $env: lines. Don't restart SSH Manager with it." + Full,
            Schema(("pc", "string", PcArg, true), ("script", "string", "The PowerShell script (several lines are fine).", true),
                   ("cwd", "string", "Working directory on the PC; environment variables work, e.g. %USERPROFILE%\\project.", false),
                   ("env", "object", "Extra environment variables for this run, e.g. {\"CONFIG\": \"Release\"}.", false),
                   ("timeout", "number", "Stop the run after this many seconds (default 600, at most 86400).", false),
                   ("wait_seconds", "number", $"Return after this many seconds even if the run goes on (default and most {wait}).", false),
                   ("title", "string", "A short title for the PC's activity log (default: the first line).", false),
                   ("background", "boolean", "Return right away with the run id.", false)),
            false, RunTool, Destructive: true);

        yield return new("run_status", "Run status",
            "Status, exit code and output of a run (from run, run_action or list_runs), or of a quick action waiting for " +
            "approval (its approval-... id). Pages through the output: from_line (0 = the start) and max_lines; use the " +
            $"returned next_line to continue. `wait` waits up to that many seconds (at most {wait}) for the run to end first. " +
            "stop=true stops the run (or withdraws the approval request). Also gives the path of the complete output on the PC.",
            Schema(("pc", "string", PcArg, true), ("run_id", "string", "The run id (or approval id).", true),
                   ("from_line", "number", "First output line to return, counted from 0 (default 0).", false),
                   ("max_lines", "number", "Most lines to return (default 1000; long output is also cut at about 30 KB).", false),
                   ("wait", "number", $"Seconds to wait for the run to end first (default 0, at most {wait}).", false),
                   ("stop", "boolean", "Stop the run.", false)),
            false, RunStatusTool);

        yield return new("list_runs", "List runs",
            "Runs on the PC since its server started (the last 50, plus any still running): id, status, exit code, title, " +
            "who started it. Use it to find a run whose id you lost. Changes nothing.",
            Schema(("pc", "string", PcArg, true), ("running_only", "boolean", "Only runs that are still running.", false)),
            true, ListRunsTool);

        yield return new("list_actions", "List quick actions",
            "The PC's quick actions: ready-made jobs its owner set up (id, group, name, whether it asks first). Prefer them " +
            "over writing your own script for the same job. Changes nothing.",
            Schema(("pc", "string", PcArg, true)), true, async (a, ctx) => [TextBlock(await ListActions(await Connect(Need(a, "pc"), ctx.Cancel), ctx.Cancel))]);

        yield return new("run_action", "Run a quick action",
            "Runs one of the PC's quick actions (by id or name) as administrator, like run, and returns its output and exit " +
            $"code, or \"still running\" with the run id after wait_seconds (default {wait}). An action that asks first runs only " +
            "after a person clicks Allow in SSH Manager (within 2 minutes); if that takes longer than wait_seconds you get an " +
            "approval id to check with run_status." + Full,
            Schema(("pc", "string", PcArg, true), ("action", "string", "The action's id or name (see list_actions).", true),
                   ("timeout", "number", "Stop the run after this many seconds (default: the action's own limit, or 600).", false),
                   ("wait_seconds", "number", $"Return after this many seconds even if it isn't done (default and most {wait}).", false),
                   ("background", "boolean", "Return right away with the run id (or approval id).", false)),
            false, RunActionTool, Destructive: true);

        yield return new("screenshot", "Screenshot",
            "A screenshot of an app window or a monitor on the PC, returned as an image (monitor 1 without `window`). " +
            "Scaled to 1568 px wide by default; width=0 keeps the full size. The PC may allow only some apps. save_to also " +
            $"saves it on this computer ({local}); an existing file is replaced only with overwrite=true.",
            Schema(("pc", "string", PcArg, true),
                   ("window", "string", "An app window: program name (e.g. notepad, chrome), part of its title, or an id from list_windows.", false),
                   ("monitor", "number", "Monitor number (1 = main) when no window is given.", false),
                   ("width", "number", "Scale to this width in pixels (default 1568; 0 = full size).", false),
                   ("format", "string", "png (lossless, default) or jpeg.", false),
                   ("save_to", "string", "Also save the image to this file on this computer.", false),
                   ("overwrite", "boolean", "Replace an existing file at save_to.", false)),
            false, ScreenshotTool);

        yield return new("list_windows", "List windows",
            "Open app windows on the PC that can be captured with screenshot (id, program, title). Changes nothing.",
            Schema(("pc", "string", PcArg, true)), true, async (a, ctx) => [TextBlock(await ListWindows(await Connect(Need(a, "pc"), ctx.Cancel), ctx.Cancel))]);

        yield return new("list_files", "List files",
            "Lists a folder on the PC (folders first, with sizes and modification times). Without `path`: the drives and " +
            "the PC's folder shortcuts. Changes nothing.",
            Schema(("pc", "string", PcArg, true), ("path", "string", "An absolute folder path; environment variables work.", false)),
            true, ListFilesTool);

        yield return new("read_file", "Read a text file",
            "Reads lines of a text file on the PC without copying it here: start_line (1 = the first; negative counts from " +
            "the end, e.g. -50 = the last 50 lines) and max_lines (default 200). Reads UTF-8, UTF-16 and the PC's ANSI " +
            "code page. Good for paging through big files and a run's complete output. Changes nothing.",
            Schema(("pc", "string", PcArg, true), ("path", "string", "Absolute path of the file on the PC.", true),
                   ("start_line", "number", "First line, from 1 (default 1); negative = that many lines from the end.", false),
                   ("max_lines", "number", "Most lines to return (default 200, at most 5000; also cut at about 30 KB).", false)),
            true, ReadFileTool);

        yield return new("read_log", "Read a log",
            "The last lines of a log on the PC: one of its configured logs (by name; omit `log` to list them) or any text " +
            "file by path. A pattern like C:\\logs\\app-*.log reads the newest match, a folder its newest file. Only the " +
            "last 256 KB are searched; use read_file for the rest. Changes nothing.",
            Schema(("pc", "string", PcArg, true), ("log", "string", "A configured log's name, or a file path or pattern.", false),
                   ("lines", "number", "How many lines from the end (default 100, at most 2000).", false),
                   ("contains", "string", "Only lines containing this text (not case-sensitive).", false)),
            true, ReadLogTool);

        yield return new("download_file", "Download a file",
            "Copies a file from the PC to this computer. Text up to 30 KB and images up to 3.5 MB come back directly; " +
            "anything else is saved (to save_to, else a temp folder) and its path returned, with the first and last lines " +
            $"of a text file. A folder comes as a .zip. save_to must be in {local}; an existing file is replaced only with " +
            "overwrite=true.",
            Schema(("pc", "string", PcArg, true), ("path", "string", "Absolute path of the file or folder on the PC.", true),
                   ("save_to", "string", "Where to save it on this computer (a file path, or a folder ending in \\).", false),
                   ("overwrite", "boolean", "Replace an existing file at save_to.", false)),
            false, DownloadTool);

        yield return new("upload_file", "Upload a file",
            "Writes a file on the PC: a file from this computer (local_path, in " + local + ") or the given text (content, " +
            "saved as UTF-8; .ps1/.psm1/.psd1 get a byte order mark so Windows PowerShell reads them right). A `path` ending " +
            "in \\ (or an existing folder) is the folder to put it in. Replaces an existing file only with overwrite=true." + Full,
            Schema(("pc", "string", PcArg, true), ("path", "string", "Absolute destination path on the PC.", true),
                   ("local_path", "string", "A file on this computer to upload.", false),
                   ("content", "string", "Text to write instead of a local file.", false),
                   ("overwrite", "boolean", "Replace an existing file.", false)),
            false, UploadTool, Destructive: true);

        yield return new("make_folder", "Make a folder",
            "Creates a folder on the PC (and any missing folders above it)." + Full,
            Schema(("pc", "string", PcArg, true), ("path", "string", "Absolute path of the new folder.", true)),
            false, MakeFolderTool);

        yield return new("rename", "Rename",
            "Renames a file or folder on the PC; it stays in the same folder (new_name is a name, not a path). Fails if " +
            "the new name exists." + Full,
            Schema(("pc", "string", PcArg, true), ("path", "string", "Absolute path of the file or folder.", true),
                   ("new_name", "string", "The new name.", true)),
            false, RenameTool);

        yield return new("delete", "Delete",
            "Moves files or folders on the PC to its Recycle Bin (they can be restored from there)." + Full,
            Schema(("pc", "string", PcArg, true), ("paths", "array", "Absolute paths of the files or folders.", false),
                   ("path", "string", "One absolute path (instead of paths).", false)),
            false, DeleteTool, Destructive: true);

        yield return new("wait_for", "Wait for a file or program",
            "Waits on the PC until a file exists (optionally: changed after a time), or a program has started or exited. " +
            $"Better than polling with run. One call waits at most {wait} s (a longer timeout is cut to that); call again to " +
            "keep waiting. Changes nothing.",
            Schema(("pc", "string", PcArg, true), ("file", "string", "A file path to wait for.", false),
                   ("changed_after", "string", "With file: only count a change after this time: \"now\", an ISO time or Unix seconds.", false),
                   ("process", "string", "A program name to wait for, e.g. setup or setup.exe.", false),
                   ("exited", "boolean", "With process: wait until no such program runs (default: until one runs).", false),
                   ("timeout", "number", $"Seconds to wait (default and most {wait}).", false)),
            true, WaitTool);
    }

    // ---- PCs ----

    static async Task<string> ListPcs(CancellationToken ct)
    {
        var r = await AskApp(new { cmd = "pcs" }, ct);
        var pcs = Arr(r["pcs"]).Select(p => p!).ToList();
        if (pcs.Count == 0) return "The SSH Manager app has no PCs yet.";
        // the connected PCs are asked side by side, so one slow PC doesn't hold up the list
        var lines = await Task.WhenAll(pcs.Select(async p =>
        {
            var state = Str(p["state"]);
            var line = $"{Str(p["name"])}  (id {Str(p["id"])}{(IsTrue(p["local"]) ? ", this computer" : "")})  " +
                       (state == "Connected" ? $"connected, SSH Manager {Str(p["version"])}" : $"{state.ToLowerInvariant()}: {Str(p["message"]).Split('\n')[0]}");
            if (state != "Connected") return line;
            try
            {
                var pc = await Connect(Str(p["id"]), ct);
                var ai = await pc.Get("/api/ai", ct, 10);
                return line + ", AI access: " + Access(Str(ai["access"]));
            }
            catch (Failure f) when (f.Status == HttpStatusCode.NotFound) { return line + ", AI access: full (older version)"; }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return line + ", AI access: unknown (it didn't answer)";
            }
        }));
        return string.Join("\n", lines);
    }

    static string Access(string level) => level switch
    {
        "read" => "read-only (agents may only look)",
        "off" => "off (agents can do nothing)",
        _ => "full (agents may run scripts and change files)",
    };

    static async Task<JsonArray> PcStatusTool(JsonObject a, ToolContext ctx)
    {
        if (Opt(a, "pc") is { } one) return [TextBlock(await PcStatus(await Connect(one, ctx.Cancel), ctx.Cancel))];
        var r = await AskApp(new { cmd = "pcs" }, ctx.Cancel);
        var pcs = Arr(r["pcs"]).Select(p => p!).ToList();
        var connected = pcs.Where(p => Str(p["state"]) == "Connected").ToList();
        if (connected.Count == 0) return [TextBlock("No PC is connected in SSH Manager right now. list_pcs shows why.")];
        var parts = await Task.WhenAll(connected.Select(async p =>
        {
            try { return await PcStatus(await Connect(Str(p["id"]), ctx.Cancel), ctx.Cancel); }
            catch (Exception e) when (e is not OperationCanceledException || !ctx.Cancel.IsCancellationRequested)
            {
                return $"{Str(p["name"])}: {(e is Failure or ToolError ? e.Message : OneLine(e))}";
            }
        }));
        var others = pcs.Count - connected.Count;
        return [TextBlock(string.Join("\n\n", parts) + (others > 0 ? $"\n\n({others} more PC(s) not connected; see list_pcs.)" : ""))];
    }

    static async Task<string> PcStatus(Pc pc, CancellationToken ct)
    {
        var s = await pc.Get("/api/status", ct);
        var h = s["host"] ?? new JsonObject();
        var ram = h["ram"] ?? new JsonObject();
        var sb = new StringBuilder();
        sb.AppendLine($"{Str(h["name"])} (computer name {Str(h["computer_name"])}), SSH Manager {Str(s["version"])}, AI access: {Access(Str(s["ai_access"]))}");
        sb.AppendLine($"Up {Duration(Dbl(h["uptime_s"]))}; CPU {Dbl(h["cpu"]):0}%; memory {Size(Long(ram["used"]))} of {Size(Long(ram["total"]))}");
        foreach (var d in Arr(h["disks"]))
            sb.AppendLine($"Disk {Str(d!["mount"])}: {Size(Long(d["free"]))} free of {Size(Long(d["total"]))}");
        var sshd = s["sshd"] ?? new JsonObject();
        sb.AppendLine($"SSH server: {Str(sshd["status"])}{(IsTrue(sshd["listening"]) ? ", listening on port 22" : ", not listening")}");
        var sessions = Arr(s["sessions"]);
        sb.AppendLine(sessions.Count == 0 ? "SSH sessions: none" : "SSH sessions:");
        foreach (var x in sessions)
        {
            var agents = x!["agents"] is JsonArray ag && ag.Count > 0 ? $" (AI agents: {string.Join(", ", ag.Select(Str))})" : "";
            var cmds = Arr(x["commands"]).Where(c => Str(c!["kind"]) != "dashboard").Select(c => Str(c!["summary"])).ToList();
            sb.AppendLine($"  {Str(x["client"])} as {Str(x["user"])} since {Str(x["start"])}{agents}{(cmds.Count > 0 ? ": " + string.Join("; ", cmds) : "")}");
        }
        if (s["agents"] is JsonArray active && active.Count > 0)
            sb.AppendLine("AI agents active in the last 2 minutes: " + string.Join(", ", active.Select(a => Str(a!["label"]))));
        sb.AppendLine($"Logins in the last 24 h: {Str(s["logins_24h"])}; failed: {Arr(s["failures_24h"]).Count}");
        return sb.ToString().TrimEnd();
    }

    // ---- runs ----

    /// <summary>Time limits this server applies to runs (run id → seconds), and the runs it stopped for them.</summary>
    static readonly ConcurrentDictionary<string, double> Limits = new();
    static readonly ConcurrentDictionary<string, bool> TimedOutRuns = new();

    /// <summary>Stops a run that is still going after `seconds` (while this MCP server keeps running).</summary>
    static void StopAfter(Pc pc, string id, double seconds)
    {
        Limits[id] = seconds;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), Shutdown.Token);
                var r = await pc.Json(HttpMethod.Post, $"/api/runs/{Q(id)}/stop", Shutdown.Token);
                if (IsTrue(r["stopped"])) TimedOutRuns[id] = true;
            }
            catch (Exception) { }
        });
    }

    static string LimitNote(string id) => Limits.TryGetValue(id, out var s) ? $"; it is stopped if it still runs {Duration(s)} after it started" : "";

    static async Task<JsonArray> RunTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var script = Need(a, "script");
        var env = new Dictionary<string, string>();
        if (a["env"] is JsonObject e)
            foreach (var (k, v) in e) env[k] = Str(v);
        var limit = Limit(a) ?? 600;
        JsonNode run;
        try
        {
            run = await pc.Json(HttpMethod.Post, "/api/run", ctx.Cancel,
                new { script, title = Opt(a, "title") ?? "", cwd = Opt(a, "cwd") ?? "", env, timeout = limit });
        }
        catch (Failure f) when (f.Lost)
        {
            throw new ToolError($"{f.Message} The script may have started on {pc.Name} anyway: check with list_runs before running it again.");
        }
        StopAfter(pc, Str(run["id"]), limit);
        return [TextBlock(await Finish(pc, run, Bool(a, "background") ? 0 : WaitSeconds(a), ctx))];
    }

    /// <summary>Quick actions that wait for a person's approval longer than one call waits.</summary>
    sealed class Pending
    {
        public required string Id, PcName, Action;
        public required Pc Pc;
        public required Task<JsonNode> Start;
        public required CancellationTokenSource Cts;
        public readonly DateTime Since = DateTime.UtcNow;
    }

    static readonly ConcurrentDictionary<string, Pending> Approvals = new();

    static async Task<JsonArray> RunActionTool(JsonObject a, ToolContext ctx)
    {
        var started = DateTime.UtcNow;
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var want = Need(a, "action");
        var actions = Arr((await pc.Get("/api/actions", ctx.Cancel))["actions"]).Select(x => x!).ToList();
        var match = actions.Where(x => Str(x["id"]) == want).ToList();
        if (match.Count == 0) match = actions.Where(x => Str(x["name"]).Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count != 1)
            throw new ToolError(match.Count == 0
                ? $"{pc.Name} has no quick action \"{want}\". Its actions: {string.Join(", ", actions.Select(x => $"{Str(x["id"])} ({Str(x["name"])})"))}"
                : $"More than one action is called \"{want}\"; use its id (see list_actions).");
        var action = match[0];
        var actionName = Str(action["name"]);
        var path = $"/api/actions/{Q(Str(action["id"]))}/run";
        var wait = Bool(a, "background") ? 0 : WaitSeconds(a);
        // the action's own limit is applied by the PC; one given here by this server
        var limit = Limit(a) ?? (Dbl(action["timeout"]) > 0 ? (double?)null : 600);

        JsonNode run;
        if (IsTrue(action["confirm"]))
        {
            await ctx.Progress(0, $"Waiting for a person to allow \"{actionName}\" in SSH Manager...");
            // the request stays open while it waits for the answer; closing it withdraws the request
            var cts = new CancellationTokenSource();
            var start = pc.Json(HttpMethod.Post, path, cts.Token, seconds: 300);
            await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(wait), ctx.Cancel)).ConfigureAwait(false);
            if (ctx.Cancel.IsCancellationRequested)
            {
                cts.Cancel();
                throw new OperationCanceledException();
            }
            if (!start.IsCompleted)
            {
                var q = new Pending { Id = "approval-" + Guid.NewGuid().ToString("N")[..8], Pc = pc, PcName = pc.Name, Action = actionName, Start = start, Cts = cts };
                Approvals[q.Id] = q;
                if (limit is { } l)
                    _ = start.ContinueWith(t => { if (t.IsCompletedSuccessfully) StopAfter(pc, Str(t.Result["id"]), l); }, TaskScheduler.Default);
                return [TextBlock($"Waiting for approval: \"{actionName}\" on {pc.Name} asks first, and nobody has clicked Allow in SSH Manager " +
                                  $"yet (request id {q.Id}; it expires 2 minutes after it was made). Check with run_status (pc \"{pc.Name}\", " +
                                  $"run_id \"{q.Id}\", wait {WaitLimit:0}); stop=true withdraws the request.")];
            }
            cts.Dispose();
            run = await start;
        }
        else
        {
            try { run = await pc.Json(HttpMethod.Post, path, ctx.Cancel); }
            catch (Failure f) when (f.Lost)
            {
                throw new ToolError($"{f.Message} The action may have started on {pc.Name} anyway: check with list_runs before running it again.");
            }
        }
        if (limit is { } lim) StopAfter(pc, Str(run["id"]), lim);
        var left = wait <= 0 ? 0 : Math.Max(1, wait - (DateTime.UtcNow - started).TotalSeconds);
        return [TextBlock(await Finish(pc, run, left, ctx))];
    }

    /// <summary>Follows a run for up to `wait` seconds (0: not at all) and describes where it is.</summary>
    static async Task<string> Finish(Pc pc, JsonNode run, double wait, ToolContext ctx)
    {
        var id = Str(run["id"]);
        if (wait <= 0)
            return $"Started run {id} on {pc.Name} (\"{Str(run["title"])}\"){LimitNote(id)}. Follow it with run_status (pc \"{pc.Name}\", run_id \"{id}\").";
        var (view, output) = await Collect(pc, id, wait, ctx);
        return Describe(pc, view, output);
    }

    static async Task<JsonArray> RunStatusTool(JsonObject a, ToolContext ctx)
    {
        var id = Need(a, "run_id");
        var prefix = "";
        Pc pc;
        if (id.StartsWith("approval-", StringComparison.OrdinalIgnoreCase))
        {
            if (!Approvals.TryGetValue(id, out var q))
                throw new ToolError($"No request {id}: approval requests are only known to the sshmgr mcp that made them, until it ends. list_runs shows the PC's runs.");
            if (Bool(a, "stop") && !q.Start.IsCompleted)
            {
                q.Cts.Cancel();
                Approvals.TryRemove(id, out _);
                return [TextBlock($"Withdrew the request: \"{q.Action}\" won't run on {q.PcName}.")];
            }
            var waitFor = Math.Clamp(Num(a, "wait") ?? 0, 0, WaitLimit);
            await Task.WhenAny(q.Start, Task.Delay(TimeSpan.FromSeconds(waitFor), ctx.Cancel)).ConfigureAwait(false);
            ctx.Cancel.ThrowIfCancellationRequested();
            if (!q.Start.IsCompleted)
                return [TextBlock($"Still waiting for a person to allow \"{q.Action}\" on {q.PcName} (asked {Duration((DateTime.UtcNow - q.Since).TotalSeconds)} ago; " +
                                  $"the request expires after 2 minutes). Check again with run_status (run_id \"{id}\", wait {WaitLimit:0}); stop=true withdraws it.")];
            if (!q.Start.IsCompletedSuccessfully)
            {
                Approvals.TryRemove(id, out _);
                var why = q.Start.Exception?.InnerException;
                throw new ToolError(why is Failure or ToolError ? why.Message : q.Start.IsCanceled ? "The request was withdrawn." : OneLine(why!));
            }
            pc = q.Pc;
            id = Str(q.Start.Result["id"]);
            prefix = $"\"{q.Action}\" was allowed and runs as run {id}.\n";
        }
        else pc = await Connect(Need(a, "pc"), ctx.Cancel);

        if (Bool(a, "stop"))
        {
            var s = await pc.Json(HttpMethod.Post, $"/api/runs/{Q(id)}/stop", ctx.Cancel);
            prefix += IsTrue(s["stopped"]) ? "Stopping it.\n" : "";
        }
        var asked = Num(a, "wait") ?? 0;
        var wait = Math.Clamp(asked, 0, WaitLimit);
        var deadline = DateTime.UtcNow.AddSeconds(wait);
        JsonNode view;
        while (true)
        {
            view = await Poll(pc, id, 1_000_000_000_000, ctx.Cancel);   // just the status, no lines
            if (Str(view["status"]) != "running" || DateTime.UtcNow >= deadline) break;
            await Nap(500, ctx.Cancel);
            ctx.Cancel.ThrowIfCancellationRequested();
        }
        var from = (long)Math.Max(0, Num(a, "from_line") ?? 0);
        var maxLines = (int)Math.Clamp(Num(a, "max_lines") is > 0 and var ml ? ml : 1000, 1, 10_000);
        var (lines, total) = await Page(pc, id, from, maxLines, ctx.Cancel);
        var status = Str(view["status"]);
        var next = from + lines.Count;
        var sb = new StringBuilder(prefix);
        sb.Append(StatusLine(pc, view)).Append('\n');
        if (asked > WaitLimit) sb.Append($"(Waited {WaitLimit:0} s, the most one call waits.)\n");
        if (lines.Count == 0) sb.Append(from > 0 ? $"(no output from line {from}; {total} lines so far)" : "(no output)");
        else sb.Append($"Output lines {from}-{next - 1} (of {total}{(status == "running" ? " so far" : "")}):\n").Append(string.Join("\n", lines));
        if (next < total) sb.Append($"\n\nMore output: run_status with from_line {next}.");
        if (status == "running")
            sb.Append($"\n\nnext_line: {next} (run_status with from_line = that and wait = seconds to wait for the run to end; stop=true stops it)");
        if (Str(view["output_file"]) is { Length: > 0 } file)
            sb.Append($"\nThe complete output is on {pc.Name} at {file} (read_file pages through it).");
        return [TextBlock(sb.ToString())];
    }

    /// <summary>One look at a run (its lines after `after`), with errors an agent can act on.</summary>
    static async Task<JsonNode> Poll(Pc pc, string id, long after, CancellationToken ct)
    {
        try { return await pc.Get($"/api/runs/{Q(id)}?after={after}", ct); }
        catch (Failure f) when (f.Status == HttpStatusCode.NotFound)
        {
            throw new ToolError($"{pc.Name} no longer has run {id} (it keeps the last 50 runs, until its server restarts). list_runs shows the ones it has.");
        }
        catch (Failure f) when (f.Code == Unreachable)
        {
            throw new ToolError($"Connection lost: {f.Message} Run {id} may still be running on {pc.Name}; check later with run_status or list_runs.");
        }
    }

    /// <summary>Output lines from `from` on (at most `max` and about MaxText characters) and the total so far.</summary>
    static async Task<(List<string> Lines, long Total)> Page(Pc pc, string id, long from, int max, CancellationToken ct)
    {
        var v = await Poll(pc, id, from, ct);
        var total = Long(v["next"]);
        var lines = new List<string>();
        var chars = 0;
        bool Take(string line)
        {
            if (lines.Count >= max) return false;
            var l = Output.Clip(line);
            if (lines.Count > 0 && chars + l.Length + 1 > MaxText) return false;
            lines.Add(l);
            chars += l.Length + 1;
            return true;
        }
        if (Long(v["skipped"]) > 0)
        {
            // lines the PC no longer keeps in memory: read them from the complete output
            using var dog = new Watchdog(60, ct);
            try
            {
                using var r = await pc.Send(HttpMethod.Get, $"/api/runs/{Q(id)}/output", dog, stream: true);
                using var reader = new StreamReader(new KickStream(await r.Content.ReadAsStreamAsync(dog.Token), dog), Utf8);
                long n = 0;
                while (n < total && await reader.ReadLineAsync(dog.Token) is { } line)
                    if (n++ >= from && !Take(line)) break;
            }
            catch (Exception e) when (pc.Lost(e, dog) is { } f) { throw new ToolError(f.Message); }
        }
        else
            foreach (var line in Arr(v["lines"]))
                if (!Take(Str(line))) break;
        return (lines, total);
    }

    static async Task<JsonArray> ListRunsTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var runs = Arr((await pc.Get("/api/runs", ctx.Cancel))["runs"]).Select(r => r!).ToList();
        if (Bool(a, "running_only")) runs = runs.Where(r => Str(r["status"]) == "running").ToList();
        if (runs.Count == 0) return [TextBlock(Bool(a, "running_only") ? $"Nothing is running on {pc.Name}." : $"No runs on {pc.Name} since its server started.")];
        var sb = new StringBuilder($"Runs on {pc.Name}, newest first:\n");
        foreach (var r in runs)
        {
            var status = Str(r["status"]);
            sb.AppendLine($"- {Str(r["id"])}: {status}{(status == "running" || r["exit_code"] is not JsonValue ? "" : $", exit code {ExitText(r["exit_code"])}")}, " +
                          $"{Duration(Dbl(r["duration_s"]))}, {Long(r["next"])} lines. \"{Str(r["title"])}\" started " +
                          $"{Str(r["started"]).Replace("T", " ").Replace("+00:00", "Z")} by {Str(r["client"])}" +
                          (Str(r["action_id"]) is { Length: > 0 } act ? $" (quick action {act})" : ""));
        }
        return [TextBlock(sb.ToString().TrimEnd())];
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

        public async Task FillHead(Pc pc, string id, CancellationToken ct)
        {
            using var dog = new Watchdog(30, ct);
            using var r = await pc.Send(HttpMethod.Get, $"/api/runs/{Q(id)}/output", dog, stream: true);
            using var reader = new StreamReader(new KickStream(await r.Content.ReadAsStreamAsync(dog.Token), dog), Utf8);
            var lines = new List<string>();
            var chars = 0;
            while (lines.Count < Total - Tail.Count && await reader.ReadLineAsync(dog.Token) is { } line && chars + line.Length + 1 <= MaxText / 5)
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

        public static string Clip(string line) => line.Length > MaxText / 2 ? line[..(MaxText / 2)] + " [line shortened]" : line;
    }

    /// <summary>Follows a run from its start for up to `seconds`. Cancelling the call stops the run.</summary>
    static async Task<(JsonNode View, Output Output)> Collect(Pc pc, string id, double seconds, ToolContext ctx)
    {
        var output = new Output();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        long after = 0;
        while (true)
        {
            if (ctx.Cancel.IsCancellationRequested)
            {
                // whoever cancelled the call doesn't want the run either
                try { await pc.Json(HttpMethod.Post, $"/api/runs/{Q(id)}/stop", CancellationToken.None, seconds: 10); } catch (Exception) { }
                throw new OperationCanceledException();
            }
            JsonNode v;
            try { v = await Poll(pc, id, after, ctx.Cancel); }
            catch (OperationCanceledException) { continue; }
            if (Long(v["skipped"]) is > 0 and var skipped) output.Skip(skipped);
            var lines = Arr(v["lines"]);
            foreach (var line in lines) output.Add(Str(line));
            after = Long(v["next"]);
            if (Str(v["status"]) != "running" || DateTime.UtcNow >= deadline)
            {
                if (output.HeadCut)
                {
                    try { await output.FillHead(pc, id, ctx.Cancel); }
                    catch (Exception e) when (e is not OperationCanceledException) { }   // no longer kept: the start stays left out
                }
                return (v, output);
            }
            if (lines.Count > 0) await ctx.Progress(output.Total, Str(lines[^1]));
            await Nap(lines.Count > 0 ? 200 : 500, ctx.Cancel);
        }
    }

    static string StatusLine(Pc pc, JsonNode view)
    {
        var id = Str(view["id"]);
        var took = Duration(Dbl(view["duration_s"]));
        return Str(view["status"]) switch
        {
            "running" => $"Run {id} on {pc.Name} is still running ({took} so far{LimitNote(id)}).",
            "stopped" => $"Run {id} on {pc.Name} was stopped after {took}" +
                         (TimedOutRuns.ContainsKey(id) && Limits.TryGetValue(id, out var s) ? $", at its time limit of {Duration(s)}." : "."),
            _ => $"Exit code {ExitText(view["exit_code"])} ({took}, run {id} on {pc.Name}).",
        };
    }

    static string Describe(Pc pc, JsonNode view, Output output)
    {
        var status = Str(view["status"]);
        var id = Str(view["id"]);
        var sb = new StringBuilder(StatusLine(pc, view)).Append('\n');
        var text = output.Text();
        sb.Append(text == "" ? (status == "running" ? "(no output yet)" : "(no output)") : (status == "running" ? "Output so far:\n" : "Output:\n") + text);
        if (output.Dropped > 0)
        {
            var file = Str(view["output_file"]);
            sb.Append($"\n\nThe output had {output.Total} lines; the middle was left out above. The complete output is on {pc.Name}" +
                      (file != "" ? $" at {file} (read_file pages through it; run_status with from_line too)." : "."));
        }
        if (status == "running")
            sb.Append($"\n\nnext_line: {Long(view["next"])}. The run keeps going: call run_status (pc \"{pc.Name}\", run_id \"{id}\", " +
                      $"from_line {Long(view["next"])}, wait {WaitLimit:0}) for the rest; stop=true stops it.");
        return sb.ToString();
    }

    // ---- quick actions, windows ----

    static async Task<string> ListActions(Pc pc, CancellationToken ct)
    {
        var actions = Arr((await pc.Get("/api/actions", ct))["actions"]);
        if (actions.Count == 0) return $"{pc.Name} has no quick actions.";
        var sb = new StringBuilder($"Quick actions on {pc.Name}:\n");
        foreach (var x in actions)
        {
            var script = Str(x!["script"]).Trim().Split('\n')[0].Trim();
            sb.AppendLine($"- {Str(x["id"])}: {Str(x["name"])}{(Str(x["group"]) != "" ? $" [{Str(x["group"])}]" : "")}" +
                          (IsTrue(x["confirm"]) ? " (asks first: needs a person's approval)" : "") +
                          (script != "" ? $"\n    {(script.Length > 120 ? script[..117] + "..." : script)}" : ""));
        }
        return sb.ToString().TrimEnd();
    }

    static async Task<string> ListWindows(Pc pc, CancellationToken ct)
    {
        var w = await pc.Get("/api/screen/windows", ct);
        var windows = Arr(w["windows"]);
        var note = IsTrue(w["filtered"]) ? " (this PC allows only some apps)" : "";
        if (windows.Count == 0) return $"No app windows on {pc.Name} that can be captured{note}.";
        var sb = new StringBuilder($"Windows on {pc.Name}{note}:\n");
        foreach (var x in windows)
            sb.AppendLine($"- {Str(x!["id"])}  {Str(x["app"])}: {Str(x["title"])}{(IsTrue(x["minimized"]) ? " (minimized)" : "")}");
        return sb.ToString().TrimEnd();
    }

    static async Task<JsonArray> ScreenshotTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var width = (int)Math.Clamp(Num(a, "width") ?? 1568, 0, 7680);
        var jpeg = (Opt(a, "format") ?? "png").ToLowerInvariant() is "jpg" or "jpeg";
        // check where it would be saved before taking it
        var save = Opt(a, "save_to") is { } s ? LocalTarget(s, $"screenshot.{(jpeg ? "jpg" : "png")}", Bool(a, "overwrite")) : null;
        string what, target;
        if (Opt(a, "window") is { } want)
        {
            var w = PickWindow(Arr((await pc.Get("/api/screen/windows", ctx.Cancel))["windows"]), want, pc.Name);
            target = $"window={Q(Str(w["id"]))}";
            what = $"{Str(w["app"])}: {Str(w["title"])}";
        }
        else
        {
            var m = (int)(Num(a, "monitor") ?? 1);
            target = $"monitor={m}";
            what = $"monitor {m}";
        }

        async Task<byte[]> Take(bool asJpeg)
        {
            using var dog = new Watchdog(60, ctx.Cancel);
            try
            {
                using var r = await pc.Send(HttpMethod.Get, $"/api/screen?{target}&width={width}&format={(asJpeg ? "jpeg" : "png")}&quality=85", dog, stream: true);
                using var ms = new MemoryStream();
                await CopyAsync(await r.Content.ReadAsStreamAsync(dog.Token), ms, dog);
                return ms.ToArray();
            }
            catch (Exception e) when (pc.Lost(e, dog) is { } f) { throw f; }
        }

        var bytes = await Take(jpeg);
        var note = "";
        if (save is not null)
        {
            await WriteLocal(save, bytes, ctx.Cancel);
            note = $" Saved to {save}.";
        }
        if (!jpeg && bytes.Length > InlineImageLimit)
        {
            bytes = await Take(true);   // too big to hand over as PNG
            jpeg = true;
            note += " Shown as JPEG because the PNG is large.";
        }
        var size = ImageSize(bytes);
        return
        [
            TextBlock($"Screenshot of {what} on {pc.Name}{(size is { } sz ? $", {sz.W}x{sz.H} px" : "")}, {Size(bytes.Length)}.{note}"),
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

    // ---- this computer's files: only in the working folder, temp, or SSHMGR_MCP_ROOT ----

    static List<string> LocalRoots()
    {
        var roots = new List<string> { Environment.CurrentDirectory, Path.GetTempPath() };
        if (Environment.GetEnvironmentVariable("SSHMGR_MCP_ROOT") is { Length: > 0 } extra)
            roots.AddRange(extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var full = new List<string>();
        foreach (var r in roots)
        {
            try { full.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(r)))); }
            catch (Exception) { }
        }
        return full;
    }

    static bool Inside(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>A path on this computer an agent may read or write: inside an allowed folder, with no junction
    /// or symbolic link on the way that could lead out of it.</summary>
    static string LocalPath(string given, string what)
    {
        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(given.Trim().Trim('"'))); }
        catch (Exception e) { throw new ToolError($"{what}: \"{given}\" isn't a usable path ({e.Message})."); }
        var root = LocalRoots().FirstOrDefault(r => Inside(full, r))
                   ?? throw new ToolError($"{what} must be inside the working folder ({Environment.CurrentDirectory}), the temp folder " +
                                          $"({Path.TrimEndingDirectorySeparator(Path.GetTempPath())}) or a folder listed in SSHMGR_MCP_ROOT; {full} isn't.");
        for (var p = full; p.Length > root.Length; p = Path.GetDirectoryName(p) ?? root)
        {
            if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
                throw new ToolError($"{what}: {p} is a link or junction; not following it.");
        }
        return full;
    }

    /// <summary>Where to save a file named `name`: save_to (a file, or a folder ending in \ or existing), checked
    /// and not over an existing file unless overwrite is set.</summary>
    static string LocalTarget(string saveTo, string name, bool overwrite)
    {
        var s = Environment.ExpandEnvironmentVariables(saveTo.Trim().Trim('"'));
        var folder = s.EndsWith('\\') || s.EndsWith('/') || Directory.Exists(s);
        var path = LocalPath(folder ? Path.Combine(s, name) : s, "save_to");
        if (Directory.Exists(path)) throw new ToolError($"{path} is a folder on this computer; give a file name or a folder ending in \\.");
        if (File.Exists(path) && !overwrite) throw new ToolError($"{path} already exists on this computer; set overwrite=true to replace it, or choose another save_to.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>A new file in this computer's temp folder for downloads from the PC.</summary>
    static string TempTarget(Pc pc, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sshmgr", SafeFileName(pc.Name, "pc"));
        Directory.CreateDirectory(dir);
        return Unique(Path.Combine(dir, name));
    }

    // ---- files on the PC ----

    static async Task<JsonArray> ListFilesTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var sb = new StringBuilder();
        if (Opt(a, "path") is not { } path)
        {
            var roots = await pc.Get("/api/fs/roots", ctx.Cancel);
            sb.AppendLine($"Drives on {pc.Name}:");
            foreach (var d in Arr(roots["drives"]))
                sb.AppendLine($"  {Str(d!["path"])}  {Size(Long(d["free"]))} free of {Size(Long(d["total"]))}");
            var bookmarks = Arr(roots["bookmarks"]);
            if (bookmarks.Count > 0)
            {
                sb.AppendLine("Folder shortcuts:");
                foreach (var b in bookmarks) sb.AppendLine($"  {Str(b!["name"])}: {Str(b["path"])}");
            }
            return [TextBlock(sb.ToString().TrimEnd())];
        }
        var list = await pc.Get("/api/fs/list?path=" + Q(path), ctx.Cancel, 60);
        var items = Arr(list["items"]);
        sb.AppendLine($"{Str(list["path"])} on {pc.Name}: {items.Count(i => IsTrue(i!["dir"]))} folders, {items.Count(i => !IsTrue(i!["dir"]))} files");
        var shown = 0;
        foreach (var i in items)
        {
            if (++shown > 500) { sb.AppendLine($"... and {items.Count - 500} more"); break; }
            var dir = IsTrue(i!["dir"]);
            var mtime = Str(i["mtime"]).Replace("T", " ").Replace("+00:00", "Z");
            sb.AppendLine(dir ? $"  {Str(i["name"])}\\  {mtime}" : $"  {Str(i["name"])}  {Size(Long(i["size"]))}  {mtime}");
        }
        return [TextBlock(sb.ToString().TrimEnd())];
    }

    static async Task<JsonArray> ReadLogTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var logs = Arr((await pc.Get("/api/logs", ctx.Cancel))["logs"]).Select(l => l!).ToList();
        if (Opt(a, "log") is not { } want)
            return [TextBlock(logs.Count == 0
                ? $"{pc.Name} has no configured logs. Give a file path as `log` to read any text file."
                : $"Logs on {pc.Name}:\n" + string.Join("\n", logs.Select(l => $"- {Str(l["name"])}: {Str(l["pattern"])}" +
                    (Str(l["file"]) is { Length: > 0 } f ? $" (now {f}, {Size(Long(l["size"]))})" : " (no file yet)"))))];
        var named = logs.FirstOrDefault(l => Str(l["name"]).Equals(want, StringComparison.OrdinalIgnoreCase));
        var r = await pc.Get(named is not null ? $"/api/logs/read?index={Q(Str(named["index"]))}" : "/api/logs/read?path=" + Q(want), ctx.Cancel, 60);
        if (Str(r["error"]) is { Length: > 0 } err) throw new ToolError($"{(named is not null ? Str(named["name"]) : want)} on {pc.Name}: {err}");
        var count = (int)Math.Clamp(Num(a, "lines") is > 0 and var n ? n : 100, 1, 2000);
        IEnumerable<string> lines = Str(r["text"]).Replace("\r\n", "\n").Split('\n');
        if (Opt(a, "contains") is { } filter) lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var all = lines.ToList();
        if (all.Count > 0 && all[^1] == "") all.RemoveAt(all.Count - 1);
        var picked = all.TakeLast(count).ToList();
        var text = string.Join("\n", picked);
        if (text.Length > MaxText) text = "[...]\n" + text[^MaxText..];
        var header = $"{Str(r["file"])} on {pc.Name} ({Size(Long(r["size"]))}): last {picked.Count} line(s)" +
                     (Opt(a, "contains") is { } c ? $" containing \"{c}\"" : "") + ":";
        return [TextBlock(header + "\n" + (text == "" ? "(nothing)" : text))];
    }

    static async Task<JsonArray> ReadFileTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var remote = Need(a, "path");
        var start = (long)(Num(a, "start_line") ?? 1);
        if (start == 0) start = 1;
        var max = (int)Math.Clamp(Num(a, "max_lines") is > 0 and var m ? m : 200, 1, 5000);
        using var dog = new Watchdog(60, ctx.Cancel);
        try
        {
            using var r = await pc.Send(HttpMethod.Get, "/api/fs/download?path=" + Q(remote), dog, stream: true);
            if (DownloadName(r, remote).EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !remote.TrimEnd('\\', '/').EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ToolError($"{remote} on {pc.Name} is a folder; list_files shows what is in it.");
            var length = r.Content.Headers.ContentLength;
            var body = await r.Content.ReadAsStreamAsync(dog.Token);
            // the start of the file tells how it is encoded
            var head = new byte[65536];
            int got = 0, n;
            while (got < head.Length && (n = await body.ReadAsync(head.AsMemory(got), dog.Token)) > 0)
            {
                got += n;
                dog.Kick();
            }
            var enc = TextEncoding(head.AsSpan(0, got), complete: got < head.Length)
                      ?? throw new ToolError($"{remote} on {pc.Name} isn't a text file{(length is { } l ? $" ({Size(l)})" : "")}; download_file copies it here.");
            using var reader = new StreamReader(new KickStream(body, dog, head, got), enc, detectEncodingFromByteOrderMarks: true);

            var picked = new List<string>();
            var tail = start < 0 ? new LinkedList<string>() : null;
            var keep = start < 0 ? (int)Math.Min(-start, max) : 0;
            long lineNo = 0;
            var chars = 0;
            var more = false;
            while (await reader.ReadLineAsync(dog.Token) is { } line)
            {
                lineNo++;
                if (tail is not null)
                {
                    tail.AddLast(Output.Clip(line));
                    if (tail.Count > keep) tail.RemoveFirst();
                    continue;
                }
                if (lineNo < start) continue;
                var c = Output.Clip(line);
                if (picked.Count >= max || (picked.Count > 0 && chars + c.Length + 1 > MaxText)) { more = true; break; }
                picked.Add(c);
                chars += c.Length + 1;
            }
            long first;
            long? total = null;
            if (tail is not null)
            {
                total = lineNo;
                // the end of the file, cut to about MaxText from the front
                var size = tail.Sum(l => l.Length + 1);
                while (tail.Count > 1 && size > MaxText) { size -= tail.First!.Value.Length + 1; tail.RemoveFirst(); }
                picked = tail.ToList();
                first = lineNo - picked.Count + 1;
            }
            else
            {
                first = start;
                if (!more) total = lineNo;
                else if (length is <= 10 * 1024 * 1024)
                {
                    // count the rest for the total (small enough to read on)
                    var count = lineNo;
                    while (await reader.ReadLineAsync(dog.Token) is not null) count++;
                    total = count;
                }
            }
            var header = $"{remote} on {pc.Name} ({(length is { } len ? Size(len) + ", " : "")}{EncodingName(enc)})";
            if (picked.Count == 0)
                return [TextBlock($"{header}: no lines there; the file has {lineNo} line(s).")];
            var last = first + picked.Count - 1;
            var sb = new StringBuilder($"{header}: lines {first}-{last}{(total is { } t ? $" of {t}" : "")}:\n");
            sb.Append(string.Join("\n", picked));
            if (more || (total is { } tt && last < tt)) sb.Append($"\n\n[more: read_file with start_line {last + 1}]");
            return [TextBlock(sb.ToString())];
        }
        catch (Exception e) when (pc.Lost(e, dog) is { } f) { throw f; }
    }

    static async Task<JsonArray> DownloadTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var remote = Need(a, "path");
        var saveTo = Opt(a, "save_to");
        var overwrite = Bool(a, "overwrite");
        if (saveTo is not null && (saveTo.EndsWith('\\') || saveTo.EndsWith('/'))) LocalPath(saveTo, "save_to");   // fail before downloading
        using var dog = new Watchdog(60, ctx.Cancel);
        try
        {
            using var r = await pc.Send(HttpMethod.Get, "/api/fs/download?path=" + Q(remote), dog, stream: true);
            var name = DownloadName(r, remote);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            var zip = ext == ".zip" && !remote.TrimEnd('\\', '/').EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var image = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => null };
            var length = r.Content.Headers.ContentLength;
            string Target() => saveTo is not null ? LocalTarget(saveTo, name, overwrite) : TempTarget(pc, name);

            string Saved(string dest, long size)
            {
                var sb = new StringBuilder($"Saved {remote} from {pc.Name} to {dest} ({Size(size)})");
                if (zip) sb.Append("; the folder came as a .zip");
                string? preview = null;
                if (!zip && image is null)
                {
                    try { preview = Preview(dest); } catch (Exception) { }
                }
                sb.Append(preview is null ? "." : $". It is text, too long to show whole here (read_file reads any lines of it).\n\n{preview}");
                return sb.ToString();
            }

            // small enough to hand over directly
            if (!zip && length is { } len && (len <= InlineReadLimit || (image is not null && len <= InlineImageLimit)))
            {
                using var ms = new MemoryStream();
                await CopyAsync(await r.Content.ReadAsStreamAsync(dog.Token), ms, dog);
                var bytes = ms.ToArray();
                string? saved = null;
                if (saveTo is not null)
                {
                    saved = Target();
                    await WriteLocal(saved, bytes, ctx.Cancel);
                }
                var where = saved is not null ? $" Saved to {saved}." : "";
                if (image is not null)
                    return [TextBlock($"{remote} on {pc.Name} ({Size(bytes.Length)}).{where}"),
                            new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = image }];
                if (DecodeText(bytes) is { } text && text.Length <= MaxText)
                    return [TextBlock($"{remote} on {pc.Name} ({Size(bytes.Length)}).{where}\n\n{text.Replace("\r\n", "\n")}")];
                if (saved is null)
                {
                    saved = Target();
                    await WriteLocal(saved, bytes, ctx.Cancel);
                }
                return [TextBlock(Saved(saved, bytes.Length))];
            }

            var dest = Target();
            var got = await SaveBody(r, dest, dog);
            return [TextBlock(Saved(dest, got))];
        }
        catch (Exception e) when (pc.Lost(e, dog) is { } f) { throw f; }
    }

    static async Task<JsonArray> UploadTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var remote = Need(a, "path");
        var local = Opt(a, "local_path");
        var content = a["content"] is { } c && c.GetValueKind() == JsonValueKind.String ? c.GetValue<string>() : null;
        if ((local is null) == (content is null)) throw new ToolError("Give either local_path or content.");
        byte[]? data = null;
        string name;
        if (local is not null)
        {
            local = LocalPath(local, "local_path");
            if (!File.Exists(local)) throw new ToolError(Directory.Exists(local) ? $"{local} is a folder; upload single files (zip a folder first)." : $"Not found on this computer: {local}");
            name = Path.GetFileName(local);
        }
        else
        {
            if (remote.EndsWith('\\') || remote.EndsWith('/')) throw new ToolError("With content, `path` must name the file, not a folder.");
            name = Path.GetFileName(remote);
            var text = Utf8.GetBytes(content!);
            // Windows PowerShell 5.1 reads a script without a byte order mark in the ANSI code page
            data = Path.GetExtension(name).ToLowerInvariant() is ".ps1" or ".psm1" or ".psd1" && !content!.StartsWith('\uFEFF')
                ? [0xEF, 0xBB, 0xBF, .. text] : text;
        }
        if (remote.EndsWith('\\') || remote.EndsWith('/')) remote += name;
        var query = local is not null
            ? "&mtime=" + (new DateTimeOffset(File.GetLastWriteTimeUtc(local)).ToUnixTimeMilliseconds() / 1000.0).ToString(CultureInfo.InvariantCulture)
            : "";
        var size = data?.Length ?? new FileInfo(local!).Length;
        // text names its file in `path`: if that is a folder, don't make up a name inside it
        var done = await Upload(pc, remote, local is not null ? name : "", () => data is not null ? new MemoryStream(data) : File.OpenRead(local!), size, query,
                                Bool(a, "overwrite"), "set overwrite=true to replace it", ctx.Cancel);
        return [TextBlock($"Uploaded {(local ?? "the text")} to {(Str(done["path"]) is { Length: > 0 } p ? p : remote)} on {pc.Name} ({Size(size)}).")];
    }

    static async Task<JsonArray> MakeFolderTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var r = await pc.Json(HttpMethod.Post, "/api/fs/mkdir", ctx.Cancel, new { path = Need(a, "path") });
        return [TextBlock($"Created the folder {Str(r["path"])} on {pc.Name}.")];
    }

    static async Task<JsonArray> RenameTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var path = Need(a, "path");
        var r = await pc.Json(HttpMethod.Post, "/api/fs/rename", ctx.Cancel, new { path, new_name = Need(a, "new_name") });
        return [TextBlock($"Renamed {path} to {Str(r["path"])} on {pc.Name}.")];
    }

    static async Task<JsonArray> DeleteTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var paths = (a["paths"] is JsonValue single ? new[] { Str(single) } : Arr(a["paths"]).Select(Str)).Where(p => p.Trim() != "").ToList();
        if (Opt(a, "path") is { } one) paths.Add(one);
        if (paths.Count == 0) throw new ToolError("Give `paths` (or `path`).");
        var r = await pc.Json(HttpMethod.Post, "/api/fs/delete", ctx.Cancel, new { paths }, seconds: 120);
        var done = Arr(r["deleted"]).Select(Str).ToList();
        return [TextBlock($"Moved to the Recycle Bin on {pc.Name}:\n" + string.Join("\n", done.Select(d => "- " + d)))];
    }

    static async Task<JsonArray> WaitTool(JsonObject a, ToolContext ctx)
    {
        var pc = await Connect(Need(a, "pc"), ctx.Cancel);
        var file = Opt(a, "file");
        var process = Opt(a, "process");
        if ((file is null) == (process is null)) throw new ToolError("Give either file or process.");
        var asked = Num(a, "timeout") is > 0 and var t ? t : WaitLimit;
        var timeout = Math.Min(asked, WaitLimit);
        var changedAfter = Opt(a, "changed_after");
        var wasNow = changedAfter == "now";
        var start = DateTime.UtcNow;
        while (true)
        {
            ctx.Cancel.ThrowIfCancellationRequested();
            var left = Math.Max(0, timeout - (DateTime.UtcNow - start).TotalSeconds);
            var chunk = Math.Min(left, 30);   // short calls, so a cancelled wait ends soon
            var query = file is not null
                ? $"file={Q(file)}" + (changedAfter is not null ? $"&changed_after={Q(changedAfter)}" : "")
                : $"process={Q(process!)}&state={(Bool(a, "exited") ? "exited" : "started")}";
            var (v, date) = await WaitCall(pc, $"/api/wait?{query}&timeout={chunk.ToString("0.#", CultureInfo.InvariantCulture)}", chunk, ctx.Cancel);
            var met = IsTrue(v["met"]);
            var waited = Math.Round((DateTime.UtcNow - start).TotalSeconds);
            if (met && file is not null)
                return [TextBlock($"Yes after {waited} s: {Str(v["file"])} ({Size(Long(v["size"]))}, modified {Str(v["mtime"])}).")];
            if (met)
                return [TextBlock($"Yes after {waited} s: {Str(v["process"])} {Str(v["state"])}" +
                                  (v["pids"] is JsonArray { Count: > 0 } pids ? $" (PID {string.Join(", ", pids.Select(Str))})." : "."))];
            // "now" must keep meaning when the wait began, by the PC's clock
            if (changedAfter == "now" && date is { } d)
                changedAfter = (d.ToUnixTimeSeconds() - Dbl(v["waited_s"])).ToString("0.#", CultureInfo.InvariantCulture);
            if (left <= 30)
            {
                var sb = new StringBuilder($"Not yet: waited {waited} s for " +
                    (file is not null ? $"{file}{(changedAfter is not null ? " to change" : " to appear")}" : $"{process} to {(Bool(a, "exited") ? "exit" : "start")}"));
                if (asked > WaitLimit)
                    sb.Append($" ({WaitLimit:0} s is the most one call waits, not the {asked:0} s asked; call wait_for again to keep waiting" +
                              (wasNow && changedAfter != "now" ? $", with changed_after \"{changedAfter}\" to still count changes since this call began" : "") + ")");
                return [TextBlock(sb.Append('.').ToString())];
            }
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

/// <summary>A response body that keeps a watchdog alive while it is read, optionally after bytes already read
/// from it.</summary>
sealed class KickStream(Stream inner, Watchdog dog, byte[]? prefix = null, int prefixLength = 0) : Stream
{
    int pos;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (prefix is not null && pos < prefixLength)
        {
            var n = Math.Min(buffer.Length, prefixLength - pos);
            prefix.AsSpan(pos, n).CopyTo(buffer);
            pos += n;
            return n;
        }
        var got = inner.Read(buffer);
        dog.Kick();
        return got;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (prefix is not null && pos < prefixLength)
        {
            var n = Math.Min(buffer.Length, prefixLength - pos);
            prefix.AsMemory(pos, n).CopyTo(buffer);
            pos += n;
            return n;
        }
        var got = await inner.ReadAsync(buffer, ct);
        dog.Kick();
        return got;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
