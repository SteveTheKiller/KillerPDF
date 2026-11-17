using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace KillerPDF.Services;

/// <summary>
/// An HTTP handler that connects only to public internet addresses. A web page can hand
/// KillerPDF a killerpdf://open link, and without this the download could reach a router, a
/// printer or another service on the local network. The check runs on the address actually
/// connected, for every connection including redirects, so DNS that changes its answer between
/// lookups gets nowhere.
/// </summary>
internal static class PublicAddressGuard
{
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = async (context, cancel) =>
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel).ConfigureAwait(false);
            IPAddress? address = addresses.FirstOrDefault(IsPublic)
                ?? throw new HttpRequestException("The download address is not on the public internet.");
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancel).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)      // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                    // link-local
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return false;
        byte first = address.GetAddressBytes()[0];
        return (first & 0xFE) != 0xFC;                              // unique local fc00::/7
    }
}
