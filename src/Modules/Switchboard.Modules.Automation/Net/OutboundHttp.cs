using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Switchboard.Modules.Automation.Net;

/// <summary>Options under <c>Switchboard:Automation</c>.</summary>
public sealed class AutomationOptions
{
    public const string Section = "Switchboard:Automation";

    /// <summary>Seconds between automation clock runs (time-based workflows, schedules, queued events). Default 300, or 20 in Development.</summary>
    public int? ClockSeconds { get; set; }

    /// <summary>CIDR ranges an operator explicitly allows web requests to reach, e.g. <c>10.20.0.0/16</c> (SR-NET-1).</summary>
    public List<string> AllowedNetworks { get; set; } = [];

    /// <summary>Largest response body read from a web request.</summary>
    public int MaxResponseBytes { get; set; } = 64 * 1024;

    public int MaxRedirects { get; set; } = 3;
}

/// <summary>A web request target was refused by the outbound network policy.</summary>
public sealed class OutboundRequestBlockedException(string message) : Exception(message);

public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// Denies private, loopback, link-local, cloud-metadata and other non-public addresses unless an
/// operator allowlisted the range (SR-NET-1).
/// </summary>
public sealed class OutboundNetworkPolicy
{
    private static readonly IPNetwork[] Blocked =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
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
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"),
        IPNetwork.Parse("ff00::/8"),
    ];

    private readonly IPNetwork[] _allowed;

    public OutboundNetworkPolicy(IOptions<AutomationOptions> options)
    {
        _allowed = options.Value.AllowedNetworks
            .Select(s => IPNetwork.TryParse(s.Trim(), out var n) ? (IPNetwork?)n : null)
            .OfType<IPNetwork>()
            .ToArray();
    }

    public bool IsAllowed(IPAddress address)
    {
        var ip = Normalize(address);
        if (_allowed.Any(n => n.Contains(ip)))
        {
            return true;
        }
        return !IsBlockedByDefault(ip);
    }

    public static bool IsBlockedByDefault(IPAddress address)
    {
        var ip = Normalize(address);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) embed an IPv4 address; judge that instead.
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes.Skip(4).Take(8).All(b => b == 0))
            {
                return IsBlockedByDefault(new IPAddress(bytes[12..16]));
            }
            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return IsBlockedByDefault(new IPAddress(bytes[2..6]));
            }
            if (ip.IsIPv6SiteLocal || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
            {
                return true;
            }
        }
        return IPAddress.IsLoopback(ip) || Blocked.Any(n => n.BaseAddress.AddressFamily == ip.AddressFamily && n.Contains(ip));
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>Checks that need no network access. Host names are judged after DNS, when the request is sent.</summary>
public static class OutboundUrlPolicy
{
    /// <summary>Returns a problem description, or null when the URL may be attempted.</summary>
    /// <param name="policy">The configured policy (honours operator allowlists); defaults to deny-private.</param>
    public static string? CheckStatic(string url, OutboundNetworkPolicy? policy = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return "enter a full http:// or https:// address.";
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "don't put a user name or password in the address. Use the auth header instead.";
        }
        var host = uri.IdnHost.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip) && !(policy?.IsAllowed(ip) ?? !OutboundNetworkPolicy.IsBlockedByDefault(ip)))
        {
            return "that address points inside your network, which web requests can't reach.";
        }
        return null;
    }
}

public sealed record OutboundRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? JsonBody, TimeSpan Timeout);

public sealed record OutboundResponse(int StatusCode, string? ContentType, string Body, bool Truncated, string FinalUrl);

/// <summary>
/// The only way automations make HTTP calls. Every hop is validated before sending, and the socket
/// connect re-resolves and re-validates the host, then connects to the vetted address (DNS rebinding).
/// Redirects are followed by hand, at most <see cref="AutomationOptions.MaxRedirects"/>, each re-validated.
/// </summary>
public sealed class SafeHttpClient
{
    private readonly IHostResolver _resolver;
    private readonly OutboundNetworkPolicy _policy;
    private readonly AutomationOptions _options;
    private readonly HttpMessageInvoker _invoker;

    public SafeHttpClient(IHostResolver resolver, OutboundNetworkPolicy policy, IOptions<AutomationOptions> options)
        : this(resolver, policy, options, null)
    {
    }

