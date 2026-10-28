using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;

namespace Switchboard.Modules.ServiceDesk.Services;

public sealed record TicketSummary(Guid Id, int Number, TicketType Type, string Subject, string StatusName, StatusCategory StatusCategory, string? AgentName, DateTimeOffset CreatedAt)
{
    public string DisplayId => Ticket.FormatDisplayId(Type, Number);
    public bool IsResolved => StatusCategory is StatusCategory.Resolved or StatusCategory.Closed;
}

public sealed record ThreadEntry(TicketConversation Conversation, string? AuthorName, IReadOnlyList<Attachment> Attachments);

public sealed record LinkEntry(Guid LinkId, string LinkType, string TargetType, Guid TargetId, bool Incoming, TicketSummary? Ticket, DateTimeOffset CreatedAt);

public sealed record ActivityItem(ActivityEntry Entry, IReadOnlyList<FieldChange> Changes);

public sealed record TaskItem(TicketTask Task, string? AssigneeName, string? GroupName);

/// <summary>What the current agent may do on this ticket.</summary>
public sealed record TicketAbilities(
    bool Edit, bool Assign, bool Reply, bool Note, bool Merge, bool Delete, bool DeletePermanently, bool MajorIncident, bool Create)
{
    public static TicketAbilities For(UserPermissions? p, Guid workspaceId) => p is null
        ? new(false, false, false, false, false, false, false, false, false)
        : new(
            p.Has(Privileges.TicketsEdit, workspaceId),
            p.Has(Privileges.TicketsAssign, workspaceId),
            p.Has(Privileges.TicketsReply, workspaceId),
            p.Has(Privileges.TicketsNote, workspaceId),
            p.Has(Privileges.TicketsMerge, workspaceId),
            p.Has(Privileges.TicketsDelete, workspaceId),
            p.Has(Privileges.TicketsDeletePermanently, workspaceId),
            p.Has(ServiceDeskModule.MajorIncidentPrivilege, workspaceId),
            p.Has(Privileges.TicketsCreate, workspaceId));
}

/// <summary>Everything the agent ticket page renders.</summary>
public sealed class TicketPageData
{
    public required Ticket Ticket { get; init; }
    public required WorkspaceOptions Options { get; init; }
    public required TicketAbilities Can { get; init; }
    public required Guid MeId { get; init; }
    public required IReadOnlyList<ThreadEntry> Thread { get; init; }
    public required IReadOnlyList<Attachment> TicketAttachments { get; init; }
    public required IReadOnlyList<TaskItem> Tasks { get; init; }
    public required IReadOnlyList<LinkEntry> Links { get; init; }
    public required TicketSummary? Parent { get; init; }
    public required IReadOnlyList<TicketSummary> Children { get; init; }
    public required IReadOnlyList<TicketSummary> MergedTickets { get; init; }
    public required TicketSummary? MergedInto { get; init; }
    public required IReadOnlyList<ActivityItem> Activity { get; init; }
    public required IReadOnlyList<TicketSummary> RequesterRecent { get; init; }
    public required JsonObject CustomValues { get; init; }
    public required int TrashRetentionDays { get; init; }
    public required DateTimeOffset LoadedAt { get; init; }

    public string Version => Convert.ToBase64String(Ticket.RowVersion);
    public bool IsReadOnly => Ticket.DeletedAt is not null || Ticket.MergedIntoId is not null;
    public bool IsChild => Ticket.ParentId is not null;
    public int RelatedCount => Links.Count + Children.Count + MergedTickets.Count + (Parent is null ? 0 : 1) + (MergedInto is null ? 0 : 1);

    /// <summary>Timestamp of the newest visible message, for the "someone replied while you were writing" check.</summary>
    public DateTimeOffset? LatestMessageAt =>
        Thread.Where(t => t.Conversation.DeletedAt is null && t.Conversation.Kind != ConversationKind.System)
            .Select(t => (DateTimeOffset?)t.Conversation.CreatedAt).Max();
}

