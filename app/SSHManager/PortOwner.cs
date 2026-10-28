using System.Net;
using System.Runtime.InteropServices;

namespace SSHManager;

/// <summary>Which processes listen on a local TCP port, and what program they are. Used before the app
/// hands this PC's server its access token: any local user could otherwise listen on the port while the
/// server is down and collect the token.</summary>
internal static class PortOwner
{
    /// <summary>Process ids listening on the port on any IPv4 or IPv6 address.</summary>
    public static HashSet<int> Listeners(int port)
    {
        var pids = new HashSet<int>();
        foreach (var family in new[] { 2, 23 })   // AF_INET, AF_INET6
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);   // TCP_TABLE_OWNER_PID_LISTENER
            if (size <= 0) continue;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, 3, 0) != 0) continue;
                var count = Marshal.ReadInt32(buffer);
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 x 4 bytes)
                // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid (56 bytes)
                var rowSize = family == 2 ? 24 : 56;
                var portOffset = family == 2 ? 8 : 20;
                var pidOffset = family == 2 ? 20 : 52;
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * rowSize;
                    var p = (ushort)IPAddress.NetworkToHostOrder((short)(Marshal.ReadInt32(row + portOffset) & 0xFFFF));
                    if (p == port) pids.Add(Marshal.ReadInt32(row + pidOffset));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return pids;
    }

    /// <summary>Full path of a process's program and its command line (empty when Windows won't say).</summary>
    public static (string image, string commandLine) Describe(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION (works for elevated processes too)
        if (h == IntPtr.Zero) return ("", "");
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var len = sb.Capacity;
            var image = QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : "";
            var cmd = "";
            var size = 0;
            NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, ref size);   // ProcessCommandLineInformation
            if (size > 0)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (NtQueryInformationProcess(h, 60, buf, size, ref size) == 0)
                    {
                        var length = (ushort)Marshal.ReadInt16(buf);   // UNICODE_STRING { Length, MaximumLength, Buffer }
                        var text = Marshal.ReadIntPtr(buf + IntPtr.Size);
                        cmd = Marshal.PtrToStringUni(text, length / 2);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return (image, cmd);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Null when every process listening on the port is SSH Manager's server from <paramref name="folder"/>;
    /// otherwise what is wrong.</summary>
    public static string? CheckServer(int port, string folder, bool nothingIsOk = false)
    {
        var pids = Listeners(port);
        if (pids.Count == 0) return nothingIsOk ? null : "SSH Manager's server isn't running on this PC.";
        var exe = Path.Combine(folder, "SSHManagerServer.exe");
        var source = !File.Exists(exe);
        foreach (var pid in pids)
        {
            var (image, cmd) = Describe(pid);
            var ok = source
                // running from a source checkout: a Python interpreter running server.py
                ? Path.GetFileName(image) is { } n && (n.Equals("python.exe", StringComparison.OrdinalIgnoreCase) || n.Equals("pythonw.exe", StringComparison.OrdinalIgnoreCase))
                  && cmd.Contains("server.py", StringComparison.OrdinalIgnoreCase)
                : image.Equals(exe, StringComparison.OrdinalIgnoreCase);
            if (!ok)
                return $"Another program is using SSH Manager's port {port} on this PC ({(image != "" ? image : $"process {pid}")}). " +
                       "SSH Manager won't send it the server's access token. Close that program, or restart this PC.";
        }
        return null;
    }

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, ref int returned);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
