using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;

namespace Switchboard.Modules.Approvals.Tests;

/// <summary>INV-APR-2: status is a pure function of levels, rules and decisions. Checked exhaustively for small chains.</summary>
public sealed class ApprovalEvaluatorTests
{
    private static readonly ApprovalRule[] Rules = [ApprovalRule.Any, ApprovalRule.Everyone, ApprovalRule.Majority];

    /// <summary>The rules as written in the SRS, independent of the implementation's threshold arithmetic.</summary>
    private static LevelOutcome Expected(ApprovalRule rule, int n, int approvals, int rejections) => rule switch
    {
        ApprovalRule.Any => approvals >= 1 ? LevelOutcome.Approved : rejections == n ? LevelOutcome.Rejected : LevelOutcome.Pending,
        ApprovalRule.Everyone => approvals == n ? LevelOutcome.Approved : rejections >= 1 ? LevelOutcome.Rejected : LevelOutcome.Pending,
        ApprovalRule.Majority => 2 * approvals > n ? LevelOutcome.Approved : 2 * rejections >= n ? LevelOutcome.Rejected : LevelOutcome.Pending,
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    private static IEnumerable<(int Approvals, int Rejections)> Splits(int n)
    {
        for (var a = 0; a <= n; a++)
        {
            for (var r = 0; a + r <= n; r++)
            {
                yield return (a, r);
            }
        }
    }

    private static (LevelInput Level, List<DecisionInput> Decisions) Level(int order, ApprovalRule rule, int n, int approvals, int rejections)
    {
        var approvers = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();
        var decisions = approvers.Take(approvals).Select(id => new DecisionInput(order, id, ApprovalVerdict.Approved))
            .Concat(approvers.Skip(approvals).Take(rejections).Select(id => new DecisionInput(order, id, ApprovalVerdict.Rejected)))
            .ToList();
        return (new LevelInput(order, rule, approvers), decisions);
    }

    [Fact]
    public void Every_single_level_combination_matches_the_rule()
    {
        foreach (var rule in Rules)
        {
            for (var n = 1; n <= 6; n++)
            {
                foreach (var (a, r) in Splits(n))
                {
                    var expected = Expected(rule, n, a, r);
                    Assert.Equal(expected, ApprovalEvaluator.EvaluateLevel(rule, n, a, r));

                    var (level, decisions) = Level(1, rule, n, a, r);
                    var result = ApprovalEvaluator.Evaluate([level], decisions, cancelled: false);
                    var status = expected switch
                    {
                        LevelOutcome.Approved => ApprovalStatus.Approved,
                        LevelOutcome.Rejected => ApprovalStatus.Rejected,
                        _ => ApprovalStatus.Requested,
                    };
                    Assert.Equal(status, result.Status);
                    Assert.Equal(status == ApprovalStatus.Requested ? 1 : null, result.CurrentLevel);
                }
            }
        }
    }

    [Fact]
    public void Every_two_level_chain_is_approved_only_when_both_levels_are()
    {
        foreach (var rule1 in Rules)
        foreach (var rule2 in Rules)
        for (var n1 = 1; n1 <= 3; n1++)
        for (var n2 = 1; n2 <= 3; n2++)
        foreach (var (a1, r1) in Splits(n1))
        foreach (var (a2, r2) in Splits(n2))
        {
            var (l1, d1) = Level(1, rule1, n1, a1, r1);
            var (l2, d2) = Level(2, rule2, n2, a2, r2);
            var result = ApprovalEvaluator.Evaluate([l2, l1], d1.Concat(d2), cancelled: false);

            var o1 = Expected(rule1, n1, a1, r1);
            var o2 = Expected(rule2, n2, a2, r2);
            var (status, current) = o1 switch
            {
                LevelOutcome.Rejected => (ApprovalStatus.Rejected, (int?)null),
                LevelOutcome.Pending => (ApprovalStatus.Requested, 1),
                _ => o2 switch
                {
                    LevelOutcome.Approved => (ApprovalStatus.Approved, (int?)null),
                    LevelOutcome.Rejected => (ApprovalStatus.Rejected, null),
                    _ => (ApprovalStatus.Requested, 2),
                },
            };
            Assert.Equal(status, result.Status);
            Assert.Equal(current, result.CurrentLevel);
            // Level 2 never counts as started while level 1 is still open or was rejected.
            Assert.Equal(o1 == LevelOutcome.Approved ? o2 : LevelOutcome.NotStarted, result.Levels.Single(l => l.Order == 2).Outcome);
        }
    }

    [Fact]
    public void Only_the_first_decision_of_a_designated_approver_counts()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var level = new LevelInput(1, ApprovalRule.Everyone, [a, b]);
        var stranger = new DecisionInput(1, Guid.NewGuid(), ApprovalVerdict.Approved);
        var result = ApprovalEvaluator.Evaluate([level],
            [new DecisionInput(1, a, ApprovalVerdict.Approved), new DecisionInput(1, a, ApprovalVerdict.Rejected), stranger], false);

        Assert.Equal(ApprovalStatus.Requested, result.Status);
        Assert.Equal(1, result.Levels[0].Approvals);
        Assert.Equal(0, result.Levels[0].Rejections);
    }

    [Fact]
    public void Cancelled_wins_and_an_empty_chain_is_never_approved()
    {
        var a = Guid.NewGuid();
        var approved = ApprovalEvaluator.Evaluate([new LevelInput(1, ApprovalRule.Any, [a])], [new DecisionInput(1, a, ApprovalVerdict.Approved)], cancelled: true);
        Assert.Equal(ApprovalStatus.Cancelled, approved.Status);
        Assert.Null(approved.CurrentLevel);

        Assert.Equal(ApprovalStatus.Requested, ApprovalEvaluator.Evaluate([], [], false).Status);
        Assert.Equal(LevelOutcome.Pending, ApprovalEvaluator.EvaluateLevel(ApprovalRule.Everyone, 0, 0, 0));
    }

    [Theory]
    [InlineData(ApprovalRule.Any, 5, 1)]
    [InlineData(ApprovalRule.Everyone, 5, 5)]
    [InlineData(ApprovalRule.Majority, 4, 3)]
    [InlineData(ApprovalRule.Majority, 5, 3)]
    [InlineData(ApprovalRule.Majority, 1, 1)]
    public void Needed_approvals(ApprovalRule rule, int approvers, int needed) =>
        Assert.Equal(needed, ApprovalEvaluator.Needed(rule, approvers));
}
