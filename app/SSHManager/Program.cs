namespace SSHManager;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // One copy per user: a second launch just brings the existing window forward.
        using var mutex = new Mutex(true, @"Local\SSHManager.App", out var first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SSHManager.Show");
        if (!first)
        {
            showSignal.Set();
            return;
        }

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
}
