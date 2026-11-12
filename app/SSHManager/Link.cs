using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SSHManager;

internal enum LinkState { Connecting, Connected, Disconnected }

/// <summary>
/// Keeps a connection to the SSH Manager server on one PC. Normally that is an `ssh -L` port forward
/// (started hidden, restarted with backoff when it drops) whose remote command starts the server if
/// needed and prints its version, port and access token. Whoever answers, at the saved address or one
/// found by looking the PC's name up, must show the host key saved for that PC (<see cref="HostKeys"/>).
/// For this computer's own server (<see cref="PcEntry.LocalId"/>) it talks to the local server directly.
/// </summary>
internal sealed partial class Link : IDisposable
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Runs ensure-running.ps1 from where the installer recorded it, but only when that record
    /// was written by an administrator and points into Program Files (or a source checkout of the server):
    /// a standard user could otherwise plant a record that makes an administrator's SSH login run their script.</summary>
    const string RemoteScript = """
        $f = Join-Path $env:ProgramData 'SSHManager\install-path.txt'
        if (-not (Test-Path $f)) { 'SSHMGR-ERROR:SSH Manager is not installed on this PC yet. Install it there and choose "Let other PCs manage this PC".'; exit 3 }
        $owner = try { (Get-Acl $f).GetOwner([Security.Principal.SecurityIdentifier]).Value } catch { '' }
        if ($owner -notin 'S-1-5-32-544', 'S-1-5-18') { 'SSHMGR-ERROR:SSH Manager''s install record on this PC (' + $f + ') was not written by an administrator, so it is not used. Reinstall SSH Manager on this PC.'; exit 4 }
        $dir = (Get-Content $f -Raw).Trim()
        $ok = $dir -and -not $dir.StartsWith('\\') -and [IO.Path]::IsPathRooted($dir)
        if ($ok) {
            $dir = [IO.Path]::GetFullPath($dir).TrimEnd('\')
            $pf = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') + '\' }
            $ok = [bool]($pf | Where-Object { ($dir + '\').StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) -or (Test-Path (Join-Path $dir 'server.py'))
        }
        if (-not $ok) { 'SSHMGR-ERROR:SSH Manager''s install record on this PC points to an unexpected folder (' + $dir + '). Reinstall SSH Manager on this PC.'; exit 4 }
        # as a script block, so an execution policy set by Group Policy can't block it
        $p = Join-Path $dir 'ensure-running.ps1'
        & ([scriptblock]::Create((Get-Content -LiteralPath $p -Raw -Encoding UTF8))) -Root $dir
        """;

    readonly Action save;
    readonly int preferredPort;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim retryNow = new(0);
    /// <summary>How often Reconnect was called. Each attempt notes the count when it starts; when it changes
    /// before the attempt is connected, the attempt starts over (it may have read the entry before it was
    /// changed), and a connection that is up ends.</summary>
    int reconnects;
    /// <summary>The count when the current attempt started (used by the link's own loop only).</summary>
    int attemptReconnects;
    bool Restarted => Volatile.Read(ref reconnects) != attemptReconnects;
    readonly object sshLock = new();
    Process? ssh;
    readonly List<string> sshOutput = [];
    volatile string? remoteToken, remoteError, remoteVersion, remoteAppSecret;
    int remotePortValue = -1;
    int? RemotePortSeen => Volatile.Read(ref remotePortValue) is var p && p >= 0 ? p : null;
    volatile bool resetBackoff;
    volatile string reachedAt = "";   // the address the current SSH connection went to
    int unsavedPort;                  // the server's port as a first connection reported it, while its host key isn't saved yet

    public PcEntry Pc { get; }
    public event Action<Link, LinkState, string>? Changed;
    public LinkState State { get; private set; } = LinkState.Connecting;
    public string Message { get; private set; } = "";
    /// <summary>The local port the app uses for this PC (assigned once, kept while the app runs).</summary>
    public int PreferredPort => preferredPort;
    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    /// <summary>Secret that proves a request comes from the app itself (not a page, not an agent): sent only
    /// when answering approvals and pairing requests. Empty for servers that don't print one.</summary>
    public string AppSecret { get; private set; } = "";
    public string Version { get; private set; } = "";
    /// <summary>The Windows computer name the PC's server reports (empty for older servers).</summary>
    public string Computer { get; private set; } = "";
    public string Url => $"http://localhost:{Port}/";
    /// <summary>Latest /api/status from this PC (refreshed by the main window).</summary>
    public JsonElement? Status { get; set; }
    /// <summary>Shared clipboard: the PC's clipboard change counter we've seen.</summary>
    public int ClipSeq { get; set; } = -1;

    public Link(PcEntry pc, int preferredPort, Action save)
    {
        Pc = pc;
        this.preferredPort = preferredPort;
        this.save = save;
        Port = preferredPort;
    }

    public void Start() => _ = Task.Run(() => RunAsync(stop.Token));

    /// <summary>Drop the current connection (if any) and connect again right away, with what the entry says
    /// now: an attempt under way, whatever it is doing, doesn't end connected but starts over.</summary>
    public void Reconnect(bool resetBackoff = false)
    {
        if (resetBackoff) this.resetBackoff = true;
        Interlocked.Increment(ref reconnects);   // before ssh is ended: an attempt that fails because of it knows why
        KillSsh();
        retryNow.Release();
    }

    void Set(LinkState state, string message)
    {
        State = state;
        Message = message;
        try { Changed?.Invoke(this, state, message); }
        catch (Exception e) { AppLog.Write("Link change handler", e); }
    }

    async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (resetBackoff) { resetBackoff = false; attempt = 0; }
            // a Reconnect from here on is seen by this attempt; the ones before are what it is for
            attemptReconnects = Volatile.Read(ref reconnects);
            while (retryNow.CurrentCount > 0) retryNow.Wait(0);
            Set(LinkState.Connecting, attempt == 0 ? $"Connecting to {Pc.DisplayName}…" : $"Reconnecting to {Pc.DisplayName} (attempt {attempt + 1})…");
            string reason;
            try
            {
                reason = await (Pc.IsLocal ? RunLocalAsync(ct) : RunTunnelAsync(ct, () => attempt = 0, lookup: attempt % 2 == 0));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { reason = e.Message; }
            finally { KillSsh(); }
            if (ct.IsCancellationRequested) break;
            if (Restarted) continue;   // Reconnect was called during the attempt: a fresh one, right away
            if (reason == "") { attempt = 0; continue; }   // reconnect immediately (e.g. learned the server's port)

            attempt++;
            Set(LinkState.Disconnected, reason);
            // a Reconnect from now on ends the wait
            try { await retryNow.WaitAsync(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), ct); } catch (OperationCanceledException) { break; }
            if (resetBackoff) { resetBackoff = false; attempt = 0; }
        }
    }

    // The server and the port forward listen on 127.0.0.1 only; asking "localhost" could reach whatever
    // someone else has listening on [::1] with the same port.
    static async Task<bool> AliveAsync(int port, CancellationToken ct)
    {
        try
        {
            using var r = await Http.GetAsync($"http://127.0.0.1:{port}/api/ping", ct);
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>A request to this PC's server with the access token.</summary>
    public HttpRequestMessage Request(HttpMethod method, string path, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(method, $"http://127.0.0.1:{Port}{path}") { Content = body };
        req.Headers.Add("X-SSHMgr-Token", Token);
        req.Headers.Add("X-SSHMgr", "1");
        return req;
    }

    /// <summary>A request only the app itself may make (answering approvals and pairing requests): also
    /// carries the app secret when this PC's server has one.</summary>
    public HttpRequestMessage AppRequest(HttpMethod method, string path, HttpContent? body = null)
    {
        var req = Request(method, path, body);
        if (AppSecret != "") req.Headers.Add("X-SSHMgr-App", AppSecret);
        return req;
    }

    /// <summary>Asks the server for its name and version (also proves the token works).</summary>
    /// <param name="learnAddress">Also remember the computer name and the address this connection went to.</param>
    /// <param name="checkOnly">Only check that the token works and keep nothing of what the server says about
    /// itself: for a first connection, as long as its host key isn't saved as the PC's identity.</param>
    async Task<bool> LearnNameAsync(CancellationToken ct, bool learnAddress, bool checkOnly = false)
    {
        try
        {
            using var r = await Http.SendAsync(Request(HttpMethod.Get, "/api/whoami"), ct);
            if (r.StatusCode != HttpStatusCode.OK) return false;
            if (checkOnly) return true;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string S(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var name = S("host");
            Version = root.TryGetProperty("version", out var vv) && vv.ValueKind == JsonValueKind.String ? vv.GetString() ?? "" : remoteVersion ?? "";
            var mac = root.TryGetProperty("mac", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : Pc.Mac;
            // only real addresses: they end up in ssh's command line and in Wake-on-LAN packets
            var ips = root.TryGetProperty("ips", out var i) && i.ValueKind == JsonValueKind.Array
                ? i.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Ssh.CleanIp(x.GetString() ?? ""))
                    .OfType<string>().Distinct().ToList()
                : Pc.Ips;
            // a PC added by its IP address: remember its computer name too, so it is still found when the
            // router gives it another address, and keep the address this connection went to. Not one of the
            // addresses the PC lists: one reached through a VPN (Tailscale) or a forwarded port isn't among them
            var computer = S("computer");
            if (!Ssh.SafeName(computer)) computer = "";
            Computer = computer;
            var byAddress = learnAddress && IPAddress.TryParse(Pc.Host, out _);
            var lookupName = byAddress && computer != "" ? computer : Pc.LookupName;
            var host = byAddress && IPAddress.TryParse(reachedAt, out _) ? reachedAt : Pc.Host;
            if (name.Length > 64 || name.Any(char.IsControl)) name = "";
            // the entry was changed meanwhile: what this connection found is not written over it
            if (Restarted) return false;
            if ((name != "" && name != Pc.PcName) || mac != Pc.Mac || !ips.SequenceEqual(Pc.Ips) || lookupName != Pc.LookupName || host != Pc.Host)
            {
                if (name != "") Pc.PcName = name;
                Pc.Mac = mac;
                Pc.Ips = ips;
                Pc.LookupName = lookupName;
                Pc.Host = host;
                save();
            }
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>Returns once the server stops answering (3 failed checks in a row).</summary>
    static async Task WatchAsync(int port, Func<bool>? sshDied, CancellationToken ct)
    {
        var failures = 0;
        while (failures < 3)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
            if (sshDied?.Invoke() == true) return;
            failures = await AliveAsync(port, ct) ? 0 : failures + 1;
        }
    }

    void ClearRemote()
    {
        remoteToken = remoteError = remoteVersion = remoteAppSecret = null;
        Volatile.Write(ref remotePortValue, -1);
    }

    async Task<string> RunLocalAsync(CancellationToken ct)
    {
        var folder = Ssh.LocalInstallFolder();
        if (folder is null) return "SSH Manager's server isn't installed on this PC.";
        var exe = Path.Combine(folder, "SSHManagerServer.exe");
        var (code, output) = File.Exists(exe)
            ? await Ssh.RunAsync(exe, ["--client-info"], 20000)
            : await Ssh.RunAsync(Path.Combine(folder, @".venv\Scripts\python.exe"), [Path.Combine(folder, "server.py"), "--client-info"], 20000);
        ClearRemote();
        foreach (var line in output.Split('\n')) Keep(line, store: false);
        if (code != 0 || remoteToken is null || RemotePortSeen is null) return LocalInfoProblem(output);
        Port = RemotePortSeen.Value;
        if (!await AliveAsync(Port, ct))
        {
            // nobody may be listening on the port before the server: check again once it's up
            if (PortOwner.CheckServer(Port, folder, nothingIsOk: true) is { } squatted) return squatted;
            await LocalServer.StartAsync(folder, ct);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!await AliveAsync(Port, ct))
            {
                if (DateTime.UtcNow > deadline) return "SSH Manager's server did not start on this PC.";
                await Task.Delay(500, ct);
            }
        }
        // the token goes only to the real server
        if (PortOwner.CheckServer(Port, folder) is { } wrong) return wrong;
        Token = remoteToken ?? "";
        AppSecret = remoteAppSecret ?? "";
        await LearnNameAsync(ct, learnAddress: false);
        if (Restarted) return "";   // asked to reconnect meanwhile: start over
        Set(LinkState.Connected, "Connected (this PC)");
        string? taken = null;
        // also notice someone else taking the port over while the server is restarting, and a Reconnect
        // (there is no ssh to end here)
        await WatchAsync(Port, () => Restarted || (taken = PortOwner.CheckServer(Port, folder, nothingIsOk: true)) is not null, ct);
        return taken ?? "SSH Manager's server on this PC stopped answering.";
    }

    /// <summary>Why the local server's --client-info output could not be used. That output holds the access
    /// token and the app secret, and this text is shown on the status page and handed to the command-line
    /// tool: it is built only from lines that are not SSHMGR- lines, and kept short.</summary>
    internal static string LocalInfoProblem(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
        var info = lines.Select(l => InfoLine().Match(l)).Where(m => m.Success).ToList();
        var port = info.FirstOrDefault(m => m.Groups[1].Value == "PORT")?.Groups[2].Value.Trim();
        if (port is not null && !(int.TryParse(port, out var n) && n is > 0 and < 65536))
        {
            port = new string(port.Where(c => !char.IsControl(c)).Take(20).ToArray());
            return $"SSH Manager's settings on this PC have an invalid port ('{port}'). Fix \"port\" in its config.json.";
        }
        var text = string.Join("\n", lines.Where(l => !l.Contains("SSHMGR-", StringComparison.OrdinalIgnoreCase)).TakeLast(12));
        // should the server ever repeat a secret in another line (an error text, say)
        foreach (var m in info.Where(m => m.Groups[1].Value is "TOKEN" or "APPSECRET"))
            if (m.Groups[2].Value.Trim() is { Length: >= 8 } secret) text = text.Replace(secret, "[hidden]");
        if (text.Length > 1000) text = "…" + text[^1000..];
        return text == "" ? "Couldn't read SSH Manager's settings on this PC." : "Couldn't read SSH Manager's settings on this PC:\n\n" + text;
    }

    /// <param name="lookup">Look the computer name up (Windows name resolution) instead of using the saved
    /// address. Attempts alternate, so a stale address or a failed lookup never locks the PC out.</param>
    async Task<string> RunTunnelAsync(CancellationToken ct, Action connected, bool lookup)
    {
        if (!Ssh.SafeName(Pc.Host.Trim()) || (Pc.User.Trim() != "" && !Ssh.SafeName(Pc.User.Trim(), allowSpaces: true)))
            return $"The address or user name saved for {Pc.DisplayName} isn't valid. Use Change connection to fix it.";
        // Only a server that shows the host key saved for this PC is talked to, at whatever address it is
        // found. With no key saved yet (a PC from an earlier version that ssh has no record of either), this
        // connection records it: only at the saved address then, never at one found by looking the name up.
        HostTrust trust;
        try { trust = await HostKeys.ForSavedAsync(Pc); }
        catch (HostKeyProblem e)
        {
            return $"Couldn't check {Pc.DisplayName}'s identity (host key), so SSH Manager did not connect.\n\n{e.Message}" +
                   (e.Passes ? "\n\nSSH Manager tries again by itself." : "");
        }
        // The entry may change while an attempt runs (Change connection): Reconnect can't end an ssh that isn't
        // started yet, so the attempt looks itself, here and until it is connected, and starts over
        if (Restarted) return "";
        var firstUse = trust.AcceptNew;
        if (!firstUse)
        {
            NoteKeySaved();
            unsavedPort = 0;
        }
        // Until a first connection's key is saved, nothing its server says is: the port it reports is used, not kept
        var remotePort = firstUse && unsavedPort > 0 ? unsavedPort : Pc.RemotePort;
        Port = Ssh.IsPortFree(preferredPort) ? preferredPort : Ssh.FreePort();
        StartSsh(trust, lookup && !firstUse, remotePort);
        ct.ThrowIfCancellationRequested();   // removed while ssh was starting: finally in RunAsync kills it
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            if (Restarted) return "";   // also when it came while ssh was starting (the name lookup can take seconds)
            if (remoteError is { } err) return err;
            if (RemotePortSeen is { } rp && rp != remotePort)
            {
                // the server uses another port than we forwarded to: remember it and start over
                if (firstUse) unsavedPort = rp;
                else
                {
                    Pc.RemotePort = rp;
                    save();
                }
                return "";
            }
            if (remoteToken is not null && await AliveAsync(Port, ct)) break;
            if (SshExited()) return DescribeSshFailure(firstUse);
            if (DateTime.UtcNow > deadline) return $"Connected over SSH, but SSH Manager on {Pc.DisplayName} did not answer.";
            await Task.Delay(300, ct);
        }
        // the key ssh recorded on this first connection; without it the PC would be trusted anew every time
        List<string> recorded = [];
        if (firstUse)
        {
            try { recorded = HostKeys.Read(trust.File); } catch (HostKeyProblem) { }
            if (recorded.Count == 0) return CantSaveKey();
        }
        Token = remoteToken!;
        AppSecret = remoteAppSecret ?? "";
        if (!await LearnNameAsync(ct, learnAddress: true, checkOnly: firstUse)) return $"SSH Manager on {Pc.DisplayName} didn't accept the login.";
        if (firstUse)
        {
            if (Restarted) return "";   // not the identity of what the entry may lead to now
            // it is the PC (it let this computer in and its server answers): this key is its identity from now on.
            // Unless another key was saved for it meanwhile: then connect again, held to that one
            try
            {
                if (!HostKeys.Keep(trust, Pc.Id, recorded)) return "";
            }
            catch (HostKeyProblem) { return CantSaveKey(); }
            firstUse = false;
            unsavedPort = 0;
            Pc.KeySaved = true;
            Pc.RemotePort = remotePort;
            save();
            if (!await LearnNameAsync(ct, learnAddress: true)) return $"SSH Manager on {Pc.DisplayName} didn't accept the login.";
        }
        if (Restarted) return "";
        Set(LinkState.Connected, "Connected over SSH");
        connected();
        Process? proc;
        lock (sshLock) proc = ssh;
        await WatchAsync(Port, () => proc is null || Exited(proc) || Restarted, ct);
        return proc is not null && Exited(proc) ? DescribeSshFailure(firstUse) : $"{Pc.DisplayName} stopped answering.";
    }

    /// <summary>From now on a missing key file for this PC is an error, not a reason to trust a new key.</summary>
    void NoteKeySaved()
    {
        if (Pc.KeySaved) return;
        Pc.KeySaved = true;
        save();
    }

    /// <summary>A first connection worked, but its host key could not be kept: the PC would be trusted anew
    /// every time, so the connection is given up.</summary>
    string CantSaveKey() =>
        $"Couldn't save {Pc.DisplayName}'s identity (host key) on this computer, so the connection was closed.\n\n{HostKeys.CantWrite}" +
        "\n\nSSH Manager tries again by itself.";

    static bool Exited(Process p)
    {
        try { return p.HasExited; } catch (Exception) { return true; }
    }

    bool SshExited()
    {
        lock (sshLock) return ssh is null || Exited(ssh);
    }

    /// <param name="remotePort">The port of SSH Manager's server on the PC.</param>
    void StartSsh(HostTrust trust, bool lookup, int remotePort)
    {
        KillSsh();
        lock (sshOutput) sshOutput.Clear();
        ClearRemote();
        var psi = new ProcessStartInfo(Ssh.Tool("ssh.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        // the PC's current address when its name resolves, else the saved one
        var resolved = lookup && Pc.LookupName.Trim() != "" ? Ssh.Resolve(Pc.LookupName.Trim()) : null;
        reachedAt = resolved ?? Pc.Host;
        foreach (var a in Ssh.Options(Pc, trust, hostName: resolved)) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3",
                                  "-L", $"127.0.0.1:{Port}:127.0.0.1:{remotePort}" })
            psi.ArgumentList.Add(a);
        // the page view opens http://localhost:port/, which may go to [::1]: hold that address too, so no
        // other program can listen there and receive the page's token cookie
        if (Ssh.Ipv6Loopback())
        {
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add($"[::1]:{Port}:127.0.0.1:{remotePort}");
        }
        foreach (var a in new[] { "--", Pc.Target, Ssh.PowerShell(RemoteScript) }) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Keep(e.Data, store: true);
        p.ErrorDataReceived += (_, e) => Keep(e.Data, store: true);
        p.Start();
        ProcessJob.Add(p);   // ends with the app, even if the app is killed
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (sshLock)
        {
            var old = ssh;
            ssh = p;
            if (old is not null) Kill(old);
        }
    }

    void Keep(string? line, bool store)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        line = line.Trim();
        if (InfoLine().Match(line) is { Success: true } m)
        {
            switch (m.Groups[1].Value)
            {
                case "TOKEN": remoteToken = m.Groups[2].Value; break;
                case "APPSECRET": remoteAppSecret = m.Groups[2].Value; break;
                case "VERSION": remoteVersion = m.Groups[2].Value; break;
                case "PORT" when int.TryParse(m.Groups[2].Value, out var port) && port is > 0 and < 65536:
                    Volatile.Write(ref remotePortValue, port);
                    break;
                // the PC's script quotes a line of the server's output here: never a token line that slipped in
                case "ERROR": remoteError = SecretInText().Replace(m.Groups[2].Value, "[hidden]"); break;
            }
            return;   // never keep the token in the error text
        }
        // nor an info line that has something printed in front of it
        if (line.Contains("SSHMGR-")) return;
        if (!store || line.Contains("post-quantum", StringComparison.OrdinalIgnoreCase) || line.StartsWith("**")) return;
        if (Ssh.KeyAddedNote(line)) return;
        lock (sshOutput)
        {
            sshOutput.Add(line);
            if (sshOutput.Count > 20) sshOutput.RemoveAt(0);
        }
    }

    [GeneratedRegex("^SSHMGR-(TOKEN|APPSECRET|PORT|ERROR|VERSION):(.*)$")]
    private static partial Regex InfoLine();

    [GeneratedRegex(@"SSHMGR-(TOKEN|APPSECRET):\S*")]
    private static partial Regex SecretInText();

    /// <param name="firstUse">No host key is saved for the PC yet, so it was not looked for by name.</param>
    string DescribeSshFailure(bool firstUse)
    {
        string text;
        lock (sshOutput) text = string.Join("\n", sshOutput);
        return FailureText(text, Pc, firstUse);
    }

    /// <summary>What the status page says after ssh ended: the reason in plain words, then ssh's own lines.</summary>
    internal static string FailureText(string text, PcEntry pc, bool firstUse)
    {
        var why = Ssh.Explain(text, pc.DisplayName);
        var refused = Ssh.KeyRefused(text);
        // ssh's own advice for another host key is to edit the key file: show only the key that was offered
        if (refused)
            text = string.Join("\n", text.Split('\n').Where(l => l.Contains("SHA256:")).Select(l => "The key it answered with: " + l.Trim().TrimEnd('.')));
        var message = why != "" ? why : string.IsNullOrWhiteSpace(text) ? "The SSH connection closed." : "The SSH connection closed:";
        if (!string.IsNullOrWhiteSpace(text)) message += "\n\n" + text;
        if (firstUse && !refused && pc.LookupName.Trim() != "" && !text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            message += $"\n\nSSH Manager has no identity (host key) on record for this PC yet, so it only tries the saved address ({pc.Host.Trim()}) " +
                       "and does not look for the PC by its name. If its address changed, use Change connection and enter the current one.";
        return message;
    }

    void KillSsh()
    {
        Process? p;
        lock (sshLock)
        {
            p = ssh;
            ssh = null;
        }
        if (p is not null) Kill(p);
    }

    static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch (Exception) { }
        try { p.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        stop.Cancel();
        KillSsh();
    }
}
