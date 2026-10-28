using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Approvals.Domain;

namespace Switchboard.Modules.Approvals.Services;

public sealed record RequestApprovalCommand
{
    public string SubjectType { get; init; } = "";
    public Guid SubjectId { get; init; }
    public Guid? WorkspaceId { get; init; }
    /// <summary>What approvers see, e.g. "SR-1042 New laptop for Marcus Chen".</summary>
    public string Title { get; init; } = "";
    /// <summary>The person the approval is for. Manager and department sources resolve from them.</summary>
    public Guid RequesterId { get; init; }
    public ApprovalConfig Config { get; init; } = new();
    public IReadOnlyDictionary<string, ApprovalLookupValue>? Lookups { get; init; }
    /// <summary>Others who may not decide unless self-approval is allowed, e.g. a change's assignee (FR-APR-8).</summary>
    public IReadOnlyList<Guid>? ConflictUserIds { get; init; }
}

public sealed record DecideApprovalCommand
{
    public ApprovalVerdict Verdict { get; init; }
    public string? Comment { get; init; }
    /// <summary>When the actor covers for several approvers of the level, whose decision this is.</summary>
    public Guid? OnBehalfOfId { get; init; }
}

public sealed record PersonSummary(Guid Id, string Name, string? Email, bool IsAgent);

public sealed record ApprovalListItem(
    Guid Id,
    string Title,
    string SubjectType,
    Guid SubjectId,
    ApprovalStatus Status,
    int? CurrentLevel,
    int LevelCount,
    Guid RequesterId,
    string RequesterName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? WaitingSince,
    /// <summary>For pending items the current user covers as a delegate: whose approval it is.</summary>
    string? OnBehalfOfName);

/// <summary>A loaded approval request with everything the detail views need, trimmed to the current viewer.</summary>
public sealed class ApprovalDetails
{
    public required AprRequest Request { get; init; }
    public required ApprovalEvaluation Evaluation { get; init; }
    public required IReadOnlyDictionary<Guid, PersonSummary> People { get; init; }
    public required ApprovalSubjectInfo Subject { get; init; }
    /// <summary>Approvers of the current level the viewer may decide for (themselves and/or people they cover for).</summary>
    public required IReadOnlyList<Guid> CanDecideFor { get; init; }
    /// <summary>Why the viewer can't decide although they are involved, e.g. self-approval.</summary>
    public string? DecideBlockedReason { get; init; }
    public required bool CanManage { get; init; }
    /// <summary>Active delegates of approvers on the current level (approver id to delegate id).</summary>
    public required IReadOnlyDictionary<Guid, Guid> Delegates { get; init; }

    public string NameOf(Guid? id) => id is { } g && People.TryGetValue(g, out var p) ? p.Name : "Someone";

    public AprLevel? CurrentLevel => Request.Levels.FirstOrDefault(l => l.Order == Request.CurrentLevel);
}

/// <summary>
/// The approval engine (FR-APR-1..8). Any module can request approval on its records; status is a pure function
/// of levels, rules and decisions (INV-APR-2); decisions are immutable (INV-APR-1); only designated approvers or
/// their active delegates decide (INV-APR-3); requesters can't approve their own requests by default (SR-APR-1).
/// Every method that changes data saves.
/// </summary>
public interface IApprovalService
{
    Task<AprRequest> RequestAsync(RequestApprovalCommand command, CancellationToken ct = default);
    Task<AprDecision> DecideAsync(Guid approvalId, DecideApprovalCommand command, CancellationToken ct = default);
    Task CancelAsync(Guid approvalId, string? reason, CancellationToken ct = default);
    /// <summary>Emails the approvers who haven't decided the current level. Returns how many were reminded.</summary>
    Task<int> RemindAsync(Guid approvalId, CancellationToken ct = default);

    /// <summary>One request the current actor may see, or <see cref="NotFoundException"/>.</summary>
    Task<ApprovalDetails> GetAsync(Guid approvalId, CancellationToken ct = default);
    /// <summary>Requests on one record, newest first, if the actor can see the record.</summary>
    Task<IReadOnlyList<ApprovalDetails>> ListForSubjectAsync(string subjectType, Guid subjectId, CancellationToken ct = default);

