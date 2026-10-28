using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Api.Data;
using Switchboard.Modules.Api.Webhooks;
using Switchboard.Testing;

namespace Switchboard.Modules.Api.Tests;

public sealed class SsrfGuardTests
{
    private static SsrfGuard Guard(params string[] allowed) => new(Options.Create(new WebhookOptions { AllowedNetworks = [.. allowed] }));

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://127.1.2.3:8080/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://172.16.4.4/hook")]
    [InlineData("https://192.168.1.10/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.100.100.200/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://[::ffff:127.0.0.1]/hook")]
    [InlineData("http://[fd00:ec2::254]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://api.localhost/hook")]
    [InlineData("http://metadata.google.internal/")]
    [InlineData("http://intranet/hook")]
    [InlineData("ftp://93.184.215.14/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:pass@93.184.215.14/hook")]
    [InlineData("not a url")]
    public void Blocks_internal_and_unsafe_targets(string url) => Assert.Throws<SsrfBlockedException>(() => Guard().ValidateUrl(url));

    [Theory]
    [InlineData("https://93.184.215.14/hook")]
    [InlineData("https://hooks.example.com/switchboard")]
    [InlineData("http://[2606:4700:4700::1111]/hook")]
    public void Allows_public_targets(string url) => Guard().ValidateUrl(url);

    [Fact]
    public void Operators_can_allow_an_internal_range_explicitly()
    {
        Assert.Throws<SsrfBlockedException>(() => Guard().ValidateUrl("https://10.20.0.7/hook"));
        Guard("10.20.0.0/16").ValidateUrl("https://10.20.0.7/hook");
        Assert.Throws<SsrfBlockedException>(() => Guard("10.20.0.0/16").ValidateUrl("https://10.21.0.7/hook"));
    }

    [Fact]
    public void Resolved_addresses_are_checked_too()
    {
        var guard = Guard();
        Assert.True(guard.IsBlocked(IPAddress.Parse("10.1.1.1")));
        Assert.True(guard.IsBlocked(IPAddress.Parse("::ffff:10.1.1.1")));
        Assert.True(guard.IsBlocked(IPAddress.Parse("64:ff9b::a01:101")));
        Assert.False(guard.IsBlocked(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public async Task Connect_callback_refuses_names_that_resolve_internally()
    {
        // localhost resolves to loopback: the socket layer refuses even if a URL check were bypassed.
        using var handler = new SocketsHttpHandler { ConnectCallback = Guard().ConnectAsync };
        using var client = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://localhost:9/"));
        Assert.IsType<SsrfBlockedException>(ex.InnerException);
    }
}

public sealed class WebhookSignatureTests
{
    [Fact]
    public void Receivers_can_verify_a_signature_and_reject_tampering_and_replays()
    {
        const string secret = "whsec_test";
        const string body = """{"type":"ticket.created"}""";
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds();
        var signature = WebhookSignature.Sign(secret, ts, body);

        Assert.StartsWith("v1=", signature);
        Assert.True(WebhookSignature.Verify(secret, ts.ToString(), signature, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(secret, ts.ToString(), signature, body + " ", now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify("whsec_other", ts.ToString(), signature, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(secret, (ts + 1).ToString(), signature, body, now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(secret, ts.ToString(), signature, body, now.AddMinutes(10), TimeSpan.FromMinutes(5)));
    }
}

/// <summary>Captures outbound calls instead of sending them.</summary>
public sealed class FakeReceiver : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
        return new HttpResponseMessage(Status) { Content = new StringContent(new string('r', 10_000)) };
    }
}

/// <summary>End to end: ticket event, after-commit handler, queued delivery, signed POST, retries, dead letter, replay.</summary>
public sealed class WebhookDeliveryTests : IAsyncLifetime
{
    private readonly FakeReceiver _receiver = new();
    private SwitchboardTestHost _host = default!;
    private User _admin = default!;
    private User _agent = default!;

    public async Task InitializeAsync()
    {
        _host = await SwitchboardTestHost.CreateAsync([new ApiModule()], services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddHttpClient(WebhookSender.ClientName).ConfigurePrimaryHttpMessageHandler(() => _receiver);
            services.Configure<WebhookOptions>(o => o.MaxAttempts = 2);
        });
        _agent = await _host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        var admin = await _host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        _admin = await _host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == admin.Id);
            user.IsOrgAdmin = true;
            await db.SaveChangesAsync();
            return user;
        });
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<(WebhookSummary Webhook, string Secret)> CreateWebhookAsync(params string[] events) =>
        _host.RunAsync(SwitchboardTestHost.As(_admin), sp => sp.GetRequiredService<WebhookService>()
            .CreateAsync(new WebhookInput("Ops", "https://93.184.215.14/hooks/switchboard", events, _host.Workspace.Id)));

    private Task<Ticket> CreateTicketAsync(string subject) =>
        _host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand { Subject = subject }));

