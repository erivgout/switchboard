using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Api.Endpoints;

[Flags]
public enum TicketIncludes
{
    None = 0,
    Conversations = 1,
    Tasks = 2,
    Links = 4,
}

/// <summary>
/// Builds API views of tickets through Core's visibility queries. Agent views follow ticket scope;
/// requester views only ever contain public content (INV-TKT-7, SR-AUTHZ-7).
/// </summary>
public sealed class ApiTicketReader(SwitchboardDbContext db, IPermissionService permissions, IActorAccessor actors, ISettingsService settings)
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public static TicketIncludes ParseIncludes(string? include)
    {
        var result = TicketIncludes.None;
        foreach (var part in (include ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result |= part.ToLowerInvariant() switch
            {
                "conversations" => TicketIncludes.Conversations,
                "tasks" => TicketIncludes.Tasks,
                "links" => TicketIncludes.Links,
                _ => throw new ValidationException("include", $"Unknown include \"{part}\". Use conversations, tasks or links."),
            };
        }
        return result;
    }

    public async Task<(TicketDto Ticket, byte[] RowVersion)> GetForAgentAsync(Guid id, TicketIncludes include, CancellationToken ct)
    {
        var visible = await permissions.AgentVisibleTicketsAsync(ct);
        var t = await visible.AsNoTracking()
            .Include(x => x.Status).Include(x => x.Source).Include(x => x.Requester).Include(x => x.RequestedFor)
            .Include(x => x.Group).Include(x => x.Agent).Include(x => x.Department).Include(x => x.Tags)
            .FirstOrDefaultAsync(x => x.Id == id && x.PurgedAt == null, ct)
            ?? throw new NotFoundException();

        var dto = new TicketDto
        {
            Id = t.Id,
            Number = t.Number,
            DisplayId = t.DisplayId,
            Type = t.Type,
            WorkspaceId = t.WorkspaceId,
            Subject = t.Subject,
            DescriptionHtml = t.DescriptionHtml,
            Status = new StatusDto(t.StatusId, t.Status!.Name, t.Status.Category),
            Priority = t.Priority,
            Urgency = t.Urgency,
            Impact = t.Impact,
            Source = new SourceDto(t.SourceId, t.Source!.Key, t.Source.Name),
            Requester = Person(t.Requester!),
            RequestedFor = t.RequestedFor is { } rf ? Person(rf) : null,
            CcEmails = t.CcEmails,
            Group = t.Group is { } g ? new RefDto(g.Id, g.Name) : null,
            Agent = t.Agent is { } a ? Person(a) : null,
            Department = t.Department is { } d ? new RefDto(d.Id, d.Name) : null,
            CategoryId = t.CategoryId,
            SubCategoryId = t.SubCategoryId,
            ItemCategoryId = t.ItemCategoryId,
            Tags = t.Tags.Select(x => x.Tag).Order(StringComparer.Ordinal).ToList(),
            CustomFields = Json(t.CustomFieldsJson),
            IsWaiting = t.IsLampLit(t.Status.Category),
            IsMajorIncident = t.IsMajorIncident,
            IsSpam = t.IsSpam,
            DeletedAt = t.DeletedAt,
            MergedIntoId = t.MergedIntoId,
            ParentId = t.ParentId,
            DueBy = t.DueBy,
            FirstResponseDueBy = t.FirstResponseDueBy,
            FirstRespondedAt = t.FirstRespondedAt,
            ResolvedAt = t.ResolvedAt,
            ClosedAt = t.ClosedAt,
            PlannedStartAt = t.PlannedStartAt,
            PlannedEndAt = t.PlannedEndAt,
            PlannedEffortMinutes = t.PlannedEffortMinutes,
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
            Attachments = (await AttachmentsAsync(AttachmentOwners.Ticket, [t.Id], ct)).GetValueOrDefault(t.Id) ?? [],
            Conversations = include.HasFlag(TicketIncludes.Conversations) ? await AgentConversationsAsync(t.Id, ct) : null,
            Tasks = include.HasFlag(TicketIncludes.Tasks) ? await TasksAsync(t.Id, ct) : null,
            Links = include.HasFlag(TicketIncludes.Links) ? await LinksAsync(t.Id, ct) : null,
        };
        return (dto, t.RowVersion);
    }

    public async Task<RequesterTicketDto> GetForRequesterAsync(Guid id, TicketIncludes include, CancellationToken ct)
    {
        if (include.HasFlag(TicketIncludes.Tasks) || include.HasFlag(TicketIncludes.Links))
        {
            throw new ValidationException("include", "Requesters can include conversations only.");
        }
        var visible = await permissions.RequesterVisibleTicketsAsync(ct);
        var t = await visible.AsNoTracking()
            .Include(x => x.Status).Include(x => x.Requester).Include(x => x.RequestedFor)
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException();

        return new RequesterTicketDto
        {
            Id = t.Id,
            Number = t.Number,
            DisplayId = t.DisplayId,
            Type = t.Type,
            Subject = t.Subject,
            DescriptionHtml = t.DescriptionHtml,
            Status = RequesterStatus(t.Status!),
            Requester = new PersonRefDto(t.Requester!.Id, t.Requester.DisplayName),
            RequestedFor = t.RequestedFor is { } rf ? new PersonRefDto(rf.Id, rf.DisplayName) : null,
            CcEmails = t.CcEmails,
            CustomFields = await RequesterFieldsAsync(t.WorkspaceId, t.CustomFieldsJson, ct),
            CreatedAt = t.CreatedAt,
            UpdatedAt = t.UpdatedAt,
            ResolvedAt = t.ResolvedAt,
            ClosedAt = t.ClosedAt,
            Attachments = (await AttachmentsAsync(AttachmentOwners.Ticket, [t.Id], ct)).GetValueOrDefault(t.Id) ?? [],
            Conversations = include.HasFlag(TicketIncludes.Conversations) ? await RequesterConversationsAsync(t.Id, t.WorkspaceId, ct) : null,
        };
    }

    public async Task<IReadOnlyList<ConversationDto>> AgentConversationsAsync(Guid ticketId, CancellationToken ct)
    {
        var rows = await db.TicketConversations.AsNoTracking()
            .Where(c => c.TicketId == ticketId && c.DeletedAt == null)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new { C = c, AuthorName = c.Author != null ? c.Author.DisplayName : null, AuthorEmail = c.Author != null ? c.Author.Email : null })
            .ToListAsync(ct);
        var files = await AttachmentsAsync(AttachmentOwners.Conversation, rows.Select(r => r.C.Id).ToList(), ct);
        return rows.Select(r => new ConversationDto
        {
            Id = r.C.Id,
            TicketId = r.C.TicketId,
            Kind = r.C.Kind,
            IsPrivate = r.C.IsPrivate,
            Author = r.C.AuthorId is { } aid ? new PersonRefDto(aid, r.AuthorName ?? "", r.AuthorEmail) : null,
            FromEmail = r.C.FromEmail,
            To = r.C.To,
            Cc = r.C.Cc,
            Bcc = r.C.Bcc,
            BodyHtml = r.C.BodyHtml,
            BodyText = r.C.BodyText,
            Channel = r.C.Channel,
            CreatedAt = r.C.CreatedAt,
            EditedAt = r.C.EditedAt,
            Attachments = files.GetValueOrDefault(r.C.Id) ?? [],
        }).ToList();
    }

    /// <summary>Public messages only: no private notes, no forwards, nothing hidden.</summary>
    public async Task<IReadOnlyList<RequesterConversationDto>> RequesterConversationsAsync(Guid ticketId, Guid workspaceId, CancellationToken ct)
    {
        var rows = await db.TicketConversations.AsNoTracking()
            .Where(c => c.TicketId == ticketId && !c.IsPrivate && c.Kind != ConversationKind.Forward && c.DeletedAt == null)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Kind, c.AuthorId, c.FromEmail, c.BodyHtml, c.CreatedAt, AuthorName = c.Author != null ? c.Author.DisplayName : null })
            .ToListAsync(ct);
        var showAgents = await settings.GetAsync(SettingKeys.ShowAgentNamesToRequesters, true, workspaceId, ct);
        var me = actors.Current.UserId;
        var files = await AttachmentsAsync(AttachmentOwners.Conversation, rows.Select(r => r.Id).ToList(), ct);
        return rows.Select(r =>
        {
            var byAgent = r.Kind is ConversationKind.AgentReply or ConversationKind.Note;
            return new RequesterConversationDto
            {
                Id = r.Id,
                Kind = r.Kind,
                AuthorName = r.Kind == ConversationKind.System ? null : byAgent && !showAgents ? "Support team" : r.AuthorName ?? r.FromEmail,
                FromYou = r.AuthorId is not null && r.AuthorId == me,
                BodyHtml = r.BodyHtml,
                CreatedAt = r.CreatedAt,
                Attachments = files.GetValueOrDefault(r.Id) ?? [],
            };
        }).ToList();
    }

    public async Task<RequesterConversationDto> RequesterConversationAsync(Guid ticketId, Guid conversationId, CancellationToken ct)
    {
        var workspaceId = await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => t.WorkspaceId).FirstAsync(ct);
        return (await RequesterConversationsAsync(ticketId, workspaceId, ct)).FirstOrDefault(c => c.Id == conversationId)
            ?? throw new NotFoundException();
    }

    public async Task<ConversationDto> AgentConversationAsync(Guid ticketId, Guid conversationId, CancellationToken ct) =>
        (await AgentConversationsAsync(ticketId, ct)).FirstOrDefault(c => c.Id == conversationId) ?? throw new NotFoundException();

    public async Task<IReadOnlyList<TaskDto>> TasksAsync(Guid ticketId, CancellationToken ct) =>
        await db.TicketTasks.AsNoTracking()
            .Where(t => t.TicketId == ticketId)
            .OrderBy(t => t.Order).ThenBy(t => t.CreatedAt)
            .Select(t => ToDto(t))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<LinkDto>> LinksAsync(Guid ticketId, CancellationToken ct) =>
        await db.TicketLinks.AsNoTracking()
            .Where(l => l.TicketId == ticketId)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new LinkDto(l.Id, l.TicketId, l.TargetType, l.TargetId, l.LinkType, l.CreatedAt))
            .ToListAsync(ct);

    public static TaskDto ToDto(TicketTask t) => new()
    {
        Id = t.Id,
        TicketId = t.TicketId,
        Title = t.Title,
        Description = t.Description,
        AssigneeId = t.AssigneeId,
        GroupId = t.GroupId,
        Status = t.Status,
        DueAt = t.DueAt,
        NotifyBeforeMinutes = t.NotifyBeforeMinutes,
        PlannedStartAt = t.PlannedStartAt,
        PlannedEndAt = t.PlannedEndAt,
        PlannedEffortMinutes = t.PlannedEffortMinutes,
        CompletedAt = t.CompletedAt,
        Order = t.Order,
        CreatedAt = t.CreatedAt,
    };

    public static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, a.FileName, a.ContentType, a.Size, a.IsInline, a.CreatedAt, $"/api/v1/attachments/{a.Id}/content");

    public static RequesterStatusDto RequesterStatus(TicketStatus s) =>
        new(string.IsNullOrWhiteSpace(s.RequesterLabel) ? s.Name : s.RequesterLabel, s.Category);

    private async Task<Dictionary<Guid, List<AttachmentDto>>> AttachmentsAsync(string ownerType, IReadOnlyList<Guid> ownerIds, CancellationToken ct)
    {
        if (ownerIds.Count == 0)
        {
            return [];
        }
        var rows = await db.Attachments.AsNoTracking()
            .Where(a => a.OwnerType == ownerType && a.OwnerId != null && ownerIds.Contains(a.OwnerId.Value))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
        return rows.GroupBy(a => a.OwnerId!.Value).ToDictionary(g => g.Key, g => g.Select(ToDto).ToList());
    }

    /// <summary>Only fields an admin made visible to requesters.</summary>
    private async Task<JsonElement> RequesterFieldsAsync(Guid workspaceId, string json, CancellationToken ct)
    {
        var visible = await db.CustomFieldDefinitions.AsNoTracking()
            .Where(d => d.EntityType == "ticket" && d.IsActive && d.VisibleToRequesters && (d.WorkspaceId == null || d.WorkspaceId == workspaceId))
            .Select(d => d.Key)
            .ToListAsync(ct);
        var values = CustomFieldService.Parse(json);
        var filtered = new JsonObject();
        foreach (var key in visible)
        {
            if (values.TryGetPropertyValue(key, out var value))
            {
                filtered[key] = value?.DeepClone();
            }
        }
        return JsonSerializer.SerializeToElement(filtered);
    }

    private static PersonRefDto Person(User u) => new(u.Id, u.DisplayName, u.Email);

    private static JsonElement Json(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EmptyObject;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : EmptyObject;
        }
        catch (JsonException)
        {
            return EmptyObject;
        }
    }
}
