namespace SSHManager;

/// <summary>Problems the app hit but survived, in %LOCALAPPDATA%\SSHManager\app.log (kept under ~1 MB).</summary>
internal static class AppLog
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(AppSettings.Folder, "app.log");

    public static void Write(string what, Exception? e = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                var f = new FileInfo(FilePath);
                if (f.Exists && f.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}{(e is null ? "" : "\r\n" + e)}\r\n");
            }
        }
        catch (Exception) { }
    }

    /// <summary>Runs a callback (timer tick, event handler) so that a failure is logged, never shown as a crash.</summary>
    public static void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Write(what + " failed", e); }
    }

    public static async Task GuardAsync(string what, Func<Task> a)
    {
        try { await a(); }
        catch (Exception e) { Write(what + " failed", e); }
    }
}
