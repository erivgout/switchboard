using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Messaging.Domain;

namespace Switchboard.Modules.Messaging.Notifications;

public sealed record ResolvedTemplate(NotificationEvent Event, string Subject, string BodyHtml, bool IsEnabled, bool IsCustomized, DateTimeOffset? UpdatedAt);

/// <summary>Per-workspace notification templates (FR-NOT-2). A workspace without a row uses the built-in default.</summary>
public sealed class TemplateService(
    SwitchboardDbContext db,
    IPermissionService permissions,
    IAuditLog audit,
    IActorAccessor actors,
    TimeProvider time)
{
    public async Task<ResolvedTemplate> ResolveAsync(Guid workspaceId, string eventKey, CancellationToken ct = default)
    {
        var definition = NotificationCatalog.Get(eventKey);
        var row = await db.Set<MsgTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.WorkspaceId == workspaceId && t.EventKey == eventKey, ct);
        return row is null
            ? new ResolvedTemplate(definition, definition.DefaultSubject, definition.DefaultBody, definition.EnabledByDefault, false, null)
            : new ResolvedTemplate(definition, row.Subject, row.BodyHtml, row.IsEnabled, row.Subject != definition.DefaultSubject || row.BodyHtml != definition.DefaultBody, row.UpdatedAt);
    }

    public async Task<IReadOnlyList<ResolvedTemplate>> ListAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var rows = await db.Set<MsgTemplate>().AsNoTracking().Where(t => t.WorkspaceId == workspaceId).ToDictionaryAsync(t => t.EventKey, ct);
        return NotificationCatalog.All.Select(e => rows.TryGetValue(e.Key, out var row)
                ? new ResolvedTemplate(e, row.Subject, row.BodyHtml, row.IsEnabled, row.Subject != e.DefaultSubject || row.BodyHtml != e.DefaultBody, row.UpdatedAt)
                : new ResolvedTemplate(e, e.DefaultSubject, e.DefaultBody, e.EnabledByDefault, false, null))
            .ToList();
    }

    public async Task SaveAsync(Guid workspaceId, string eventKey, string subject, string bodyHtml, bool enabled, CancellationToken ct = default)
    {
        await permissions.DemandAsync(MessagingPrivileges.Manage, workspaceId, ct);
        NotificationCatalog.Get(eventKey);
        subject = (subject ?? "").Trim();
        bodyHtml = (bodyHtml ?? "").Trim();
        var errors = new Dictionary<string, string[]>();
        if (subject.Length == 0)
        {
            errors["subject"] = ["Add a subject."];
        }
        else if (subject.Length > TemplateRenderer.MaxSubjectLength)
        {
            errors["subject"] = [$"Keep the subject under {TemplateRenderer.MaxSubjectLength} characters."];
        }
        if (bodyHtml.Length == 0)
        {
            errors["body"] = ["Add a message."];
        }
        else if (bodyHtml.Length > TemplateRenderer.MaxTemplateLength)
        {
            errors["body"] = [$"Keep the message under {TemplateRenderer.MaxTemplateLength:N0} characters."];
        }
        var unknown = TemplateRenderer.UnknownPlaceholders(subject + " " + bodyHtml);
        if (unknown.Count > 0)
        {
            errors["placeholders"] = [$"These placeholders don't exist: {string.Join(", ", unknown.Select(u => "{{" + u + "}}"))}. Pick from the list."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct))
        {
            throw new ValidationException("workspace", "That workspace doesn't exist.");
        }

        var row = await db.Set<MsgTemplate>().FirstOrDefaultAsync(t => t.WorkspaceId == workspaceId && t.EventKey == eventKey, ct);
        if (row is null)
        {
            row = new MsgTemplate { WorkspaceId = workspaceId, EventKey = eventKey };
            db.Add(row);
        }
        var wasEnabled = row.IsEnabled;
        row.Subject = subject;
        row.BodyHtml = bodyHtml;
        row.IsEnabled = enabled;
        row.UpdatedAt = time.GetUtcNow();
        row.UpdatedById = actors.Current.UserId;
        audit.Record("messaging.template.updated", "notification_template", $"{workspaceId}:{eventKey}", new { eventKey, workspaceId, enabled, enabledChanged = wasEnabled != enabled });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(Guid workspaceId, string eventKey, bool enabled, CancellationToken ct = default)
    {
        await permissions.DemandAsync(MessagingPrivileges.Manage, workspaceId, ct);
        var definition = NotificationCatalog.Get(eventKey);
        var row = await db.Set<MsgTemplate>().FirstOrDefaultAsync(t => t.WorkspaceId == workspaceId && t.EventKey == eventKey, ct);
        if (row is null)
        {
            row = new MsgTemplate { WorkspaceId = workspaceId, EventKey = eventKey, Subject = definition.DefaultSubject, BodyHtml = definition.DefaultBody };
            db.Add(row);
        }
        row.IsEnabled = enabled;
        row.UpdatedAt = time.GetUtcNow();
        row.UpdatedById = actors.Current.UserId;
        audit.Record(enabled ? "messaging.template.enabled" : "messaging.template.disabled", "notification_template", $"{workspaceId}:{eventKey}", new { eventKey, workspaceId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Puts the built-in subject and message back; keeps the on/off choice.</summary>
    public async Task ResetAsync(Guid workspaceId, string eventKey, CancellationToken ct = default)
    {
        await permissions.DemandAsync(MessagingPrivileges.Manage, workspaceId, ct);
        var definition = NotificationCatalog.Get(eventKey);
        var row = await db.Set<MsgTemplate>().FirstOrDefaultAsync(t => t.WorkspaceId == workspaceId && t.EventKey == eventKey, ct);
        if (row is null)
        {
            return;
        }
        row.Subject = definition.DefaultSubject;
        row.BodyHtml = definition.DefaultBody;
        row.UpdatedAt = time.GetUtcNow();
        row.UpdatedById = actors.Current.UserId;
        audit.Record("messaging.template.reset", "notification_template", $"{workspaceId}:{eventKey}", new { eventKey, workspaceId });
        await db.SaveChangesAsync(ct);
    }
}
