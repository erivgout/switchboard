using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;
using Switchboard.Modules.Catalog.Domain;

namespace Switchboard.Modules.Catalog.Services;

public sealed record SubmitRequestCommand
{
    public Guid ItemId { get; init; }
    public int Quantity { get; init; } = 1;
    /// <summary>Raw answers by field key; several values for multi-select.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Answers { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>Someone else the item is for, when the item allows it.</summary>
    public Guid? RequestedForId { get; init; }
    /// <summary>An agent requesting on someone's behalf. Null: the current user requests for themselves.</summary>
    public Guid? RequesterId { get; init; }
    /// <summary>Reject answers to questions that aren't on the form (API and agent calls).</summary>
    public bool StrictAnswers { get; init; }
}

public sealed record ResubmitRequestCommand
{
    public int Quantity { get; init; } = 1;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Answers { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public Guid? RequestedForId { get; init; }
}

public sealed record SubmitResult(Ticket Ticket, CatRequestedItem Item, AprRequest? Approval, string? PostSubmitMessage);

public sealed record RequestedItemView(CatRequestedItem Row, IReadOnlyList<FieldAnswer> Answers, IReadOnlyList<CatRequestedItem> Children, string? RequestedForName);

/// <summary>The catalog side of one service request ticket.</summary>
public sealed class TicketCatalogView
{
    public required Guid TicketId { get; init; }
    public required string DisplayId { get; init; }
    public required int TicketNumber { get; init; }
    public required string Subject { get; init; }
    public required Guid RequesterId { get; init; }
    public required string RequesterName { get; init; }
    public required IReadOnlyList<RequestedItemView> Items { get; init; }
    public long? TotalMinor { get; init; }
    public string Currency { get; init; } = "USD";
    public AprRequest? Approval { get; init; }
    public string? RejectionReason { get; init; }
    public string? RejectedBy { get; init; }
    /// <summary>The requester may change the request and send it for approval again (FR-SRM-10).</summary>
    public bool CanResubmit { get; init; }
    public bool CanChangeStages { get; init; }
    public string? PostSubmitMessage { get; init; }
}

/// <summary>Service requests from the catalog (FR-SRM-6..10).</summary>
public interface ICatalogRequestService
{
    /// <summary>
    /// Creates a service request ticket through <see cref="ITicketService"/>, its requested items, and starts approvals when
    /// the item needs them; otherwise assigns the fulfillment group. All in one transaction.
    /// </summary>
    Task<SubmitResult> SubmitAsync(SubmitRequestCommand command, CancellationToken ct = default);

    /// <summary>After a rejection, the requester changes the request; the ticket reopens and approval starts again.</summary>
    Task<SubmitResult> ResubmitAsync(Guid ticketId, ResubmitRequestCommand command, CancellationToken ct = default);

    Task ChangeStageAsync(Guid requestedItemId, RequestedItemStage stage, CancellationToken ct = default);

