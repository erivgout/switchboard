using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Api.Data;

namespace Switchboard.Modules.Api.Webhooks;

internal static class WebhookSecrets
{
    private const string Purpose = "Switchboard.Api.Webhooks.Secret.v1";

    public static string Protect(IDataProtectionProvider provider, string secret) => provider.CreateProtector(Purpose).Protect(secret);

    public static string Unprotect(IDataProtectionProvider provider, string protectedSecret) => provider.CreateProtector(Purpose).Unprotect(protectedSecret);

    public static string Hint(string secret) => "…" + secret[^4..];
}

public sealed record WebhookInput(string? Name, string? Url, IReadOnlyList<string>? Events, Guid? WorkspaceId, bool IsActive = true);

public sealed record WebhookSummary(
    Guid Id,
    string Name,
    string Url,
    IReadOnlyList<string> Events,
    Guid? WorkspaceId,
    string? WorkspaceName,
    bool IsActive,
    string SecretHint,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastDeliveryAt,
    bool? LastDeliverySucceeded,
    int FailingCount);

public sealed record DeliveryRow(
    Guid Id,
    Guid WebhookId,
    string WebhookName,
    string EventType,
    Guid? TicketId,
    WebhookDeliveryStatus Status,
    int Attempts,
    int? LastStatusCode,
    string? LastError,
    string? ResponseSnippet,
    int? DurationMs,
    string PayloadJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset NextAttemptAt);

