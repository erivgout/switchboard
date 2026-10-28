using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Switchboard.Modules.Api.Webhooks;

public sealed class WebhookOptions
{
    public const string Section = "Switchboard:Webhooks";

    /// <summary>CIDR ranges an operator explicitly allows even though they are private (SR-NET-1), e.g. <c>10.20.0.0/16</c>.</summary>
    public List<string> AllowedNetworks { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 8;
    public int MaxRedirects { get; set; } = 3;
    public int MaxResponseBytes { get; set; } = 4096;
}

public sealed class SsrfBlockedException(string message) : Exception(message);

/// <summary>
/// SSRF protection for outbound webhook calls (SR-NET-1): private, loopback, link-local, metadata and other
/// special-purpose addresses are refused, checked again on the resolved address the socket actually uses.
/// </summary>
public sealed class SsrfGuard(IOptions<WebhookOptions> options)
{
    private static readonly IPNetwork[] Blocked =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),     // carrier-grade NAT (also Alibaba metadata 100.100.100.200)
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),    // link-local, cloud metadata 169.254.169.254
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("64:ff9b::/96"),      // NAT64 can reach IPv4 internals
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001::/32"),         // Teredo
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"),         // 6to4 embeds IPv4
        IPNetwork.Parse("fc00::/7"),          // unique local, incl. AWS IMDS fd00:ec2::254
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"),
        IPNetwork.Parse("ff00::/8"),
    ];

    private static readonly string[] BlockedHostSuffixes = [".localhost", ".local", ".internal", ".intranet", ".lan", ".home.arpa"];

    private IReadOnlyList<IPNetwork>? _allowed;

    private IReadOnlyList<IPNetwork> Allowed => _allowed ??= options.Value.AllowedNetworks
        .Select(n => IPNetwork.TryParse(n.Trim(), out var net) ? net : (IPNetwork?)null)
        .Where(n => n is not null).Select(n => n!.Value).ToList();

    /// <summary>Checks the URL itself: absolute http(s), no credentials, no internal host names or blocked literal IPs.</summary>
    public Uri ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2000 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new SsrfBlockedException("Enter a full URL starting with https://.");
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new SsrfBlockedException("Webhook URLs must use https:// or http://.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new SsrfBlockedException("Don't put a user name or password in the URL. Use the signing secret to authenticate.");
        }
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host == "localhost" || BlockedHostSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)) || !host.Contains('.') && !IPAddress.TryParse(host.Trim('[', ']'), out _))
        {
            throw new SsrfBlockedException("That host is on an internal network. Use a public address.");
        }
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            EnsureAllowed(literal);
        }
        return uri;
    }

    /// <summary>Resolves the host and checks every address. Used when saving a webhook, for an early answer.</summary>
    public async Task ValidateResolvedAsync(Uri uri, CancellationToken ct)
    {
        foreach (var address in await ResolveAsync(uri.IdnHost, ct))
        {
            EnsureAllowed(address);
        }
    }

    public bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (Allowed.Any(n => n.Contains(address)))
        {
            return false;
        }
        return Blocked.Any(n => n.Contains(address));
    }

    public void EnsureAllowed(IPAddress address)
    {
        if (IsBlocked(address))
        {
            throw new SsrfBlockedException("That address is on an internal network. Use a public address.");
        }
    }

    /// <summary>
    /// Socket connect callback: resolves the host, refuses if any address is blocked, and connects to the
    /// checked address itself so a second DNS answer (rebinding) can't swap in an internal one.
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await ResolveAsync(host, ct);
        foreach (var address in addresses)
        {
            EnsureAllowed(address);
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw new HttpRequestException($"Couldn't connect to {host}.", last);
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var trimmed = host.Trim('[', ']');
        if (IPAddress.TryParse(trimmed, out var literal))
        {
            return [literal];
        }
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(trimmed, ct);
        }
        catch (SocketException)
        {
            throw new SsrfBlockedException($"{host} doesn't resolve to an address.");
        }
        return addresses.Length == 0 ? throw new SsrfBlockedException($"{host} doesn't resolve to an address.") : addresses;
    }
}
