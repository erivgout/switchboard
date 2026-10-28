using System.Net;
using System.Net.Sockets;

namespace Switchboard.Modules.Mcp.OAuth;

/// <summary>
/// SSRF protection for the one outbound request this module makes: fetching Client ID Metadata
/// Documents (SR-NET-1). Private, loopback, link-local, shared and metadata addresses are refused, and
/// the address is checked again after DNS resolution, at connect time, so DNS rebinding can't slip past.
/// </summary>
public static class SafeHttp
{
    public const int MaxDocumentBytes = 64 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>True for addresses an outbound request must never reach.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8 "this network"
                || b[0] == 10                                  // 10.0.0.0/8 private
                || b[0] == 127                                 // loopback
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)  // 100.64.0.0/10 shared (CGNAT)
                || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16 link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12 private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)     // 192.0.0.0/24 IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)     // TEST-NET-1
                || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16 private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // 198.18.0.0/15 benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)  // TEST-NET-2
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)   // TEST-NET-3
                || b[0] >= 224;                                // multicast and reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC                        // fc00::/7 unique local (includes fd00:ec2::254 metadata)
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) // 2001:db8::/32 documentation
                || (b[0] == 0x00 && b.Take(10).All(x => x == 0)) // ::/80, covers ::, ::1 and IPv4-compatible
                || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) // 64:ff9b::/96 NAT64
                || (b[0] == 0x20 && b[1] == 0x02);              // 2002::/16 6to4 (embeds arbitrary IPv4)
        }

        return true;
    }

    /// <summary>
    /// An HttpClient that resolves names itself and connects only to public addresses, follows no
    /// redirects and gives up after <see cref="Timeout"/>.
    /// </summary>
    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, ct);
                var allowed = addresses.Where(a => !IsBlocked(a)).ToList();
                if (allowed.Count == 0 || allowed.Count != addresses.Length)
                {
                    throw new HttpRequestException($"Refusing to connect to {host}: it resolves to a private or reserved address.");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed.ToArray(), context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = Timeout };
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> of the body; throws if it is longer.</summary>
    public static async Task<string> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new HttpRequestException("The document is too large.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new HttpRequestException("The document is too large.");
            }
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}
