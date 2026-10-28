using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Modules.Api.Data;
using Switchboard.Modules.Api.Http;

namespace Switchboard.Modules.Api.Webhooks;

public sealed class WebhookSignal
{
    private readonly System.Threading.Channels.Channel<bool> _channel = System.Threading.Channels.Channel.CreateBounded<bool>(
        new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

public sealed record WebhookSendResult(bool Succeeded, int? StatusCode, string? Error, string? ResponseSnippet, int DurationMs);

/// <summary>Sends one signed POST through the SSRF-guarded client, following at most a few 307/308 redirects.</summary>
public sealed class WebhookSender(IHttpClientFactory clients, SsrfGuard guard, IOptions<WebhookOptions> options, TimeProvider time)
{
    public const string ClientName = "Switchboard.Webhooks";

    public async Task<WebhookSendResult> SendAsync(string url, string secret, string eventType, Guid deliveryId, string body, CancellationToken ct)
    {
        var o = options.Value;
        var watch = Stopwatch.StartNew();
        try
        {
            var target = guard.ValidateUrl(url);
            var client = clients.CreateClient(ClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));

            for (var hop = 0; ; hop++)
            {
                var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
                using var request = new HttpRequestMessage(HttpMethod.Post, target)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Switchboard-Webhooks", "1.0"));
                request.Headers.Add(WebhookSignature.EventHeader, eventType);
                request.Headers.Add(WebhookSignature.DeliveryHeader, deliveryId.ToString());
                request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Sign(secret, timestamp, body));

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect && response.Headers.Location is { } location)
                {
                    if (hop >= o.MaxRedirects)
                    {
                        return new WebhookSendResult(false, status, "Too many redirects.", null, (int)watch.ElapsedMilliseconds);
                    }
                    // Each hop is checked again; the connect callback re-checks the resolved address.
                    target = guard.ValidateUrl(new Uri(target, location).ToString());
                    continue;
                }

                var snippet = await ReadCappedAsync(response, o.MaxResponseBytes, timeout.Token);
                var ok = status is >= 200 and < 300;
                var error = ok ? null : (int)response.StatusCode is >= 300 and < 400 ? "Redirects other than 307 and 308 aren't followed." : $"The receiver answered {status}.";
                return new WebhookSendResult(ok, status, error, snippet, (int)watch.ElapsedMilliseconds);
            }
        }
        catch (SsrfBlockedException ex)
        {
            return new WebhookSendResult(false, null, ex.Message, null, (int)watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SsrfBlockedException blocked)
        {
            return new WebhookSendResult(false, null, blocked.Message, null, (int)watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookSendResult(false, null, $"No answer within {o.TimeoutSeconds} seconds.", null, (int)watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            return new WebhookSendResult(false, null, Truncate($"Couldn't reach the receiver: {ex.Message}", 500), null, (int)watch.ElapsedMilliseconds);
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var total = 0;
        int read;
        while (total < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), ct)) > 0)
        {
            total += read;
        }
        // Stop reading here: nothing past the cap is downloaded.
        return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>
/// Delivers queued webhook calls with exponential backoff; after the last attempt a delivery is dead
/// (the dead-letter list) until an admin replays it. Leases rows so several instances can run.
/// </summary>
internal sealed class WebhookDispatcher(
    IServiceScopeFactory scopes,
    WebhookSignal signal,
    IOptions<WebhookOptions> options,
    TimeProvider time,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private DateTimeOffset _nextCleanup;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await ProcessDueAsync(stoppingToken);
                await CleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Webhook delivery batch failed");
            }
            if (processed < BatchSize)
            {
                await signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            var now = time.GetUtcNow();
            var leaseUntil = now.Add(Lease);
            ids = await db.Database.SqlQuery<Guid>($"""
                UPDATE TOP ({BatchSize}) [ApiWebhookDeliveries] WITH (ROWLOCK, READPAST)
                SET [LockedUntil] = {leaseUntil}
                OUTPUT inserted.[Id] AS [Value]
                WHERE [Status] IN (1, 2) AND [NextAttemptAt] <= {now} AND ([LockedUntil] IS NULL OR [LockedUntil] < {now})
                """).ToListAsync(ct);
        }
        foreach (var id in ids)
        {
            await DeliverAsync(id, ct);
        }
        return ids.Count;
    }

