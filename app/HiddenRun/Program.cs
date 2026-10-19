// Runs a program without any window. Scheduled tasks that start `powershell -WindowStyle Hidden`
// still flash a console for a moment; starting it from here with CreateNoWindow does not.
// Usage: HiddenRun.exe <program> [args...]   (exits with the program's exit code)
using System.Diagnostics;

if (args.Length == 0)
    return 2;

var psi = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true };
foreach (var arg in args.Skip(1))
    psi.ArgumentList.Add(arg);

using var process = Process.Start(psi)!;
process.WaitForExit();
return process.ExitCode;
