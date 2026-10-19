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

    const string RemoteScript = """
        $f = Join-Path $env:ProgramData 'SSHManager\install-path.txt'
        if (-not (Test-Path $f)) { 'SSHMGR-ERROR:SSH Manager is not installed on this PC yet. Install it there and choose "Let other PCs manage this PC".'; exit 3 }
        & (Join-Path (Get-Content $f -Raw).Trim() 'ensure-running.ps1')
        """;

    readonly Action save;
    readonly int preferredPort;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim retryNow = new(0);
    Process? ssh;
    readonly List<string> sshOutput = [];
    string? remoteToken, remoteError, remoteVersion;
    int? remotePort;

    public PcEntry Pc { get; }
    public event Action<Link, LinkState, string>? Changed;
    public LinkState State { get; private set; } = LinkState.Connecting;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Url => $"http://localhost:{Port}/";
    /// <summary>Latest /api/status from this PC (refreshed by the main window).</summary>
    public JsonElement? Status { get; set; }

    public Link(PcEntry pc, int preferredPort, Action save)
    {
        Pc = pc;
        this.preferredPort = preferredPort;
        this.save = save;
        Port = preferredPort;
    }

    public void Start() => _ = Task.Run(() => RunAsync(stop.Token));

    /// <summary>Drop the current connection (if any) and connect again right away.</summary>
    public void Reconnect()
    {
        KillSsh();
        retryNow.Release();
    }

    void Set(LinkState state, string message)
    {
        State = state;
        Message = message;
        Changed?.Invoke(this, state, message);
    }

    async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            Set(LinkState.Connecting, attempt == 0 ? $"Connecting to {Pc.DisplayName}…" : $"Reconnecting to {Pc.DisplayName} (attempt {attempt + 1})…");
            string reason;
            try
            {
                reason = await (Pc.IsLocal ? RunLocalAsync(ct) : RunTunnelAsync(ct, () => attempt = 0));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { reason = e.Message; }
            if (ct.IsCancellationRequested) break;
            if (reason == "") { attempt = 0; continue; }   // reconnect immediately (e.g. learned the server's port)

            attempt++;
            Set(LinkState.Disconnected, reason);
            while (retryNow.CurrentCount > 0) retryNow.Wait(0);
            try { await retryNow.WaitAsync(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), ct); } catch (OperationCanceledException) { break; }
        }
    }

    static async Task<bool> AliveAsync(int port, CancellationToken ct)
    {
        try
        {
            using var r = await Http.GetAsync($"http://localhost:{port}/api/ping", ct);
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>A request to this PC's server with the access token.</summary>
    public HttpRequestMessage Request(HttpMethod method, string path, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(method, $"http://localhost:{Port}{path}") { Content = body };
        req.Headers.Add("X-SSHMgr-Token", Token);
        req.Headers.Add("X-SSHMgr", "1");
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
            var name = doc.RootElement.GetProperty("host").GetString() ?? "";
            Version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : remoteVersion ?? "";
            if (name != "" && name != Pc.PcName)
            {
                Pc.PcName = name;
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

    async Task<string> RunLocalAsync(CancellationToken ct)
    {
        var folder = Ssh.LocalInstallFolder();
        if (folder is null) return "SSH Manager's server isn't installed on this PC.";
        var exe = Path.Combine(folder, "SSHManagerServer.exe");
        var (code, output) = File.Exists(exe)
            ? await Ssh.RunAsync(exe, ["--client-info"], 20000)
            : await Ssh.RunAsync(Path.Combine(folder, @".venv\Scripts\python.exe"), [Path.Combine(folder, "server.py"), "--client-info"], 20000);
        remoteToken = remoteError = remoteVersion = null;
        remotePort = null;
        foreach (var line in output.Split('\n')) Keep(line, store: false);
        if (code != 0 || remoteToken is null || remotePort is null)
            return "Couldn't read SSH Manager's settings on this PC:\n\n" + output.Trim();
        Port = remotePort.Value;
        Token = remoteToken;
        if (!await AliveAsync(Port, ct))
        {
            StartLocalServer(folder);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!await AliveAsync(Port, ct))
            {
                if (DateTime.UtcNow > deadline) return "SSH Manager's server did not start on this PC.";
                await Task.Delay(500, ct);
            }
        }
        await LearnNameAsync(ct);
        Set(LinkState.Connected, "Connected (this PC)");
        await WatchAsync(Port, null, ct);
        return "SSH Manager's server on this PC stopped answering.";
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

    async Task<string> RunTunnelAsync(CancellationToken ct, Action connected)
    {
        Port = Ssh.IsPortFree(preferredPort) ? preferredPort : Ssh.FreePort();
        StartSsh();
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            if (remoteError is not null)
            {
                KillSsh();
                return remoteError;
            }
            if (remotePort is { } rp && rp != Pc.RemotePort)
            {
                // the server uses another port than we forwarded to: remember it and start over
                Pc.RemotePort = rp;
                save();
                KillSsh();
                return "";
            }
            if (remoteToken is not null && await AliveAsync(Port, ct)) break;
            if (ssh is null || ssh.HasExited) return DescribeSshFailure();
            if (DateTime.UtcNow > deadline)
            {
                KillSsh();
                return $"Connected over SSH, but SSH Manager on {Pc.DisplayName} did not answer.";
            }
            await Task.Delay(300, ct);
        }
        Token = remoteToken!;
        if (!await LearnNameAsync(ct))
        {
            KillSsh();
            return $"SSH Manager on {Pc.DisplayName} didn't accept the login.";
        }
        Set(LinkState.Connected, "Connected over SSH");
        connected();
        var proc = ssh;
        await WatchAsync(Port, () => proc is null || proc.HasExited, ct);
        var why = proc is { HasExited: true } ? DescribeSshFailure() : $"{Pc.DisplayName} stopped answering.";
        KillSsh();
        return why;
    }

    void StartSsh()
    {
        KillSsh();
        lock (sshOutput) sshOutput.Clear();
        remoteToken = remoteError = remoteVersion = null;
        remotePort = null;
        var psi = new ProcessStartInfo("ssh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in Ssh.Options(Pc)) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3",
                                  "-L", $"{Port}:127.0.0.1:{Pc.RemotePort}", Pc.Target, Ssh.PowerShell(RemoteScript) })
            psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Keep(e.Data, store: true);
        p.ErrorDataReceived += (_, e) => Keep(e.Data, store: true);
        p.Start();
        ProcessJob.Add(p);   // ends with the app, even if the app is killed
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        ssh = p;
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
                case "VERSION": remoteVersion = m.Groups[2].Value; break;
                case "PORT" when int.TryParse(m.Groups[2].Value, out var port): remotePort = port; break;
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

    [GeneratedRegex("^SSHMGR-(TOKEN|PORT|ERROR|VERSION):(.*)$")]
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
        var p = ssh;
        ssh = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(true); } catch (Exception) { }
        p.Dispose();
    }

    public void Dispose()
    {
        stop.Cancel();
        KillSsh();
    }
}