    /// <param name="inner">Test seam; production uses a socket handler that validates at connect time.</param>
    public SafeHttpClient(IHostResolver resolver, OutboundNetworkPolicy policy, IOptions<AutomationOptions> options, HttpMessageHandler? inner)
    {
        _resolver = resolver;
        _policy = policy;
        _options = options.Value;
        _invoker = new HttpMessageInvoker(inner ?? CreateHandler(resolver, policy), disposeHandler: inner is null);
    }

    public static SocketsHttpHandler CreateHandler(IHostResolver resolver, OutboundNetworkPolicy policy) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var endpoint = context.DnsEndPoint;
            var addresses = await ResolveAllowedAsync(resolver, policy, endpoint.Host, ct);
            var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addresses[0], endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public async Task<OutboundResponse> SendAsync(OutboundRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);

        var method = new HttpMethod(request.Method.ToUpperInvariant());
        var body = request.JsonBody;
        var headers = request.Headers;
        var uri = ParseTarget(request.Url);
        for (var hop = 0; ; hop++)
        {
            await ResolveAllowedAsync(_resolver, _policy, uri.IdnHost, timeout.Token);

            using var message = new HttpRequestMessage(method, uri);
            message.Headers.UserAgent.ParseAdd("Switchboard-Automation/1.0");
            foreach (var (name, value) in headers)
            {
                message.Headers.TryAddWithoutValidation(name, value.Replace("\r", "").Replace("\n", ""));
            }
            if (body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                message.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
            }

            HttpResponseMessage response;
            try
            {
                response = await _invoker.SendAsync(message, timeout.Token);
            }
            catch (HttpRequestException ex) when (FindBlocked(ex) is { } blocked)
            {
                throw blocked;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The request took longer than {request.Timeout.TotalSeconds:0} seconds.");
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (hop + 1 > _options.MaxRedirects)
                    {
                        throw new OutboundRequestBlockedException($"Stopped after {_options.MaxRedirects} redirects.");
                    }
                    var next = ParseTarget(new Uri(uri, location).ToString());
                    if (!string.Equals(next.GetLeftPart(UriPartial.Authority), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                    {
                        // Never hand the auth header to another host, or send it over a downgraded connection.
                        headers = new Dictionary<string, string>();
                    }
                    uri = next;
                    if (status == 303 || (status is 301 or 302 && method == HttpMethod.Post))
                    {
                        method = HttpMethod.Get;
                        body = null;
                    }
                    continue;
                }

                var (text, truncated) = await ReadLimitedAsync(response.Content, _options.MaxResponseBytes, timeout.Token);
                return new OutboundResponse(status, response.Content.Headers.ContentType?.ToString(), text, truncated, uri.ToString());
            }
        }
    }

    private Uri ParseTarget(string url)
    {
        var problem = OutboundUrlPolicy.CheckStatic(url, _policy);
        if (problem is not null)
        {
            throw new OutboundRequestBlockedException($"Blocked {SafeDisplay(url)}: {problem}");
        }
        return new Uri(url, UriKind.Absolute);
    }

    private static async Task<IPAddress[]> ResolveAllowedAsync(IHostResolver resolver, OutboundNetworkPolicy policy, string host, CancellationToken ct)
    {
        var trimmed = host.Trim('[', ']');
        IPAddress[] addresses;
        if (IPAddress.TryParse(trimmed, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver.ResolveAsync(trimmed, ct);
            }
            catch (SocketException)
            {
                throw new OutboundRequestBlockedException($"Couldn't find the host {trimmed}.");
            }
        }
        if (addresses.Length == 0)
        {
            throw new OutboundRequestBlockedException($"Couldn't find the host {trimmed}.");
        }
        // Every address must be public: a mixed answer is how rebinding attacks start.
        var denied = addresses.FirstOrDefault(a => !policy.IsAllowed(a));
        if (denied is not null)
        {
            throw new OutboundRequestBlockedException($"Blocked {trimmed}: it resolves to {denied}, which is inside a private or reserved network.");
        }
        return addresses;
    }

    private static OutboundRequestBlockedException? FindBlocked(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OutboundRequestBlockedException blocked)
            {
                return blocked;
            }
        }
        return null;
    }

    private static async Task<(string Text, bool Truncated)> ReadLimitedAsync(HttpContent content, int max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[max + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0)
            {
                break;
            }
            read += n;
        }
        var truncated = read > max;
        return (Encoding.UTF8.GetString(buffer, 0, Math.Min(read, max)), truncated);
    }

    /// <summary>The URL without its query string, for messages and logs.</summary>
    public static string SafeDisplay(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : url.Split('?')[0];
}
