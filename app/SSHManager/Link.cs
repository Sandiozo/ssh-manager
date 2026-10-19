using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SSHManager;

internal enum LinkState { Connecting, Connected, Disconnected, NeedsSetup }

/// <summary>
/// Keeps a connection to the SSH Manager server. Normally that is an `ssh -L` port forward to the PC
/// (started hidden, restarted with backoff when it drops) whose remote command starts the server if
/// needed and prints its port and access token. When this computer is the server itself it talks to
/// the local server directly.
/// </summary>
internal sealed partial class Link : IDisposable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    const string RemoteScript = """
        $f = Join-Path $env:ProgramData 'SSHManager\install-path.txt'
        if (-not (Test-Path $f)) { 'SSHMGR-ERROR:SSH Manager is not installed on this PC yet. Run Setup.cmd on it first.'; exit 3 }
        & (Join-Path (Get-Content $f -Raw).Trim() 'ensure-running.ps1')
        """;

    readonly AppSettings settings;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim retryNow = new(0);
    Process? ssh;
    readonly List<string> sshOutput = [];
    string? remoteToken, remoteError;
    int? remotePort;

    public event Action<LinkState, string>? Changed;
    public LinkState State { get; private set; } = LinkState.Connecting;
    public string Message { get; private set; } = "";
    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public string Url => $"http://localhost:{Port}/";
    public bool IsLocal { get; private set; }

    public Link(AppSettings settings)
    {
        this.settings = settings;
        Port = settings.LocalPort;
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
        Changed?.Invoke(state, message);
    }

    async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var local = Ssh.LocalInstallFolder();
            IsLocal = local is not null && !settings.IsConfigured;
            if (!IsLocal && !settings.IsConfigured)
            {
                Set(LinkState.NeedsSetup, "");
                try { await retryNow.WaitAsync(ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            Set(LinkState.Connecting, attempt == 0 ? $"Connecting to {settings.DisplayName}…" : $"Reconnecting to {settings.DisplayName} (attempt {attempt + 1})…");
            string reason;
            try
            {
                reason = await (IsLocal ? RunLocalAsync(local!, ct) : RunTunnelAsync(ct, () => attempt = 0));
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

    /// <summary>Asks the server for its name (also proves the token works).</summary>
    async Task<bool> LearnNameAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{Port}/api/whoami");
            req.Headers.Add("X-SSHMgr-Token", Token);
            using var r = await Http.SendAsync(req, ct);
            if (r.StatusCode != HttpStatusCode.OK) return false;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var name = doc.RootElement.GetProperty("host").GetString() ?? "";
            if (name != "" && name != settings.PcName)
            {
                settings.PcName = name;
                settings.Save();
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

    async Task<string> RunLocalAsync(string folder, CancellationToken ct)
    {
        var python = Path.Combine(folder, @".venv\Scripts\python.exe");
        var (code, output) = await Ssh.RunAsync(python, [Path.Combine(folder, "server.py"), "--client-info"], 20000);
        remoteToken = remoteError = null;
        remotePort = null;
        ParseInfo(output);
        if (code != 0 || remoteToken is null || remotePort is null)
            return "Couldn't read the SSH Manager settings on this PC:\n\n" + output.Trim();
        Port = remotePort.Value;
        Token = remoteToken;
        if (!await AliveAsync(Port, ct))
        {
            StartLocalServer(folder);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!await AliveAsync(Port, ct))
            {
                if (DateTime.UtcNow > deadline) return "The SSH Manager server did not start on this PC.";
                await Task.Delay(500, ct);
            }
        }
        await LearnNameAsync(ct);
        Set(LinkState.Connected, "Connected (this PC)");
        await WatchAsync(Port, null, ct);
        return "The SSH Manager server on this PC stopped answering.";
    }

    static void StartLocalServer(string folder)
    {
        // Prefer the logon task Setup.cmd creates (it runs with admin rights); fall back to a plain start.
        try
        {
            using var schtasks = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"SSH Manager\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            schtasks.WaitForExit(5000);
            if (schtasks.ExitCode == 0) return;
        }
        catch (Exception) { }
        Process.Start(new ProcessStartInfo(Path.Combine(folder, @".venv\Scripts\pythonw.exe"), "server.py")
        { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true });
    }

    async Task<string> RunTunnelAsync(CancellationToken ct, Action connected)
    {
        Port = Ssh.IsPortFree(settings.LocalPort) ? settings.LocalPort : Ssh.FreePort();
        StartSsh();
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            if (remoteError is not null)
            {
                KillSsh();
                return remoteError;
            }
            if (remotePort is { } rp && rp != settings.RemotePort)
            {
                // the server uses another port than we forwarded to: remember it and start over
                settings.RemotePort = rp;
                settings.Save();
                KillSsh();
                return "";
            }
            if (remoteToken is not null && await AliveAsync(Port, ct)) break;
            if (ssh is null || ssh.HasExited) return DescribeSshFailure();
            if (DateTime.UtcNow > deadline)
            {
                KillSsh();
                return $"Connected over SSH, but SSH Manager on {settings.DisplayName} did not answer.";
            }
            await Task.Delay(300, ct);
        }
        Token = remoteToken!;
        if (!await LearnNameAsync(ct))
        {
            KillSsh();
            return $"SSH Manager on {settings.DisplayName} didn't accept the login.";
        }
        Set(LinkState.Connected, "Connected over SSH");
        connected();
        var proc = ssh;
        await WatchAsync(Port, () => proc is null || proc.HasExited, ct);
        var why = proc is { HasExited: true } ? DescribeSshFailure() : $"{settings.DisplayName} stopped answering.";
        KillSsh();
        return why;
    }

    void StartSsh()
    {
        KillSsh();
        lock (sshOutput) sshOutput.Clear();
        remoteToken = remoteError = null;
        remotePort = null;
        var psi = new ProcessStartInfo("ssh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in Ssh.Options(settings)) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3",
                                  "-L", $"{Port}:127.0.0.1:{settings.RemotePort}", settings.Target, Ssh.PowerShell(RemoteScript) })
            psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Keep(e.Data);
        p.ErrorDataReceived += (_, e) => Keep(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        ssh = p;
    }

    void ParseInfo(string text)
    {
        foreach (var line in text.Split('\n')) Keep(line, store: false);
    }

    void Keep(string? line) => Keep(line, store: true);

    void Keep(string? line, bool store)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        line = line.Trim();
        if (InfoLine().Match(line) is { Success: true } m)
        {
            switch (m.Groups[1].Value)
            {
                case "TOKEN": remoteToken = m.Groups[2].Value; break;
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

    [GeneratedRegex("^SSHMGR-(TOKEN|PORT|ERROR):(.*)$")]
    private static partial Regex InfoLine();

    string DescribeSshFailure()
    {
        string text;
        lock (sshOutput) text = string.Join("\n", sshOutput);
        var why = Ssh.Explain(text, settings.DisplayName);
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
