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

    /// <summary>Where Setup.cmd installed the server on this computer, if it did. The record is only trusted
    /// when an administrator (or Windows) wrote it: a standard user could otherwise point it elsewhere.</summary>
    public static string? LocalInstallFolder()
    {
        var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SSHManager", "install-path.txt");
        try
        {
            if (!File.Exists(f) || !OwnedByAdmins(f)) return null;
            var path = File.ReadAllText(f).Trim();
            if (path == "" || path.StartsWith(@"\\") || !Path.IsPathFullyQualified(path)) return null;
            path = Path.GetFullPath(path);
            return File.Exists(Path.Combine(path, "SSHManagerServer.exe")) || File.Exists(Path.Combine(path, "server.py")) ? path : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The file's owner is Administrators or SYSTEM.</summary>
    public static bool OwnedByAdmins(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier))?.Value;
            return owner is "S-1-5-32-544" or "S-1-5-18";
        }
        catch (Exception) { return false; }
    }

    /// <summary>A host, alias, computer name or user name that is safe to hand to ssh: no leading '-'
    /// (it would be read as an option), no spaces or control characters.</summary>
    public static bool SafeName(string s, bool allowSpaces = false) =>
        s.Length is > 0 and <= 255 && s[0] != '-'
        && !s.Any(c => char.IsControl(c) || (!allowSpaces && char.IsWhiteSpace(c)))
        && !(allowSpaces && (s != s.Trim() || s.Contains('@')));

    /// <summary>A real IPv4/IPv6 address in its normal form, or null.</summary>
    public static string? CleanIp(string s) =>
        IPAddress.TryParse(s.Trim(), out var ip) && s.Trim() == ip.ToString() ? ip.ToString() : null;

    /// <summary>ssh.exe / ssh-keygen.exe from Windows' OpenSSH Client; a clear message when it's missing.</summary>
    public static string Tool(string exe)
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", exe);
        if (File.Exists(system)) return system;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), exe);
                if (Path.IsPathFullyQualified(p) && File.Exists(p)) return p;
            }
            catch (Exception) { }
        }
        throw new InvalidOperationException(MissingClient);
    }

    public const string MissingClient = "Windows' OpenSSH Client isn't installed on this computer. Install it in Settings → System → " +
                                        "Optional features → Add a feature → \"OpenSSH Client\", then try again.";

    /// <summary>Common options for non-interactive use. New host keys are accepted on first contact
    /// (trust on first use) and checked from then on.</summary>
    public static List<string> Options(PcEntry s, bool batch = true, bool lookup = true)
    {
        var a = new List<string>();
        if (batch) a.AddRange(["-o", "BatchMode=yes"]);
        a.AddRange(["-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=10"]);
        if (s.KeyFile.Trim() != "") a.AddRange(["-i", s.KeyFile.Trim(), "-o", "IdentitiesOnly=yes"]);
        if (lookup && s.LookupName.Trim() != "" && Resolve(s.LookupName.Trim()) is { } ip) a.AddRange(["-o", $"HostName={ip}"]);
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
    public static async Task<(int code, string output)> RunAsync(string exe, IEnumerable<string> args, int timeoutMs = 30000,
        IDictionary<string, string>? env = null)
    {
        if (exe.StartsWith("ssh", StringComparison.OrdinalIgnoreCase) && !Path.IsPathRooted(exe)) exe = Tool(exe);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        ProcessJob.Add(p);
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

    /// <summary>Lines of known_hosts for this host (with fingerprints), empty when ssh has never seen it.</summary>
    public static async Task<List<string>> KnownFingerprintsAsync(string host)
    {
        var known = Path.Combine(SshFolder, "known_hosts");
        if (!File.Exists(known)) return [];
        var (code, output) = await RunAsync("ssh-keygen.exe", ["-l", "-F", host, "-f", known], 10000);
        if (code != 0) return [];
        return output.Split('\n').Select(l => l.Trim()).Where(l => l != "" && !l.StartsWith('#'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 3 && p[^1].StartsWith("SHA256:"))
            .Select(p => $"{p[^2]} {p[^1]}").Distinct().ToList();   // "ED25519 SHA256:…"
    }

    /// <summary>Forgets a host key (when the user doesn't trust it).</summary>
    public static Task ForgetHostAsync(string host) =>
        RunAsync("ssh-keygen.exe", ["-R", host, "-f", Path.Combine(SshFolder, "known_hosts")], 10000);

    /// <summary>Free on both 127.0.0.1 and ::1, so nobody else can answer for "localhost" on that port.</summary>
    public static bool IsPortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
        }
        catch (SocketException) { return false; }
        if (!Ipv6Loopback()) return true;
        try
        {
            var l6 = new TcpListener(IPAddress.IPv6Loopback, port);
            l6.Start();
            l6.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>This computer has an IPv6 loopback address (::1) programs can listen on.</summary>
    public static bool Ipv6Loopback()
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            var l = new TcpListener(IPAddress.IPv6Loopback, 0);
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
