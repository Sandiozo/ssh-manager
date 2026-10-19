using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SSHManager;

/// <summary>"Search my network": finds computers on the local network(s) that accept SSH (port 22).</summary>
internal static class Discovery
{
    public sealed record Found(string Address, string Name);

    public static async Task<List<Found>> ScanAsync(CancellationToken ct = default)
    {
        var own = new HashSet<string>();
        var targets = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = u.Address.GetAddressBytes();
                if (b[0] == 127 || (b[0] == 169 && b[1] == 254) || u.PrefixLength < 16) continue;
                own.Add(u.Address.ToString());
                for (var i = 1; i < 255; i++) targets.Add($"{b[0]}.{b[1]}.{b[2]}.{i}");   // its /24
            }
        }
        targets.ExceptWith(own);

        var open = new List<string>();
        using var gate = new SemaphoreSlim(96);
        await Task.WhenAll(targets.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(500);
                await client.ConnectAsync(IPAddress.Parse(ip), 22, cts.Token);
                lock (open) open.Add(ip);
            }
            catch (Exception) { }
            finally { gate.Release(); }
        }));

        var found = await Task.WhenAll(open.Select(async ip =>
        {
            try
            {
                var lookup = Dns.GetHostEntryAsync(ip, ct);
                var name = await Task.WhenAny(lookup, Task.Delay(1500, ct)) == lookup ? lookup.Result.HostName.Split('.')[0] : "";
                return new Found(ip, name);
            }
            catch (Exception) { return new Found(ip, ""); }
        }));
        return found.OrderBy(f => f.Name == "").ThenBy(f => f.Name).ThenBy(f => Version.Parse(f.Address)).ToList();
    }
}
