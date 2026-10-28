using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Knowledge.Domain;

namespace Switchboard.Modules.Knowledge.Services;

public sealed class KbAnnouncementInput
{
    public Guid WorkspaceId { get; set; }
    public string Title { get; set; } = "";
    public string? BodyHtml { get; set; }
    public KbAnnouncementAudience Audience { get; set; } = KbAnnouncementAudience.Everyone;
    public IReadOnlyList<Guid> AgentGroupIds { get; set; } = [];
    public IReadOnlyList<Guid> RequesterGroupIds { get; set; } = [];
    public IReadOnlyList<Guid> DepartmentIds { get; set; } = [];
    public DateTimeOffset ActiveFrom { get; set; }
    public DateTimeOffset? ActiveTo { get; set; }
    public bool IsPublished { get; set; }
    public IReadOnlyList<Guid>? AttachmentIds { get; set; }
}

public enum KbAnnouncementState
{
    Draft,
    Scheduled,
    Active,
    Ended,
}

public sealed record KbAnnouncementItem(
    Guid Id,
    Guid WorkspaceId,
    string Title,
    string BodyHtml,
    KbAnnouncementAudience Audience,
    DateTimeOffset ActiveFrom,
    DateTimeOffset? ActiveTo,
    KbAnnouncementState State,
    string? AuthorName,
    int TargetCount);

