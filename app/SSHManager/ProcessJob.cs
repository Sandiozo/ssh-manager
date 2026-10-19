using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SSHManager;

/// <summary>Ties child processes (the ssh tunnels) to the app: they are in a job object that Windows
/// closes when the app exits, even when it is killed (e.g. by the installer), so no tunnel is left behind.</summary>
internal static class ProcessJob
{
    static readonly IntPtr Job = Create();

    static IntPtr Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = 0x2000 } };   // KILL_ON_JOB_CLOSE
        SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>());          // ExtendedLimitInformation
        return job;
    }

    public static void Add(Process process)
    {
        try { AssignProcessToJobObject(Job, process.Handle); } catch (Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
