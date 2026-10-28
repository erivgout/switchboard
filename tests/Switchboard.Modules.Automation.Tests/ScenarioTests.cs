using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Scenarios;
using Switchboard.Testing;

namespace Switchboard.Modules.Automation.Tests;

/// <summary>FR-AUT-6: personal, group and global scenarios, run as the agent.</summary>
public sealed class ScenarioTests : AutomationTestBase
{
    private User _other = default!;
    private User _hardware = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _other = await Host.CreateAgentAsync("Olli Other", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _hardware = await Host.CreateAgentAsync("Hana Hardware", RoleKeys.Agent, TicketScope.Global, "Hardware");
    }

    private Task<AutScenario> SaveAsync(User as_, string name, ScenarioVisibility visibility, Guid? groupId = null, params ActionDefinition[] actions) =>
        Host.RunAsync(SwitchboardTestHost.As(as_), sp => sp.GetRequiredService<ScenarioService>().SaveAsync(null,
            new ScenarioInput(Host.Workspace.Id, name, null, visibility, groupId,
                actions.Length > 0 ? [.. actions] : [Act(ActionTypes.AddTags, ("tags", "scenario"))])));

    private Task<List<string>> VisibleAsync(User user) =>
        Host.RunAsync(SwitchboardTestHost.As(user), async sp => (await sp.GetRequiredService<ScenarioService>().VisibleAsync(Host.Workspace.Id)).Select(s => s.Name).ToList());

    [Fact]
    public async Task Agents_keep_personal_scenarios_and_only_admins_share_them()
    {
        var mine = await SaveAsync(Agent, "My triage", ScenarioVisibility.Personal);
        Assert.Equal(Agent.Id, mine.OwnerUserId);
        await Assert.ThrowsAsync<ForbiddenException>(() => SaveAsync(Agent, "Shared triage", ScenarioVisibility.Global));
        await Assert.ThrowsAsync<ForbiddenException>(() => SaveAsync(Agent, "Group triage", ScenarioVisibility.Group, Guid.NewGuid()));

        await SaveAsync(Admin, "Everyone", ScenarioVisibility.Global);
        await SaveAsync(Admin, "Hardware only", ScenarioVisibility.Group, await GroupIdAsync("Hardware"));

        Assert.Equal(["Everyone", "My triage"], await VisibleAsync(Agent));
        Assert.Equal(["Everyone"], await VisibleAsync(_other));
        Assert.Equal(["Everyone", "Hardware only"], await VisibleAsync(_hardware));

        // Someone else's personal scenario can't be edited, deleted or run.
        await Assert.ThrowsAsync<NotFoundException>(() => Host.RunAsync(SwitchboardTestHost.As(_other), sp => sp.GetRequiredService<ScenarioService>().GetForEditAsync(mine.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => Host.RunAsync(SwitchboardTestHost.As(_other), sp => sp.GetRequiredService<ScenarioService>().DeleteAsync(mine.Id)));
        var ticket = await FileAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => Host.RunAsync(SwitchboardTestHost.As(_other), sp => sp.GetRequiredService<ScenarioService>().RunAsync(mine.Id, ticket.Id)));

        // A shared scenario is read-only for agents.
        var shared = (await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ScenarioService>().VisibleAsync(Host.Workspace.Id))).First(s => s.Name == "Everyone");
        await Assert.ThrowsAsync<ForbiddenException>(() => Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ScenarioService>().DeleteAsync(shared.Id)));
    }

    [Fact]
    public async Task Running_a_scenario_applies_every_action_as_the_agent()
    {
        var pending = await StatusIdAsync("Pending");
        var scenario = await SaveAsync(Agent, "Waiting on requester", ScenarioVisibility.Personal, null,
            Act(ActionTypes.SetStatus, ("statusId", pending.ToString())),
            Act(ActionTypes.AddTags, ("tags", "waiting")),
            Act(ActionTypes.AddNote, ("body", "Asked {{requester.name}} for details.")));
        var ticket = await FileAsync();

        var result = await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ScenarioService>().RunAsync(scenario.Id, ticket.Id));
        Assert.Equal(3, result.Done.Count);

        var after = await TicketAsync(ticket.Id);
        Assert.Equal(pending, after.StatusId);
        Assert.Contains("waiting", after.Tags.Select(t => t.Tag));
        var note = await Db(db => db.TicketConversations.AsNoTracking().SingleAsync(c => c.TicketId == ticket.Id && c.Kind == ConversationKind.Note));
        Assert.Equal(Agent.Id, note.AuthorId);
        Assert.Equal(Channel.Web, note.Channel);
        Assert.Contains("Asked Dana Requester for details.", note.BodyText);
        var activity = await Db(db => db.ActivityEntries.AsNoTracking().Where(a => a.SubjectId == ticket.Id && a.Action == "updated").ToListAsync());
        Assert.All(activity, a => Assert.Equal(Agent.Id, a.ActorUserId));
    }

    [Fact]
    public async Task A_scenario_is_all_or_nothing_and_respects_ticket_permissions()
    {
        var scenario = await SaveAsync(Admin, "Escalate", ScenarioVisibility.Global, null,
            Act(ActionTypes.AddTags, ("tags", "escalated")),
            Act(ActionTypes.SetStatus, ("statusId", (await StatusIdAsync("Resolved")).ToString())));
        await Host.RunAsync(SwitchboardTestHost.As(Admin), sp => sp.GetRequiredService<Closure.ClosureRuleService>()
            .AddAsync(Host.Workspace.Id, new Closure.ClosureRuleInput(ClosureRuleKind.CategorySet, null, null, null)));
        var ticket = await FileAsync();

        var error = await Assert.ThrowsAsync<ValidationException>(() => Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ScenarioService>().RunAsync(scenario.Id, ticket.Id)));
        Assert.Contains("Nothing was changed.", error.Message);
        Assert.DoesNotContain("escalated", (await TicketAsync(ticket.Id)).Tags.Select(t => t.Tag));

        // A read-only agent can see the scenario but the ticket service refuses the changes.
        var readOnly = await Host.CreateAgentAsync("Rhea ReadOnly", RoleKeys.ReadOnly);
        await Assert.ThrowsAsync<ForbiddenException>(() => Host.RunAsync(SwitchboardTestHost.As(readOnly), sp => sp.GetRequiredService<ScenarioService>().RunAsync(scenario.Id, ticket.Id)));
        // Requesters can't run scenarios at all.
        await Assert.ThrowsAsync<NotFoundException>(() => Host.RunAsync(SwitchboardTestHost.As(Requester, Channel.Portal), sp => sp.GetRequiredService<ScenarioService>().RunAsync(scenario.Id, ticket.Id)));
    }

    [Fact]
    public async Task Scenarios_reject_actions_that_reach_outside_the_ticket()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() => SaveAsync(Agent, "Sneaky", ScenarioVisibility.Personal, null,
            Act(ActionTypes.WebRequest, ("method", "GET"), ("url", "https://example.com/"))));
        Assert.Contains("Action 1: choose an action from the list.", error.Errors["actions"]);
    }
}
