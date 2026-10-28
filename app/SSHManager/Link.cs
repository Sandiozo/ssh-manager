using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SSHManager;

internal enum LinkState { Connecting, Connected, Disconnected }

/// <summary>
/// Keeps a connection to the SSH Manager server on one PC. Normally that is an `ssh -L` port forward
/// (started hidden, restarted with backoff when it drops) whose remote command starts the server if
/// needed and prints its version, port and access token. For this computer's own server
/// (<see cref="PcEntry.LocalId"/>) it talks to the local server directly.
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
    readonly object sshLock = new();
    Process? ssh;
    readonly List<string> sshOutput = [];
    volatile string? remoteToken, remoteError, remoteVersion, remoteAppSecret;
    int remotePortValue = -1;
    int? RemotePortSeen => Volatile.Read(ref remotePortValue) is var p && p >= 0 ? p : null;
    volatile bool resetBackoff;

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

    /// <summary>Drop the current connection (if any) and connect again right away.</summary>
    public void Reconnect(bool resetBackoff = false)
    {
        if (resetBackoff) this.resetBackoff = true;
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
            if (reason == "") { attempt = 0; continue; }   // reconnect immediately (e.g. learned the server's port)

            attempt++;
            Set(LinkState.Disconnected, reason);
            while (retryNow.CurrentCount > 0) retryNow.Wait(0);
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
    async Task<bool> LearnNameAsync(CancellationToken ct)
    {
        try
        {
            using var r = await Http.SendAsync(Request(HttpMethod.Get, "/api/whoami"), ct);
            if (r.StatusCode != HttpStatusCode.OK) return false;
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
            // router gives it another address, and keep the saved address up to date
            var computer = S("computer");
            if (!Ssh.SafeName(computer)) computer = "";
            Computer = computer;
            var byAddress = IPAddress.TryParse(Pc.Host, out _);
            var lookupName = byAddress && computer != "" ? computer : Pc.LookupName;
            var host = byAddress && ips.Count > 0 && !ips.Contains(Pc.Host)
                ? (Ssh.Resolve(lookupName) is { } now && ips.Contains(now) ? now : ips[0]) : Pc.Host;
            if (name.Length > 64 || name.Any(char.IsControl)) name = "";
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
        if (code != 0 || remoteToken is null || RemotePortSeen is null)
            return "Couldn't read SSH Manager's settings on this PC:\n\n" + output.Trim();
        Port = RemotePortSeen.Value;
        if (!await AliveAsync(Port, ct))
        {
            // nobody may be listening on the port before the server: check again once it's up
            if (PortOwner.CheckServer(Port, folder, nothingIsOk: true) is { } squatted) return squatted;
            StartLocalServer(folder);
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
        await LearnNameAsync(ct);
        Set(LinkState.Connected, "Connected (this PC)");
        string? taken = null;
        // also notice someone else taking the port over while the server is restarting
        await WatchAsync(Port, () => (taken = PortOwner.CheckServer(Port, folder, nothingIsOk: true)) is not null, ct);
        return taken ?? "SSH Manager's server on this PC stopped answering.";
    }

    static void StartLocalServer(string folder)
    {
        // The logon task the installer creates runs it with admin rights; fall back to a plain start.
        try
        {
            using var schtasks = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"SSH Manager\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            schtasks.WaitForExit(5000);
            if (schtasks.ExitCode == 0) return;
        }
        catch (Exception) { }
        var exe = Path.Combine(folder, "SSHManagerServer.exe");
        var psi = File.Exists(exe)
            ? new ProcessStartInfo(exe, "--log-console")
            : new ProcessStartInfo(Path.Combine(folder, @".venv\Scripts\pythonw.exe"), "server.py");
        psi.WorkingDirectory = folder;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        Process.Start(psi);
    }

    /// <param name="lookup">Look the computer name up (Windows name resolution) instead of using the saved
    /// address. Attempts alternate, so a stale address or a failed lookup never locks the PC out.</param>
    async Task<string> RunTunnelAsync(CancellationToken ct, Action connected, bool lookup)
    {
        if (!Ssh.SafeName(Pc.Host.Trim()) || (Pc.User.Trim() != "" && !Ssh.SafeName(Pc.User.Trim(), allowSpaces: true)))
            return $"The address or user name saved for {Pc.DisplayName} isn't valid. Use Change connection to fix it.";
        Port = Ssh.IsPortFree(preferredPort) ? preferredPort : Ssh.FreePort();
        StartSsh(lookup);
        ct.ThrowIfCancellationRequested();   // removed while ssh was starting: finally in RunAsync kills it
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            if (remoteError is { } err) return err;
            if (RemotePortSeen is { } rp && rp != Pc.RemotePort)
            {
                // the server uses another port than we forwarded to: remember it and start over
                Pc.RemotePort = rp;
                save();
                return "";
            }
            if (remoteToken is not null && await AliveAsync(Port, ct)) break;
            if (SshExited()) return DescribeSshFailure();
            if (DateTime.UtcNow > deadline) return $"Connected over SSH, but SSH Manager on {Pc.DisplayName} did not answer.";
            await Task.Delay(300, ct);
        }
        Token = remoteToken!;
        AppSecret = remoteAppSecret ?? "";
        if (!await LearnNameAsync(ct)) return $"SSH Manager on {Pc.DisplayName} didn't accept the login.";
        Set(LinkState.Connected, "Connected over SSH");
        connected();
        Process? proc;
        lock (sshLock) proc = ssh;
        await WatchAsync(Port, () => proc is null || Exited(proc), ct);
        return proc is not null && Exited(proc) ? DescribeSshFailure() : $"{Pc.DisplayName} stopped answering.";
    }

    static bool Exited(Process p)
    {
        try { return p.HasExited; } catch (Exception) { return true; }
    }

    bool SshExited()
    {
        lock (sshLock) return ssh is null || Exited(ssh);
    }

    void StartSsh(bool lookup = true)
    {
        KillSsh();
        lock (sshOutput) sshOutput.Clear();
        ClearRemote();
        var psi = new ProcessStartInfo(Ssh.Tool("ssh.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in Ssh.Options(Pc, lookup: lookup)) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3",
                                  "-L", $"127.0.0.1:{Port}:127.0.0.1:{Pc.RemotePort}" })
            psi.ArgumentList.Add(a);
        // the page view opens http://localhost:port/, which may go to [::1]: hold that address too, so no
        // other program can listen there and receive the page's token cookie
        if (Ssh.Ipv6Loopback())
        {
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add($"[::1]:{Port}:127.0.0.1:{Pc.RemotePort}");
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
                case "ERROR": remoteError = m.Groups[2].Value; break;
            }
            return;   // never keep the token in the error text
        }
        if (!store || line.Contains("post-quantum", StringComparison.OrdinalIgnoreCase) || line.StartsWith("**")) return;
        lock (sshOutput)
        {
            sshOutput.Add(line);
            if (sshOutput.Count > 20) sshOutput.RemoveAt(0);
        }
    }

    [GeneratedRegex("^SSHMGR-(TOKEN|APPSECRET|PORT|ERROR|VERSION):(.*)$")]
    private static partial Regex InfoLine();

    string DescribeSshFailure()
    {
        string text;
        lock (sshOutput) text = string.Join("\n", sshOutput);
        var why = Ssh.Explain(text, Pc.DisplayName);
        if (why != "") return why + "\n\n" + text;
        return string.IsNullOrWhiteSpace(text) ? "The SSH connection closed." : "The SSH connection closed:\n\n" + text;
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
