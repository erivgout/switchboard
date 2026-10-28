using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Core.Tickets;

/// <summary>
/// Every ticket write goes through this service: UI, API, MCP, email, catalog and automations.
/// It enforces permissions, invariants and activity, and raises domain events. Each method saves.
/// </summary>
public interface ITicketService
{
    Task<Ticket> CreateAsync(CreateTicketCommand command, CancellationToken ct = default);
    Task<Ticket> UpdateAsync(Guid ticketId, TicketPatch patch, byte[]? expectedVersion = null, CancellationToken ct = default);
    Task<TicketConversation> ReplyAsync(Guid ticketId, ReplyCommand command, CancellationToken ct = default);
    Task<TicketConversation> AddNoteAsync(Guid ticketId, NoteCommand command, CancellationToken ct = default);
    Task<TicketConversation> ForwardAsync(Guid ticketId, ForwardCommand command, CancellationToken ct = default);
    Task<RequesterMessageResult> AddRequesterMessageAsync(Guid ticketId, RequesterMessageCommand command, CancellationToken ct = default);
    Task<TicketConversation> EditNoteAsync(Guid conversationId, string bodyHtml, CancellationToken ct = default);
    Task HideConversationAsync(Guid conversationId, CancellationToken ct = default);
    Task MergeAsync(Guid primaryId, IReadOnlyList<Guid> secondaryIds, CancellationToken ct = default);
    Task<TicketLink> LinkAsync(Guid ticketId, string targetType, Guid targetId, string linkType, CancellationToken ct = default);
    Task UnlinkAsync(Guid linkId, CancellationToken ct = default);
    Task MoveToTrashAsync(Guid ticketId, CancellationToken ct = default);
    Task RestoreAsync(Guid ticketId, CancellationToken ct = default);
    Task SetSpamAsync(Guid ticketId, bool isSpam, CancellationToken ct = default);
    Task PurgeAsync(Guid ticketId, CancellationToken ct = default);
    Task<TicketTask> AddTaskAsync(Guid ticketId, TaskCommand command, CancellationToken ct = default);
    Task<TicketTask> UpdateTaskAsync(Guid taskId, TaskPatch patch, CancellationToken ct = default);
    Task DeleteTaskAsync(Guid taskId, CancellationToken ct = default);
    Task ShareAsync(Guid ticketId, Guid userId, CancellationToken ct = default);
    Task UnshareAsync(Guid ticketId, Guid userId, CancellationToken ct = default);

    /// <summary>Loads a ticket the current actor can see as an agent, or throws <see cref="NotFoundException"/>.</summary>
    Task<Ticket> GetForAgentAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>Loads a ticket the current actor can see as a requester, or throws <see cref="NotFoundException"/>.</summary>
    Task<Ticket> GetForRequesterAsync(Guid ticketId, CancellationToken ct = default);
}

