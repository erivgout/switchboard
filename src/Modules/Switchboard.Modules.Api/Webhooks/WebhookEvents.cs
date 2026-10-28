using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Data;
using Switchboard.Modules.Api.Http;

namespace Switchboard.Modules.Api.Webhooks;

public sealed record WebhookEventDefinition(string Key, string Title);

public static class WebhookEvents
{
    public const string Ping = "webhook.ping";

    public static IReadOnlyList<WebhookEventDefinition> All { get; } =
    [
        new("ticket.created", "Ticket created"),
        new("ticket.updated", "Ticket fields changed"),
        new("ticket.status_changed", "Status changed"),
        new("ticket.assigned", "Assignment changed"),
        new("ticket.conversation_added", "Reply, note or message added"),
        new("ticket.merged", "Tickets merged"),
        new("ticket.deleted", "Moved to trash or deleted"),
        new("ticket.restored", "Restored from trash"),
        new("ticket.task_changed", "Task added or changed"),
    ];

    public static bool IsKnown(string key) => All.Any(e => e.Key == key);

    public static string Title(string key) => All.FirstOrDefault(e => e.Key == key)?.Title ?? (key == Ping ? "Test delivery" : key);
}

/// <summary>
/// Signs deliveries: <c>X-Switchboard-Signature: v1=hex(HMAC-SHA256(secret, "{timestamp}.{body}"))</c> with the
/// Unix timestamp in <c>X-Switchboard-Timestamp</c>. Receivers recompute it and reject old timestamps (replay).
/// </summary>
public static class WebhookSignature
{
    public const string SignatureHeader = "X-Switchboard-Signature";
    public const string TimestampHeader = "X-Switchboard-Timestamp";
    public const string EventHeader = "X-Switchboard-Event";
    public const string DeliveryHeader = "X-Switchboard-Delivery";

    public static string Sign(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "v1=" + Convert.ToHexStringLower(mac);
    }

    /// <summary>What a receiver does: constant-time compare, and a timestamp within <paramref name="tolerance"/>.</summary>
    public static bool Verify(string secret, string? timestampHeader, string? signatureHeader, string body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (!long.TryParse(timestampHeader, out var timestamp) || signatureHeader is null)
        {
            return false;
        }
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > tolerance.TotalSeconds)
        {
            return false;
        }
        var expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp, body));
        return signatureHeader.Split(',', StringSplitOptions.TrimEntries)
            .Any(candidate => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(candidate)));
    }

    public static string NewSecret() =>
        "whsec_" + RandomNumberGenerator.GetString("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", 40);
}

