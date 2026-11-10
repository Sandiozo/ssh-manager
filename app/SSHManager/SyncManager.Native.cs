using System.Runtime.InteropServices;

namespace SSHManager;

/// <summary>The parts of folder sync that reach Windows itself: the Recycle Bin and the question asked in the
/// app's own window.</summary>
internal sealed partial class SyncManager
{
    // ---- Recycle Bin ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public bool Aborted;
        public IntPtr NameMappings;
        public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    static extern int SHFileOperation(ref ShFileOp op);

    static bool Recycle(string path)
    {
        // FO_DELETE with FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR
        var op = new ShFileOp { Func = 3, From = path + "\0", Flags = 0x40 | 0x10 | 0x4 | 0x400 | 0x200 };
        return SHFileOperation(ref op) == 0 && !op.Aborted && !File.Exists(path);
    }

    // ---- asking the user ----

    /// <summary>A Yes/No question in a box of the app's main window, No by default. Only a person at this
    /// computer can answer it: nothing a web page sends can. False when there is no window to ask in.</summary>
    static Task<bool> AskAsync(string title, string text)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var owner = Application.OpenForms.OfType<MainForm>().FirstOrDefault(f => !f.IsDisposed && f.IsHandleCreated);
            if (owner is null) answer.SetResult(false);
            else
                owner.BeginInvoke(() =>
                {
                    try
                    {
                        answer.TrySetResult(MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2) == DialogResult.Yes);
                    }
                    catch (Exception e)
                    {
                        AppLog.Write("Folder sync question", e);
                        answer.TrySetResult(false);
                    }
                });
        }
        catch (Exception e)
        {
            AppLog.Write("Folder sync question", e);
            answer.TrySetResult(false);
        }
        return answer.Task;
    }
}
