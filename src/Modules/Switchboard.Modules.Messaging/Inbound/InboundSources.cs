using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Outbound;

namespace Switchboard.Modules.Messaging.Inbound;

/// <summary>
/// <c>POST /email/inbound/{mailboxId}</c>: a mail provider or relay posts one raw RFC 5322 message.
/// Authenticated by an HMAC of the timestamp and body with the mailbox's secret, compared in
/// constant time; requests outside a five-minute window are rejected as replays (SR-NET-5).
/// </summary>
/// <remarks>
/// Headers: <c>X-Switchboard-Timestamp</c> (Unix seconds) and <c>X-Switchboard-Signature: sha256=&lt;hex&gt;</c>
/// where the hex is HMAC-SHA256(secret, "&lt;timestamp&gt;." + body).
/// </remarks>
public static class InboundWebhook
{
    public const string TimestampHeader = "X-Switchboard-Timestamp";
    public const string SignatureHeader = "X-Switchboard-Signature";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/email/inbound/{mailboxId:guid}", HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("MessagingInboundWebhook");
    }

    private static async Task<IResult> HandleAsync(Guid mailboxId, HttpContext http, SwitchboardDbContext db, MessagingSecrets secrets,
        InboundPipeline pipeline, TimeProvider time, CancellationToken ct)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = InboundPipeline.MaxMessageBytes;
        }

        var mailbox = await db.Set<MsgMailbox>().AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mailboxId && m.IsEnabled && m.Connection == MailboxConnection.Webhook, ct);
        var secret = secrets.Unprotect(mailbox?.WebhookSecretProtected);
        if (mailbox is null || secret is null)
        {
            return Results.NotFound();
        }

        if (http.Request.ContentLength > InboundPipeline.MaxMessageBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }
        catch (BadHttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!Verify(secret, http.Request.Headers[TimestampHeader], http.Request.Headers[SignatureHeader], body, time.GetUtcNow()))
        {
            return Results.Unauthorized();
        }

        var record = await pipeline.ProcessAsync(new InboundRequest(mailbox, body, InboundSource.Webhook), ct);
        return Results.Ok(new { id = record.Id, outcome = record.Outcome.ToString() });
    }

    public static bool Verify(string secret, string? timestamp, string? signature, byte[] body, DateTimeOffset now)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || signature is null || !signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        DateTimeOffset sent;
        try
        {
            sent = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        if ((now - sent).Duration() > Window)
        {
            return false;
        }
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signature["sha256=".Length..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = Sign(secret, timestamp!, body);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    public static byte[] Sign(string secret, string timestamp, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
        hmac.TransformFinalBlock(body, 0, body.Length);
        return hmac.Hash!;
    }
}

/// <summary>Polls IMAP mailboxes for unread mail. Several instances can run: each mailbox is leased while polled.</summary>
public sealed class ImapPoller(IServiceScopeFactory scopes, TimeProvider time, ILogger<ImapPoller> logger) : BackgroundService
{
    private const int BatchSize = 25;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollDueAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "IMAP polling failed");
                }
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task PollDueAsync(CancellationToken ct)
    {
        List<MsgMailbox> due;
        var now = time.GetUtcNow();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            var candidates = await db.Set<MsgMailbox>().AsNoTracking()
                .Where(m => m.IsEnabled && m.Connection == MailboxConnection.Imap)
                .ToListAsync(ct);
            due = candidates.Where(m => m.LastCheckedAt is null || m.LastCheckedAt.Value.AddSeconds(m.PollIntervalSeconds) <= now).ToList();
        }

        foreach (var mailbox in due)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            var leased = await db.Set<MsgMailbox>()
                .Where(m => m.Id == mailbox.Id && (m.LockedUntil == null || m.LockedUntil < now))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntil, now.Add(Lease)), ct);
            if (leased == 0)
            {
                continue;
            }

            string? error = null;
            try
            {
                await PollAsync(mailbox, scope.ServiceProvider, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
                logger.LogWarning("IMAP mailbox {Mailbox} failed: {Error}", mailbox.Address, ex.Message);
            }
            var checkedAt = time.GetUtcNow();
            await db.Set<MsgMailbox>().Where(m => m.Id == mailbox.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LastCheckedAt, checkedAt)
                    .SetProperty(m => m.LastError, error), ct);
        }
    }

    private async Task PollAsync(MsgMailbox mailbox, IServiceProvider services, CancellationToken ct)
    {
        var secrets = services.GetRequiredService<MessagingSecrets>();
        using var client = new ImapClient { Timeout = 60_000 };
        await client.ConnectAsync(mailbox.ImapHost ?? throw new InvalidOperationException("No IMAP server is set."), mailbox.ImapPort, SmtpMailTransport.ToSocketOptions(mailbox.ImapSecurity), ct);
        await client.AuthenticateAsync(mailbox.ImapUsername ?? "", secrets.Unprotect(mailbox.ImapPasswordProtected) ?? "", ct);
        var folder = string.Equals(mailbox.ImapFolder, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(mailbox.ImapFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        var unread = await folder.SearchAsync(SearchQuery.NotSeen, ct);

        foreach (var uid in unread.Take(BatchSize))
        {
            var message = await folder.GetMessageAsync(uid, ct);
            using var buffer = new MemoryStream();
            await message.WriteToAsync(buffer, ct);

            // Each message in its own scope, so one bad message can't poison the next.
            await using (var messageScope = scopes.CreateAsyncScope())
            {
                var pipeline = messageScope.ServiceProvider.GetRequiredService<InboundPipeline>();
                await pipeline.ProcessAsync(new InboundRequest(mailbox, buffer.ToArray(), InboundSource.Imap), ct);
            }
            // The pipeline records every outcome (and keeps failed mail), so the message is always marked read.
            await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
        }
        await client.DisconnectAsync(true, ct);
    }
}
