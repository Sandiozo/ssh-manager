using System.Diagnostics;

namespace SSHManager;

/// <summary>Starting this PC's own server from the app, for when it doesn't answer. It is started only when
/// nothing else is starting it: a second copy would fight the first for the port, and a copy started from
/// here has no administrator rights (the app has none), while the installer's logon task gives it those.</summary>
internal static class LocalServer
{
    /// <summary>Starts the server from <paramref name="folder"/> unless it is being started already: an
    /// update that is still running starts it when it is done, and a server that is starting up takes a few
    /// seconds (longer on a busy PC) before it answers. The caller waits for it to answer either way.</summary>
    public static async Task StartAsync(string folder, CancellationToken ct)
    {
        if (SetupRunning() || Running(folder)) return;
        // The logon task the installer creates runs it with admin rights. No answer in time doesn't mean it
        // failed: Task Scheduler can be slow on a busy PC, and the request still stands.
        var task = await RunLogonTaskAsync(ct);
        if (task is null) AppLog.Write("Windows didn't say within 15 s whether the server's logon task started; waiting for the server instead of starting a copy.");
        if (task is 0 or null) return;
        // No such task (a source checkout, or the task was removed): a plain start, unless it came up meanwhile
        if (SetupRunning() || Running(folder)) return;
        var exe = Path.Combine(folder, "SSHManagerServer.exe");
        var psi = File.Exists(exe)
            ? new ProcessStartInfo(exe, "--log-console")
            : new ProcessStartInfo(Path.Combine(folder, @".venv\Scripts\pythonw.exe"), "server.py");
        psi.WorkingDirectory = folder;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var started = Process.Start(psi);
    }

    /// <summary>A server from <paramref name="folder"/> is running, also one that is still starting up and
    /// doesn't listen yet. A run that only prints the settings (--client-info, which the app and connecting
    /// PCs start) is not one.</summary>
    public static bool Running(string folder)
    {
        var exe = Path.Combine(folder, "SSHManagerServer.exe");
        var source = !File.Exists(exe);
        foreach (var name in source ? new[] { "python", "pythonw" } : ["SSHManagerServer"])
        {
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch (Exception) { continue; }
            var pids = new List<int>();
            foreach (var p in found)
            {
                try { pids.Add(p.Id); }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            foreach (var pid in pids)
            {
                var (image, cmd) = PortOwner.Describe(pid);
                if (IsServer(image, cmd, exe, folder, source)) return true;
            }
        }
        return false;
    }

    /// <summary>What <see cref="Running"/> counts as the server, from a process's program and command line.
    /// The command line is empty when Windows won't say: the installed server's program then still counts,
    /// a Python process of a source checkout doesn't (it could be running anything).</summary>
    internal static bool IsServer(string image, string commandLine, string exe, string folder, bool source)
    {
        if (commandLine.Contains("--client-info", StringComparison.OrdinalIgnoreCase)) return false;
        if (!source) return image.Equals(exe, StringComparison.OrdinalIgnoreCase);
        // a source checkout: Python running this folder's server.py (the venv's python starts the real one
        // with the same command line, which names the venv in this folder)
        return commandLine.Contains("server.py", StringComparison.OrdinalIgnoreCase)
            && commandLine.Contains(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The installer is running (an update): it replaces the server's files and starts the server
    /// itself. Found by the installer's mutex, or by its process for a setup that hasn't made the mutex.</summary>
    public static bool SetupRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(@"Global\SSHManagerSetup", out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch (UnauthorizedAccessException) { return true; }   // it is there, though this account may not open it
        catch (Exception) { }
        try
        {
            var found = false;
            foreach (var p in Process.GetProcesses())
            {
                try { found |= p.ProcessName.StartsWith("SSHManager-Setup", StringComparison.OrdinalIgnoreCase); }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            return found;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Asks Windows to run the server's logon task. The exit code of schtasks (0: it runs, or is
    /// running already), or null when there was no answer in time.</summary>
    static async Task<int?> RunLogonTaskAsync(CancellationToken ct)
    {
        try
        {
            using var schtasks = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/run /tn \"SSH Manager\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(15000);
            try { await schtasks.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            return schtasks.ExitCode;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return -1; }   // schtasks couldn't be started
    }
}
