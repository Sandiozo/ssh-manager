using System.Runtime.InteropServices;

namespace SSHManager;

/// <summary>CPU, memory, disks, battery and uptime of the computer the app runs on. Sent to the PCs it
/// is connected to, so they can show this computer's card with the same details as their own.</summary>
internal static class LocalStats
{
    static long lastIdle, lastTotal;

    public static object Collect(string machineId = "")
    {
        var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref mem);
        var power = SystemInformation.PowerStatus;
        var hasBattery = power.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery && power.BatteryChargeStatus != BatteryChargeStatus.Unknown;
        return new
        {
            machine = Environment.MachineName,
            machine_id = machineId,   // tells two computers with the same name apart
            user = Environment.UserName,
            features = new[] { "pair" },   // this app can approve pairing requests (see Pairing.cs)
            cpu = CpuPercent(),
            ram_total = (long)mem.TotalPhys,
            ram_used = (long)(mem.TotalPhys - mem.AvailPhys),
            disks = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => new { mount = d.Name, total = d.TotalSize, free = d.AvailableFreeSpace }).ToArray(),
            battery = hasBattery ? (int?)Math.Round(power.BatteryLifePercent * 100) : null,
            charging = hasBattery ? (bool?)(power.PowerLineStatus == PowerLineStatus.Online) : null,
            uptime_s = Environment.TickCount64 / 1000,
            app_version = Application.ProductVersion.Split('+')[0],
            macs = Wol.OwnMacs(),
        };
    }

    /// <summary>CPU use since the previous call.</summary>
    static double? CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        long total = kernel + user;   // kernel time includes idle time
        double? pct = null;
        if (lastTotal != 0 && total > lastTotal)
            pct = Math.Round(100.0 * (1 - (double)(idle - lastIdle) / (total - lastTotal)), 1);
        lastIdle = idle;
        lastTotal = total;
        return pct;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