/// <summary>
/// Webhook administration. A webhook for one workspace needs settings.manage there; a webhook for
/// every workspace needs an organization admin, because it would receive events from all of them.
/// </summary>
public sealed class WebhookService(
    SwitchboardDbContext db,
    IPermissionService permissions,
    IActorAccessor actors,
    IAuditLog audit,
    IDataProtectionProvider protection,
    SsrfGuard guard,
    TimeProvider time)
{
    public const int MaxWebhooks = 50;

    public async Task<IReadOnlyList<WebhookSummary>> ListAsync(CancellationToken ct = default)
    {
        var scope = await ManageableAsync(ct);
        var hooks = await db.Set<ApiWebhook>().AsNoTracking()
            .Where(h => scope == null || (h.WorkspaceId != null && scope.Contains(h.WorkspaceId.Value)))
            .OrderBy(h => h.Name)
            .ToListAsync(ct);
        var ids = hooks.Select(h => h.Id).ToList();
        var failing = await db.Set<ApiWebhookDelivery>().AsNoTracking()
            .Where(d => ids.Contains(d.WebhookId) && (d.Status == WebhookDeliveryStatus.Dead || d.Status == WebhookDeliveryStatus.Retrying))
            .GroupBy(d => d.WebhookId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var workspaces = await db.Workspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        return hooks.Select(h => new WebhookSummary(
            h.Id, h.Name, h.Url, h.Events, h.WorkspaceId, h.WorkspaceId is { } w ? workspaces.GetValueOrDefault(w) : null,
            h.IsActive, h.SecretHint, h.CreatedAt, h.LastDeliveryAt, h.LastDeliverySucceeded, failing.GetValueOrDefault(h.Id))).ToList();
    }

    public async Task<WebhookSummary> GetAsync(Guid id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(h => h.Id == id) ?? throw new NotFoundException();

    /// <summary>Creates a webhook and returns it with its signing secret, shown once.</summary>
    public async Task<(WebhookSummary Webhook, string Secret)> CreateAsync(WebhookInput input, CancellationToken ct = default)
    {
        await DemandAsync(input.WorkspaceId, ct);
        if (await db.Set<ApiWebhook>().CountAsync(ct) >= MaxWebhooks)
        {
            throw new ValidationException("name", $"This instance already has {MaxWebhooks} webhooks. Delete one you no longer use first.");
        }
        var (name, url, events) = await ValidateAsync(input, ct);
        var secret = WebhookSignature.NewSecret();
        var hook = new ApiWebhook
        {
            Name = name,
            Url = url,
            Events = events,
            WorkspaceId = input.WorkspaceId,
            IsActive = input.IsActive,
            ProtectedSecret = WebhookSecrets.Protect(protection, secret),
            SecretHint = WebhookSecrets.Hint(secret),
            CreatedById = actors.Current.UserId,
        };
        db.Set<ApiWebhook>().Add(hook);
        audit.Record("webhooks.created", "webhook", hook.Id.ToString(), new { hook.Name, hook.Url, hook.Events, hook.WorkspaceId, hook.IsActive });
        await db.SaveChangesAsync(ct);
        return (await GetAsync(hook.Id, ct), secret);
    }

    public async Task UpdateAsync(Guid id, WebhookInput input, CancellationToken ct = default)
    {
        var hook = await LoadAsync(id, ct);
        await DemandAsync(input.WorkspaceId, ct);
        var (name, url, events) = await ValidateAsync(input, ct);
        var before = new { hook.Name, hook.Url, hook.Events, hook.WorkspaceId, hook.IsActive };
        hook.Name = name;
        hook.Url = url;
        hook.Events = events;
        hook.WorkspaceId = input.WorkspaceId;
        hook.IsActive = input.IsActive;
        audit.Record("webhooks.updated", "webhook", hook.Id.ToString(), new { before, after = new { hook.Name, hook.Url, hook.Events, hook.WorkspaceId, hook.IsActive } });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var hook = await LoadAsync(id, ct);
        if (hook.IsActive == active)
        {
            return;
        }
        hook.IsActive = active;
        audit.Record(active ? "webhooks.resumed" : "webhooks.paused", "webhook", hook.Id.ToString(), new { hook.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        var hook = await LoadAsync(id, ct);
        var secret = WebhookSignature.NewSecret();
        hook.ProtectedSecret = WebhookSecrets.Protect(protection, secret);
        hook.SecretHint = WebhookSecrets.Hint(secret);
        audit.Record("webhooks.secret_rotated", "webhook", hook.Id.ToString(), new { hook.Name });
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var hook = await LoadAsync(id, ct);
        db.Set<ApiWebhook>().Remove(hook);
        audit.Record("webhooks.deleted", "webhook", hook.Id.ToString(), new { hook.Name, hook.Url });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Queues a signed test delivery right away.</summary>
    public async Task<Guid> PingAsync(Guid id, CancellationToken ct = default)
    {
        var hook = await LoadAsync(id, ct);
        var now = time.GetUtcNow();
        var eventId = Guid.CreateVersion7();
        var delivery = new ApiWebhookDelivery
        {
            WebhookId = hook.Id,
            EventId = eventId,
            EventType = WebhookEvents.Ping,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                id = eventId,
                type = WebhookEvents.Ping,
                occurredAt = now,
                data = new { webhook = new { hook.Id, hook.Name } },
            }, Http.ApiJson.Options),
            NextAttemptAt = now,
            CreatedAt = now,
        };
        db.Set<ApiWebhookDelivery>().Add(delivery);
        await db.SaveChangesAsync(ct);
        return delivery.Id;
    }

    public async Task<IReadOnlyList<DeliveryRow>> DeliveriesAsync(Guid? webhookId, WebhookDeliveryStatus? status, int take, CancellationToken ct = default)
    {
        var scope = await ManageableAsync(ct);
        var query =
            from d in db.Set<ApiWebhookDelivery>().AsNoTracking()
            join h in db.Set<ApiWebhook>().AsNoTracking() on d.WebhookId equals h.Id
            where scope == null || (h.WorkspaceId != null && scope.Contains(h.WorkspaceId.Value))
            select new { d, h.Name };
        if (webhookId is { } wid)
        {
            query = query.Where(x => x.d.WebhookId == wid);
        }
        if (status is { } s)
        {
            query = query.Where(x => x.d.Status == s);
        }
        var rows = await query.OrderByDescending(x => x.d.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return rows.Select(x => new DeliveryRow(
            x.d.Id, x.d.WebhookId, x.Name, x.d.EventType, x.d.TicketId, x.d.Status, x.d.Attempts, x.d.LastStatusCode, x.d.LastError,
            x.d.ResponseSnippet, x.d.DurationMs, x.d.PayloadJson, x.d.CreatedAt, x.d.LastAttemptAt, x.d.NextAttemptAt)).ToList();
    }

    /// <summary>Sends a dead delivery again with a fresh retry budget.</summary>
    public async Task ReplayAsync(Guid deliveryId, CancellationToken ct = default)
    {
        var delivery = await db.Set<ApiWebhookDelivery>().FirstOrDefaultAsync(d => d.Id == deliveryId, ct) ?? throw new NotFoundException();
        var hook = await LoadAsync(delivery.WebhookId, ct);
        if (delivery.Status != WebhookDeliveryStatus.Dead)
        {
            throw new ValidationException("delivery", "Only failed deliveries can be sent again.");
        }
        if (!hook.IsActive)
        {
            throw new ValidationException("delivery", "Resume the webhook before sending deliveries again.");
        }
        delivery.Status = WebhookDeliveryStatus.Pending;
        delivery.Attempts = 0;
        delivery.NextAttemptAt = time.GetUtcNow();
        delivery.CompletedAt = null;
        delivery.LockedUntil = null;
        audit.Record("webhooks.delivery_replayed", "webhook_delivery", delivery.Id.ToString(), new { webhookId = hook.Id, delivery.EventType });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Workspaces whose webhooks the caller can manage; null means all (org admin).</summary>
    public async Task<IReadOnlyList<Guid>?> ManageableAsync(CancellationToken ct = default)
    {
        var p = await permissions.CurrentAsync(ct);
        if (actors.Current.IsInternal)
        {
            return null;
        }
        if (p is null || !await permissions.HasAsync(Privileges.SettingsManage, ct: ct))
        {
            throw new ForbiddenException();
        }
        return p.IsOrgAdmin ? null : p.WorkspacesWith(Privileges.SettingsManage);
    }

    private async Task<ApiWebhook> LoadAsync(Guid id, CancellationToken ct)
    {
        var scope = await ManageableAsync(ct);
        var hook = await db.Set<ApiWebhook>().FirstOrDefaultAsync(h => h.Id == id, ct) ?? throw new NotFoundException();
        if (scope is not null && (hook.WorkspaceId is not { } ws || !scope.Contains(ws)))
        {
            throw new NotFoundException();
        }
        return hook;
    }

    private async Task DemandAsync(Guid? workspaceId, CancellationToken ct)
    {
        var scope = await ManageableAsync(ct);
        if (scope is null)
        {
            return;
        }
        if (workspaceId is not { } ws)
        {
            throw new ValidationException("workspaceId", "Only organization admins can send events from every workspace. Choose a workspace.");
        }
        if (!scope.Contains(ws))
        {
            throw new ForbiddenException("You can't manage settings in that workspace.");
        }
    }

    private async Task<(string Name, string Url, List<string> Events)> ValidateAsync(WebhookInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            errors["name"] = [name.Length == 0 ? "Name the webhook after the system that receives it." : "Keep the name under 200 characters."];
        }
        var url = (input.Url ?? "").Trim();
        try
        {
            var uri = guard.ValidateUrl(url);
            await guard.ValidateResolvedAsync(uri, ct);
            url = uri.ToString();
        }
        catch (SsrfBlockedException ex)
        {
            errors["url"] = [ex.Message];
        }
        var events = (input.Events ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (events.Count == 0)
        {
            errors["events"] = ["Choose at least one event to send."];
        }
        else if (events.FirstOrDefault(e => !WebhookEvents.IsKnown(e)) is { } unknown)
        {
            errors["events"] = [$"Unknown event: {unknown}."];
        }
        if (input.WorkspaceId is { } ws && !await db.Workspaces.AnyAsync(w => w.Id == ws && !w.IsArchived, ct))
        {
            errors["workspaceId"] = ["That workspace doesn't exist."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
        return (name, url, WebhookEvents.All.Select(e => e.Key).Where(events.Contains).ToList());
    }
}
