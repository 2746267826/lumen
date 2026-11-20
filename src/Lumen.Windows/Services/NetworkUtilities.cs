using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Lumen.Windows.Services;

public sealed record LocalAddressInfo(string Address, string Family, string InterfaceName);

public static class NetworkUtilities
{
    public static IReadOnlyList<LocalAddressInfo> GetLocalAddresses()
    {
        var results = new List<LocalAddressInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily is not (
                    AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
                    IPAddress.IsLoopback(address) ||
                    address.Equals(IPAddress.Any) ||
                    address.Equals(IPAddress.IPv6Any))
                {
                    continue;
                }

                if (address.AddressFamily == AddressFamily.InterNetworkV6 &&
                    address.IsIPv6Multicast)
                {
                    continue;
                }

                results.Add(new LocalAddressInfo(
                    address.ToString(),
                    address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6",
                    nic.Name));
            }
        }

        return results
            .DistinctBy(x => x.Address)
            .OrderBy(x => x.Family == "IPv4" ? 0 : 1)
            .ToList();
    }

    public static string FamilyOf(string? address) =>
        string.IsNullOrWhiteSpace(address) ? "" :
        address.Contains(':', StringComparison.Ordinal) ? "IPv6" : "IPv4";

    public static string BuildHttpBase(string address, int port)
    {
        var host = address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address;
        return $"http://{host}:{port}";
    }

    public static string NormalizeIp(IPAddress? address)
    {
        if (address is null)
        {
            return "";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }

    public static int FindAvailablePort(int start, int end)
    {
        for (var port = start; port <= end; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.IPv6Any, port)
                {
                    Server = { DualMode = true }
                };
                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch
                {
                }
            }
        }

        throw new IOException("没有可用的局域网直连端口。");
    }
}