    /// <summary>Requests waiting on the current user, directly or as a delegate.</summary>
    Task<IReadOnlyList<ApprovalListItem>> ListPendingAsync(int take = 50, CancellationToken ct = default);
    Task<int> CountPendingAsync(CancellationToken ct = default);
    /// <summary>Requests the current user decided.</summary>
    Task<IReadOnlyList<ApprovalListItem>> ListDecidedAsync(int take = 50, CancellationToken ct = default);
    /// <summary>Requests made by or for the current user.</summary>
    Task<IReadOnlyList<ApprovalListItem>> ListRequestedAsync(int take = 50, CancellationToken ct = default);

    ApprovalEvaluation Evaluate(AprRequest request);
}

/// <summary>Raised when approvers are reminded, so emails go out after commit.</summary>
public sealed record ApprovalReminded(Guid ApprovalId, int Level, IReadOnlyList<Guid> ApproverIds) : DomainEvent;

internal sealed class ApprovalService(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IEnumerable<IApprovalSubjectPolicy> policies,
    IApprovalDelegationService delegation,
    ApproverResolver resolver,
    ISettingsService settings,
    TimeProvider time) : IApprovalService
{
    public const int MaxLevels = 10;
    public const int MaxComment = 2000;
    private static readonly TimeSpan ManualReminderGap = TimeSpan.FromHours(1);

    private DbSet<AprRequest> Requests => db.Set<AprRequest>();

    // ---------------------------------------------------------------- request

    public async Task<AprRequest> RequestAsync(RequestApprovalCommand command, CancellationToken ct = default)
    {
        var actor = actors.Current;
        var policy = Policy(command.SubjectType) ?? throw new ValidationException("subject", "Approvals aren't available for this kind of record.");
        if (!actor.IsInternal && !await policy.CanManageAsync(command.SubjectId, ct))
        {
            throw new ForbiddenException();
        }
        var title = (command.Title ?? "").Trim();
        if (title.Length == 0)
        {
            throw new ValidationException("title", "Give the approval a title.");
        }
        if (command.Config.Levels.Count == 0)
        {
            throw new ValidationException("levels", "Add at least one approval level.");
        }
        if (command.Config.Levels.Count > MaxLevels)
        {
            throw new ValidationException("levels", $"Use at most {MaxLevels} approval levels.");
        }
        if (await Requests.AnyAsync(r => r.SubjectType == command.SubjectType && r.SubjectId == command.SubjectId && r.Status == ApprovalStatus.Requested, ct))
        {
            throw new ValidationException("approval", "This already has an approval in progress. Cancel it before requesting a new one.");
        }

        var allowSelf = await settings.GetAsync(ApprovalSettingKeys.AllowSelfApproval, ApprovalSettingKeys.AllowSelfApprovalDefault, command.WorkspaceId, ct);
        var conflicts = new HashSet<Guid> { command.RequesterId };
        if (actor.UserId is { } starter && !actor.IsInternal)
        {
            conflicts.Add(starter);
        }
        foreach (var id in command.ConflictUserIds ?? [])
        {
            conflicts.Add(id);
        }

        var resolved = await resolver.ResolveAsync(command.Config, command.RequesterId, command.Lookups, allowSelf ? new HashSet<Guid>() : conflicts, ct);
        var now = time.GetUtcNow();
        var request = new AprRequest
        {
            SubjectType = command.SubjectType,
            SubjectId = command.SubjectId,
            WorkspaceId = command.WorkspaceId,
            Title = title.Length > 300 ? title[..300] : title,
            RequesterId = command.RequesterId,
            RequestedById = actor.IsInternal ? null : actor.UserId,
            ConfigJson = JsonSerializer.Serialize(command.Config, JsonDefaults.Options),
            ConflictUserIds = conflicts.ToList(),
            AllowSelfApproval = allowSelf,
            CreatedAt = now,
            UpdatedAt = now,
            Levels = resolved.Select(l => new AprLevel
            {
                Order = l.Order,
                Name = l.Name,
                Rule = l.Rule,
                Approvers = l.Approvers.Select(a => new AprApprover { UserId = a.UserId, Source = a.Source }).ToList(),
            }).ToList(),
        };
        var evaluation = ApprovalEvaluator.Evaluate(request);
        request.Status = evaluation.Status;
        request.CurrentLevel = evaluation.CurrentLevel;
        request.WaitingSince = now;
        Requests.Add(request);

        var first = request.Levels.First(l => l.Order == request.CurrentLevel);
        var names = await NamesAsync(first.Approvers.Select(a => a.UserId), ct);
        db.Activity(request, "approval_requested", $"Approval requested from {JoinNames(first.Approvers.Select(a => names[a.UserId]), first.Rule)}");
        db.Raise(new ApprovalRequested(request.Id, request.SubjectType, request.SubjectId, request.WorkspaceId, request.RequesterId,
            first.Order, first.Approvers.Select(a => a.UserId).ToList()));
        await db.SaveChangesAsync(ct);
        return request;
    }

    // ---------------------------------------------------------------- decide

    public async Task<AprDecision> DecideAsync(Guid approvalId, DecideApprovalCommand command, CancellationToken ct = default)
    {
        var actor = actors.Current;
        if (actor.IsInternal || actor.UserId is not { } uid)
        {
            throw new ForbiddenException("Approvals need a decision from a person.");
        }
        var now = time.GetUtcNow();
        var request = await LoadAsync(approvalId, tracked: true, ct) ?? throw new NotFoundException();
        var delegators = await delegation.DelegatorsOfAsync(uid, now, ct);
        if (!await CanViewAsync(request, uid, delegators, ct))
        {
            throw new NotFoundException();
        }
        if (request.Status != ApprovalStatus.Requested)
        {
            throw new ValidationException("approval", $"This request is already {StatusWord(request.Status)}.");
        }
        if (!request.AllowSelfApproval && request.ConflictUserIds.Contains(uid))
        {
            // SR-APR-1: enforced here, for every channel.
            throw new ForbiddenException("You can't decide on a request you made. Someone else has to approve it.");
        }

        var level = request.Levels.First(l => l.Order == request.CurrentLevel);
        var pending = level.Approvers
            .Where(a => request.Decisions.All(d => d.LevelId != level.Id || d.ApproverId != a.UserId))
            .Select(a => a.UserId)
            .ToList();
        var candidates = pending.Where(id => id == uid || delegators.Contains(id)).ToList();

        Guid approverId;
        if (command.OnBehalfOfId is { } chosen)
        {
            approverId = candidates.Contains(chosen) ? chosen : throw new ForbiddenException("You can't decide for that person on this request.");
        }
        else if (candidates.Contains(uid))
        {
            approverId = uid;
        }
        else if (candidates.Count == 1)
        {
            approverId = candidates[0];
        }
        else if (candidates.Count > 1)
        {
            throw new ValidationException("onBehalfOf", "You cover approvals for more than one approver here. Choose whose decision this is.");
        }
        else if (level.Approvers.Any(a => a.UserId == uid))
        {
            throw new ValidationException("approval", "You've already decided this level.");
        }
        else
        {
            throw new ForbiddenException("Only this level's approvers, or their delegates, can decide.");
        }
        if (!request.AllowSelfApproval && request.ConflictUserIds.Contains(approverId))
        {
            throw new ForbiddenException("This approver made the request, so their approval can't count.");
        }

        var comment = string.IsNullOrWhiteSpace(command.Comment) ? null : command.Comment.Trim();
        if (comment is { Length: > MaxComment })
        {
            throw new ValidationException("comment", $"Keep the comment under {MaxComment} characters.");
        }
        if (command.Verdict == ApprovalVerdict.Rejected && comment is null)
        {
            throw new ValidationException("comment", "Say why you're rejecting it, so the requester knows what to change.");
        }
        if (!Enum.IsDefined(command.Verdict))
        {
            throw new ValidationException("verdict", "Choose approve or reject.");
        }

        var decision = new AprDecision
        {
            RequestId = request.Id,
            LevelId = level.Id,
            ApproverId = approverId,
            DecidedById = uid,
            OnBehalfOfId = approverId == uid ? null : approverId,
            Verdict = command.Verdict,
            Comment = comment,
            Channel = actor.Channel,
            ClientName = actor.ClientName,
            CreatedAt = now,
        };
        db.Set<AprDecision>().Add(decision);
        request.Decisions.Add(decision);
        request.DecisionCount++;

        var before = request.CurrentLevel;
        var evaluation = ApprovalEvaluator.Evaluate(request);
        request.Status = evaluation.Status;
        request.CurrentLevel = evaluation.CurrentLevel;
        if (request.Status != ApprovalStatus.Requested)
        {
            request.CompletedAt = now;
            request.WaitingSince = null;
        }
        else if (request.CurrentLevel != before)
        {
            request.WaitingSince = now;
        }

        var names = await NamesAsync([uid, approverId, .. request.Levels.SelectMany(l => l.Approvers.Select(a => a.UserId))], ct);
        var who = approverId == uid ? names[uid] : $"{names[uid]}, on behalf of {names[approverId]},";
        var verb = command.Verdict == ApprovalVerdict.Approved ? "approved" : "rejected";
        var summary = request.Status switch
        {
            ApprovalStatus.Approved => $"{who} {verb}. The request is approved.",
            ApprovalStatus.Rejected => $"{who} {verb}. The request is rejected.",
            _ when request.CurrentLevel != before => $"{who} {verb}. Level {request.CurrentLevel} is next.",
            _ => $"{who} {verb}.",
        };
        db.Activity(request, command.Verdict == ApprovalVerdict.Approved ? "approval_approved" : "approval_rejected", summary);

        db.Raise(new ApprovalDecided(request.Id, request.SubjectType, request.SubjectId, request.WorkspaceId, decision.Id,
            approverId, uid, decision.OnBehalfOfId, decision.Verdict, request.Status, comment));
        if (request.Status == ApprovalStatus.Requested && request.CurrentLevel != before)
        {
            var next = request.Levels.First(l => l.Order == request.CurrentLevel);
            db.Raise(new ApprovalRequested(request.Id, request.SubjectType, request.SubjectId, request.WorkspaceId, request.RequesterId,
                next.Order, next.Approvers.Select(a => a.UserId).ToList()));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Someone else decided at the same moment. Reload and try again.");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_AprDecisions_LevelId_ApproverId", StringComparison.Ordinal) == true)
        {
            throw new ConflictException("That approval was just decided. Reload to see the decision.");
        }
        return decision;
    }

    // ---------------------------------------------------------------- cancel and remind

    public async Task CancelAsync(Guid approvalId, string? reason, CancellationToken ct = default)
    {
        var actor = actors.Current;
        var request = await LoadAsync(approvalId, tracked: true, ct) ?? throw new NotFoundException();
        if (!actor.IsInternal && !await CanManageAsync(request, ct))
        {
            throw await CanViewAsync(request, actor.UserId, await DelegatorsAsync(actor, ct), ct) ? new ForbiddenException() : new NotFoundException();
        }
        if (request.Status != ApprovalStatus.Requested)
        {
            throw new ValidationException("approval", $"This request is already {StatusWord(request.Status)}.");
        }
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (trimmed is { Length: > 1000 })
        {
            throw new ValidationException("reason", "Keep the reason under 1000 characters.");
        }

        var now = time.GetUtcNow();
        request.CancelledAt = now;
        request.CancelledById = actor.IsInternal ? null : actor.UserId;
        request.CancelReason = trimmed;
        request.CompletedAt = now;
        var evaluation = ApprovalEvaluator.Evaluate(request);
        request.Status = evaluation.Status;
        request.CurrentLevel = evaluation.CurrentLevel;
        request.WaitingSince = null;
        db.Activity(request, "approval_cancelled", trimmed is null ? "Approval cancelled" : $"Approval cancelled: {Truncate(trimmed, 200)}");
        db.Raise(new ApprovalCancelled(request.Id, request.SubjectType, request.SubjectId, request.WorkspaceId, trimmed));
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> RemindAsync(Guid approvalId, CancellationToken ct = default)
    {
        var actor = actors.Current;
        var request = await LoadAsync(approvalId, tracked: true, ct) ?? throw new NotFoundException();
        if (!actor.IsInternal)
        {
            var involved = actor.UserId is { } uid && (uid == request.RequesterId || uid == request.RequestedById);
            if (!involved && !await CanManageAsync(request, ct))
            {
                throw await CanViewAsync(request, actor.UserId, await DelegatorsAsync(actor, ct), ct) ? new ForbiddenException() : new NotFoundException();
            }
        }
        if (request.Status != ApprovalStatus.Requested)
        {
            throw new ValidationException("approval", $"This request is already {StatusWord(request.Status)}.");
        }
        var now = time.GetUtcNow();
        if (!actor.IsInternal && request.LastRemindedAt is { } last && now - last < ManualReminderGap)
        {
            throw new ValidationException("approval", "A reminder went out less than an hour ago. Give the approvers some time.");
        }

        var level = request.Levels.First(l => l.Order == request.CurrentLevel);
        var waiting = level.Approvers
            .Where(a => request.Decisions.All(d => d.LevelId != level.Id || d.ApproverId != a.UserId))
            .Select(a => a.UserId)
            .ToList();
        if (waiting.Count == 0)
        {
            return 0;
        }
        request.LastRemindedAt = now;
        request.ReminderCount++;
        var names = await NamesAsync(waiting, ct);
        db.Activity(request, "approval_reminded", $"Reminder sent to {string.Join(", ", waiting.Select(id => names[id]))}");
        db.Raise(new ApprovalReminded(request.Id, level.Order, waiting));
        await db.SaveChangesAsync(ct);
        return waiting.Count;
    }

    // ---------------------------------------------------------------- read

    public async Task<ApprovalDetails> GetAsync(Guid approvalId, CancellationToken ct = default)
    {
        var actor = actors.Current;
        var request = await LoadAsync(approvalId, tracked: false, ct) ?? throw new NotFoundException();
        var delegators = await DelegatorsAsync(actor, ct);
        if (!actor.IsInternal && !await CanViewAsync(request, actor.UserId, delegators, ct))
        {
            throw new NotFoundException();
        }
        return await DetailsAsync(request, actor, delegators, ct);
    }

    public async Task<IReadOnlyList<ApprovalDetails>> ListForSubjectAsync(string subjectType, Guid subjectId, CancellationToken ct = default)
    {
        var actor = actors.Current;
        var policy = Policy(subjectType);
        if (!actor.IsInternal && (policy is null || !await policy.CanViewAsync(subjectId, ct)))
        {
            return [];
        }
        var ids = await Requests.AsNoTracking()
            .Where(r => r.SubjectType == subjectType && r.SubjectId == subjectId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.Id)
            .Take(20)
            .ToListAsync(ct);
        var delegators = await DelegatorsAsync(actor, ct);
        var result = new List<ApprovalDetails>();
        foreach (var id in ids)
        {
            var request = await LoadAsync(id, tracked: false, ct);
            if (request is not null)
            {
                result.Add(await DetailsAsync(request, actor, delegators, ct));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<ApprovalListItem>> ListPendingAsync(int take = 50, CancellationToken ct = default)
    {
        if (await PendingQueryAsync(ct) is not { } pending)
        {
            return [];
        }
        var (query, uid, delegators) = pending;
        var rows = await query
            .OrderBy(r => r.WaitingSince)
            .Take(Math.Clamp(take, 1, 200))
            .Select(r => new
            {
                Request = r,
                LevelCount = r.Levels.Count,
                ApproverIds = r.Levels.Where(l => l.Order == r.CurrentLevel).SelectMany(l => l.Approvers)
                    .Where(a => !r.Decisions.Any(d => d.LevelId == a.LevelId && d.ApproverId == a.UserId))
                    .Select(a => a.UserId).ToList(),
            })
            .ToListAsync(ct);
        var names = await NamesAsync(rows.SelectMany(r => r.ApproverIds.Append(r.Request.RequesterId)), ct);
        return rows.Select(r =>
        {
            var forWhom = r.ApproverIds.Contains(uid) ? Guid.Empty : r.ApproverIds.FirstOrDefault(delegators.Contains);
            return ToItem(r.Request, r.LevelCount, names, forWhom != Guid.Empty ? names[forWhom] : null);
        }).ToList();
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default) =>
        await PendingQueryAsync(ct) is { } pending ? await pending.Query.CountAsync(ct) : 0;

    public async Task<IReadOnlyList<ApprovalListItem>> ListDecidedAsync(int take = 50, CancellationToken ct = default)
    {
        if (CurrentUser() is not { } uid)
        {
            return [];
        }
        return await ListAsync(Requests.AsNoTracking().Where(r => r.Decisions.Any(d => d.DecidedById == uid)), take, ct);
    }

    public async Task<IReadOnlyList<ApprovalListItem>> ListRequestedAsync(int take = 50, CancellationToken ct = default)
    {
        if (CurrentUser() is not { } uid)
        {
            return [];
        }
        return await ListAsync(Requests.AsNoTracking().Where(r => r.RequesterId == uid || r.RequestedById == uid), take, ct);
    }

    public ApprovalEvaluation Evaluate(AprRequest request) => ApprovalEvaluator.Evaluate(request);

    // ---------------------------------------------------------------- helpers

    private async Task<(IQueryable<AprRequest> Query, Guid UserId, IReadOnlyList<Guid> Delegators)?> PendingQueryAsync(CancellationToken ct)
    {
        if (CurrentUser() is not { } uid)
        {
            return null;
        }
        var delegators = await delegation.DelegatorsOfAsync(uid, time.GetUtcNow(), ct);
        var ids = delegators.Append(uid).ToList();
        var query = Requests.AsNoTracking().Where(r =>
            r.Status == ApprovalStatus.Requested
            && r.Levels.Any(l => l.Order == r.CurrentLevel
                && l.Approvers.Any(a => ids.Contains(a.UserId)
                    && !r.Decisions.Any(d => d.LevelId == l.Id && d.ApproverId == a.UserId))));
        return (query, uid, delegators);
    }

    private async Task<IReadOnlyList<ApprovalListItem>> ListAsync(IQueryable<AprRequest> query, int take, CancellationToken ct)
    {
        var rows = await query
            .OrderByDescending(r => r.UpdatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .Select(r => new { Request = r, LevelCount = r.Levels.Count })
            .ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.Request.RequesterId), ct);
        return rows.Select(r => ToItem(r.Request, r.LevelCount, names, null)).ToList();
    }

    private static ApprovalListItem ToItem(AprRequest r, int levelCount, IReadOnlyDictionary<Guid, string> names, string? onBehalfOf) =>
        new(r.Id, r.Title, r.SubjectType, r.SubjectId, r.Status, r.CurrentLevel, levelCount, r.RequesterId,
            names.GetValueOrDefault(r.RequesterId, "Someone"), r.CreatedAt, r.UpdatedAt, r.WaitingSince, onBehalfOf);

    private async Task<ApprovalDetails> DetailsAsync(AprRequest request, Actor actor, IReadOnlyList<Guid> delegators, CancellationToken ct)
    {
        var evaluation = ApprovalEvaluator.Evaluate(request);
        var uid = actor.IsInternal ? null : actor.UserId;
        var canDecideFor = new List<Guid>();
        string? blocked = null;
        IReadOnlyDictionary<Guid, Guid> delegates = new Dictionary<Guid, Guid>();

        if (request.Status == ApprovalStatus.Requested && request.Levels.FirstOrDefault(l => l.Order == request.CurrentLevel) is { } level)
        {
            var pending = level.Approvers
                .Where(a => request.Decisions.All(d => d.LevelId != level.Id || d.ApproverId != a.UserId))
                .Select(a => a.UserId)
                .ToList();
            delegates = await delegation.DelegatesOfAsync(pending, time.GetUtcNow(), ct);
            if (uid is { } me)
            {
                var candidates = pending.Where(id => id == me || delegators.Contains(id)).ToList();
                if (candidates.Count > 0 && !request.AllowSelfApproval && request.ConflictUserIds.Contains(me))
                {
                    blocked = "You made this request, so someone else has to approve it.";
                }
                else
                {
                    canDecideFor.AddRange(candidates.Where(id => request.AllowSelfApproval || !request.ConflictUserIds.Contains(id)));
                }
            }
        }

        var peopleIds = request.Levels.SelectMany(l => l.Approvers.Select(a => a.UserId))
            .Concat(request.Decisions.SelectMany(d => new[] { d.ApproverId, d.DecidedById }))
            .Concat(delegates.Values)
            .Append(request.RequesterId)
            .Concat(new[] { request.RequestedById, request.CancelledById }.OfType<Guid>())
            .Distinct()
            .ToList();
        var people = await db.Users.AsNoTracking()
            .Where(u => peopleIds.Contains(u.Id))
            .Select(u => new PersonSummary(u.Id, u.DisplayName, u.Email, u.IsAgent || u.IsOrgAdmin))
            .ToDictionaryAsync(p => p.Id, ct);

        var policy = Policy(request.SubjectType);
        return new ApprovalDetails
        {
            Request = request,
            Evaluation = evaluation,
            People = people,
            Subject = policy is null ? new ApprovalSubjectInfo(null, null) : await policy.DescribeAsync(request.SubjectId, ct),
            CanDecideFor = canDecideFor,
            DecideBlockedReason = blocked,
            CanManage = request.Status == ApprovalStatus.Requested && (actor.IsInternal || await CanManageAsync(request, ct)),
            Delegates = delegates,
        };
    }

    private async Task<AprRequest?> LoadAsync(Guid id, bool tracked, CancellationToken ct)
    {
        var query = Requests
            .Include(r => r.Levels).ThenInclude(l => l.Approvers)
            .Include(r => r.Decisions)
            .AsSplitQuery();
        return await (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    /// <summary>Approvers, their delegates, deciders, the requester and whoever can see the record (INV-APR-3 for viewing).</summary>
    private async Task<bool> CanViewAsync(AprRequest request, Guid? uid, IReadOnlyList<Guid> delegators, CancellationToken ct)
    {
        if (uid is { } me)
        {
            if (me == request.RequesterId || me == request.RequestedById
                || request.Decisions.Any(d => d.DecidedById == me)
                || request.Levels.Any(l => l.Approvers.Any(a => a.UserId == me || delegators.Contains(a.UserId))))
            {
                return true;
            }
        }
        return Policy(request.SubjectType) is { } policy && await policy.CanViewAsync(request.SubjectId, ct);
    }

    private async Task<bool> CanManageAsync(AprRequest request, CancellationToken ct) =>
        Policy(request.SubjectType) is { } policy && await policy.CanManageAsync(request.SubjectId, ct);

    private async Task<IReadOnlyList<Guid>> DelegatorsAsync(Actor actor, CancellationToken ct) =>
        !actor.IsInternal && actor.UserId is { } uid ? await delegation.DelegatorsOfAsync(uid, time.GetUtcNow(), ct) : [];

    private IApprovalSubjectPolicy? Policy(string subjectType) =>
        policies.FirstOrDefault(p => string.Equals(p.SubjectType, subjectType, StringComparison.Ordinal));

    private Guid? CurrentUser()
    {
        var actor = actors.Current;
        return actor.IsInternal ? null : actor.UserId;
    }

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

    private static string JoinNames(IEnumerable<string> names, ApprovalRule rule)
    {
        var list = names.ToList();
        if (list.Count <= 1)
        {
            return list.FirstOrDefault() ?? "nobody";
        }
        var joined = string.Join(", ", list.Take(list.Count - 1)) + (rule == ApprovalRule.Any ? " or " : " and ") + list[^1];
        return rule == ApprovalRule.Majority ? $"a majority of {joined}" : joined;
    }

    internal static string StatusWord(ApprovalStatus status) => status switch
    {
        ApprovalStatus.Approved => "approved",
        ApprovalStatus.Rejected => "rejected",
        ApprovalStatus.Cancelled => "cancelled",
        _ => "waiting for approval",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