/// <summary>Announcements for agents and requesters (FR-KB-10).</summary>
public interface IKbAnnouncementService
{
    Task<IReadOnlyList<KbAnnouncementItem>> ListForManagersAsync(CancellationToken ct = default);
    Task<KbAnnouncement> GetForEditAsync(Guid id, CancellationToken ct = default);
    Task<KbAnnouncement> SaveAsync(Guid? id, KbAnnouncementInput input, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>Active announcements for the current user: <paramref name="asAgent"/> picks the agent or requester audience.</summary>
    Task<IReadOnlyList<KbAnnouncementItem>> ActiveAsync(bool asAgent, CancellationToken ct = default);
}

public sealed class KbAnnouncementService(
    SwitchboardDbContext db,
    IKnowledgeVisibility visibility,
    IActorAccessor actors,
    IHtmlContent html,
    IAttachmentService attachments,
    IAuditLog audit,
    TimeProvider time) : IKbAnnouncementService
{
    private DbSet<KbAnnouncement> Announcements => db.Set<KbAnnouncement>();

    public async Task<IReadOnlyList<KbAnnouncementItem>> ListForManagersAsync(CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var workspaces = viewer.IsInternal ? null : viewer.ManageWorkspaces.ToList();
        var rows = await Announcements.AsNoTracking()
            .Include(a => a.Targets)
            .Where(a => workspaces == null || workspaces.Contains(a.WorkspaceId))
            .OrderByDescending(a => a.ActiveFrom)
            .ToListAsync(ct);
        return await ToItemsAsync(rows, ct);
    }

    public async Task<KbAnnouncement> GetForEditAsync(Guid id, CancellationToken ct = default)
    {
        var item = await Announcements.AsNoTracking().Include(a => a.Targets).FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException();
        await DemandAsync(item.WorkspaceId, ct);
        return item;
    }

    public async Task<KbAnnouncement> SaveAsync(Guid? id, KbAnnouncementInput input, CancellationToken ct = default)
    {
        KbAnnouncement item;
        if (id is { } existing)
        {
            item = await Announcements.Include(a => a.Targets).FirstOrDefaultAsync(a => a.Id == existing, ct) ?? throw new NotFoundException();
            await DemandAsync(item.WorkspaceId, ct);
        }
        else
        {
            await DemandAsync(input.WorkspaceId, ct);
            item = new KbAnnouncement { WorkspaceId = input.WorkspaceId, CreatedById = actors.Current.UserId };
            Announcements.Add(item);
        }

        var errors = new Dictionary<string, string[]>();
        var title = (input.Title ?? "").Trim();
        if (title.Length == 0)
        {
            errors["Title"] = ["Give the announcement a title."];
        }
        else if (title.Length > 200)
        {
            errors["Title"] = ["Keep the title under 200 characters."];
        }
        var body = html.Sanitize(input.BodyHtml);
        if (html.ToPlainText(body).Length == 0)
        {
            errors["BodyHtml"] = ["Write what people need to know."];
        }
        if (input.ActiveTo is { } end && end <= input.ActiveFrom)
        {
            errors["ActiveTo"] = ["The end must be after the start."];
        }

        var targets = new List<KbAnnouncementTarget>();
        if (input.Audience == KbAnnouncementAudience.Specific)
        {
            var agentGroups = await db.AgentGroups.Where(g => input.AgentGroupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            var requesterGroups = await db.RequesterGroups.Where(g => input.RequesterGroupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            var departments = await db.Departments.Where(d => input.DepartmentIds.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct);
            targets.AddRange(agentGroups.Select(t => new KbAnnouncementTarget { AnnouncementId = item.Id, Kind = KbAudienceKind.AgentGroup, TargetId = t }));
            targets.AddRange(requesterGroups.Select(t => new KbAnnouncementTarget { AnnouncementId = item.Id, Kind = KbAudienceKind.RequesterGroup, TargetId = t }));
            targets.AddRange(departments.Select(t => new KbAnnouncementTarget { AnnouncementId = item.Id, Kind = KbAudienceKind.Department, TargetId = t }));
            if (targets.Count == 0)
            {
                errors["Audience"] = ["Choose at least one group or department, or pick a different audience."];
            }
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        item.Title = title;
        item.BodyHtml = body;
        item.BodyText = html.ToPlainText(body);
        item.Audience = Enum.IsDefined(input.Audience) ? input.Audience : KbAnnouncementAudience.Everyone;
        item.ActiveFrom = input.ActiveFrom == default ? time.GetUtcNow() : input.ActiveFrom;
        item.ActiveTo = input.ActiveTo;
        item.IsPublished = input.IsPublished;
        item.Targets.Clear();
        item.Targets.AddRange(targets);

        var referenced = KbHtml.ReferencedFileIds(body);
        foreach (var file in await attachments.ClaimPendingAsync(referenced.Concat(input.AttachmentIds ?? []), KbAttachmentOwners.Announcement, item.Id, ct))
        {
            file.IsInline = referenced.Contains(file.Id);
        }
        audit.Record(id is null ? "kb.announcement.created" : "kb.announcement.updated", "kb_announcement", item.Id.ToString(),
            new { item.Title, Audience = item.Audience.ToString(), item.IsPublished, item.ActiveFrom, item.ActiveTo });
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var item = await Announcements.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException();
        await DemandAsync(item.WorkspaceId, ct);
        Announcements.Remove(item);
        audit.Record("kb.announcement.deleted", "kb_announcement", item.Id.ToString(), new { item.Title });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<KbAnnouncementItem>> ActiveAsync(bool asAgent, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var now = time.GetUtcNow();
        var rows = await Announcements.AsNoTracking()
            .Include(a => a.Targets)
            .Where(a => a.IsPublished && a.ActiveFrom <= now && (a.ActiveTo == null || a.ActiveTo > now))
            .OrderByDescending(a => a.ActiveFrom)
            .Take(50)
            .ToListAsync(ct);
        var visible = rows.Where(a => Shows(a, viewer, asAgent)).Take(5).ToList();
        return await ToItemsAsync(visible, ct);
    }

    /// <summary>Signed-out visitors see nothing; announcements are for the organization's people.</summary>
    internal static bool Shows(KbAnnouncement a, KbViewer viewer, bool asAgent)
    {
        if (viewer.IsInternal)
        {
            return true;
        }
        if (!viewer.SignedIn)
        {
            return false;
        }
        var agentHere = asAgent && viewer.IsKbAgent(a.WorkspaceId);
        return a.Audience switch
        {
            KbAnnouncementAudience.Everyone => true,
            KbAnnouncementAudience.Agents => agentHere,
            KbAnnouncementAudience.Requesters => !asAgent,
            KbAnnouncementAudience.Specific => a.Targets.Any(t => t.Kind switch
            {
                KbAudienceKind.AgentGroup => asAgent && viewer.AgentGroupIds.Contains(t.TargetId),
                KbAudienceKind.RequesterGroup => viewer.RequesterGroupIds.Contains(t.TargetId),
                KbAudienceKind.Department => viewer.DepartmentId == t.TargetId,
                _ => false,
            }),
            _ => false,
        };
    }

    private async Task<IReadOnlyList<KbAnnouncementItem>> ToItemsAsync(IReadOnlyList<KbAnnouncement> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.CreatedById).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var now = time.GetUtcNow();
        return rows.Select(a => new KbAnnouncementItem(
                a.Id, a.WorkspaceId, a.Title, a.BodyHtml, a.Audience, a.ActiveFrom, a.ActiveTo,
                !a.IsPublished ? KbAnnouncementState.Draft
                    : a.ActiveFrom > now ? KbAnnouncementState.Scheduled
                    : a.ActiveTo is { } end && end <= now ? KbAnnouncementState.Ended
                    : KbAnnouncementState.Active,
                a.CreatedById is { } c && names.TryGetValue(c, out var n) ? n : null,
                a.Targets.Count))
            .ToList();
    }

    private async Task DemandAsync(Guid workspaceId, CancellationToken ct)
    {
        if (!(await visibility.ViewerAsync(ct)).CanManage(workspaceId))
        {
            throw new ForbiddenException();
        }
    }
}
