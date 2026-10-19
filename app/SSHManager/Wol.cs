using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SSHManager;

/// <summary>Wake-on-LAN "magic packet" for a PC on the same network, and this computer's own MACs.</summary>
internal static class Wol
{
    public static void Send(string mac, IEnumerable<string> ips)
    {
        var bytes = Convert.FromHexString(new string(mac.Where(Uri.IsHexDigit).ToArray()));
        if (bytes.Length != 6) throw new ArgumentException($"\"{mac}\" is not a MAC address.");
        var packet = Enumerable.Repeat((byte)0xFF, 6).Concat(Enumerable.Repeat(bytes, 16).SelectMany(b => b)).ToArray();
        var targets = new HashSet<string> { "255.255.255.255" };
        foreach (var ip in ips)
        {
            var parts = ip.Split('.');
            if (parts.Length != 4) continue;
            targets.Add(ip);
            targets.Add($"{parts[0]}.{parts[1]}.{parts[2]}.255");
        }
        using var udp = new UdpClient { EnableBroadcast = true };
        foreach (var t in targets)
            foreach (var port in new[] { 9, 7 })
                try { udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Parse(t), port)); } catch (Exception) { }
    }

    static readonly string[] Virtual = ["virtual", "vpn", "tap-", "tap ", "hyper-v", "wireguard", "wintun", "bluetooth", "loopback", "npcap", "vmware", "virtualbox"];

    /// <summary>MACs of this computer's real network cards that are up (wired first).</summary>
    public static List<string> OwnMacs() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                        && !Virtual.Any(v => n.Description.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .Select(n => string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))))
            .Where(m => m.Length == 17)
            .ToList();
}
