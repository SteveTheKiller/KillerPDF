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
        // Through a proxy the callback would only see the proxy's address, and the proxy would
        // fetch any host for us, so the connection always goes direct.
        UseProxy = false,
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
                || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2))   // IETF and TEST-NET-1
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)      // TEST-NET-2
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)       // TEST-NET-3
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));   // benchmarking
        }
        // NAT64, 6to4 and IPv4-compatible addresses carry an IPv4 address that a translator or relay
        // reaches, so the embedded address decides.
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && EmbeddedIPv4(address) is { } embedded)
            return IsPublic(embedded);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return false;
        byte first = address.GetAddressBytes()[0];
        return (first & 0xFE) != 0xFC;                              // unique local fc00::/7
    }

    private static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        // 64:ff9b::/96 well-known NAT64 prefix: the last four bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            return new IPAddress(b.AsSpan(12, 4));
        // 64:ff9b:1::/48 local-use NAT64 prefix: the embedding depends on the operator's prefix
        // length, so any address in it is refused through an unroutable stand-in.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01)
            return IPAddress.Any;
        // 2002::/16 6to4: bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02)
            return new IPAddress(b.AsSpan(2, 4));
        // ::a.b.c.d IPv4-compatible (deprecated); :: and ::1 are handled by the callers' other checks.
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && !address.Equals(IPAddress.IPv6Any)
            && !address.Equals(IPAddress.IPv6Loopback))
            return new IPAddress(b.AsSpan(12, 4));
        return null;
    }
}