    Task<TicketCatalogView?> GetForAgentAsync(Guid ticketId, CancellationToken ct = default);
    Task<TicketCatalogView?> GetForRequesterAsync(Guid ticketId, CancellationToken ct = default);
    /// <summary>For an approver looking at <paramref name="approval"/>; the caller has already checked they can see it.</summary>
    Task<TicketCatalogView?> GetForApprovalAsync(AprRequest approval, CancellationToken ct = default);
}

internal sealed class CatalogRequestService(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IPermissionService permissions,
    ITicketService tickets,
    IApprovalService approvals,
    CatalogService catalog,
    TimeProvider time) : ICatalogRequestService
{
    public const string SystemActorName = "Service catalog";

    // ---------------------------------------------------------------- submit

    public async Task<SubmitResult> SubmitAsync(SubmitRequestCommand command, CancellationToken ct = default)
    {
        var actor = actors.Current;
        if (!actor.IsInternal && actor.UserId is null)
        {
            throw new ForbiddenException("Sign in to request something.");
        }
        var onBehalf = command.RequesterId is { } rid && rid != actor.UserId;
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.IsPrimary, ct);
        Guid requesterId;
        if (onBehalf)
        {
            await permissions.DemandAsync(Privileges.TicketsCreate, workspace.Id, ct);
            requesterId = await db.Users.Where(u => u.Id == command.RequesterId && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct)
                ?? throw new ValidationException("requester", "That person doesn't exist or is inactive.");
        }
        else
        {
            requesterId = actor.UserId ?? throw new ValidationException("requester", "Say who the request is for.");
        }

        var item = await LoadPublishedAsync(command.ItemId, ct);
        if (!await catalog.IsVisibleToAsync(item.Id, requesterId, ct))
        {
            if (onBehalf)
            {
                throw new ValidationException("requester", "That person can't request this item.");
            }
            throw new NotFoundException();
        }
        var (answers, requestedFor) = await ValidateAsync(item, requesterId, command.Quantity, command.RequestedForId, command.Answers, command.StrictAnswers, ct);
        var names = await NamesAsync([requesterId, .. requestedFor is { } rf ? new[] { rf } : []], ct);
        var subject = requestedFor is { } forId ? $"{item.Name} for {names[forId]}" : item.Name;

        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;

        var ticket = await tickets.CreateAsync(new CreateTicketCommand
        {
            WorkspaceId = workspace.Id,
            Type = TicketType.ServiceRequest,
            Subject = subject,
            DescriptionHtml = DescribeHtml(item.Name, command.Quantity, answers),
            AsRequester = !onBehalf,
            RequesterId = requesterId,
            RequestedForId = requestedFor,
            SourceKey = actor.Channel switch
            {
                Channel.Mcp => TicketSourceKeys.Mcp,
                Channel.Api => TicketSourceKeys.Api,
                _ => onBehalf ? TicketSourceKeys.Agent : TicketSourceKeys.Portal,
            },
        }, ct);

        var row = await AddRowsAsync(ticket, item, command.Quantity, answers, requestedFor, ct);
        await db.SaveChangesAsync(ct);

        var approval = await StartFulfillmentAsync(ticket, item, answers, requesterId, requestedFor, subject, onBehalf, ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
        return new SubmitResult(ticket, row, approval, item.PostSubmitMessage);
    }

    // ---------------------------------------------------------------- resubmit

    public async Task<SubmitResult> ResubmitAsync(Guid ticketId, ResubmitRequestCommand command, CancellationToken ct = default)
    {
        var actor = actors.Current;
        if (actor.IsInternal || actor.UserId is not { } uid)
        {
            throw new ForbiddenException("Sign in to change your request.");
        }
        var ticket = await tickets.GetForRequesterAsync(ticketId, ct);
        if (ticket.RequesterId != uid)
        {
            throw new ForbiddenException("Only the person who made the request can change it.");
        }
        var latest = await LatestApprovalAsync(ticket.Id, ct);
        if (latest?.Status != ApprovalStatus.Rejected)
        {
            throw new ValidationException("request", "Only a request that wasn't approved can be changed and sent again.");
        }
        var rows = await db.Set<CatRequestedItem>().Where(r => r.TicketId == ticket.Id).ToListAsync(ct);
        var top = rows.Where(r => r.ParentId == null).ToList();
        if (top.Count != 1)
        {
            throw new ValidationException("request", "This request can't be changed here. Reply on the request instead.");
        }
        var row = top[0];
        var item = await LoadPublishedAsync(row.ItemId, ct);
        if (!await catalog.IsVisibleToAsync(item.Id, uid, ct))
        {
            throw new ValidationException("request", "This item isn't available to you anymore.");
        }
        var (answers, requestedFor) = await ValidateAsync(item, uid, command.Quantity, command.RequestedForId, command.Answers, strict: false, ct);
        var names = await NamesAsync([uid, .. requestedFor is { } rf ? new[] { rf } : []], ct);
        var subject = requestedFor is { } forId ? $"{item.Name} for {names[forId]}" : item.Name;

        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var now = time.GetUtcNow();
        row.Quantity = command.Quantity;
        row.AnswersJson = CatalogForm.Serialize(answers);
        row.UnitCostMinor = item.CostMinor;
        row.CostMinor = item.CostMinor * command.Quantity;
        row.Currency = item.Currency;
        row.RequestedForId = requestedFor;
        foreach (var r in rows)
        {
            if (r.ParentId == row.Id)
            {
                // Bundle items follow the bundle's quantity and recipient.
                r.Quantity = command.Quantity;
                r.RequestedForId = requestedFor;
            }
            if (r.Stage != RequestedItemStage.Requested)
            {
                r.Stage = RequestedItemStage.Requested;
                r.StageChangedAt = now;
            }
        }
        db.Activity(row, "catalog_resubmitted", $"{item.Name} was changed and sent for approval again");
        await db.SaveChangesAsync(ct);

        // The requester's message reopens the resolved ticket (INV-TKT-4).
        await tickets.AddRequesterMessageAsync(ticket.Id, new RequesterMessageCommand
        {
            BodyHtml = "<p>I changed my request and sent it for approval again.</p>" + DescribeHtml(null, command.Quantity, answers),
        }, ct);
        if (requestedFor != ticket.RequestedForId)
        {
            using (actors.BeginScope(Actor.System(SystemActorName, actor.Channel)))
            {
                await tickets.UpdateAsync(ticket.Id, new TicketPatch { RequestedForId = requestedFor }, ct: ct);
            }
        }

        var reloaded = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id, ct);
        var approval = await StartFulfillmentAsync(reloaded, item, answers, uid, requestedFor, subject, onBehalf: false, ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
        return new SubmitResult(reloaded, row, approval, item.PostSubmitMessage);
    }

    // ---------------------------------------------------------------- stages

    public async Task ChangeStageAsync(Guid requestedItemId, RequestedItemStage stage, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ValidationException("stage", "Choose a stage.");
        }
        var row = await db.Set<CatRequestedItem>().FirstOrDefaultAsync(r => r.Id == requestedItemId, ct) ?? throw new NotFoundException();
        var ticket = await tickets.GetForAgentAsync(row.TicketId, ct);
        await permissions.DemandAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
        if (row.Stage == stage)
        {
            return;
        }
        if (stage is RequestedItemStage.InProgress or RequestedItemStage.Delivered
            && (await LatestApprovalAsync(ticket.Id, ct))?.Status is ApprovalStatus.Requested or ApprovalStatus.Rejected)
        {
            throw new ValidationException("stage", "This request isn't approved, so it can't be worked on yet.");
        }
        db.Activity(row, "catalog_stage_changed", $"{row.ItemName} changed from {StageLabel(row.Stage)} to {StageLabel(stage)}",
            [new FieldChange("Stage", StageLabel(row.Stage), StageLabel(stage))]);
        row.Stage = stage;
        row.StageChangedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public static string StageLabel(RequestedItemStage stage) => stage switch
    {
        RequestedItemStage.Requested => "Requested",
        RequestedItemStage.InProgress => "In progress",
        RequestedItemStage.Delivered => "Delivered",
        _ => "Cancelled",
    };

    // ---------------------------------------------------------------- read

    public async Task<TicketCatalogView?> GetForAgentAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await tickets.GetForAgentAsync(ticketId, ct);
        var canEdit = await permissions.HasAsync(Privileges.TicketsEdit, ticket.WorkspaceId, ct);
        return await BuildAsync(ticket, canResubmit: false, canChangeStages: canEdit, ct);
    }

