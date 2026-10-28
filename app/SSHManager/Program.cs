using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SSHManager;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Started by ssh as its SSH_ASKPASS helper while the setup page logs in with a password:
        // hand ssh the password the page passed down, nothing else.
        if (Environment.GetEnvironmentVariable("SSHMGR_ASKPASS") is { Length: > 0 } secret)
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(System.Text.Encoding.UTF8.GetBytes(secret + "\n"));
            return;
        }

        // One copy per Windows user, also across sign-ins (console + remote desktop): a second launch in
        // the same session brings the existing window forward; one in another session says where it runs.
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var mutex = new Mutex(true, @"Global\SSHManager.App." + sid, out var first);
        using var sessionMutex = new Mutex(true, @"Local\SSHManager.App", out var firstHere);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SSHManager.Show");
        if (!first)
        {
            if (!firstHere) showSignal.Set();
            else MessageBox.Show("SSH Manager is already running in another Windows sign-in of yours (for example a remote desktop session). " +
                                 "Close it there first.", "SSH Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Nothing the app does should end in an error dialog or a silent exit: log and carry on.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppLog.Write("Unhandled error on the window thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Unhandled error" + (e.IsTerminating ? " (closing)" : ""), e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("Unobserved task error", e.Exception);
            e.SetObserved();
        };

        // Lets Windows start the app again after the installer closed it for an update.
        RegisterApplicationRestart(null, 0);

        ApplicationConfiguration.Initialize();
        var form = new MainForm(AppSettings.Load(), args.Contains("--tray"));
        var listener = new Thread(() =>
        {
            while (showSignal.WaitOne())
                if (form.IsHandleCreated) form.BeginInvoke(form.ShowWindow);
        }) { IsBackground = true };
        listener.Start();
        Application.Run(form);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterApplicationRestart(string? commandLine, int flags);
}
