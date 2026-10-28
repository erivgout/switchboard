using System.Net;
using Microsoft.Extensions.Options;
using Switchboard.Modules.Automation.Net;

namespace Switchboard.Modules.Automation.Tests;

/// <summary>SR-NET-1: private, loopback, link-local and metadata targets are refused, after DNS and on redirects.</summary>
public sealed class SsrfTests
{
    private static OutboundNetworkPolicy Policy(params string[] allowed) =>
        new(Options.Create(new AutomationOptions { AllowedNetworks = [.. allowed] }));

    private static OutboundRequest Get(string url) => new("GET", url, new Dictionary<string, string>(), null, TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    public void Non_public_addresses_are_blocked(string ip) =>
        Assert.False(Policy().IsAllowed(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("93.184.215.14")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string ip) =>
        Assert.True(Policy().IsAllowed(IPAddress.Parse(ip)));

    [Fact]
    public void Operators_can_allowlist_an_internal_range()
    {
        var policy = Policy("10.20.0.0/16");
        Assert.True(policy.IsAllowed(IPAddress.Parse("10.20.5.5")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("10.21.5.5")));
    }

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://localhost:5000/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("ftp://example.com/file")]
    [InlineData("https://user:pass@example.com/")]
    public async Task Literal_and_local_targets_never_reach_the_network(string url)
    {
        var handler = new FakeHttpHandler();
        var resolver = new FakeResolver();
        resolver.Set("localhost", "127.0.0.1");
        resolver.Set("metadata.google.internal", "169.254.169.254");
        var client = new SafeHttpClient(resolver, Policy(), Options.Create(new AutomationOptions()), handler);
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => client.SendAsync(Get(url), default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Allowlisted_internal_addresses_are_reachable()
    {
        var handler = new FakeHttpHandler();
        var policy = Policy("10.20.0.0/16");
        var client = new SafeHttpClient(new FakeResolver(), policy, Options.Create(new AutomationOptions()), handler);
        var response = await client.SendAsync(Get("http://10.20.5.5/hook"), default);
        Assert.Equal(200, response.StatusCode);
        Assert.Null(OutboundUrlPolicy.CheckStatic("http://10.20.5.5/hook", policy));
        Assert.NotNull(OutboundUrlPolicy.CheckStatic("http://10.20.5.5/hook"));
    }

    [Fact]
    public async Task The_auth_header_never_follows_a_redirect_to_another_origin()
    {
        var handler = new FakeHttpHandler();
        handler.Responses.Enqueue(_ => Redirect("https://hooks.example.com/second"));
        handler.Responses.Enqueue(_ => Redirect("https://elsewhere.example.net/third"));
        var client = new SafeHttpClient(new FakeResolver(), Policy(), Options.Create(new AutomationOptions()), handler);
        var request = new OutboundRequest("GET", "https://hooks.example.com/first", new Dictionary<string, string> { ["X-Api-Key"] = "s3cret" }, null, TimeSpan.FromSeconds(5));
        await client.SendAsync(request, default);
        var auth = handler.Requests.Select(r => r.Auth).ToList();
        Assert.Equal(["s3cret", "s3cret", null], auth);
    }

    [Fact]
    public async Task Hosts_that_resolve_to_private_addresses_are_blocked()
    {
        var resolver = new FakeResolver();
        resolver.Set("intranet.example.com", "10.0.0.7");
        var handler = new FakeHttpHandler();
        var client = new SafeHttpClient(resolver, Policy(), Options.Create(new AutomationOptions()), handler);
        var ex = await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => client.SendAsync(Get("https://intranet.example.com/"), default));
        Assert.Contains("10.0.0.7", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Dns_rebinding_is_caught_when_the_socket_connects()
    {
        // First lookup (validation) says public; the lookup at connect time says loopback.
        var resolver = new FakeResolver();
        resolver.Set("rebind.example.com", "93.184.215.14", "127.0.0.1");
        var client = new SafeHttpClient(resolver, Policy(), Options.Create(new AutomationOptions()));
        var ex = await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => client.SendAsync(Get("http://rebind.example.com/"), default));
        Assert.Contains("127.0.0.1", ex.Message);
        Assert.Equal(2, resolver.Calls);
    }

    [Fact]
    public async Task Redirects_to_metadata_or_private_hosts_are_blocked()
    {
        var resolver = new FakeResolver();
        resolver.Set("evil.example.net", "192.168.0.10");
        var handler = new FakeHttpHandler();
        handler.Responses.Enqueue(_ => Redirect("http://169.254.169.254/latest/meta-data/iam"));
        var client = new SafeHttpClient(resolver, Policy(), Options.Create(new AutomationOptions()), handler);
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => client.SendAsync(Get("https://hooks.example.com/start"), default));
        Assert.Single(handler.Requests);

        handler.Responses.Enqueue(_ => Redirect("http://evil.example.net/"));
        await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => client.SendAsync(Get("https://hooks.example.com/start"), default));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Redirect_chains_are_capped()
    {
        var handler = new FakeHttpHandler();
        for (var i = 0; i < 5; i++)
        {
            handler.Responses.Enqueue(_ => Redirect($"https://hooks.example.com/{Guid.NewGuid():N}"));
        }
        var client = new SafeHttpClient(new FakeResolver(), Policy(), Options.Create(new AutomationOptions { MaxRedirects = 3 }), handler);
        var ex = await Assert.ThrowsAsync<OutboundRequestBlockedException>(() => client.SendAsync(Get("https://hooks.example.com/"), default));
        Assert.Contains("3 redirects", ex.Message);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Public_targets_succeed_and_large_bodies_are_truncated()
    {
        var handler = new FakeHttpHandler();
        handler.Responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 5000)) });
        var client = new SafeHttpClient(new FakeResolver(), Policy(), Options.Create(new AutomationOptions { MaxResponseBytes = 1000 }), handler);
        var response = await client.SendAsync(Get("https://hooks.example.com/"), default);
        Assert.Equal(200, response.StatusCode);
        Assert.True(response.Truncated);
        Assert.Equal(1000, response.Body.Length);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }
}