    public async Task<TicketCatalogView?> GetForRequesterAsync(Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await tickets.GetForRequesterAsync(ticketId, ct);
        var isRequester = actors.Current.UserId == ticket.RequesterId;
        return await BuildAsync(ticket, canResubmit: isRequester, canChangeStages: false, ct);
    }

    public async Task<TicketCatalogView?> GetForApprovalAsync(AprRequest approval, CancellationToken ct = default)
    {
        if (approval.SubjectType != ApprovalSubjectTypes.Ticket)
        {
            return null;
        }
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == approval.SubjectId, ct);
        return ticket is null ? null : await BuildAsync(ticket, canResubmit: false, canChangeStages: false, ct);
    }

    private async Task<TicketCatalogView?> BuildAsync(Ticket ticket, bool canResubmit, bool canChangeStages, CancellationToken ct)
    {
        var rows = await db.Set<CatRequestedItem>().AsNoTracking().Where(r => r.TicketId == ticket.Id).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return null;
        }
        var top = rows.Where(r => r.ParentId == null).ToList();
        var nameIds = rows.Select(r => r.RequestedForId).OfType<Guid>().Append(ticket.RequesterId).Distinct().ToList();
        var latest = await LatestApprovalAsync(ticket.Id, ct);
        var rejection = latest?.Status == ApprovalStatus.Rejected ? latest.Decisions.OrderBy(d => d.CreatedAt).LastOrDefault(d => d.Verdict == ApprovalVerdict.Rejected) : null;
        if (rejection is not null)
        {
            nameIds.Add(rejection.DecidedById);
        }
        var names = await NamesAsync(nameIds, ct);
        var items = top.Select(r => new RequestedItemView(r, CatalogForm.Deserialize(r.AnswersJson), rows.Where(c => c.ParentId == r.Id).ToList(),
            r.RequestedForId is { } f ? names[f] : null)).ToList();
        var priced = rows.Where(r => r.CostMinor is not null && r.Stage != RequestedItemStage.Cancelled).ToList();
        var itemIds = top.Select(r => r.ItemId).ToList();
        var message = await db.Set<CatItem>().AsNoTracking().Where(i => itemIds.Contains(i.Id)).Select(i => i.PostSubmitMessage).FirstOrDefaultAsync(ct);
        return new TicketCatalogView
        {
            TicketId = ticket.Id,
            DisplayId = ticket.DisplayId,
            TicketNumber = ticket.Number,
            Subject = ticket.Subject,
            RequesterId = ticket.RequesterId,
            RequesterName = names[ticket.RequesterId],
            Items = items,
            TotalMinor = priced.Count == 0 ? null : priced.Sum(r => r.CostMinor!.Value),
            Currency = priced.FirstOrDefault()?.Currency ?? top[0].Currency,
            Approval = latest,
            RejectionReason = rejection?.Comment,
            RejectedBy = rejection is null ? null : names[rejection.DecidedById],
            CanResubmit = canResubmit && rejection is not null && top.Count == 1,
            CanChangeStages = canChangeStages,
            PostSubmitMessage = message,
        };
    }

    // ---------------------------------------------------------------- helpers

    private async Task<CatItem> LoadPublishedAsync(Guid itemId, CancellationToken ct) =>
        await db.Set<CatItem>().AsNoTracking()
            .Include(i => i.Fields).ThenInclude(f => f.SharedField)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == itemId && i.State == CatItemState.Published, ct)
        ?? throw new NotFoundException();

    private async Task<(IReadOnlyList<FieldAnswer> Answers, Guid? RequestedFor)> ValidateAsync(
        CatItem item, Guid requesterId, int quantity, Guid? requestedForId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> input, bool strict, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (quantity < 1 || quantity > item.MaxQuantity)
        {
            errors["quantity"] = [item.MaxQuantity == 1 ? "You can ask for one of these per request." : $"Ask for 1 to {item.MaxQuantity}."];
        }
        Guid? requestedFor = null;
        if (requestedForId is { } rf && rf != requesterId)
        {
            if (!item.AllowRequestedFor)
            {
                errors["requestedFor"] = ["This item can only be requested for yourself."];
            }
            else if (!await db.Users.AnyAsync(u => u.Id == rf && u.IsActive && !u.IsForgotten, ct))
            {
                errors["requestedFor"] = ["That person doesn't exist or is inactive."];
            }
            else
            {
                requestedFor = rf;
            }
        }

        var fields = item.Fields.OrderBy(f => f.Order).Select(FormField.From).ToList();
        var lookupIds = CatalogForm.LookupIds(fields, input);
        var known = await db.Users.AsNoTracking()
            .Where(u => lookupIds.Contains(u.Id) && u.IsActive && !u.IsForgotten)
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var result = CatalogForm.Validate(fields, input, known, strict);
        foreach (var (key, messages) in result.Errors)
        {
            errors[key] = messages;
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
        return (result.Answers, requestedFor);
    }

    private async Task<CatRequestedItem> AddRowsAsync(Ticket ticket, CatItem item, int quantity, IReadOnlyList<FieldAnswer> answers, Guid? requestedFor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var row = new CatRequestedItem
        {
            TicketId = ticket.Id,
            ItemId = item.Id,
            ItemName = item.Name,
            Icon = item.Icon,
            Quantity = quantity,
            AnswersJson = CatalogForm.Serialize(answers),
            UnitCostMinor = item.CostMinor,
            CostMinor = item.CostMinor * quantity,
            Currency = item.Currency,
            RequestedForId = requestedFor,
            StageChangedAt = now,
            CreatedAt = now,
        };
        db.Set<CatRequestedItem>().Add(row);
        db.Activity(row, "catalog_requested", quantity > 1 ? $"Requested {item.Name} (quantity {quantity})" : $"Requested {item.Name}");

        if (item.Kind == CatItemKind.Bundle && item.BundleItemIds.Count > 0)
        {
            var children = await db.Set<CatItem>().AsNoTracking().Where(i => item.BundleItemIds.Contains(i.Id)).OrderBy(i => i.Name).ToListAsync(ct);
            foreach (var child in children)
            {
                db.Set<CatRequestedItem>().Add(new CatRequestedItem
                {
                    TicketId = ticket.Id,
                    ItemId = child.Id,
                    ItemName = child.Name,
                    Icon = child.Icon,
                    ParentId = row.Id,
                    Quantity = quantity,
                    Currency = child.Currency,
                    RequestedForId = requestedFor,
                    StageChangedAt = now,
                    CreatedAt = now,
                });
            }
        }
        return row;
    }

    /// <summary>Starts approval when the item needs it; otherwise hands the ticket to the fulfillment group straight away.</summary>
    private async Task<AprRequest?> StartFulfillmentAsync(Ticket ticket, CatItem item, IReadOnlyList<FieldAnswer> answers,
        Guid requesterId, Guid? requestedFor, string subject, bool onBehalf, CancellationToken ct)
    {
        var actor = actors.Current;
        if (CatalogService.ParseApproval(item.ApprovalConfigJson) is { } config)
        {
            var lookups = answers
                .Where(a => a.Type == CatFieldType.UserLookup && a.Value?["id"]?.GetValue<string>() is { } id && Guid.TryParse(id, out _))
                .ToDictionary(a => a.Key, a => new ApprovalLookupValue(a.Label, [Guid.Parse(a.Value!["id"]!.GetValue<string>())]));
            var command = new RequestApprovalCommand
            {
                SubjectType = ApprovalSubjectTypes.Ticket,
                SubjectId = ticket.Id,
                WorkspaceId = ticket.WorkspaceId,
                Title = $"{ticket.DisplayId} {subject}",
                // Manager and department approvers are the recipient's, when someone asks on another person's behalf.
                RequesterId = requestedFor ?? requesterId,
                Config = config,
                Lookups = lookups,
                ConflictUserIds = [requesterId],
            };
            // Agents can manage the ticket and are recorded as having started the approval; requesters can't, so the catalog starts it.
            if (onBehalf && !actor.IsInternal)
            {
                return await approvals.RequestAsync(command, ct);
            }
            using (actors.BeginScope(Actor.System(SystemActorName, actor.Channel)))
            {
                return await approvals.RequestAsync(command, ct);
            }
        }
        if (item.FulfillmentGroupId is { } group)
        {
            using (actors.BeginScope(Actor.System(SystemActorName, actor.Channel)))
            {
                await tickets.UpdateAsync(ticket.Id, new TicketPatch { GroupId = group }, ct: ct);
            }
        }
        return null;
    }

    private async Task<AprRequest?> LatestApprovalAsync(Guid ticketId, CancellationToken ct) =>
        await db.Set<AprRequest>().AsNoTracking()
            .Include(r => r.Decisions)
            .Where(r => r.SubjectType == ApprovalSubjectTypes.Ticket && r.SubjectId == ticketId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var found = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        foreach (var id in list)
        {
            found.TryAdd(id, "Someone");
        }
        return found;
    }

    internal static string DescribeHtml(string? itemName, int quantity, IReadOnlyList<FieldAnswer> answers)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var html = new StringBuilder();
        if (itemName is not null)
        {
            html.Append($"<p>Requested from the service catalog: <strong>{E(itemName)}</strong>{(quantity > 1 ? $", quantity {quantity}" : "")}.</p>");
        }
        else if (quantity > 1)
        {
            html.Append($"<p>Quantity: {quantity}</p>");
        }
        if (answers.Count > 0)
        {
            html.Append("<ul>");
            foreach (var a in answers)
            {
                html.Append($"<li><strong>{E(a.Label)}:</strong> {E(a.Display)}</li>");
            }
            html.Append("</ul>");
        }
        return html.ToString();
    }
}

