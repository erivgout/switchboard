using System.Text.Json.Nodes;
using Switchboard.Core.Domain;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Engine;
using Switchboard.Modules.ServiceLevels.Monitor;

namespace Switchboard.Modules.ServiceLevels.Tests;

/// <summary>Pure policy selection (FR-SLA-2, INV-SLA-1) and firing plans (INV-SLA-4).</summary>
public sealed class PolicyAndPlannerTests
{
    private static readonly Guid Network = Guid.NewGuid();
    private static readonly Guid Email = Guid.NewGuid();
    private static readonly Guid Hardware = Guid.NewGuid();
    private static readonly Guid Laptop = Guid.NewGuid();
    private static readonly Guid Executives = Guid.NewGuid();

    private static SlaTicketFacts Facts(TicketType type = TicketType.Incident, Guid? group = null, Guid? source = null,
        Guid[]? categories = null, Guid[]? requesterGroups = null, string custom = "{}") =>
        new(type, source ?? Guid.NewGuid(), (requesterGroups ?? []).ToHashSet(), null, categories ?? [], group, JsonNode.Parse(custom)!.AsObject());

    private static SlaPolicy Policy(string name, int order, params SlaCondition[] conditions) =>
        new() { Name = name, Order = order, Conditions = [.. conditions] };

    [Fact]
    public void The_first_matching_policy_in_order_applies()
    {
        var vip = Policy("Executives", 1, new SlaCondition(SlaConditionFields.RequesterGroup, [Executives.ToString()]));
        var network = Policy("Network", 2, new SlaCondition(SlaConditionFields.Group, [Network.ToString()]));
        var fallback = Policy("Default", 3);
        SlaPolicy[] policies = [fallback, network, vip];

        Assert.Same(network, SlaPolicyMatcher.Select(policies, Facts(group: Network)));
        Assert.Same(vip, SlaPolicyMatcher.Select(policies, Facts(group: Network, requesterGroups: [Executives])));
        Assert.Same(fallback, SlaPolicyMatcher.Select(policies, Facts()));
    }

    [Fact]
    public void Inactive_and_deleted_policies_are_skipped()
    {
        var off = Policy("Off", 1);
        off.IsActive = false;
        var gone = Policy("Gone", 2);
        gone.DeletedAt = DateTimeOffset.UtcNow;
        var on = Policy("On", 3);
        Assert.Same(on, SlaPolicyMatcher.Select([off, gone, on], Facts()));
        Assert.Null(SlaPolicyMatcher.Select([off, gone], Facts()));
    }

    [Fact]
    public void All_conditions_must_hold_and_any_value_of_one_condition_will_do()
    {
        var policy = Policy("Requests by email about hardware", 1,
            new SlaCondition(SlaConditionFields.Type, ["request"]),
            new SlaCondition(SlaConditionFields.Source, [Email.ToString(), Guid.NewGuid().ToString()]),
            new SlaCondition(SlaConditionFields.Category, [Hardware.ToString()]));

        Assert.True(SlaPolicyMatcher.Matches(policy, Facts(TicketType.ServiceRequest, source: Email, categories: [Hardware, Laptop])));
        Assert.False(SlaPolicyMatcher.Matches(policy, Facts(TicketType.Incident, source: Email, categories: [Hardware])));
        Assert.False(SlaPolicyMatcher.Matches(policy, Facts(TicketType.ServiceRequest, categories: [Hardware])));
        Assert.False(SlaPolicyMatcher.Matches(policy, Facts(TicketType.ServiceRequest, source: Email)));
    }

    [Fact]
    public void A_sub_category_condition_matches_tickets_filed_under_it()
    {
        var policy = Policy("Laptops", 1, new SlaCondition(SlaConditionFields.Category, [Laptop.ToString()]));
        Assert.True(SlaPolicyMatcher.Matches(policy, Facts(categories: [Hardware, Laptop])));
        Assert.False(SlaPolicyMatcher.Matches(policy, Facts(categories: [Hardware])));
    }

    [Fact]
    public void Custom_field_conditions_compare_values_case_insensitively()
    {
        var policy = Policy("Gold contracts", 1, new SlaCondition("custom:contract_tier", ["Gold"]));
        Assert.True(SlaPolicyMatcher.Matches(policy, Facts(custom: """{"contract_tier":"gold"}""")));
        Assert.True(SlaPolicyMatcher.Matches(policy, Facts(custom: """{"contract_tier":["silver","GOLD"]}""")));
        Assert.False(SlaPolicyMatcher.Matches(policy, Facts(custom: """{"contract_tier":"silver"}""")));
        Assert.False(SlaPolicyMatcher.Matches(policy, Facts()));

        var flag = Policy("Flagged", 1, new SlaCondition("custom:after_hours", ["true"]));
        Assert.True(SlaPolicyMatcher.Matches(flag, Facts(custom: """{"after_hours":true}""")));
        var number = Policy("Site 7", 1, new SlaCondition("custom:site", ["7"]));
        Assert.True(SlaPolicyMatcher.Matches(number, Facts(custom: """{"site":7}""")));
    }

