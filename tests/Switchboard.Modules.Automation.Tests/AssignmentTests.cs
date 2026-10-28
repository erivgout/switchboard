using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Testing;

namespace Switchboard.Modules.Automation.Tests;

/// <summary>FR-RTE-1/2: automatic assignment when a ticket lands in a group without an agent.</summary>
public sealed class AssignmentTests : AutomationTestBase
{
    private User _b = default!;
    private User _c = default!;
    private User _away = default!;
    private User _observer = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _b = await Host.CreateAgentAsync("Bea Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _c = await Host.CreateAgentAsync("Cal Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _away = await Host.CreateAgentAsync("Aki Away", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _observer = await Host.CreateAgentAsync("Obi Observer", RoleKeys.Agent, TicketScope.Global);
        var serviceDesk = await GroupIdAsync("Service Desk");
        await Db(async db =>
        {
            db.AgentGroupMembers.Add(new AgentGroupMember { GroupId = serviceDesk, UserId = _observer.Id, IsObserver = true });
            await db.SaveChangesAsync();
        });
        foreach (var u in new[] { Agent, _b, _c, _observer })
        {
            await SetAvailabilityAsync(u, AgentAvailability.Available);
        }
        await SetAvailabilityAsync(_away, AgentAvailability.Away);
    }

    [Fact]
    public async Task Round_robin_shares_tickets_evenly_and_skips_unavailable_agents_and_observers()
    {
        var tickets = new List<Ticket>();
        for (var i = 0; i < 6; i++)
        {
            tickets.Add(await FileAsync($"Ticket {i}", group: "Service Desk"));
            await ProcessAsync();
        }

        var assigned = await Db(db => db.Tickets.AsNoTracking().Where(t => tickets.Select(x => x.Id).Contains(t.Id)).OrderBy(t => t.Number).Select(t => t.AgentId).ToListAsync());
        Assert.All(assigned, a => Assert.NotNull(a));
        var counts = assigned.GroupBy(a => a!.Value).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(3, counts.Count);
        Assert.All(counts.Values, n => Assert.Equal(2, n));
        Assert.DoesNotContain(_away.Id, counts.Keys);
        Assert.DoesNotContain(_observer.Id, counts.Keys);

        // The pointer moves on: the next ticket goes to whoever follows the last agent.
        var last = await Db(db => db.AgentGroups.Where(g => g.Name == "Service Desk").Select(g => g.LastAssignedUserId).FirstAsync());
        Assert.Equal(assigned[^1], last);

        // Ticket activity says an automation assigned it.
        var activity = await Db(db => db.ActivityEntries.AsNoTracking().FirstAsync(a => a.SubjectId == tickets[0].Id && a.Summary.Contains("agent")));
        Assert.Equal(ActorKind.Automation, activity.ActorKind);
        Assert.Equal("Auto-assignment", activity.ActorName);
    }

    [Fact]
    public async Task Moving_a_ticket_into_a_group_assigns_it_but_unassigning_in_place_does_not()
    {
        var ticket = await FileAsync("No group yet");
        await ProcessAsync();
        Assert.Null((await TicketAsync(ticket.Id)).AgentId);

        var serviceDesk = await GroupIdAsync("Service Desk");
        await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { GroupId = serviceDesk }));
        await ProcessAsync();
        Assert.NotNull((await TicketAsync(ticket.Id)).AgentId);

        await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { AgentId = (Guid?)null }));
        await ProcessAsync();
        Assert.Null((await TicketAsync(ticket.Id)).AgentId);
    }

    [Fact]
    public async Task An_explicit_agent_is_never_overwritten()
    {
        var ticket = await Host.RunAsync(SwitchboardTestHost.As(Agent), async sp => await sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
        {
            Subject = "Mine",
            RequesterId = Requester.Id,
            GroupId = await GroupIdAsync("Service Desk"),
            AgentId = _c.Id,
        }));
        await ProcessAsync();
        Assert.Equal(_c.Id, (await TicketAsync(ticket.Id)).AgentId);
    }

    [Fact]
    public async Task Load_based_picks_the_least_busy_and_respects_the_cap()
    {
        var serviceDesk = await GroupIdAsync("Service Desk");
        await Db(db => db.AgentGroups.Where(g => g.Id == serviceDesk).ExecuteUpdateAsync(s => s
            .SetProperty(g => g.AssignmentMode, AssignmentMode.LoadBased)
            .SetProperty(g => g.MaxOpenPerAgent, (int?)1)));
        // Agent and Bea already have one open ticket each.
        foreach (var owner in new[] { Agent, _b })
        {
            await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
            {
                Subject = $"Busy {owner.DisplayName}", RequesterId = Requester.Id, AgentId = owner.Id,
            }));
        }

        var first = await FileAsync("Goes to Cal", group: "Service Desk");
        await ProcessAsync();
        Assert.Equal(_c.Id, (await TicketAsync(first.Id)).AgentId);

        var second = await FileAsync("Everyone is at the cap", group: "Service Desk");
        await ProcessAsync();
        Assert.Null((await TicketAsync(second.Id)).AgentId);
    }

    [Fact]
    public async Task Away_agents_get_nothing_and_a_group_with_nobody_available_stays_unassigned()
    {
        foreach (var u in new[] { Agent, _b, _c })
        {
            await SetAvailabilityAsync(u, AgentAvailability.Away);
        }
        var ticket = await FileAsync("Nobody home", group: "Service Desk");
        await ProcessAsync();
        Assert.Null((await TicketAsync(ticket.Id)).AgentId);
    }
}