/// <summary>
/// Follows approval outcomes on catalog tickets (runs after commit, as the system). Approved: the fulfillment group is
/// assigned, which notifies it. Rejected: requested items are cancelled and the ticket is resolved with the reason.
/// Idempotent: a repeat finds the work done and changes nothing.
/// </summary>
internal sealed class CatalogApprovalFollowUp(SwitchboardDbContext db, ITicketService tickets) : IEventHandler<ApprovalDecided>
{
    public async Task HandleAsync(ApprovalDecided e, CancellationToken ct)
    {
        if (e.SubjectType != ApprovalSubjectTypes.Ticket || e.Status is not (ApprovalStatus.Approved or ApprovalStatus.Rejected))
        {
            return;
        }
        var rows = await db.Set<CatRequestedItem>().Where(r => r.TicketId == e.SubjectId).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return;
        }
        // A newer approval (after a resubmit) supersedes this one.
        var latestId = await db.Set<AprRequest>().AsNoTracking()
            .Where(r => r.SubjectType == ApprovalSubjectTypes.Ticket && r.SubjectId == e.SubjectId)
            .OrderByDescending(r => r.CreatedAt).Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (latestId != e.ApprovalId)
        {
            return;
        }
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Status).FirstOrDefaultAsync(t => t.Id == e.SubjectId, ct);
        if (ticket is null || ticket.DeletedAt is not null || ticket.MergedIntoId is not null)
        {
            return;
        }

        if (e.Status == ApprovalStatus.Approved)
        {
            var itemIds = rows.Where(r => r.ParentId == null).Select(r => r.ItemId).ToList();
            var group = await db.Set<CatItem>().AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.FulfillmentGroupId != null)
                .Select(i => i.FulfillmentGroupId).FirstOrDefaultAsync(ct);
            if (group is not null && ticket.GroupId is null)
            {
                await tickets.UpdateAsync(ticket.Id, new TicketPatch { GroupId = group }, ct: ct);
            }
            return;
        }

        // Cancelling the items, the note and the resolution commit together, so a retry never repeats the note.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var row in rows.Where(r => r.Stage == RequestedItemStage.Requested))
        {
            db.Activity(row, "catalog_stage_changed", $"{row.ItemName} was cancelled because the request wasn't approved",
                [new FieldChange("Stage", CatalogRequestService.StageLabel(row.Stage), CatalogRequestService.StageLabel(RequestedItemStage.Cancelled))]);
            row.Stage = RequestedItemStage.Cancelled;
            row.StageChangedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        if (ticket.Status!.Category is StatusCategory.Resolved or StatusCategory.Closed)
        {
            await tx.CommitAsync(ct);
            return;
        }
        var resolved = await db.TicketStatuses.AsNoTracking()
            .Where(s => s.WorkspaceId == ticket.WorkspaceId && s.Category == StatusCategory.Resolved)
            .OrderBy(s => s.Order).FirstOrDefaultAsync(ct);
        var reason = string.IsNullOrWhiteSpace(e.Comment) ? "No reason was given." : e.Comment;
        await tickets.AddNoteAsync(ticket.Id, new NoteCommand
        {
            IsPrivate = false,
            BodyHtml = $"<p>This request wasn't approved.</p><blockquote>{WebUtility.HtmlEncode(reason)}</blockquote>"
                + "<p>You can change the request and send it again from the portal.</p>",
        }, ct);
        if (resolved is not null)
        {
            await tickets.UpdateAsync(ticket.Id, new TicketPatch { StatusId = resolved.Id }, ct: ct);
        }
        await tx.CommitAsync(ct);
    }
}