/// <summary>
/// After-commit handler (at least once): turns ticket events into one queued delivery per matching webhook.
/// The unique (webhook, event) index makes a re-delivered event a no-op. Sending happens in <see cref="WebhookDispatcher"/>.
/// </summary>
internal sealed class WebhookEventHandler(
    SwitchboardDbContext db,
    IOptions<SwitchboardOptions> options,
    WebhookSignal signal,
    TimeProvider time) :
    IEventHandler<TicketCreated>,
    IEventHandler<TicketUpdated>,
    IEventHandler<TicketStatusChanged>,
    IEventHandler<TicketAssigned>,
    IEventHandler<TicketConversationAdded>,
    IEventHandler<TicketsMerged>,
    IEventHandler<TicketDeleted>,
    IEventHandler<TicketRestored>,
    IEventHandler<TicketTaskChanged>
{
    public Task HandleAsync(TicketCreated e, CancellationToken ct) => EnqueueAsync(e, "ticket.created", e.TicketId, new { }, ct);

    public Task HandleAsync(TicketUpdated e, CancellationToken ct) =>
        EnqueueAsync(e, "ticket.updated", e.TicketId, new { changes = e.Changes }, ct);

    public Task HandleAsync(TicketStatusChanged e, CancellationToken ct) =>
        EnqueueAsync(e, "ticket.status_changed", e.TicketId, new { e.FromStatusId, e.ToStatusId, e.FromCategory, e.ToCategory }, ct);

    public Task HandleAsync(TicketAssigned e, CancellationToken ct) =>
        EnqueueAsync(e, "ticket.assigned", e.TicketId, new { e.FromGroupId, e.ToGroupId, e.FromAgentId, e.ToAgentId }, ct);

    // Metadata only: message bodies stay behind the API's permission checks.
    public Task HandleAsync(TicketConversationAdded e, CancellationToken ct) =>
        EnqueueAsync(e, "ticket.conversation_added", e.TicketId, new { conversation = new { id = e.ConversationId, e.Kind, e.IsPrivate, e.ByAgent } }, ct);

    public Task HandleAsync(TicketsMerged e, CancellationToken ct) =>
        EnqueueAsync(e, "ticket.merged", e.PrimaryTicketId, new { secondaryTicketIds = e.SecondaryTicketIds }, ct);

    public Task HandleAsync(TicketDeleted e, CancellationToken ct) =>
        EnqueueAsync(e, "ticket.deleted", e.TicketId, new { e.Permanently }, ct);

    public Task HandleAsync(TicketRestored e, CancellationToken ct) => EnqueueAsync(e, "ticket.restored", e.TicketId, new { }, ct);

    public Task HandleAsync(TicketTaskChanged e, CancellationToken ct) =>
        EnqueueAsync(e, "ticket.task_changed", e.TicketId, new { task = new { id = e.TaskId, e.Status, e.AssigneeId, e.Created } }, ct);

    private async Task EnqueueAsync(DomainEvent e, string type, Guid ticketId, object details, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking()
            .Where(t => t.Id == ticketId)
            .Select(t => new
            {
                t.Id, t.Number, t.Type, t.WorkspaceId, t.Subject, t.StatusId, StatusName = t.Status!.Name, StatusCategory = t.Status.Category,
                t.Priority, t.GroupId, t.AgentId, t.RequesterId, RequesterName = t.Requester!.DisplayName, RequesterEmail = t.Requester.Email,
                t.CreatedAt, t.UpdatedAt,
            })
            .FirstOrDefaultAsync(ct);
        if (ticket is null)
        {
            return;
        }

        var hooks = await db.Set<ApiWebhook>().AsNoTracking()
            .Where(h => h.IsActive && (h.WorkspaceId == null || h.WorkspaceId == ticket.WorkspaceId))
            .Select(h => new { h.Id, h.Events })
            .ToListAsync(ct);
        var targets = hooks.Where(h => h.Events.Contains(type)).Select(h => h.Id).ToList();
        if (targets.Count == 0)
        {
            return;
        }
        var already = await db.Set<ApiWebhookDelivery>()
            .Where(d => d.EventId == e.EventId && targets.Contains(d.WebhookId))
            .Select(d => d.WebhookId)
            .ToListAsync(ct);

        var baseUrl = options.Value.BaseUrl.TrimEnd('/');
        var payload = JsonSerializer.Serialize(new
        {
            id = e.EventId,
            type,
            occurredAt = e.OccurredAt,
            actor = e.Actor is { } a ? new { a.Kind, a.UserId, name = a.DisplayName, a.Channel } : null,
            data = new
            {
                ticket = new
                {
                    ticket.Id,
                    ticket.Number,
                    displayId = Ticket.FormatDisplayId(ticket.Type, ticket.Number),
                    ticket.Type,
                    ticket.WorkspaceId,
                    ticket.Subject,
                    status = new { id = ticket.StatusId, name = ticket.StatusName, category = ticket.StatusCategory },
                    ticket.Priority,
                    ticket.GroupId,
                    ticket.AgentId,
                    requester = new { id = ticket.RequesterId, name = ticket.RequesterName, email = ticket.RequesterEmail },
                    ticket.CreatedAt,
                    ticket.UpdatedAt,
                    url = $"{baseUrl}/tickets/{ticket.Number}",
                    apiUrl = $"{baseUrl}/api/v1/tickets/{ticket.Id}",
                },
                details,
            },
        }, ApiJson.Options);

        var now = time.GetUtcNow();
        foreach (var webhookId in targets.Except(already))
        {
            db.Set<ApiWebhookDelivery>().Add(new ApiWebhookDelivery
            {
                WebhookId = webhookId,
                EventId = e.EventId,
                EventType = type,
                TicketId = ticket.Id,
                PayloadJson = payload,
                NextAttemptAt = now,
                CreatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        signal.Notify();
    }
}
