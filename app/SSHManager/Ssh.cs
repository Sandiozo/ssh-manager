using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SSHManager;

/// <summary>Helpers around the Windows OpenSSH client (ssh.exe, ssh-keygen.exe).</summary>
internal static class Ssh
{
    public static readonly string SshFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    public static readonly string AppKey = Path.Combine(SshFolder, "id_ed25519_sshmanager");

    /// <summary>Where Setup.cmd installed the server on this computer, if it did.</summary>
    public static string? LocalInstallFolder()
    {
        var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SSHManager", "install-path.txt");
        try
        {
            var path = File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            return path is { Length: > 0 } && (File.Exists(Path.Combine(path, "SSHManagerServer.exe")) || File.Exists(Path.Combine(path, "server.py")))
                ? path : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Common options for non-interactive use. New host keys are accepted on first contact
    /// (trust on first use) and checked from then on.</summary>
    public static List<string> Options(PcEntry s, bool batch = true)
    {
        var a = new List<string>();
        if (batch) a.AddRange(["-o", "BatchMode=yes"]);
        a.AddRange(["-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=10"]);
        if (s.KeyFile.Trim() != "") a.AddRange(["-i", s.KeyFile.Trim(), "-o", "IdentitiesOnly=yes"]);
        if (s.LookupName.Trim() != "" && Resolve(s.LookupName.Trim()) is { } ip) a.AddRange(["-o", $"HostName={ip}"]);
        return a;
    }

    public static string? Resolve(string name)
    {
        try
        {
            return Dns.GetHostAddresses(name).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>A PowerShell script as a command line that survives any remote shell's quoting.</summary>
    public static string PowerShell(string script) =>
        "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'\n" + script));

    /// <summary>Runs ssh (or another tool) without a window; returns exit code and combined output.</summary>
    public static async Task<(int code, string output)> RunAsync(string exe, IEnumerable<string> args, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (Exception) { }
            return (-1, "Timed out.");
        }
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    /// <summary>Host aliases defined in ~/.ssh/config (no wildcards).</summary>
    public static List<string> ConfigHosts()
    {
        var path = Path.Combine(SshFolder, "config");
        var hosts = new List<string>();
        try
        {
            if (!File.Exists(path)) return hosts;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (!line.StartsWith("Host ", StringComparison.OrdinalIgnoreCase)) continue;
                hosts.AddRange(line[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(h => !h.Contains('*') && !h.Contains('?')));
            }
        }
        catch (Exception) { }
        return hosts.Distinct().ToList();
    }

    /// <summary>Plain-language reason for a failed ssh run.</summary>
    public static string Explain(string output, string host)
    {
        var o = output.ToLowerInvariant();
        if (o.Contains("could not resolve hostname"))
            return $"\"{host}\" could not be found on the network. Check the name, or use the PC's IP address.";
        if (o.Contains("timed out") || o.Contains("no route") || o.Contains("network is unreachable"))
            return $"{host} didn't answer. Is it switched on and on the same network? Is its firewall allowing SSH (port 22)?";
        if (o.Contains("connection refused"))
            return $"{host} refused the connection. OpenSSH Server isn't running there: run Setup.cmd on that PC.";
        if (o.Contains("permission denied"))
            return $"{host} didn't accept this computer's key.";
        if (o.Contains("host key verification failed") || o.Contains("remote host identification has changed"))
            return $"{host}'s identity (host key) is different from last time. If the PC was reinstalled, remove its line from ~/.ssh/known_hosts.";
        return "";
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