    internal async Task DeliverAsync(Guid deliveryId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        var delivery = await db.Set<ApiWebhookDelivery>().FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null || delivery.Status is WebhookDeliveryStatus.Succeeded or WebhookDeliveryStatus.Dead)
        {
            return;
        }
        var hook = await db.Set<ApiWebhook>().FirstOrDefaultAsync(h => h.Id == delivery.WebhookId, ct);
        var now = time.GetUtcNow();
        var o = options.Value;
        WebhookSendResult result;
        if (hook is null || !hook.IsActive)
        {
            result = new WebhookSendResult(false, null, "The webhook is paused.", null, 0);
            delivery.Attempts = o.MaxAttempts;
        }
        else if (sp.GetRequiredService<IOptions<SwitchboardOptions>>().Value.NonProduction && delivery.EventType != WebhookEvents.Ping)
        {
            // SR-DEP-6: non-production instances don't call real receivers.
            result = new WebhookSendResult(false, null, "Outbound webhooks are off on non-production instances.", null, 0);
            delivery.Attempts = o.MaxAttempts;
        }
        else
        {
            var secret = WebhookSecrets.Unprotect(sp.GetRequiredService<IDataProtectionProvider>(), hook.ProtectedSecret);
            result = await sp.GetRequiredService<WebhookSender>().SendAsync(hook.Url, secret, delivery.EventType, delivery.Id, delivery.PayloadJson, ct);
            delivery.Attempts++;
        }

        delivery.LastAttemptAt = now;
        delivery.LastStatusCode = result.StatusCode;
        delivery.LastError = result.Error is null ? null : WebhookSender.Truncate(result.Error, 2000);
        delivery.ResponseSnippet = result.ResponseSnippet is null ? null : WebhookSender.Truncate(result.ResponseSnippet, 4000);
        delivery.DurationMs = result.DurationMs;
        delivery.LockedUntil = null;
        if (result.Succeeded)
        {
            delivery.Status = WebhookDeliveryStatus.Succeeded;
            delivery.CompletedAt = now;
        }
        else if (delivery.Attempts >= o.MaxAttempts)
        {
            delivery.Status = WebhookDeliveryStatus.Dead;
            delivery.CompletedAt = now;
        }
        else
        {
            delivery.Status = WebhookDeliveryStatus.Retrying;
            delivery.NextAttemptAt = now + Backoff(delivery.Attempts);
        }
        if (hook is not null)
        {
            hook.LastDeliveryAt = now;
            hook.LastDeliverySucceeded = result.Succeeded;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>30s, 1m, 2m, 4m … capped at 6 hours, with up to 20% jitter so retries don't stampede.</summary>
    internal static TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Min(6 * 3600, 30 * Math.Pow(2, Math.Max(0, attempts - 1)));
        return TimeSpan.FromSeconds(seconds * (1 + Random.Shared.NextDouble() * 0.2));
    }

    /// <summary>Hourly: forget idempotency records past 24 hours and delivery logs older than 30 days.</summary>
    private async Task CleanupAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (now < _nextCleanup)
        {
            return;
        }
        _nextCleanup = now.AddHours(1);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        await db.Set<ApiIdempotencyRecord>().Where(r => r.ExpiresAt < now).ExecuteDeleteAsync(ct);
        var cutoff = now.AddDays(-30);
        await db.Set<ApiWebhookDelivery>()
            .Where(d => d.CreatedAt < cutoff && (d.Status == WebhookDeliveryStatus.Succeeded || d.Status == WebhookDeliveryStatus.Dead))
            .ExecuteDeleteAsync(ct);
    }
}
