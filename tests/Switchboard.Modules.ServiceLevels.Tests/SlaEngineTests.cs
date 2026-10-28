using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Engine;
using Switchboard.Modules.ServiceLevels.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.ServiceLevels.Tests;

/// <summary>
/// The pre-commit engine against SQL Server. Default policy (High: first response 1h, resolution 8h)
/// on the seeded Mon-Fri 9-17 UTC calendar; the clock starts Wednesday 10:00.
/// </summary>
public sealed class SlaEngineTests : SlaTestBase
{
    [Fact]
    public async Task A_new_ticket_gets_due_dates_and_the_policy_choice_is_recorded_with_its_reason()
    {
        var ticket = await CreateAsync(Priority.High);

        var saved = await TicketAsync(ticket.Id);
        Assert.Equal(At(28, 11), saved.FirstResponseDueBy);
        Assert.Equal(At(29, 10), saved.DueBy);

        var state = await StateAsync(ticket.Id);
        Assert.Equal("Default service levels", state.PolicyName);
        Assert.Equal(state.PolicyId, saved.SlaPolicyId);
        Assert.Contains("no conditions", state.SelectionReason);
        Assert.Equal(At(29, 10), state.ResolutionTargetAt);

        var activity = await Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().ActivityEntries.AsNoTracking()
            .Where(a => a.SubjectId == ticket.Id && a.Action == "sla_applied").ToListAsync());
        Assert.Contains("Default service levels", Assert.Single(activity).Summary);
    }

    [Fact]
    public async Task The_first_matching_policy_in_order_applies()
    {
        var admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        var network = await Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().AgentGroups.FirstAsync(g => g.Name == "Network"));
        await Host.RunAsync(SwitchboardTestHost.As(admin), async sp =>
        {
            var service = sp.GetRequiredService<SlaAdminService>();
            var policy = await service.SavePolicyAsync(new PolicyInput
            {
                WorkspaceId = Host.Workspace.Id,
                Name = "Network outages",
                Conditions = [new SlaCondition(SlaConditionFields.Group, [network.Id.ToString()])],
                Targets = [new SlaTarget(Priority.High, 15, null, 120)],
            });
            var all = await service.PoliciesAsync(Host.Workspace.Id);
            await service.ReorderAsync(Host.Workspace.Id, [policy.Id, .. all.Where(p => p.Id != policy.Id).Select(p => p.Id)]);
        });

        var networkTicket = await CreateAsync(Priority.High, "Network");
        var deskTicket = await CreateAsync(Priority.High, "Service Desk");

        var state = await StateAsync(networkTicket.Id);
        Assert.Equal("Network outages", state.PolicyName);
        Assert.Contains("Group is Network", state.SelectionReason);
        Assert.Equal(At(28, 12), (await TicketAsync(networkTicket.Id)).DueBy);
        Assert.Equal("Default service levels", (await StateAsync(deskTicket.Id)).PolicyName);
        Assert.Contains("1 earlier policy didn't match", (await StateAsync(deskTicket.Id)).SelectionReason);
    }

    [Fact]
    public async Task Changing_priority_re_evaluates_the_targets_unless_turned_off()
    {
        var ticket = await CreateAsync(Priority.High);
        await UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.Urgent });
        var urgent = await TicketAsync(ticket.Id);
        Assert.Equal(At(28, 10, 30), urgent.FirstResponseDueBy);
        Assert.Equal(At(28, 14), urgent.DueBy);

        var admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        await Host.RunAsync(SwitchboardTestHost.As(admin), sp => sp.GetRequiredService<SlaAdminService>().SetReevaluateOnChangeAsync(Host.Workspace.Id, false));
        await UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.Low });
        Assert.Equal(At(28, 14), (await TicketAsync(ticket.Id)).DueBy);
    }

    [Fact]
    public async Task Pausing_stops_the_clock_and_resuming_moves_due_dates_by_the_paused_business_time()
    {
        var ticket = await CreateAsync(Priority.High);

        Clock.Now = At(28, 12);
        await SetStatusAsync(ticket.Id, "Pending");
        var paused = await StateAsync(ticket.Id);
        Assert.Equal(At(28, 12), paused.PausedSince);
        Assert.Equal(At(29, 10), (await TicketAsync(ticket.Id)).DueBy);

        Clock.Now = At(28, 15);
        await SetStatusAsync(ticket.Id, "Open");
        Assert.Equal(At(29, 13), (await TicketAsync(ticket.Id)).DueBy);

        // Overnight: 16:00 Wednesday to 10:00 Thursday is two working hours.
        Clock.Now = At(28, 16);
        await SetStatusAsync(ticket.Id, "Pending");
        Clock.Now = At(29, 10);
        await SetStatusAsync(ticket.Id, "Open");

        var state = await StateAsync(ticket.Id);
        Assert.Null(state.PausedSince);
        Assert.Equal([(At(28, 12), At(28, 15)), (At(28, 16), At(29, 10))], state.Pauses.Select(p => (p.StartedAt, p.EndedAt!.Value)));
        Assert.Equal(At(29, 15), (await TicketAsync(ticket.Id)).DueBy);
    }

    [Fact]
    public async Task A_manual_due_date_is_kept_and_the_original_target_is_remembered()
    {
        var ticket = await CreateAsync(Priority.High);
        var friday = At(30, 12);
        await UpdateAsync(ticket.Id, new TicketPatch { DueBy = friday, DueOverrideReason = "Waiting on the vendor's replacement part" });

        var state = await StateAsync(ticket.Id);
        Assert.True(state.ResolutionOverridden);
        Assert.Equal(At(29, 10), state.ResolutionTargetAt);
        Assert.Equal("Waiting on the vendor's replacement part", state.OverrideReason);
        Assert.Equal(Agent.Id, state.OverriddenById);
        Assert.Equal(friday, (await TicketAsync(ticket.Id)).DueBy);

        // Recomputing for other reasons leaves the hand-set date alone.
        await UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.Urgent });
        Clock.Now = At(28, 11);
        await SetStatusAsync(ticket.Id, "Pending");
        Clock.Now = At(28, 12);
        await UpdateAsync(ticket.Id, new TicketPatch { StatusId = await StatusAsync("Open"), DueBy = At(30, 15), DueOverrideReason = "Vendor slipped" });

        var after = await TicketAsync(ticket.Id);
        Assert.Equal(At(30, 15), after.DueBy);
        Assert.Equal(At(28, 15), (await StateAsync(ticket.Id)).ResolutionTargetAt);
        // The first response target was due before the pause began, so the pause doesn't move it.
        Assert.Equal(At(28, 10, 30), after.FirstResponseDueBy);

        // Clearing the date hands it back to the SLA target.
        await UpdateAsync(ticket.Id, new TicketPatch { DueBy = (DateTimeOffset?)null, DueOverrideReason = "Back to the SLA" });
        Assert.Equal(At(28, 15), (await TicketAsync(ticket.Id)).DueBy);
        Assert.False((await StateAsync(ticket.Id)).ResolutionOverridden);
    }

    [Fact]
    public async Task An_answered_first_response_target_stays_put()
    {
        var ticket = await CreateAsync(Priority.High);
        Clock.Now = At(28, 10, 30);
        var pending = await StatusAsync("Pending");
        await AsAgent(sp => sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>Try this.</p>", StatusId = pending }));
        Clock.Now = At(28, 12);
        await SetStatusAsync(ticket.Id, "Open");
        await UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.Urgent });

        Assert.Equal(At(28, 11), (await TicketAsync(ticket.Id)).FirstResponseDueBy);
        Assert.Equal(At(28, 11), (await StateAsync(ticket.Id)).FirstResponseTargetAt);
    }

    [Fact]
    public async Task An_answered_first_response_target_survives_losing_the_policy()
    {
        var ticket = await CreateAsync(Priority.High);
        Clock.Now = At(28, 10, 30);
        await AsAgent(sp => sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>On it.</p>" }));
        var admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        await Host.RunAsync(SwitchboardTestHost.As(admin), async sp =>
        {
            var service = sp.GetRequiredService<SlaAdminService>();
            await service.SetActiveAsync((await service.PoliciesAsync(Host.Workspace.Id)).Single().Id, false);
        });
        await UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.Low });

        var saved = await TicketAsync(ticket.Id);
        Assert.Null((await StateAsync(ticket.Id)).PolicyId);
        Assert.Null(saved.DueBy);
        Assert.Equal(At(28, 11), saved.FirstResponseDueBy);
    }

    [Fact]
    public async Task Clearing_a_manual_first_response_date_after_answering_restores_the_target()
    {
        var ticket = await CreateAsync(Priority.High);
        Clock.Now = At(28, 10, 30);
        await AsAgent(sp => sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>On it.</p>" }));
        await UpdateAsync(ticket.Id, new TicketPatch { FirstResponseDueBy = At(28, 12), DueOverrideReason = "Agreed later reply" });
        Assert.Equal(At(28, 12), (await TicketAsync(ticket.Id)).FirstResponseDueBy);

        await UpdateAsync(ticket.Id, new TicketPatch { FirstResponseDueBy = (DateTimeOffset?)null, DueOverrideReason = "Back to the SLA" });
        Assert.Equal(At(28, 11), (await TicketAsync(ticket.Id)).FirstResponseDueBy);
        Assert.False((await StateAsync(ticket.Id)).FirstResponseOverridden);
    }

    [Fact]
    public async Task Internal_actors_changing_the_due_date_do_not_override_the_sla()
    {
        var ticket = await CreateAsync(Priority.High);
        await Host.RunAsSystemAsync(sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { DueBy = At(30, 12) }));
        Assert.False((await StateAsync(ticket.Id)).ResolutionOverridden);
    }

    [Fact]
    public async Task Recomputing_is_idempotent()
    {
        var ticket = await CreateAsync(Priority.High);
        Clock.Now = At(28, 12);
        await SetStatusAsync(ticket.Id, "Pending");
        Clock.Now = At(28, 15);
        await SetStatusAsync(ticket.Id, "Open");

        var before = await TicketAsync(ticket.Id);
        var stateBefore = await StateAsync(ticket.Id);
        var entriesBefore = await ActivityCountAsync(ticket.Id);

        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(TimeSpan.FromHours(5));
            await Host.RunAsSystemAsync(async sp =>
            {
                var db = sp.GetRequiredService<SwitchboardDbContext>();
                await sp.GetRequiredService<SlaEngine>().EvaluateAsync(ticket.Id, new SlaChange(SlaTrigger.Updated, Clock.Now, null, []), default);
                return await db.SaveChangesAsync();
            });
        }

        var after = await TicketAsync(ticket.Id);
        var stateAfter = await StateAsync(ticket.Id);
        Assert.Equal(before.DueBy, after.DueBy);
        Assert.Equal(before.FirstResponseDueBy, after.FirstResponseDueBy);
        Assert.Equal(stateBefore.PausesJson, stateAfter.PausesJson);
        Assert.Equal(stateBefore.UpdatedAt, stateAfter.UpdatedAt);
        Assert.Equal(entriesBefore, await ActivityCountAsync(ticket.Id));
    }

    [Fact]
    public async Task Reopening_starts_a_new_cycle_with_a_fresh_resolution_clock()
    {
        var ticket = await CreateAsync(Priority.High);
        Clock.Now = At(28, 11);
        await SetStatusAsync(ticket.Id, "Resolved");
        Clock.Now = At(29, 9, 30);
        await SetStatusAsync(ticket.Id, "Open");

        var state = await StateAsync(ticket.Id);
        Assert.Equal(2, state.Cycle);
        Assert.Equal(At(29, 9, 30), state.CycleStartedAt);
        Assert.Equal(At(30, 9, 30), (await TicketAsync(ticket.Id)).DueBy);
        Assert.Equal(At(28, 11), (await TicketAsync(ticket.Id)).FirstResponseDueBy);
    }

    [Fact]
    public async Task Trashed_tickets_accrue_no_sla_time()
    {
        var ticket = await CreateAsync(Priority.High);
        Clock.Now = At(28, 11);
        await AsAgent(sp => sp.GetRequiredService<ITicketService>().MoveToTrashAsync(ticket.Id));
        Assert.NotNull((await StateAsync(ticket.Id)).PausedSince);
        Clock.Now = At(28, 14);
        await AsAgent(sp => sp.GetRequiredService<ITicketService>().RestoreAsync(ticket.Id));
        Assert.Equal(At(29, 13), (await TicketAsync(ticket.Id)).DueBy);
    }

    [Fact]
    public async Task Without_a_matching_policy_a_ticket_has_no_due_dates()
    {
        var admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        await Host.RunAsync(SwitchboardTestHost.As(admin), async sp =>
        {
            var service = sp.GetRequiredService<SlaAdminService>();
            foreach (var p in await service.PoliciesAsync(Host.Workspace.Id))
            {
                await service.SetActiveAsync(p.Id, false);
            }
        });
        var ticket = await CreateAsync(Priority.Urgent);
        var saved = await TicketAsync(ticket.Id);
        Assert.Null(saved.DueBy);
        Assert.Null(saved.FirstResponseDueBy);
        var state = await StateAsync(ticket.Id);
        Assert.Null(state.PolicyId);
        Assert.Equal("No active SLA policy matches this ticket.", state.SelectionReason);
    }

    [Fact]
    public async Task Next_response_clock_runs_from_a_requester_reply_until_an_agent_answers()
    {
        var admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        await Host.RunAsync(SwitchboardTestHost.As(admin), async sp =>
        {
            var service = sp.GetRequiredService<SlaAdminService>();
            var policy = (await service.PoliciesAsync(Host.Workspace.Id)).Single();
            await service.SavePolicyAsync(new PolicyInput
            {
                Id = policy.Id,
                WorkspaceId = policy.WorkspaceId,
                Name = policy.Name,
                Targets = [new SlaTarget(Priority.High, 60, 120, 480)],
            });
        });
        var ticket = await CreateAsync(Priority.High);
        await AsAgent(sp => sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>Looking.</p>" }));
        Clock.Now = At(28, 12);
        await Host.RunAsync(SwitchboardTestHost.As(Requester, Channel.Portal), sp =>
            sp.GetRequiredService<ITicketService>().AddRequesterMessageAsync(ticket.Id, new RequesterMessageCommand { BodyHtml = "<p>Still broken.</p>" }));

        var waiting = await StateAsync(ticket.Id);
        Assert.Equal(At(28, 14), waiting.NextResponseDueBy);
        Assert.Equal(1, waiting.NextResponseSeq);

        await AsAgent(sp => sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>Fixed now.</p>" }));
        Assert.Null((await StateAsync(ticket.Id)).NextResponseDueBy);
    }

    private Task<int> ActivityCountAsync(Guid ticketId) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().ActivityEntries.CountAsync(a => a.SubjectId == ticketId));
}