public sealed class TicketService(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IPermissionService permissions,
    IDisplayIdGenerator displayIds,
    IHtmlContent html,
    ICustomFieldService customFields,
    IUserDirectory directory,
    IAttachmentService attachments,
    ISettingsService settings,
    IAuditLog audit,
    IEnumerable<ITicketTransitionGuard> guards,
    IFileStore fileStore,
    TimeProvider time) : ITicketService
{
    private const int MaxSubject = 500;
    private const int MaxBodyChars = 200_000;

    // ---------------------------------------------------------------- create

    public async Task<Ticket> CreateAsync(CreateTicketCommand command, CancellationToken ct = default)
    {
        var actor = actors.Current;
        var now = time.GetUtcNow();
        var workspace = command.WorkspaceId is { } wsId
            ? await db.Workspaces.FirstOrDefaultAsync(w => w.Id == wsId && !w.IsArchived, ct) ?? throw new ValidationException("workspace", "That workspace doesn't exist.")
            : await db.Workspaces.FirstAsync(w => w.IsPrimary, ct);

        var asRequester = command.AsRequester;
        if (asRequester)
        {
            if (!actor.IsInternal && actor.UserId is null)
            {
                throw new ForbiddenException("Sign in to submit a request.");
            }
        }
        else
        {
            await permissions.DemandAsync(Privileges.TicketsCreate, workspace.Id, ct);
        }

        var subject = (command.Subject ?? "").Trim();
        if (subject.Length == 0)
        {
            throw new ValidationException("subject", "Add a subject.");
        }
        if (subject.Length > MaxSubject)
        {
            throw new ValidationException("subject", $"Keep the subject under {MaxSubject} characters.");
        }

        var requester = await ResolveRequesterAsync(command, actor, ct);
        Guid? requestedFor = null;
        if (command.RequestedForId is { } rf && rf != requester.Id)
        {
            requestedFor = await db.Users.Where(u => u.Id == rf && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
                ?? throw new ValidationException("requestedFor", "That person doesn't exist or is inactive.");
        }

        var status = !asRequester && command.StatusId is { } sid
            ? await db.TicketStatuses.FirstOrDefaultAsync(s => s.Id == sid && s.WorkspaceId == workspace.Id, ct) ?? throw new ValidationException("status", "That status isn't available in this workspace.")
            : await DefaultStatusAsync(workspace.Id, ct);
        var source = await db.TicketSources.FirstOrDefaultAsync(s => s.Key == command.SourceKey, ct)
            ?? await db.TicketSources.FirstAsync(s => s.Key == (asRequester ? TicketSourceKeys.Portal : TicketSourceKeys.Agent), ct);

        var urgency = command.Urgency ?? Level.Low;
        var impact = command.Impact ?? Level.Low;
        var priority = command.Priority
            ?? (await settings.GetAsync(SettingKeys.PriorityMatrixEnabled, true, workspace.Id, ct) && (command.Urgency is not null || command.Impact is not null)
                ? PriorityMatrix.Derive(impact, urgency)
                : Priority.Low);

        Guid? groupId = null, agentId = null;
        if (!asRequester && (command.GroupId is not null || command.AgentId is not null))
        {
            await permissions.DemandAsync(Privileges.TicketsAssign, workspace.Id, ct);
            groupId = await ValidateGroupAsync(command.GroupId, workspace.Id, ct);
            agentId = await ValidateAgentAsync(command.AgentId, workspace.Id, ct);
        }

        await ValidateCategoriesAsync(workspace.Id, command.CategoryId, command.SubCategoryId, command.ItemCategoryId, ct);

        Ticket? parent = null;
        if (command.ParentId is { } parentId)
        {
            parent = await GetForAgentAsync(parentId, ct);
            if (parent.ParentId is not null)
            {
                throw new ValidationException("parent", "A child ticket can't have its own children.");
            }
            if (parent.WorkspaceId != workspace.Id)
            {
                throw new ValidationException("parent", "Child tickets must be in the same workspace as the parent.");
            }
        }

        var customJson = command.CustomFields is { Count: > 0 }
            ? await customFields.ApplyAsync("ticket", workspace.Id, "{}", command.CustomFields, asRequester ? FieldAudience.Requester : FieldAudience.Agent, ct)
            : "{}";
        var missing = await customFields.MissingRequiredAsync("ticket", workspace.Id, customJson, asRequester ? RequiredMoment.RequesterSubmit : RequiredMoment.AgentSave, ct);
        if (missing.Count > 0 && !actor.IsInternal)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["custom"] = missing.ToArray() });
        }

        var descriptionHtml = html.Sanitize(Limit(command.DescriptionHtml));
        var ticket = new Ticket
        {
            Number = await displayIds.NextAsync(Ticket.Family, ct),
            Type = command.Type,
            WorkspaceId = workspace.Id,
            Subject = subject,
            DescriptionHtml = descriptionHtml,
            DescriptionText = html.ToPlainText(descriptionHtml),
            RequesterId = requester.Id,
            RequestedForId = requestedFor,
            CcEmails = NormalizeEmails(command.CcEmails, "cc"),
            StatusId = status.Id,
            Status = status,
            Priority = priority,
            Urgency = urgency,
            Impact = impact,
            SourceId = source.Id,
            CategoryId = command.CategoryId,
            SubCategoryId = command.SubCategoryId,
            ItemCategoryId = command.ItemCategoryId,
            GroupId = groupId,
            AgentId = agentId,
            DepartmentId = command.DepartmentId ?? requester.DepartmentId,
            IsMajorIncident = !asRequester && command.IsMajorIncident,
            ParentId = parent?.Id,
            PlannedStartAt = command.PlannedStartAt,
            PlannedEndAt = command.PlannedEndAt,
            PlannedEffortMinutes = command.PlannedEffortMinutes,
            CustomFieldsJson = customJson,
            Tags = NormalizeTags(command.Tags).Select(t => new TicketTag { Tag = t }).ToList(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        // A new ticket lights the lamp unless an agent logged it from the console.
        var createdByAgent = !asRequester && actor.Kind == ActorKind.User && actor.Channel is Channel.Web;
        ticket.AwaitingAgentSince = createdByAgent ? null : now;
        if (asRequester || actor.UserId == requester.Id)
        {
            ticket.LastRequesterActivityAt = now;
        }
        if (status.Category is StatusCategory.Resolved or StatusCategory.Closed)
        {
            ticket.ResolvedAt = now;
            ticket.ClosedAt = status.Category == StatusCategory.Closed ? now : null;
        }

        db.Tickets.Add(ticket);
        if (command.AttachmentIds is { Count: > 0 })
        {
            await attachments.ClaimPendingAsync(command.AttachmentIds, AttachmentOwners.Ticket, ticket.Id, ct);
        }

        db.Activity(ticket, "created", parent is null ? $"Created {ticket.DisplayId}" : $"Created {ticket.DisplayId} as a child of {parent.DisplayId}");
        db.Raise(new TicketCreated(ticket.Id, ticket.WorkspaceId, ticket.Type));
        if (groupId is not null || agentId is not null)
        {
            db.Raise(new TicketAssigned(ticket.Id, ticket.WorkspaceId, null, groupId, null, agentId));
        }

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    // ---------------------------------------------------------------- update

    public async Task<Ticket> UpdateAsync(Guid ticketId, TicketPatch patch, byte[]? expectedVersion = null, CancellationToken ct = default)
    {
        var ticket = await LoadWritableAsync(ticketId, ct);
        await permissions.DemandAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
        if (expectedVersion is { Length: > 0 })
        {
            if (!ticket.RowVersion.AsSpan().SequenceEqual(expectedVersion))
            {
                throw new ConflictException();
            }
            db.Entry(ticket).Property(t => t.RowVersion).OriginalValue = expectedVersion;
        }

        if (patch.IsEmpty)
        {
            return ticket;
        }

        var changes = new List<FieldChange>();
        await ApplyPatchAsync(ticket, patch, changes, ct);

        if (changes.Count > 0)
        {
            db.Activity(ticket, "updated", Describe(changes), changes);
            db.Raise(new TicketUpdated(ticket.Id, ticket.WorkspaceId, changes));
        }

        await SaveAsync(ct);
        return ticket;
    }

    private async Task ApplyPatchAsync(Ticket ticket, TicketPatch patch, List<FieldChange> changes, CancellationToken ct)
    {
        var ws = ticket.WorkspaceId;

        if (patch.Subject.HasValue)
        {
            var subject = (patch.Subject.Value ?? "").Trim();
            if (subject.Length is 0 or > MaxSubject)
            {
                throw new ValidationException("subject", subject.Length == 0 ? "Add a subject." : $"Keep the subject under {MaxSubject} characters.");
            }
            Track(changes, "Subject", ticket.Subject, subject);
            ticket.Subject = subject;
        }

        if (patch.DescriptionHtml.HasValue)
        {
            var sanitized = html.Sanitize(Limit(patch.DescriptionHtml.Value));
            if (sanitized != ticket.DescriptionHtml)
            {
                changes.Add(new FieldChange("Description", null, "edited"));
                ticket.DescriptionHtml = sanitized;
                ticket.DescriptionText = html.ToPlainText(sanitized);
            }
        }

        if (patch.Type.HasValue && patch.Type.Value != ticket.Type)
        {
            Track(changes, "Type", Labels.Type(ticket.Type), Labels.Type(patch.Type.Value));
            ticket.Type = patch.Type.Value;
        }

        if (patch.RequesterId.HasValue && patch.RequesterId.Value != ticket.RequesterId)
        {
            var requester = await db.Users.FirstOrDefaultAsync(u => u.Id == patch.RequesterId.Value && u.IsActive, ct)
                ?? throw new ValidationException("requester", "That person doesn't exist or is inactive.");
            Track(changes, "Requester", await UserNameAsync(ticket.RequesterId, ct), requester.DisplayName);
            ticket.RequesterId = requester.Id;
        }

        if (patch.RequestedForId.HasValue && patch.RequestedForId.Value != ticket.RequestedForId)
        {
            if (patch.RequestedForId.Value is { } rf && !await db.Users.AnyAsync(u => u.Id == rf && u.IsActive, ct))
            {
                throw new ValidationException("requestedFor", "That person doesn't exist or is inactive.");
            }
            Track(changes, "Requested for", await UserNameAsync(ticket.RequestedForId, ct), await UserNameAsync(patch.RequestedForId.Value, ct));
            ticket.RequestedForId = patch.RequestedForId.Value;
        }

        if (patch.CcEmails.HasValue)
        {
            var cc = NormalizeEmails(patch.CcEmails.Value, "cc");
            if (!cc.SequenceEqual(ticket.CcEmails))
            {
                Track(changes, "CC", string.Join(", ", ticket.CcEmails), string.Join(", ", cc));
                ticket.CcEmails = cc;
            }
        }

        if (patch.SourceId.HasValue && patch.SourceId.Value != ticket.SourceId)
        {
            var source = await db.TicketSources.FirstOrDefaultAsync(s => s.Id == patch.SourceId.Value, ct)
                ?? throw new ValidationException("source", "Unknown source.");
            Track(changes, "Source", await db.TicketSources.Where(s => s.Id == ticket.SourceId).Select(s => s.Name).FirstAsync(ct), source.Name);
            ticket.SourceId = source.Id;
        }

        var urgencyChanged = patch.Urgency.HasValue && patch.Urgency.Value != ticket.Urgency;
        var impactChanged = patch.Impact.HasValue && patch.Impact.Value != ticket.Impact;
        if (urgencyChanged)
        {
            Track(changes, "Urgency", ticket.Urgency.ToString(), patch.Urgency.Value.ToString());
            ticket.Urgency = patch.Urgency.Value;
        }
        if (impactChanged)
        {
            Track(changes, "Impact", ticket.Impact.ToString(), patch.Impact.Value.ToString());
            ticket.Impact = patch.Impact.Value;
        }

        var newPriority = patch.Priority.HasValue
            ? patch.Priority.Value
            : (urgencyChanged || impactChanged) && await settings.GetAsync(SettingKeys.PriorityMatrixEnabled, true, ws, ct)
                ? PriorityMatrix.Derive(ticket.Impact, ticket.Urgency)
                : ticket.Priority;
        if (newPriority != ticket.Priority)
        {
            Track(changes, "Priority", ticket.Priority.ToString(), newPriority.ToString());
            ticket.Priority = newPriority;
        }

        if (patch.CategoryId.HasValue || patch.SubCategoryId.HasValue || patch.ItemCategoryId.HasValue)
        {
            var cat = patch.CategoryId.HasValue ? patch.CategoryId.Value : ticket.CategoryId;
            var sub = patch.SubCategoryId.HasValue ? patch.SubCategoryId.Value : patch.CategoryId.HasValue ? null : ticket.SubCategoryId;
            var item = patch.ItemCategoryId.HasValue ? patch.ItemCategoryId.Value : patch.CategoryId.HasValue || patch.SubCategoryId.HasValue ? null : ticket.ItemCategoryId;
            await ValidateCategoriesAsync(ws, cat, sub, item, ct);
            var before = await CategoryPathAsync(ticket.CategoryId, ticket.SubCategoryId, ticket.ItemCategoryId, ct);
            var after = await CategoryPathAsync(cat, sub, item, ct);
            Track(changes, "Category", before, after);
            ticket.CategoryId = cat;
            ticket.SubCategoryId = sub;
            ticket.ItemCategoryId = item;
        }

        if (patch.DepartmentId.HasValue && patch.DepartmentId.Value != ticket.DepartmentId)
        {
            if (patch.DepartmentId.Value is { } d && !await db.Departments.AnyAsync(x => x.Id == d, ct))
            {
                throw new ValidationException("department", "Unknown department.");
            }
            Track(changes, "Department",
                await db.Departments.Where(x => x.Id == ticket.DepartmentId).Select(x => x.Name).FirstOrDefaultAsync(ct),
                await db.Departments.Where(x => x.Id == patch.DepartmentId.Value).Select(x => x.Name).FirstOrDefaultAsync(ct));
            ticket.DepartmentId = patch.DepartmentId.Value;
        }

        if (patch.GroupId.HasValue || patch.AgentId.HasValue)
        {
            var group = patch.GroupId.HasValue ? patch.GroupId.Value : ticket.GroupId;
            var agent = patch.AgentId.HasValue ? patch.AgentId.Value : ticket.AgentId;
            if (group != ticket.GroupId || agent != ticket.AgentId)
            {
                await permissions.DemandAsync(Privileges.TicketsAssign, ws, ct);
                await ValidateGroupAsync(group, ws, ct);
                await ValidateAgentAsync(agent, ws, ct);
                if (group != ticket.GroupId)
                {
                    Track(changes, "Group",
                        await db.AgentGroups.Where(g => g.Id == ticket.GroupId).Select(g => g.Name).FirstOrDefaultAsync(ct),
                        await db.AgentGroups.Where(g => g.Id == group).Select(g => g.Name).FirstOrDefaultAsync(ct));
                }
                if (agent != ticket.AgentId)
                {
                    Track(changes, "Agent", await UserNameAsync(ticket.AgentId, ct), await UserNameAsync(agent, ct));
                }
                db.Raise(new TicketAssigned(ticket.Id, ws, ticket.GroupId, group, ticket.AgentId, agent));
                ticket.GroupId = group;
                ticket.AgentId = agent;
            }
        }

        if (patch.Tags.HasValue)
        {
            var tags = NormalizeTags(patch.Tags.Value);
            var current = ticket.Tags.Select(t => t.Tag).Order().ToList();
            if (!tags.Order().SequenceEqual(current))
            {
                Track(changes, "Tags", string.Join(", ", current), string.Join(", ", tags.Order()));
                ticket.Tags.RemoveAll(t => !tags.Contains(t.Tag));
                foreach (var tag in tags.Where(t => current.All(c => c != t)))
                {
                    ticket.Tags.Add(new TicketTag { TicketId = ticket.Id, Tag = tag });
                }
            }
        }

        if (patch.CustomFields.HasValue && patch.CustomFields.Value.Count > 0)
        {
            var updated = await customFields.ApplyAsync("ticket", ws, ticket.CustomFieldsJson, patch.CustomFields.Value, FieldAudience.Agent, ct);
            if (updated != ticket.CustomFieldsJson)
            {
                foreach (var key in patch.CustomFields.Value.Keys)
                {
                    var before = CustomFieldService.Parse(ticket.CustomFieldsJson)[key]?.ToJsonString();
                    var after = CustomFieldService.Parse(updated)[key]?.ToJsonString();
                    Track(changes, key, before, after);
                }
                ticket.CustomFieldsJson = updated;
            }
        }

        if (patch.IsMajorIncident.HasValue && patch.IsMajorIncident.Value != ticket.IsMajorIncident)
        {
            Track(changes, "Major incident", ticket.IsMajorIncident ? "Yes" : "No", patch.IsMajorIncident.Value ? "Yes" : "No");
            ticket.IsMajorIncident = patch.IsMajorIncident.Value;
        }

        if (patch.DueBy.HasValue || patch.FirstResponseDueBy.HasValue)
        {
            if (string.IsNullOrWhiteSpace(patch.DueOverrideReason) && !actors.Current.IsInternal)
            {
                throw new ValidationException("dueOverrideReason", "Give a reason for changing the due date.");
            }
            if (patch.DueBy.HasValue && patch.DueBy.Value != ticket.DueBy)
            {
                Track(changes, "Due by", ticket.DueBy?.ToString("O"), patch.DueBy.Value?.ToString("O"));
                ticket.DueBy = patch.DueBy.Value;
            }
            if (patch.FirstResponseDueBy.HasValue && patch.FirstResponseDueBy.Value != ticket.FirstResponseDueBy)
            {
                Track(changes, "First response due", ticket.FirstResponseDueBy?.ToString("O"), patch.FirstResponseDueBy.Value?.ToString("O"));
                ticket.FirstResponseDueBy = patch.FirstResponseDueBy.Value;
            }
            if (!actors.Current.IsInternal)
            {
                audit.Record("tickets.due_overridden", "ticket", ticket.Id.ToString(), new { reason = patch.DueOverrideReason, ticket.DueBy, ticket.FirstResponseDueBy });
            }
        }

        if (patch.PlannedStartAt.HasValue || patch.PlannedEndAt.HasValue || patch.PlannedEffortMinutes.HasValue)
        {
            var start = patch.PlannedStartAt.HasValue ? patch.PlannedStartAt.Value : ticket.PlannedStartAt;
            var end = patch.PlannedEndAt.HasValue ? patch.PlannedEndAt.Value : ticket.PlannedEndAt;
            if (start is not null && end is not null && start >= end)
            {
                throw new ValidationException("plannedEndAt", "The planned end must be after the planned start.");
            }
            Track(changes, "Planned start", ticket.PlannedStartAt?.ToString("O"), start?.ToString("O"));
            Track(changes, "Planned end", ticket.PlannedEndAt?.ToString("O"), end?.ToString("O"));
            ticket.PlannedStartAt = start;
            ticket.PlannedEndAt = end;
            if (patch.PlannedEffortMinutes.HasValue)
            {
                Track(changes, "Planned effort", ticket.PlannedEffortMinutes?.ToString(), patch.PlannedEffortMinutes.Value?.ToString());
                ticket.PlannedEffortMinutes = patch.PlannedEffortMinutes.Value;
            }
        }

        // Status last, so closure rules see the other changes in this patch.
        if (patch.StatusId.HasValue && patch.StatusId.Value != ticket.StatusId)
        {
            var target = await db.TicketStatuses.FirstOrDefaultAsync(s => s.Id == patch.StatusId.Value && s.WorkspaceId == ws, ct)
                ?? throw new ValidationException("status", "That status isn't available in this workspace.");
            await ChangeStatusAsync(ticket, target, changes, ct);
        }
    }

    private async Task ChangeStatusAsync(Ticket ticket, TicketStatus target, List<FieldChange> changes, CancellationToken ct)
    {
        var current = ticket.Status ?? await db.TicketStatuses.FirstAsync(s => s.Id == ticket.StatusId, ct);
        if (target.Category is StatusCategory.Resolved or StatusCategory.Closed && !actors.Current.IsInternal)
        {
            var errors = new List<string>();
            foreach (var guard in guards)
            {
                errors.AddRange(await guard.ValidateAsync(ticket, target, ct));
            }
            if (errors.Count > 0)
            {
                throw new ValidationException(new Dictionary<string, string[]> { ["status"] = errors.ToArray() });
            }
        }

        var now = time.GetUtcNow();
        Track(changes, "Status", current.Name, target.Name);
        ticket.StatusId = target.Id;
        ticket.Status = target;

        // INV-TKT-4: resolved_at iff Resolved/Closed; closed_at iff Closed; reopening clears both.
        switch (target.Category)
        {
            case StatusCategory.Resolved:
                ticket.ResolvedAt ??= now;
                ticket.ClosedAt = null;
                break;
            case StatusCategory.Closed:
                ticket.ResolvedAt ??= now;
                ticket.ClosedAt ??= now;
                break;
            default:
                ticket.ResolvedAt = null;
                ticket.ClosedAt = null;
                break;
        }

        db.Raise(new TicketStatusChanged(ticket.Id, ticket.WorkspaceId, current.Id, target.Id, current.Category, target.Category));
    }

    // ---------------------------------------------------------------- conversations

    public async Task<TicketConversation> ReplyAsync(Guid ticketId, ReplyCommand command, CancellationToken ct = default)
    {
        var ticket = await LoadWritableAsync(ticketId, ct);
        await permissions.DemandAsync(Privileges.TicketsReply, ticket.WorkspaceId, ct);
        var body = RequireBody(command.BodyHtml);
        var requester = await db.Users.AsNoTracking().FirstAsync(u => u.Id == ticket.RequesterId, ct);
        if (string.IsNullOrWhiteSpace(requester.Email) || requester.IsForgotten)
        {
            throw new ValidationException("body", "The requester has no email address to reply to.");
        }

        var now = time.GetUtcNow();
        var actor = actors.Current;
        var conversation = new TicketConversation
        {
            TicketId = ticket.Id,
            Kind = ConversationKind.AgentReply,
            IsPrivate = false,
            AuthorId = actor.UserId,
            To = [requester.Email!],
            Cc = ticket.CcEmails.Union(NormalizeEmails(command.Cc, "cc"), StringComparer.OrdinalIgnoreCase).ToList(),
            Bcc = NormalizeEmails(command.Bcc, "bcc"),
            BodyHtml = body,
            BodyText = html.ToPlainText(body),
            Channel = actor.Channel,
            CreatedAt = now,
        };
        db.TicketConversations.Add(conversation);
        await ClaimAsync(command.AttachmentIds, conversation.Id, ct);

        ticket.FirstRespondedAt ??= now;
        ticket.LastAgentActivityAt = now;
        ticket.AwaitingAgentSince = null;
        db.Activity(conversation, "replied", "Replied to the requester");

        if (command.StatusId is { } statusId && statusId != ticket.StatusId)
        {
            var target = await db.TicketStatuses.FirstOrDefaultAsync(s => s.Id == statusId && s.WorkspaceId == ticket.WorkspaceId, ct)
                ?? throw new ValidationException("status", "That status isn't available in this workspace.");
            await permissions.DemandAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
            var changes = new List<FieldChange>();
            await ChangeStatusAsync(ticket, target, changes, ct);
            db.Activity(ticket, "updated", Describe(changes), changes);
            db.Raise(new TicketUpdated(ticket.Id, ticket.WorkspaceId, changes));
        }

        db.Raise(new TicketConversationAdded(ticket.Id, ticket.WorkspaceId, conversation.Id, conversation.Kind, false, true));
        await SaveAsync(ct);
        return conversation;
    }

    public async Task<TicketConversation> AddNoteAsync(Guid ticketId, NoteCommand command, CancellationToken ct = default)
    {
        var ticket = await LoadWritableAsync(ticketId, ct, allowMerged: true);
        await permissions.DemandAsync(command.IsPrivate ? Privileges.TicketsNote : Privileges.TicketsReply, ticket.WorkspaceId, ct);
        var body = RequireBody(command.BodyHtml);
        var actor = actors.Current;
        var now = time.GetUtcNow();

        var conversation = new TicketConversation
        {
            TicketId = ticket.Id,
            Kind = ConversationKind.Note,
            IsPrivate = command.IsPrivate,
            AuthorId = actor.UserId,
            BodyHtml = body,
            BodyText = html.ToPlainText(body),
            Channel = actor.Channel,
            CreatedAt = now,
        };
        db.TicketConversations.Add(conversation);
        await ClaimAsync(command.AttachmentIds, conversation.Id, ct);
        ticket.LastAgentActivityAt = now;
        db.Activity(conversation, "noted", command.IsPrivate ? "Added a private note" : "Added a public note");
        db.Raise(new TicketConversationAdded(ticket.Id, ticket.WorkspaceId, conversation.Id, conversation.Kind, command.IsPrivate, true));

        if (command.NotifyUserIds is { Count: > 0 })
        {
            var ids = command.NotifyUserIds.Distinct().ToList();
            var agents = await db.Users.Where(u => ids.Contains(u.Id) && u.IsActive && u.IsAgent).Select(u => u.Id).ToListAsync(ct);
            if (agents.Count > 0)
            {
                db.Raise(new AgentsMentioned(ticket.Id, conversation.Id, agents));
            }
        }

        await SaveAsync(ct);
        return conversation;
    }

    public async Task<TicketConversation> ForwardAsync(Guid ticketId, ForwardCommand command, CancellationToken ct = default)
    {
        var ticket = await LoadWritableAsync(ticketId, ct, allowMerged: true);
        await permissions.DemandAsync(Privileges.TicketsReply, ticket.WorkspaceId, ct);
        var to = NormalizeEmails(command.To, "to");
        if (to.Count == 0)
        {
            throw new ValidationException("to", "Add at least one recipient.");
        }
        var body = html.Sanitize(Limit(command.BodyHtml));
        var actor = actors.Current;
        var now = time.GetUtcNow();
        var conversation = new TicketConversation
        {
            TicketId = ticket.Id,
            Kind = ConversationKind.Forward,
            IsPrivate = true,
            AuthorId = actor.UserId,
            To = to,
            Cc = NormalizeEmails(command.Cc, "cc"),
            BodyHtml = body,
            BodyText = html.ToPlainText(body),
            Channel = actor.Channel,
            CreatedAt = now,
        };
        db.TicketConversations.Add(conversation);
        await ClaimAsync(command.AttachmentIds, conversation.Id, ct);
        ticket.LastAgentActivityAt = now;
        db.Activity(conversation, "forwarded", $"Forwarded to {string.Join(", ", to)}");
        db.Raise(new TicketConversationAdded(ticket.Id, ticket.WorkspaceId, conversation.Id, conversation.Kind, true, true));
        await SaveAsync(ct);
        return conversation;
    }

    public async Task<RequesterMessageResult> AddRequesterMessageAsync(Guid ticketId, RequesterMessageCommand command, CancellationToken ct = default)
    {
        var actor = actors.Current;
        var ticket = actor.IsInternal
            ? await db.Tickets.Include(t => t.Status).FirstOrDefaultAsync(t => t.Id == ticketId && t.PurgedAt == null, ct) ?? throw new NotFoundException()
            : await GetForRequesterAsync(ticketId, ct);
        if (ticket.MergedIntoId is { } primaryId)
        {
            // Replies to a merged ticket continue on its primary.
            ticket = await db.Tickets.Include(t => t.Status).FirstAsync(t => t.Id == primaryId, ct);
        }

        var body = RequireBody(command.BodyHtml);
        var now = time.GetUtcNow();
        User? author = actor.UserId is { } uid ? await db.Users.FirstOrDefaultAsync(u => u.Id == uid, ct) : null;
        if (author is null && command.FromEmail is { } from)
        {
            author = await directory.FindByEmailAsync(from, ct);
        }

        if (ticket.Status!.Category == StatusCategory.Closed
            && await settings.GetAsync(SettingKeys.ClosedReplyBehavior, "reopen", ticket.WorkspaceId, ct) == "followup")
        {
            var followUp = await CreateFollowUpAsync(ticket, author, body, command, ct);
            return followUp;
        }

        var conversation = new TicketConversation
        {
            TicketId = ticket.Id,
            Kind = ConversationKind.RequesterMessage,
            AuthorId = author?.Id,
            FromEmail = command.FromEmail ?? author?.Email,
            BodyHtml = body,
            BodyText = html.ToPlainText(body),
            Channel = actor.Channel,
            EmailMessageId = command.EmailMessageId,
            CreatedAt = now,
        };
        db.TicketConversations.Add(conversation);
        await ClaimAsync(command.AttachmentIds, conversation.Id, ct);

        ticket.LastRequesterActivityAt = now;
        ticket.AwaitingAgentSince ??= now;
        db.Activity(conversation, "requester_replied", "The requester replied");

        if (ticket.Status.Category is StatusCategory.Resolved or StatusCategory.Closed)
        {
            var reopenTo = await DefaultStatusAsync(ticket.WorkspaceId, ct);
            var changes = new List<FieldChange>();
            await ChangeStatusAsync(ticket, reopenTo, changes, ct);
            db.Activity(ticket, "reopened", "Reopened by the requester's reply", changes);
            db.Raise(new TicketUpdated(ticket.Id, ticket.WorkspaceId, changes));
        }

        db.Raise(new TicketConversationAdded(ticket.Id, ticket.WorkspaceId, conversation.Id, conversation.Kind, false, false));
        await SaveAsync(ct);
        return new RequesterMessageResult(conversation, ticket, false);
    }

    private async Task<RequesterMessageResult> CreateFollowUpAsync(Ticket closed, User? author, string body, RequesterMessageCommand command, CancellationToken ct)
    {
        var followUp = await CreateAsync(new CreateTicketCommand
        {
            WorkspaceId = closed.WorkspaceId,
            Type = closed.Type,
            Subject = closed.Subject.StartsWith("Re: ", StringComparison.OrdinalIgnoreCase) ? closed.Subject : $"Re: {closed.Subject}",
            DescriptionHtml = body,
            RequesterId = author?.Id ?? closed.RequesterId,
            CcEmails = closed.CcEmails,
            SourceKey = actors.Current.Channel == Channel.Email ? TicketSourceKeys.Email : TicketSourceKeys.Portal,
            AsRequester = true,
            AttachmentIds = command.AttachmentIds,
            EmailMessageId = command.EmailMessageId,
        }, ct);

        db.TicketLinks.Add(new TicketLink
        {
            TicketId = followUp.Id,
            TargetType = Ticket.Family,
            TargetId = closed.Id,
            LinkType = TicketLinkTypes.FollowUpOf,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);

        var first = await db.TicketConversations.FirstOrDefaultAsync(c => c.TicketId == followUp.Id, ct)
            ?? new TicketConversation { TicketId = followUp.Id, Kind = ConversationKind.RequesterMessage, BodyHtml = body };
        return new RequesterMessageResult(first, followUp, true);
    }

    public async Task<TicketConversation> EditNoteAsync(Guid conversationId, string bodyHtml, CancellationToken ct = default)
    {
        var conversation = await db.TicketConversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.DeletedAt == null, ct)
            ?? throw new NotFoundException();
        var ticket = await GetForAgentAsync(conversation.TicketId, ct);
        if (conversation.Kind != ConversationKind.Note)
        {
            throw new ValidationException("body", "Sent messages can't be edited.");
        }
        if (conversation.AuthorId != actors.Current.UserId)
        {
            throw new ForbiddenException("Only the author can edit a note.");
        }
        var body = RequireBody(bodyHtml);
        var before = conversation.BodyText;
        conversation.BodyHtml = body;
        conversation.BodyText = html.ToPlainText(body);
        conversation.EditedAt = time.GetUtcNow();
        db.Activity(conversation, "note_edited", "Edited a note", [new FieldChange("Note", Truncate(before, 2000), Truncate(conversation.BodyText, 2000))]);
        await SaveAsync(ct);
        _ = ticket;
        return conversation;
    }

    public async Task HideConversationAsync(Guid conversationId, CancellationToken ct = default)
    {
        var conversation = await db.TicketConversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.DeletedAt == null, ct)
            ?? throw new NotFoundException();
        var ticket = await GetForAgentAsync(conversation.TicketId, ct);
        var isOwnNote = conversation.Kind == ConversationKind.Note && conversation.AuthorId == actors.Current.UserId;
        if (!isOwnNote)
        {
            await permissions.DemandAsync(Privileges.TicketsDelete, ticket.WorkspaceId, ct);
        }
        conversation.DeletedAt = time.GetUtcNow();
        db.Activity(conversation, "conversation_hidden", conversation.Kind == ConversationKind.Note ? "Deleted a note" : "Hid a sent message");
        audit.Record("tickets.conversation_hidden", "conversation", conversation.Id.ToString(), new { ticketId = ticket.Id, conversation.Kind });
        await SaveAsync(ct);
    }

    // ---------------------------------------------------------------- relationships

    public async Task MergeAsync(Guid primaryId, IReadOnlyList<Guid> secondaryIds, CancellationToken ct = default)
    {
        var ids = secondaryIds.Distinct().Where(id => id != primaryId).ToList();
        if (ids.Count == 0)
        {
            throw new ValidationException("tickets", "Choose at least one other ticket to merge.");
        }

        var primary = await LoadWritableAsync(primaryId, ct);
        await permissions.DemandAsync(Privileges.TicketsMerge, primary.WorkspaceId, ct);
        var closed = await db.TicketStatuses
            .Where(s => s.WorkspaceId == primary.WorkspaceId && s.Category == StatusCategory.Closed)
            .OrderBy(s => s.Order).FirstAsync(ct);
        var now = time.GetUtcNow();
        var mergedLabels = new List<string>();

        foreach (var id in ids)
        {
            var secondary = await LoadWritableAsync(id, ct);
            if (secondary.WorkspaceId != primary.WorkspaceId)
            {
                throw new ValidationException("tickets", $"{secondary.DisplayId} is in a different workspace.");
            }

            // INV-TKT-5: no chains. Anything merged into the secondary moves to the primary.
            var chained = await db.Tickets.Where(t => t.MergedIntoId == secondary.Id).ToListAsync(ct);
            foreach (var c in chained)
            {
                c.MergedIntoId = primary.Id;
            }

            var changes = new List<FieldChange>();
            if (secondary.StatusId != closed.Id)
            {
                using (actors.BeginScope(actors.Current with { Kind = ActorKind.System }))
                {
                    await ChangeStatusAsync(secondary, closed, changes, ct);
                }
            }
            secondary.MergedIntoId = primary.Id;
            secondary.AwaitingAgentSince = null;
            db.Activity(secondary, "merged", $"Merged into {primary.DisplayId}", changes);
            db.TicketConversations.Add(new TicketConversation
            {
                TicketId = secondary.Id,
                Kind = ConversationKind.System,
                IsPrivate = false,
                BodyHtml = html.FromPlainText($"This request was merged into {primary.DisplayId}. Further updates continue there."),
                BodyText = $"Merged into {primary.DisplayId}.",
                Channel = actors.Current.Channel,
                CreatedAt = now,
            });

            var secondaryRequester = await db.Users.AsNoTracking().Where(u => u.Id == secondary.RequesterId).Select(u => u.Email).FirstOrDefaultAsync(ct);
            var additions = secondary.CcEmails.Append(secondaryRequester ?? "").Where(e => e.Length > 0);
            var primaryRequester = await db.Users.AsNoTracking().Where(u => u.Id == primary.RequesterId).Select(u => u.Email).FirstOrDefaultAsync(ct);
            primary.CcEmails = primary.CcEmails
                .Union(additions, StringComparer.OrdinalIgnoreCase)
                .Where(e => !string.Equals(e, primaryRequester, StringComparison.OrdinalIgnoreCase))
                .ToList();

            mergedLabels.Add($"{secondary.DisplayId}: {secondary.Subject}");
        }

        db.TicketConversations.Add(new TicketConversation
        {
            TicketId = primary.Id,
            Kind = ConversationKind.System,
            IsPrivate = true,
            AuthorId = actors.Current.UserId,
            BodyHtml = html.FromPlainText("Merged into this ticket:\n" + string.Join("\n", mergedLabels)),
            BodyText = "Merged: " + string.Join("; ", mergedLabels),
            Channel = actors.Current.Channel,
            CreatedAt = now,
        });
        db.Activity(primary, "merged", $"Merged {string.Join(", ", mergedLabels.Select(l => l[..l.IndexOf(':')]))} into this ticket");
        db.Raise(new TicketsMerged(primary.Id, ids));
        await SaveAsync(ct);
    }

    public async Task<TicketLink> LinkAsync(Guid ticketId, string targetType, Guid targetId, string linkType, CancellationToken ct = default)
    {
        var ticket = await LoadWritableAsync(ticketId, ct, allowMerged: true);
        await permissions.DemandAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(linkType))
        {
            throw new ValidationException("link", "Choose what to link.");
        }
        if (targetType == Ticket.Family)
        {
            if (targetId == ticket.Id)
            {
                throw new ValidationException("link", "A ticket can't be linked to itself.");
            }
            var target = await GetForAgentAsync(targetId, ct);
            if (target.WorkspaceId != ticket.WorkspaceId)
            {
                throw new ValidationException("link", "Linked tickets must be in the same workspace.");
            }
        }

        var existing = await db.TicketLinks.FirstOrDefaultAsync(l => l.TicketId == ticket.Id && l.TargetType == targetType && l.TargetId == targetId && l.LinkType == linkType, ct);
        if (existing is not null)
        {
            return existing;
        }

        var link = new TicketLink
        {
            TicketId = ticket.Id,
            TargetType = targetType,
            TargetId = targetId,
            LinkType = linkType,
            CreatedById = actors.Current.UserId,
            CreatedAt = time.GetUtcNow(),
        };
        db.TicketLinks.Add(link);
        db.Activity(link, "linked", $"Linked a {targetType.Replace('_', ' ')} ({linkType.Replace('_', ' ')})");
        await SaveAsync(ct);
        return link;
    }

    public async Task UnlinkAsync(Guid linkId, CancellationToken ct = default)
    {
        var link = await db.TicketLinks.FirstOrDefaultAsync(l => l.Id == linkId, ct) ?? throw new NotFoundException();
        var ticket = await GetForAgentAsync(link.TicketId, ct);
        await permissions.DemandAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
        db.TicketLinks.Remove(link);
        db.Activity(link, "unlinked", $"Removed a {link.TargetType.Replace('_', ' ')} link");
        await SaveAsync(ct);
    }

    // ---------------------------------------------------------------- trash, spam, purge

    public async Task MoveToTrashAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await GetTrackedForAgentAsync(ticketId, ct);
        await permissions.DemandAsync(Privileges.TicketsDelete, ticket.WorkspaceId, ct);
        if (ticket.DeletedAt is not null)
        {
            return;
        }
        ticket.DeletedAt = time.GetUtcNow();
        ticket.DeletedById = actors.Current.UserId;
        db.Activity(ticket, "deleted", "Moved to trash");
        db.Raise(new TicketDeleted(ticket.Id, ticket.WorkspaceId, false));
        await SaveAsync(ct);
    }

    public async Task RestoreAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await GetTrackedForAgentAsync(ticketId, ct);
        await permissions.DemandAsync(Privileges.TicketsDelete, ticket.WorkspaceId, ct);
        if (ticket.DeletedAt is null)
        {
            return;
        }
        ticket.DeletedAt = null;
        ticket.DeletedById = null;
        db.Activity(ticket, "restored", "Restored from trash");
        db.Raise(new TicketRestored(ticket.Id, ticket.WorkspaceId));
        await SaveAsync(ct);
    }

    public async Task SetSpamAsync(Guid ticketId, bool isSpam, CancellationToken ct = default)
    {
        var ticket = await GetTrackedForAgentAsync(ticketId, ct);
        await permissions.DemandAsync(Privileges.TicketsDelete, ticket.WorkspaceId, ct);
        if (ticket.IsSpam == isSpam)
        {
            return;
        }
        ticket.IsSpam = isSpam;
        if (isSpam)
        {
            ticket.AwaitingAgentSince = null;
        }
        db.Activity(ticket, isSpam ? "spam" : "not_spam", isSpam ? "Marked as spam" : "Marked as not spam");
        await SaveAsync(ct);
    }

    public async Task PurgeAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await GetTrackedForAgentAsync(ticketId, ct);
        await permissions.DemandAsync(Privileges.TicketsDeletePermanently, ticket.WorkspaceId, ct);
        if (ticket.DeletedAt is null && !ticket.IsSpam)
        {
            throw new ValidationException("ticket", "Move the ticket to trash before deleting it permanently.");
        }

        var conversationIds = await db.TicketConversations.Where(c => c.TicketId == ticket.Id).Select(c => c.Id).ToListAsync(ct);
        var files = await db.Attachments
            .Where(a => (a.OwnerType == AttachmentOwners.Ticket && a.OwnerId == ticket.Id)
                || (a.OwnerType == AttachmentOwners.Conversation && a.OwnerId != null && conversationIds.Contains(a.OwnerId.Value)))
            .ToListAsync(ct);
        db.Attachments.RemoveRange(files);
        await db.TicketConversations.Where(c => c.TicketId == ticket.Id).ExecuteDeleteAsync(ct);
        await db.TicketTasks.Where(t => t.TicketId == ticket.Id).ExecuteDeleteAsync(ct);
        await db.TicketTags.Where(t => t.TicketId == ticket.Id).ExecuteDeleteAsync(ct);
        await db.TicketShares.Where(t => t.TicketId == ticket.Id).ExecuteDeleteAsync(ct);

        ticket.Subject = "[deleted]";
        ticket.DescriptionHtml = "";
        ticket.DescriptionText = "";
        ticket.CcEmails = [];
        ticket.CustomFieldsJson = "{}";
        ticket.PurgedAt = time.GetUtcNow();
        db.Activity(ticket, "purged", "Permanently deleted");
        audit.Record("tickets.purged", "ticket", ticket.Id.ToString(), new { ticket.Number, ticket.Type });
        db.Raise(new TicketDeleted(ticket.Id, ticket.WorkspaceId, true));
        await SaveAsync(ct);

        foreach (var file in files)
        {
            await fileStore.DeleteAsync(file.StorageKey, ct);
        }
    }

    // ---------------------------------------------------------------- tasks

    public async Task<TicketTask> AddTaskAsync(Guid ticketId, TaskCommand command, CancellationToken ct = default)
    {
        var ticket = await LoadWritableAsync(ticketId, ct);
        await permissions.DemandAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
        var title = (command.Title ?? "").Trim();
        if (title.Length is 0 or > 300)
        {
            throw new ValidationException("title", title.Length == 0 ? "Add a task title." : "Keep the title under 300 characters.");
        }
        await ValidateAgentAsync(command.AssigneeId, ticket.WorkspaceId, ct);
        await ValidateGroupAsync(command.GroupId, ticket.WorkspaceId, ct);

        var order = await db.TicketTasks.Where(t => t.TicketId == ticket.Id).Select(t => (int?)t.Order).MaxAsync(ct) ?? 0;
        var task = new TicketTask
        {
            TicketId = ticket.Id,
            Title = title,
            Description = command.Description,
            AssigneeId = command.AssigneeId,
            GroupId = command.GroupId,
            DueAt = command.DueAt,
            NotifyBeforeMinutes = command.NotifyBeforeMinutes,
            PlannedStartAt = command.PlannedStartAt,
            PlannedEndAt = command.PlannedEndAt,
            PlannedEffortMinutes = command.PlannedEffortMinutes,
            Order = order + 1,
            CreatedById = actors.Current.UserId,
            CreatedAt = time.GetUtcNow(),
        };
        db.TicketTasks.Add(task);
        db.Activity(task, "task_added", $"Added task \"{title}\"");
        db.Raise(new TicketTaskChanged(ticket.Id, task.Id, task.Status, task.AssigneeId, true));
        await SaveAsync(ct);
        return task;
    }

    public async Task<TicketTask> UpdateTaskAsync(Guid taskId, TaskPatch patch, CancellationToken ct = default)
    {
        var task = await db.TicketTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct) ?? throw new NotFoundException();
        var ticket = await GetForAgentAsync(task.TicketId, ct);
        await permissions.DemandAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
        var changes = new List<FieldChange>();
        if (patch.Title.HasValue)
        {
            var title = (patch.Title.Value ?? "").Trim();
            if (title.Length is 0 or > 300)
            {
                throw new ValidationException("title", "Task titles need 1 to 300 characters.");
            }
            Track(changes, "Title", task.Title, title);
            task.Title = title;
        }
        if (patch.Description.HasValue)
        {
            task.Description = patch.Description.Value;
        }
        if (patch.AssigneeId.HasValue && patch.AssigneeId.Value != task.AssigneeId)
        {
            await ValidateAgentAsync(patch.AssigneeId.Value, ticket.WorkspaceId, ct);
            Track(changes, "Assignee", await UserNameAsync(task.AssigneeId, ct), await UserNameAsync(patch.AssigneeId.Value, ct));
            task.AssigneeId = patch.AssigneeId.Value;
        }
        if (patch.GroupId.HasValue && patch.GroupId.Value != task.GroupId)
        {
            await ValidateGroupAsync(patch.GroupId.Value, ticket.WorkspaceId, ct);
            task.GroupId = patch.GroupId.Value;
        }
        if (patch.DueAt.HasValue)
        {
            task.DueAt = patch.DueAt.Value;
        }
        if (patch.Status.HasValue && patch.Status.Value != task.Status)
        {
            Track(changes, "Task status", task.Status.ToString(), patch.Status.Value.ToString());
            task.Status = patch.Status.Value;
            task.CompletedAt = task.Status == TicketTaskStatus.Completed ? time.GetUtcNow() : null;
        }
        db.Activity(task, "task_updated", $"Updated task \"{task.Title}\"", changes);
        db.Raise(new TicketTaskChanged(ticket.Id, task.Id, task.Status, task.AssigneeId, false));
        await SaveAsync(ct);
        return task;
    }

    public async Task DeleteTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await db.TicketTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct) ?? throw new NotFoundException();
        var ticket = await GetForAgentAsync(task.TicketId, ct);
        await permissions.DemandAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
        db.TicketTasks.Remove(task);
        db.Activity(task, "task_deleted", $"Deleted task \"{task.Title}\"");
        await SaveAsync(ct);
    }

    // ---------------------------------------------------------------- sharing

    public async Task ShareAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var ticket = await LoadForShareAsync(ticketId, ct);
        if (userId == ticket.RequesterId || await db.TicketShares.AnyAsync(s => s.TicketId == ticketId && s.UserId == userId, ct))
        {
            return;
        }
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct)
            ?? throw new ValidationException("user", "That person doesn't exist or is inactive.");
        db.TicketShares.Add(new TicketShare { TicketId = ticketId, UserId = userId, SharedById = actors.Current.UserId, CreatedAt = time.GetUtcNow() });
        db.Activity(ticket, "shared", $"Shared with {user.DisplayName}");
        await SaveAsync(ct);
    }

    public async Task UnshareAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var ticket = await LoadForShareAsync(ticketId, ct);
        var share = await db.TicketShares.FirstOrDefaultAsync(s => s.TicketId == ticketId && s.UserId == userId, ct);
        if (share is null)
        {
            return;
        }
        db.TicketShares.Remove(share);
        db.Activity(ticket, "unshared", $"Stopped sharing with {await UserNameAsync(userId, ct)}");
        await SaveAsync(ct);
    }

    private async Task<Ticket> LoadForShareAsync(Guid ticketId, CancellationToken ct)
    {
        var actor = actors.Current;
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.PurgedAt == null, ct) ?? throw new NotFoundException();
        if (actor.IsInternal || ticket.RequesterId == actor.UserId || ticket.RequestedForId == actor.UserId)
        {
            return ticket;
        }
        var p = await permissions.CurrentAsync(ct);
        if (p is not null && PermissionService.CanAgentSee(ticket, p) && p.Has(Privileges.TicketsEdit, ticket.WorkspaceId))
        {
            return ticket;
        }
        throw new NotFoundException();
    }

    // ---------------------------------------------------------------- loading

    public async Task<Ticket> GetForAgentAsync(Guid ticketId, CancellationToken ct = default)
    {
        var visible = await permissions.AgentVisibleTicketsAsync(ct);
        return await visible.Include(t => t.Status).FirstOrDefaultAsync(t => t.Id == ticketId && t.PurgedAt == null, ct)
            ?? throw new NotFoundException();
    }

    public async Task<Ticket> GetForRequesterAsync(Guid ticketId, CancellationToken ct = default)
    {
        var visible = await permissions.RequesterVisibleTicketsAsync(ct);
        return await visible.Include(t => t.Status).FirstOrDefaultAsync(t => t.Id == ticketId, ct)
            ?? throw new NotFoundException();
    }

    private async Task<Ticket> GetTrackedForAgentAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await GetForAgentAsync(ticketId, ct);
        await db.Entry(ticket).Collection(t => t.Tags).LoadAsync(ct);
        return ticket;
    }

    /// <summary>Loads for writing: visible, not purged, not trashed, and (unless allowed) not merged away.</summary>
    private async Task<Ticket> LoadWritableAsync(Guid ticketId, CancellationToken ct, bool allowMerged = false)
    {
        var ticket = await GetTrackedForAgentAsync(ticketId, ct);
        if (ticket.DeletedAt is not null)
        {
            throw new ValidationException("ticket", $"{ticket.DisplayId} is in the trash. Restore it first.");
        }
        if (!allowMerged && ticket.MergedIntoId is not null)
        {
            throw new ValidationException("ticket", $"{ticket.DisplayId} was merged and is read-only.");
        }
        return ticket;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<User> ResolveRequesterAsync(CreateTicketCommand command, Actor actor, CancellationToken ct)
    {
        if (command.AsRequester && !actor.IsInternal)
        {
            // Requesters always file as themselves, and deactivated users can't file at all (INV-ID-4).
            return await db.Users.FirstOrDefaultAsync(u => u.Id == actor.UserId && u.IsActive, ct)
                ?? throw new ForbiddenException("Your account is inactive.");
        }
        if (command.RequesterId is { } id)
        {
            return await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct)
                ?? throw new ValidationException("requester", "That requester doesn't exist or is inactive.");
        }
        if (!string.IsNullOrWhiteSpace(command.RequesterEmail))
        {
            var user = await directory.EnsureRequesterAsync(command.RequesterEmail, command.RequesterName, ct);
            if (!user.IsActive)
            {
                throw new ValidationException("requester", "That requester is inactive.");
            }
            return user;
        }
        if (actor.UserId is { } self)
        {
            return await db.Users.FirstAsync(u => u.Id == self, ct);
        }
        throw new ValidationException("requester", "Choose a requester.");
    }

    private async Task<TicketStatus> DefaultStatusAsync(Guid workspaceId, CancellationToken ct) =>
        await db.TicketStatuses.Where(s => s.WorkspaceId == workspaceId && s.IsDefault).FirstOrDefaultAsync(ct)
        ?? await db.TicketStatuses.Where(s => s.WorkspaceId == workspaceId && s.Category == StatusCategory.Open).OrderBy(s => s.Order).FirstAsync(ct);

    private async Task<Guid?> ValidateGroupAsync(Guid? groupId, Guid workspaceId, CancellationToken ct)
    {
        if (groupId is { } g && !await db.AgentGroups.AnyAsync(x => x.Id == g && x.WorkspaceId == workspaceId, ct))
        {
            throw new ValidationException("group", "That group isn't in this workspace.");
        }
        return groupId;
    }

    /// <summary>INV-TKT-3: an assignee is an active agent with a role in the workspace.</summary>
    private async Task<Guid?> ValidateAgentAsync(Guid? agentId, Guid workspaceId, CancellationToken ct)
    {
        if (agentId is not { } a)
        {
            return null;
        }
        var ok = await db.Users.AnyAsync(u => u.Id == a && u.IsActive && (u.IsOrgAdmin || u.Memberships.Any(m => m.WorkspaceId == workspaceId)), ct);
        if (!ok)
        {
            throw new ValidationException("agent", "That person isn't an active agent in this workspace.");
        }
        return agentId;
    }

    private async Task ValidateCategoriesAsync(Guid workspaceId, Guid? categoryId, Guid? subId, Guid? itemId, CancellationToken ct)
    {
        if (categoryId is null && (subId is not null || itemId is not null) || subId is null && itemId is not null)
        {
            throw new ValidationException("category", "Pick the category before the sub-category or item.");
        }
        var ids = new[] { categoryId, subId, itemId }.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (ids.Count == 0)
        {
            return;
        }
        var cats = await db.TicketCategories.AsNoTracking().Where(c => ids.Contains(c.Id) && c.WorkspaceId == workspaceId).ToDictionaryAsync(c => c.Id, ct);
        bool Valid(Guid? id, Guid? parent, int level) => id is null || (cats.TryGetValue(id.Value, out var c) && c.ParentId == parent && c.Level == level);
        if (!Valid(categoryId, null, 1) || !Valid(subId, categoryId, 2) || !Valid(itemId, subId, 3))
        {
            throw new ValidationException("category", "That category combination isn't valid.");
        }
    }

    private async Task<string?> CategoryPathAsync(Guid? cat, Guid? sub, Guid? item, CancellationToken ct)
    {
        var ids = new[] { cat, sub, item }.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (ids.Count == 0)
        {
            return null;
        }
        var names = await db.TicketCategories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return string.Join(" / ", ids.Where(names.ContainsKey).Select(id => names[id]));
    }

    private async Task<string?> UserNameAsync(Guid? id, CancellationToken ct) =>
        id is null ? null : await db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);

    private async Task ClaimAsync(IReadOnlyList<Guid>? attachmentIds, Guid conversationId, CancellationToken ct)
    {
        if (attachmentIds is { Count: > 0 })
        {
            await attachments.ClaimPendingAsync(attachmentIds, AttachmentOwners.Conversation, conversationId, ct);
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException();
        }
    }

    private string RequireBody(string? bodyHtml)
    {
        var body = html.Sanitize(Limit(bodyHtml));
        if (html.ToPlainText(body).Length == 0 && !body.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("body", "Write a message first.");
        }
        return body;
    }

    private static string Limit(string? value) =>
        value is null ? "" : value.Length > MaxBodyChars ? throw new ValidationException("body", "The message is too long.") : value;

    private static List<string> NormalizeEmails(IEnumerable<string>? emails, string field)
    {
        var list = new List<string>();
        foreach (var raw in emails ?? [])
        {
            foreach (var part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IUserDirectory.IsValidEmail(part))
                {
                    throw new ValidationException(field, $"{part} isn't a valid email address.");
                }
                if (!list.Contains(part, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(part);
                }
            }
        }
        if (list.Count > 50)
        {
            throw new ValidationException(field, "Use at most 50 addresses.");
        }
        return list;
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? [])
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length is > 0 and <= 64)
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToList();

    private static void Track(List<FieldChange> changes, string field, string? from, string? to)
    {
        if (!string.Equals(from, to, StringComparison.Ordinal))
        {
            changes.Add(new FieldChange(field, from, to));
        }
    }

    private static string Describe(IReadOnlyList<FieldChange> changes) =>
        changes.Count switch
        {
            0 => "Updated the ticket",
            1 when changes[0].Field == "Status" => $"Changed status to {changes[0].To}",
            1 => $"Changed {changes[0].Field.ToLowerInvariant()}",
            _ => $"Changed {string.Join(", ", changes.Select(c => c.Field.ToLowerInvariant()))}",
        };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

/// <summary>Default impact × urgency matrix (Appendix C).</summary>
public static class PriorityMatrix
{
    public static Priority Derive(Level impact, Level urgency) => (impact, urgency) switch
    {
        (Level.High, Level.High) => Priority.Urgent,
        (Level.High, Level.Medium) or (Level.Medium, Level.High) => Priority.High,
        (Level.High, Level.Low) or (Level.Medium, Level.Medium) or (Level.Low, Level.High) => Priority.Medium,
        _ => Priority.Low,
    };
}

public static class Labels
{
    public static string Type(TicketType type) => type == TicketType.Incident ? "Incident" : "Service request";
}
