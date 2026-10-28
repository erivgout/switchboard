using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.ServiceDesk.Data;

namespace Switchboard.Modules.ServiceDesk.Services;

public sealed record TicketTemplateInput
{
    public Guid WorkspaceId { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public TicketType Type { get; init; } = TicketType.Incident;
    public string Subject { get; init; } = "";
    public string? DescriptionHtml { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? SubCategoryId { get; init; }
    public Guid? ItemCategoryId { get; init; }
    public Priority? Priority { get; init; }
    public Level? Urgency { get; init; }
    public Level? Impact { get; init; }
    public Guid? GroupId { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>Ticket templates (FR-TKT-23): agents pick one to prefill a new ticket; settings managers maintain them.</summary>
public sealed class TicketTemplateService(SwitchboardDbContext db, IPermissionService permissions, IActorAccessor actors, IHtmlContent html, IAuditLog audit)
{
    /// <summary>Active templates for workspaces where the current user can create tickets.</summary>
    public async Task<IReadOnlyList<SdTicketTemplate>> AvailableAsync(Guid workspaceId, TicketType? type = null, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(Privileges.TicketsCreate, workspaceId, ct))
        {
            return [];
        }
        var query = db.Set<SdTicketTemplate>().AsNoTracking().Where(t => t.WorkspaceId == workspaceId && t.IsActive);
        if (type is { } ty)
        {
            query = query.Where(t => t.Type == ty);
        }
        return await query.OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<SdTicketTemplate?> FindAvailableAsync(Guid id, CancellationToken ct = default)
    {
        var template = await db.Set<SdTicketTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.IsActive, ct);
        return template is not null && await permissions.HasAsync(Privileges.TicketsCreate, template.WorkspaceId, ct) ? template : null;
    }

    /// <summary>Every template in workspaces the current user manages.</summary>
    public async Task<IReadOnlyList<SdTicketTemplate>> ManageableAsync(CancellationToken ct = default)
    {
        var workspaces = await ManagedWorkspacesAsync(ct);
        return await db.Set<SdTicketTemplate>().AsNoTracking()
            .Where(t => workspaces.Contains(t.WorkspaceId))
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }

    public async Task<SdTicketTemplate> GetForManageAsync(Guid id, CancellationToken ct = default)
    {
        var template = await db.Set<SdTicketTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException();
        await permissions.DemandAsync(Privileges.SettingsManage, template.WorkspaceId, ct);
        return template;
    }

    public async Task<SdTicketTemplate> SaveAsync(Guid? id, TicketTemplateInput input, CancellationToken ct = default)
    {
        await permissions.DemandAsync(Privileges.SettingsManage, input.WorkspaceId, ct);
        SdTicketTemplate template;
        if (id is { } existingId)
        {
            template = await db.Set<SdTicketTemplate>().FirstOrDefaultAsync(t => t.Id == existingId, ct) ?? throw new NotFoundException();
            await permissions.DemandAsync(Privileges.SettingsManage, template.WorkspaceId, ct);
        }
        else
        {
            template = new SdTicketTemplate { CreatedById = actors.Current.UserId };
            db.Set<SdTicketTemplate>().Add(template);
        }

        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 100)
        {
            throw new ValidationException("Name", name.Length == 0 ? "Name the template." : "Keep the name under 100 characters.");
        }
        var subject = (input.Subject ?? "").Trim();
        if (subject.Length > 500)
        {
            throw new ValidationException("Subject", "Keep the subject under 500 characters.");
        }
        await ValidateReferencesAsync(input, ct);

        template.WorkspaceId = input.WorkspaceId;
        template.Name = name;
        template.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        template.Type = input.Type;
        template.Subject = subject;
        template.DescriptionHtml = html.Sanitize(input.DescriptionHtml);
        template.CategoryId = input.CategoryId;
        template.SubCategoryId = input.CategoryId is null ? null : input.SubCategoryId;
        template.ItemCategoryId = input.SubCategoryId is null ? null : input.ItemCategoryId;
        template.Priority = input.Priority;
        template.Urgency = input.Urgency;
        template.Impact = input.Impact;
        template.GroupId = input.GroupId;
        template.Tags = (input.Tags ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 64).Distinct().Take(50).ToList();
        template.IsActive = input.IsActive;
        audit.Record(id is null ? "servicedesk.template_created" : "servicedesk.template_updated", "ticket_template", template.Id.ToString(), new { template.Name });
        await db.SaveChangesAsync(ct);
        return template;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var template = await db.Set<SdTicketTemplate>().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException();
        await permissions.DemandAsync(Privileges.SettingsManage, template.WorkspaceId, ct);
        db.Set<SdTicketTemplate>().Remove(template);
        audit.Record("servicedesk.template_deleted", "ticket_template", template.Id.ToString(), new { template.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> ManagedWorkspacesAsync(CancellationToken ct = default)
    {
        var p = await permissions.CurrentAsync(ct);
        if (p is null)
        {
            return [];
        }
        var all = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).Select(w => w.Id).ToListAsync(ct);
        return all.Where(w => p.Has(Privileges.SettingsManage, w)).ToList();
    }

    private async Task ValidateReferencesAsync(TicketTemplateInput input, CancellationToken ct)
    {
        var ws = input.WorkspaceId;
        if (input.GroupId is { } g && !await db.AgentGroups.AnyAsync(x => x.Id == g && x.WorkspaceId == ws, ct))
        {
            throw new ValidationException("GroupId", "That group isn't in this workspace.");
        }
        var ids = new[] { input.CategoryId, input.SubCategoryId, input.ItemCategoryId }.OfType<Guid>().ToList();
        if (ids.Count > 0)
        {
            var count = await db.TicketCategories.CountAsync(c => ids.Contains(c.Id) && c.WorkspaceId == ws, ct);
            if (count != ids.Count)
            {
                throw new ValidationException("CategoryId", "That category isn't in this workspace.");
            }
        }
    }
}
