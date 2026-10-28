using Switchboard.Modules.Approvals.Domain;

namespace Switchboard.Modules.Approvals.Services;

public enum LevelOutcome
{
    /// <summary>An earlier level is still open or was rejected.</summary>
    NotStarted = 0,
    Pending = 1,
    Approved = 2,
    Rejected = 3,
}

public sealed record LevelInput(int Order, ApprovalRule Rule, IReadOnlyCollection<Guid> ApproverIds);

/// <summary>A decision that counts for <see cref="ApproverId"/> on the level with order <see cref="LevelOrder"/>.</summary>
public sealed record DecisionInput(int LevelOrder, Guid ApproverId, ApprovalVerdict Verdict);

public sealed record LevelEvaluation(int Order, LevelOutcome Outcome, int Approvers, int Approvals, int Rejections, int Needed);

public sealed record ApprovalEvaluation(ApprovalStatus Status, int? CurrentLevel, IReadOnlyList<LevelEvaluation> Levels);

/// <summary>
/// Approval status as a pure function of levels, rules and recorded decisions (INV-APR-2).
/// </summary>
/// <remarks>
/// A level needs <see cref="Needed"/> approvals: one for <see cref="ApprovalRule.Any"/>, all for
/// <see cref="ApprovalRule.Everyone"/>, more than half for <see cref="ApprovalRule.Majority"/>. It is rejected as
/// soon as too few approvers are left undecided to reach that number. Levels run in order, so the request is
/// Approved only when every level is approved, and Rejected when the first unfinished level is rejected.
/// Only one decision per designated approver counts (the first); decisions by anyone else are ignored.
/// </remarks>
public static class ApprovalEvaluator
{
    public static int Needed(ApprovalRule rule, int approvers) => rule switch
    {
        ApprovalRule.Any => 1,
        ApprovalRule.Everyone => approvers,
        ApprovalRule.Majority => approvers / 2 + 1,
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    public static LevelOutcome EvaluateLevel(ApprovalRule rule, int approvers, int approvals, int rejections)
    {
        if (approvers <= 0)
        {
            // Never created by the service; a level nobody can decide stays open rather than passing.
            return LevelOutcome.Pending;
        }
        var needed = Needed(rule, approvers);
        if (approvals >= needed)
        {
            return LevelOutcome.Approved;
        }
        return approvers - rejections < needed ? LevelOutcome.Rejected : LevelOutcome.Pending;
    }

    public static ApprovalEvaluation Evaluate(IEnumerable<LevelInput> levels, IEnumerable<DecisionInput> decisions, bool cancelled)
    {
        var ordered = levels.OrderBy(l => l.Order).ToList();
        var byLevel = decisions
            .GroupBy(d => d.LevelOrder)
            .ToDictionary(g => g.Key, g => g.ToList());

        var results = new List<LevelEvaluation>(ordered.Count);
        ApprovalStatus? status = null;
        int? current = null;

        foreach (var level in ordered)
        {
            var designated = level.ApproverIds.ToHashSet();
            var counted = new Dictionary<Guid, ApprovalVerdict>();
            foreach (var d in byLevel.GetValueOrDefault(level.Order) ?? [])
            {
                if (designated.Contains(d.ApproverId))
                {
                    counted.TryAdd(d.ApproverId, d.Verdict);
                }
            }
            var approvals = counted.Values.Count(v => v == ApprovalVerdict.Approved);
            var rejections = counted.Values.Count(v => v == ApprovalVerdict.Rejected);
            var needed = designated.Count == 0 ? 1 : Needed(level.Rule, designated.Count);

            var outcome = status is null ? EvaluateLevel(level.Rule, designated.Count, approvals, rejections) : LevelOutcome.NotStarted;
            results.Add(new LevelEvaluation(level.Order, outcome, designated.Count, approvals, rejections, needed));

            if (status is not null)
            {
                continue;
            }
            if (outcome == LevelOutcome.Rejected)
            {
                status = ApprovalStatus.Rejected;
            }
            else if (outcome == LevelOutcome.Pending)
            {
                status = ApprovalStatus.Requested;
                current = level.Order;
            }
        }

        // No levels at all is never treated as approval.
        status ??= ordered.Count == 0 ? ApprovalStatus.Requested : ApprovalStatus.Approved;
        if (cancelled)
        {
            return new ApprovalEvaluation(ApprovalStatus.Cancelled, null, results);
        }
        return new ApprovalEvaluation(status.Value, status == ApprovalStatus.Requested ? current : null, results);
    }

    /// <summary>Evaluates a loaded request (levels, approvers and decisions included).</summary>
    public static ApprovalEvaluation Evaluate(AprRequest request)
    {
        var orderById = request.Levels.ToDictionary(l => l.Id, l => l.Order);
        return Evaluate(
            request.Levels.Select(l => new LevelInput(l.Order, l.Rule, l.Approvers.Select(a => a.UserId).ToList())),
            request.Decisions
                .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
                .Where(d => orderById.ContainsKey(d.LevelId))
                .Select(d => new DecisionInput(orderById[d.LevelId], d.ApproverId, d.Verdict)),
            request.CancelledAt is not null);
    }
}