    [Fact]
    public void Unknown_condition_fields_never_match_and_empty_conditions_are_ignored()
    {
        Assert.False(SlaPolicyMatcher.Matches(Policy("Odd", 1, new SlaCondition("planet", ["mars"])), Facts()));
        Assert.True(SlaPolicyMatcher.Matches(Policy("Blank", 1, new SlaCondition(SlaConditionFields.Group, [])), Facts()));
    }

    // ------------------------------------------------------------------ firing plans

    private static readonly DateTimeOffset Due = new(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);

    private static SlaCandidate Candidate(DateTimeOffset? frDue = null, DateTimeOffset? responded = null, DateTimeOffset? due = null, int cycle = 1) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, frDue, responded, due, cycle, Guid.NewGuid(), null, 0);

    private static SlaPolicy Plan() => new()
    {
        Name = "Plan",
        Reminders = [new SlaReminderRule(SlaTargetKind.Resolution, 30)],
        Escalations = new SlaEscalationPlan
        {
            FirstResponse = new SlaNotifyTargets { Assignee = true },
            Resolution = [new SlaEscalationLevel(1, 0, new SlaNotifyTargets { Assignee = true }), new SlaEscalationLevel(2, 120, new SlaNotifyTargets { AssigneeManager = true })],
        },
    };

    [Fact]
    public void Plans_the_reminder_then_the_breach_then_each_escalation_level()
    {
        var c = Candidate(due: Due);
        var none = new HashSet<SlaFiringKey>();

        var (early, next) = SlaFiringPlanner.Plan(c, Plan(), none, Due.AddHours(-2));
        Assert.Empty(early);
        Assert.Equal(Due.AddMinutes(-30), next);

        var (reminder, afterReminder) = SlaFiringPlanner.Plan(c, Plan(), none, Due.AddMinutes(-10));
        Assert.Equal(SlaEventKind.Reminder, Assert.Single(reminder).Key.Kind);
        Assert.Equal(Due, afterReminder);

        var (breach, afterBreach) = SlaFiringPlanner.Plan(c, Plan(), reminder.Select(f => f.Key).ToHashSet(), Due.AddMinutes(1));
        Assert.Equal([SlaEventKind.Breach, SlaEventKind.Escalation], breach.Select(f => f.Key.Kind));
        Assert.Equal(1, breach[1].Key.Level);
        Assert.Equal(Due.AddHours(2), afterBreach);
    }

    [Fact]
    public void Fired_keys_never_fire_again_and_nothing_is_left_to_check()
    {
        var c = Candidate(due: Due);
        var all = SlaFiringPlanner.Plan(c, Plan(), new HashSet<SlaFiringKey>(), Due.AddHours(3)).Due;
        Assert.Equal(3, all.Count);
        var (again, next) = SlaFiringPlanner.Plan(c, Plan(), all.Select(f => f.Key).ToHashSet(), Due.AddHours(4));
        Assert.Empty(again);
        Assert.Null(next);
    }

    [Fact]
    public void A_reminder_whose_target_already_passed_is_dropped()
    {
        var plan = SlaFiringPlanner.Plan(Candidate(due: Due), Plan(), new HashSet<SlaFiringKey>(), Due.AddMinutes(5)).Due;
        Assert.DoesNotContain(plan, f => f.Key.Kind == SlaEventKind.Reminder);
    }

    [Fact]
    public void A_new_cycle_gets_its_own_breach_and_first_response_fires_once_per_ticket()
    {
        var firstCycle = SlaFiringPlanner.Plan(Candidate(frDue: Due, due: Due), Plan(), new HashSet<SlaFiringKey>(), Due.AddMinutes(1)).Due;
        var fired = firstCycle.Select(f => f.Key).ToHashSet();
        var reopened = SlaFiringPlanner.Plan(Candidate(frDue: Due, due: Due.AddDays(1), cycle: 2), Plan(), fired, Due.AddDays(1).AddMinutes(1)).Due;
        Assert.DoesNotContain(reopened, f => f.Key.Target == SlaTargetKind.FirstResponse);
        Assert.Contains(reopened, f => f.Key is { Kind: SlaEventKind.Breach, Target: SlaTargetKind.Resolution, Cycle: 2 });
    }

    [Fact]
    public void A_responded_ticket_has_no_first_response_breach()
    {
        var plan = SlaFiringPlanner.Plan(Candidate(frDue: Due, responded: Due.AddHours(-1)), Plan(), new HashSet<SlaFiringKey>(), Due.AddHours(1)).Due;
        Assert.Empty(plan);
    }
}