/// <summary>Loads a ticket for the agent ticket page, trimmed to what the current agent can see.</summary>
public sealed class TicketPageLoader(
    SwitchboardDbContext db,
    IPermissionService permissions,
    IActorAccessor actors,
    TicketFormOptions formOptions,
    ISettingsService settings,
    TimeProvider time)
{
    /// <summary>The ticket with this number, if the current agent can see it; null otherwise.</summary>
    public async Task<Ticket?> FindAsync(int number, CancellationToken ct = default)
    {
        var visible = await permissions.AgentVisibleTicketsAsync(ct);
        return await visible.AsNoTracking()
            .Include(t => t.Status)
            .Include(t => t.Source)
            .Include(t => t.Group)
            .Include(t => t.Agent)
            .Include(t => t.Requester!).ThenInclude(r => r.Department)
            .Include(t => t.RequestedFor)
            .Include(t => t.Department)
            .Include(t => t.Tags)
            .FirstOrDefaultAsync(t => t.Number == number && t.PurgedAt == null, ct);
    }

    public async Task<TicketPageData> LoadAsync(int number, CancellationToken ct = default)
    {
        var ticket = await FindAsync(number, ct) ?? throw new NotFoundException();
        var me = actors.Current.UserId ?? throw new ForbiddenException();
        var p = await permissions.CurrentAsync(ct);
        var options = await formOptions.LoadAsync(ticket.WorkspaceId, ct);
        var visible = (await permissions.AgentVisibleTicketsAsync(ct)).AsNoTracking();

        // Conversation and attachments.
        var conversations = await db.TicketConversations.AsNoTracking()
            .Where(c => c.TicketId == ticket.Id)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { Conversation = c, AuthorName = c.Author != null ? c.Author.DisplayName : null })
            .ToListAsync(ct);
        var conversationIds = conversations.Select(c => c.Conversation.Id).ToList();
        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => (a.OwnerType == AttachmentOwners.Conversation && a.OwnerId != null && conversationIds.Contains(a.OwnerId.Value))
                || (a.OwnerType == AttachmentOwners.Ticket && a.OwnerId == ticket.Id))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
        var byOwner = attachments.Where(a => a.OwnerType == AttachmentOwners.Conversation).ToLookup(a => a.OwnerId!.Value);
        var thread = conversations
            .Where(c => c.Conversation.DeletedAt is null || c.Conversation.Kind != ConversationKind.Note)
            .Select(c => new ThreadEntry(c.Conversation, c.AuthorName, c.Conversation.DeletedAt is null ? byOwner[c.Conversation.Id].ToList() : []))
            .ToList();

        // Tasks.
        var tasks = await db.TicketTasks.AsNoTracking()
            .Where(t => t.TicketId == ticket.Id)
            .OrderBy(t => t.Order)
            .Select(t => new
            {
                Task = t,
                Assignee = t.Assignee != null ? t.Assignee.DisplayName : null,
                Group = db.AgentGroups.Where(g => g.Id == t.GroupId).Select(g => g.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);

        // Relationships.
        var links = await db.TicketLinks.AsNoTracking()
            .Where(l => l.TicketId == ticket.Id || (l.TargetType == Ticket.Family && l.TargetId == ticket.Id))
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(ct);
        var relatedIds = links.Select(l => l.TicketId == ticket.Id ? l.TargetId : l.TicketId).ToList();
        if (ticket.ParentId is { } parentId)
        {
            relatedIds.Add(parentId);
        }
        if (ticket.MergedIntoId is { } mergedIntoId)
        {
            relatedIds.Add(mergedIntoId);
        }
        var summaries = await Summaries(visible.Where(t => relatedIds.Contains(t.Id))).ToDictionaryAsync(s => s.Id, ct);
        var children = await Summaries(visible.Where(t => t.ParentId == ticket.Id && t.DeletedAt == null && t.PurgedAt == null).OrderBy(t => t.Number)).ToListAsync(ct);
        var merged = await Summaries(visible.Where(t => t.MergedIntoId == ticket.Id).OrderBy(t => t.Number)).ToListAsync(ct);
        var linkEntries = links.Select(l =>
        {
            var incoming = l.TicketId != ticket.Id;
            var otherId = incoming ? l.TicketId : l.TargetId;
            var isTicket = incoming || l.TargetType == Ticket.Family;
            return new LinkEntry(l.Id, l.LinkType, isTicket ? Ticket.Family : l.TargetType, otherId, incoming,
                isTicket ? summaries.GetValueOrDefault(otherId) : null, l.CreatedAt);
        }).ToList();

        // Activity (INV-HIS-1), newest first.
        var activity = await db.ActivityEntries.AsNoTracking()
            .Where(a => a.SubjectType == Ticket.Family && a.SubjectId == ticket.Id)
            .OrderByDescending(a => a.CreatedAt)
            .Take(300)
            .ToListAsync(ct);

        // Requester context (FR-TKT-27).
        var recent = await Summaries(visible
                .Where(t => t.RequesterId == ticket.RequesterId && t.Id != ticket.Id && t.DeletedAt == null && !t.IsSpam && t.PurgedAt == null)
                .OrderByDescending(t => t.CreatedAt).Take(5))
            .ToListAsync(ct);

        return new TicketPageData
        {
            Ticket = ticket,
            Options = options,
            Can = TicketAbilities.For(p, ticket.WorkspaceId),
            MeId = me,
            Thread = thread,
            TicketAttachments = attachments.Where(a => a.OwnerType == AttachmentOwners.Ticket).ToList(),
            Tasks = tasks.Select(t => new TaskItem(t.Task, t.Assignee, t.Group)).ToList(),
            Links = linkEntries,
            Parent = ticket.ParentId is { } pid ? summaries.GetValueOrDefault(pid) : null,
            Children = children,
            MergedTickets = merged,
            MergedInto = ticket.MergedIntoId is { } mid ? summaries.GetValueOrDefault(mid) : null,
            Activity = activity.Select(a => new ActivityItem(a, ParseChanges(a.ChangesJson))).ToList(),
            RequesterRecent = recent,
            CustomValues = CustomFieldService.Parse(ticket.CustomFieldsJson),
            TrashRetentionDays = await settings.GetAsync(SettingKeys.TrashRetentionDays, 30, ticket.WorkspaceId, ct),
            LoadedAt = time.GetUtcNow(),
        };
    }

    /// <summary>Tickets matching a number or subject, for the merge and link dialogs.</summary>
    public async Task<IReadOnlyList<TicketSummary>> FindCandidatesAsync(Ticket current, string? q, CancellationToken ct = default)
    {
        var term = (q ?? "").Trim();
        if (term.Length == 0)
        {
            return [];
        }
        var visible = (await permissions.AgentVisibleTicketsAsync(ct)).AsNoTracking()
            .Where(t => t.Id != current.Id && t.WorkspaceId == current.WorkspaceId && t.DeletedAt == null && t.PurgedAt == null
                && t.MergedIntoId == null && !t.IsSpam);
        if (TicketReference.TryParse(term, out var n) || int.TryParse(term, out n))
        {
            visible = visible.Where(t => t.Number == n);
        }
        else
        {
            if (term.Length > 200)
            {
                term = term[..200];
            }
            visible = visible.Where(t => t.Subject.Contains(term));
        }
        return await Summaries(visible.OrderByDescending(t => t.CreatedAt).Take(10)).ToListAsync(ct);
    }

    /// <summary>Plain-language note on who changed the ticket since <paramref name="since"/>, for conflict messages.</summary>
    public async Task<string> DescribeChangesSinceAsync(Guid ticketId, DateTimeOffset since, CancellationToken ct = default)
    {
        var me = actors.Current.UserId;
        var entries = await db.ActivityEntries.AsNoTracking()
            .Where(a => a.SubjectType == Ticket.Family && a.SubjectId == ticketId && a.CreatedAt > since && a.ActorUserId != me)
            .OrderByDescending(a => a.CreatedAt)
            .Take(5)
            .Select(a => new { a.ActorName, a.Summary })
            .ToListAsync(ct);
        if (entries.Count == 0)
        {
            return "Someone else changed this ticket. Reload to see the update.";
        }
        var latest = entries[0];
        var summary = latest.Summary.Length > 0 ? char.ToLowerInvariant(latest.Summary[0]) + latest.Summary[1..] : "changed it";
        return $"{latest.ActorName} {summary.TrimEnd('.')}. Reload to see the update.";
    }

    internal static IQueryable<TicketSummary> Summaries(IQueryable<Ticket> query) =>
        query.Select(t => new TicketSummary(t.Id, t.Number, t.Type, t.Subject, t.Status!.Name, t.Status.Category,
            t.Agent != null ? t.Agent.DisplayName : null, t.CreatedAt));

    private static IReadOnlyList<FieldChange> ParseChanges(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<FieldChange>>(json, JsonDefaults.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
