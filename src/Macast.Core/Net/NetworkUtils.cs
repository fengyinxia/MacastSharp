using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Macast.Core.Settings;

namespace Macast.Core.Net;

public sealed record InterfaceAddress(string Name, string Id, IPAddress Address, IPAddress Mask, bool HasGateway);

public static class NetworkUtils
{
    public static List<InterfaceAddress> GetAddresses(AppSettings settings)
    {
        var blocked = new HashSet<string>(settings.GetStringList("Blocked_Interfaces"), StringComparer.OrdinalIgnoreCase);
        var additional = new HashSet<string>(settings.GetStringList("Additional_Interfaces"), StringComparer.OrdinalIgnoreCase);
        var result = new List<InterfaceAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (blocked.Contains(nic.Name) || blocked.Contains(nic.Id))
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any));

            // Mirrors the Python host: only interfaces with a default gateway are
            // advertised, unless the user listed them in Additional_Interfaces.
            if (!hasGateway && !additional.Contains(nic.Name) && !additional.Contains(nic.Id))
            {
                continue;
            }

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null)
                {
                    continue;
                }

                if (IPAddress.IsLoopback(ua.Address))
                {
                    continue;
                }

                result.Add(new InterfaceAddress(nic.Name, nic.Id, ua.Address, ua.IPv4Mask, hasGateway));
            }
        }

        result = result
            .GroupBy(a => a.Address.ToString())
            .Select(g => g.First())
            .ToList();

        if (result.Count == 0)
        {
            // Useful for loopback-only CI/self-test environments. Discovery will be
            // limited to this machine, but the HTTP and SSDP receive paths still work.
            result.Add(new InterfaceAddress("loopback", "loopback", IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), false));
        }

        return result;
    }

    public static IPAddress GetDefaultIp(IReadOnlyList<InterfaceAddress> addresses)
    {
        return addresses.FirstOrDefault(a => a.HasGateway)?.Address
               ?? addresses.FirstOrDefault()?.Address
               ?? IPAddress.Loopback;
    }

    public static bool SameSubnet(IPAddress left, IPAddress right, IPAddress mask)
    {
        var l = left.GetAddressBytes();
        var r = right.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (l.Length != 4 || r.Length != 4 || m.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if ((l[i] & m[i]) != (r[i] & m[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static string Format(IEnumerable<InterfaceAddress> addresses) =>
        string.Join("/", addresses.Select(a => a.Address.ToString()).Distinct());
}