    private async Task DeliverAllAsync()
    {
        var dispatcher = _host.Services.GetRequiredService<WebhookDispatcher>();
        await dispatcher.ProcessDueAsync(default);
    }

    [Fact]
    public async Task Ticket_event_is_delivered_once_signed_with_the_webhook_secret()
    {
        var (hook, secret) = await CreateWebhookAsync("ticket.created");
        var stored = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<ApiWebhook>().AsNoTracking().FirstAsync(h => h.Id == hook.Id));
        Assert.DoesNotContain(secret, stored.ProtectedSecret);

        var ticket = await CreateTicketAsync("Webhook ticket");
        await _host.ProcessOutboxAsync();
        await _host.ProcessOutboxAsync();
        await DeliverAllAsync();

        var call = Assert.Single(_receiver.Calls);
        Assert.Equal("https://93.184.215.14/hooks/switchboard", call.Request.RequestUri!.ToString());
        Assert.Equal("ticket.created", call.Request.Headers.GetValues(WebhookSignature.EventHeader).Single());
        var ts = call.Request.Headers.GetValues(WebhookSignature.TimestampHeader).Single();
        var sig = call.Request.Headers.GetValues(WebhookSignature.SignatureHeader).Single();
        Assert.True(WebhookSignature.Verify(secret, ts, sig, call.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
        Assert.Contains(ticket.Id.ToString(), call.Body);
        Assert.Contains("\"type\":\"ticket.created\"", call.Body);

        var delivery = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<ApiWebhookDelivery>().AsNoTracking().SingleAsync(d => d.WebhookId == hook.Id));
        Assert.Equal(WebhookDeliveryStatus.Succeeded, delivery.Status);
        Assert.True(delivery.ResponseSnippet!.Length <= 4096);

        // A re-run of the same event (at-least-once outbox) doesn't queue it twice.
        await _host.RunAsSystemAsync(async sp =>
        {
            var handler = ActivatorUtilities.CreateInstance<WebhookEventHandler>(sp);
            await handler.HandleAsync(new Core.Events.TicketCreated(ticket.Id, ticket.WorkspaceId, ticket.Type) { EventId = delivery.EventId }, default);
            return true;
        });
        Assert.Equal(1, await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<ApiWebhookDelivery>().CountAsync(d => d.WebhookId == hook.Id)));
    }

    [Fact]
    public async Task Failing_receiver_is_retried_then_dead_lettered_and_can_be_replayed()
    {
        var (hook, _) = await CreateWebhookAsync("ticket.created");
        _receiver.Status = HttpStatusCode.InternalServerError;
        await CreateTicketAsync("Will fail");
        await _host.ProcessOutboxAsync();
        await DeliverAllAsync();

        async Task<ApiWebhookDelivery> Load() => await _host.RunAsSystemAsync(sp =>
            sp.GetRequiredService<SwitchboardDbContext>().Set<ApiWebhookDelivery>().AsNoTracking().SingleAsync(d => d.WebhookId == hook.Id));

        var first = await Load();
        Assert.Equal(WebhookDeliveryStatus.Retrying, first.Status);
        Assert.Equal(500, first.LastStatusCode);
        Assert.True(first.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(20));

        // Due again now: the second (last) attempt fails too and the delivery is dead.
        await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<ApiWebhookDelivery>()
            .Where(d => d.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
        await DeliverAllAsync();
        Assert.Equal(WebhookDeliveryStatus.Dead, (await Load()).Status);

        var dead = await _host.RunAsync(SwitchboardTestHost.As(_admin), sp => sp.GetRequiredService<WebhookService>().DeliveriesAsync(null, WebhookDeliveryStatus.Dead, 50));
        Assert.Contains(dead, d => d.Id == first.Id);

        _receiver.Status = HttpStatusCode.OK;
        await _host.RunAsync(SwitchboardTestHost.As(_admin), sp => sp.GetRequiredService<WebhookService>().ReplayAsync(first.Id));
        await DeliverAllAsync();
        Assert.Equal(WebhookDeliveryStatus.Succeeded, (await Load()).Status);
        var log = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().AuditEntries.AnyAsync(a => a.Action == "webhooks.delivery_replayed"));
        Assert.True(log);
    }

    [Fact]
    public async Task Only_subscribed_events_are_sent()
    {
        var (hook, _) = await CreateWebhookAsync("ticket.status_changed");
        await CreateTicketAsync("Not subscribed");
        await _host.ProcessOutboxAsync();
        Assert.False(await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<ApiWebhookDelivery>().AnyAsync(d => d.WebhookId == hook.Id)));
    }

    [Fact]
    public async Task Webhook_urls_are_ssrf_checked_when_saved_and_when_sent()
    {
        var service = (Func<WebhookInput, Task>)(input => _host.RunAsync(SwitchboardTestHost.As(_admin), sp => sp.GetRequiredService<WebhookService>().CreateAsync(input)));
        var ex = await Assert.ThrowsAsync<ValidationException>(() => service(new WebhookInput("Bad", "http://169.254.169.254/latest", ["ticket.created"], null)));
        Assert.Contains("url", ex.Errors.Keys);

        // A stored URL that turned internal is refused at send time without any request going out.
        var (hook, _) = await CreateWebhookAsync("ticket.created");
        await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<ApiWebhook>()
            .Where(h => h.Id == hook.Id).ExecuteUpdateAsync(s => s.SetProperty(h => h.Url, "http://127.0.0.1:8080/")));
        var before = _receiver.Calls.Count;
        await CreateTicketAsync("Rebound");
        await _host.ProcessOutboxAsync();
        await DeliverAllAsync();
        Assert.Equal(before, _receiver.Calls.Count);
        var delivery = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<ApiWebhookDelivery>().AsNoTracking().SingleAsync(d => d.WebhookId == hook.Id));
        Assert.NotEqual(WebhookDeliveryStatus.Succeeded, delivery.Status);
        Assert.Contains("internal", delivery.LastError);
    }

    [Fact]
    public async Task Workspace_admins_cannot_create_instance_wide_webhooks_and_agents_cannot_manage_them()
    {
        var wsAdmin = await _host.CreateAgentAsync("Wanda Workspace", RoleKeys.WorkspaceAdmin);
        var ex = await Assert.ThrowsAsync<ValidationException>(() => _host.RunAsync(SwitchboardTestHost.As(wsAdmin), sp =>
            sp.GetRequiredService<WebhookService>().CreateAsync(new WebhookInput("All", "https://93.184.215.14/x", ["ticket.created"], null))));
        Assert.Contains("workspaceId", ex.Errors.Keys);

        var (created, _) = await _host.RunAsync(SwitchboardTestHost.As(wsAdmin), sp =>
            sp.GetRequiredService<WebhookService>().CreateAsync(new WebhookInput("Mine", "https://93.184.215.14/x", ["ticket.created"], _host.Workspace.Id)));
        Assert.Equal(_host.Workspace.Id, created.WorkspaceId);

        await Assert.ThrowsAsync<ForbiddenException>(() => _host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<WebhookService>().ListAsync()));
        Assert.True(await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().AuditEntries.AnyAsync(a => a.Action == "webhooks.created" && a.ObjectId == created.Id.ToString())));
    }

    [Fact]
    public async Task Rotating_the_secret_signs_new_deliveries_with_it()
    {
        var (hook, oldSecret) = await CreateWebhookAsync("ticket.created");
        var newSecret = await _host.RunAsync(SwitchboardTestHost.As(_admin), sp => sp.GetRequiredService<WebhookService>().RotateSecretAsync(hook.Id));
        Assert.NotEqual(oldSecret, newSecret);

        var before = _receiver.Calls.Count;
        await _host.RunAsync(SwitchboardTestHost.As(_admin), sp => sp.GetRequiredService<WebhookService>().PingAsync(hook.Id));
        await DeliverAllAsync();
        var call = _receiver.Calls.Skip(before).Single();
        var ts = call.Request.Headers.GetValues(WebhookSignature.TimestampHeader).Single();
        var sig = call.Request.Headers.GetValues(WebhookSignature.SignatureHeader).Single();
        Assert.True(WebhookSignature.Verify(newSecret, ts, sig, call.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(oldSecret, ts, sig, call.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
    }
}
