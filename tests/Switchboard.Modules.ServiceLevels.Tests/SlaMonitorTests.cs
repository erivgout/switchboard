using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Events;
using Switchboard.Testing;

namespace Switchboard.Modules.ServiceLevels.Tests;

public sealed class CapturingEmailSender : ISystemEmailSender
{
    public static readonly ConcurrentQueue<OutboundEmail> Sent = new();

    public Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        Sent.Enqueue(email);
        return Task.FromResult($"<{Guid.NewGuid():N}@test>");
    }
}

/// <summary>
/// Reminders, breaches and escalations (FR-SLA-3, INV-SLA-4, INV-SLA-5). Default policy, Urgent:
/// first response 30m (reminder 15m before), resolution 4h (reminder 1h before); escalation level 1
/// at the breach, level 2 four hours later. Clock starts Wednesday 10:00.
/// </summary>
public sealed class SlaMonitorTests : SlaTestBase
{
    [Fact]
    public async Task Each_reminder_breach_and_escalation_fires_once()
    {
        var ticket = await CreateAsync(Priority.Urgent, "Service Desk", agentId: Agent.Id);

        Clock.Now = At(28, 10, 16);
        Assert.Equal(1, await MonitorAsync());
        Assert.Equal(0, await MonitorAsync());

        Clock.Now = At(28, 10, 31);
        Assert.Equal(1, await MonitorAsync());
        Assert.Equal(0, await MonitorAsync());

        Clock.Now = At(28, 14, 1);
        Assert.Equal(2, await MonitorAsync());
        Assert.Equal(0, await MonitorAsync());

        Clock.Now = At(28, 18, 1);
        Assert.Equal(1, await MonitorAsync());
        Clock.Now = At(29, 18, 1);
        Assert.Equal(0, await MonitorAsync());

        var events = await EventsAsync(ticket.Id);
        Assert.Equal(
            [
                (SlaEventKind.Reminder, SlaTargetKind.FirstResponse, 1),
                (SlaEventKind.Breach, SlaTargetKind.FirstResponse, 0),
                (SlaEventKind.Breach, SlaTargetKind.Resolution, 0),
                (SlaEventKind.Escalation, SlaTargetKind.Resolution, 1),
                (SlaEventKind.Escalation, SlaTargetKind.Resolution, 2),
            ],
            events.Select(e => (e.Kind, e.Target, e.Level)));
        Assert.Contains("Missed the first response target", events[1].Summary);
        Assert.Contains("Sam Agent", events[1].Summary);

        var activity = await Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().ActivityEntries.AsNoTracking()
            .Where(a => a.SubjectId == ticket.Id && a.Action.StartsWith("sla_")).Select(a => a.Action).ToListAsync());
        Assert.Equal(2, activity.Count(a => a == "sla_breached"));
        Assert.Equal(2, activity.Count(a => a == "sla_escalated"));

        var outbox = await Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().OutboxMessages.AsNoTracking()
            .Where(m => m.EventType.Contains("Sla")).Select(m => m.EventType).ToListAsync());
        Assert.Equal(1, outbox.Count(t => t.Contains(nameof(SlaReminderDue))));
        Assert.Equal(2, outbox.Count(t => t.Contains(nameof(SlaBreached))));
        Assert.Equal(2, outbox.Count(t => t.Contains(nameof(SlaEscalated))));
    }

    [Fact]
    public async Task Breaches_are_detected_within_a_minute_of_the_due_time()
    {
        var ticket = await CreateAsync(Priority.Urgent);
        Clock.Now = At(28, 10, 30).AddSeconds(30);
        await MonitorAsync();
        Assert.Contains(await EventsAsync(ticket.Id), e => e is { Kind: SlaEventKind.Breach, Target: SlaTargetKind.FirstResponse });
    }

    [Fact]
    public async Task Nothing_fires_for_paused_resolved_or_trashed_tickets()
    {
        var paused = await CreateAsync(Priority.Urgent);
        var resolved = await CreateAsync(Priority.Urgent);
        var trashed = await CreateAsync(Priority.Urgent);
        Clock.Now = At(28, 10, 5);
        await SetStatusAsync(paused.Id, "Pending");
        await SetStatusAsync(resolved.Id, "Resolved");
        await AsAgent(sp => sp.GetRequiredService<ITicketService>().MoveToTrashAsync(trashed.Id));

        Clock.Now = At(29, 12);
        await MonitorAsync();
        Assert.Empty(await EventsAsync(paused.Id));
        Assert.Empty(await EventsAsync(resolved.Id));
        Assert.Empty(await EventsAsync(trashed.Id));

        // Once the clock runs again, the shifted targets are watched.
        await SetStatusAsync(paused.Id, "Open");
        var resumed = await TicketAsync(paused.Id);
        Assert.True(resumed.DueBy > Clock.Now);
        Clock.Now = resumed.DueBy!.Value.AddMinutes(1);
        await MonitorAsync();
        Assert.Contains(await EventsAsync(paused.Id), e => e is { Kind: SlaEventKind.Breach, Target: SlaTargetKind.Resolution });
    }

    [Fact]
    public async Task A_manual_due_date_is_what_the_monitor_watches()
    {
        var ticket = await CreateAsync(Priority.Urgent);
        await AsAgent(sp => sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>On it.</p>" }));
        await UpdateAsync(ticket.Id, new TicketPatch { DueBy = At(30, 12), DueOverrideReason = "Agreed with the requester" });

        Clock.Now = At(28, 15);
        await MonitorAsync();
        Assert.DoesNotContain(await EventsAsync(ticket.Id), e => e.Kind == SlaEventKind.Breach);

        Clock.Now = At(30, 12, 1);
        await MonitorAsync();
        Assert.Contains(await EventsAsync(ticket.Id), e => e is { Kind: SlaEventKind.Breach, Target: SlaTargetKind.Resolution });
    }

    [Fact]
    public async Task A_reopened_ticket_can_breach_again_in_its_new_cycle()
    {
        var ticket = await CreateAsync(Priority.Urgent);
        await AsAgent(sp => sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>On it.</p>" }));
        Clock.Now = At(28, 14, 1);
        await MonitorAsync();
        await SetStatusAsync(ticket.Id, "Resolved");
        Clock.Now = At(29, 9);
        await SetStatusAsync(ticket.Id, "Open");
        Clock.Now = At(29, 13, 1);
        await MonitorAsync();

        var breaches = (await EventsAsync(ticket.Id)).Where(e => e is { Kind: SlaEventKind.Breach, Target: SlaTargetKind.Resolution }).ToList();
        Assert.Equal([1, 2], breaches.Select(b => b.Cycle));
    }

    [Fact]
    public async Task Concurrent_monitors_fire_each_breach_once()
    {
        var tickets = new List<Ticket>();
        for (var i = 0; i < 4; i++)
        {
            tickets.Add(await CreateAsync(Priority.Urgent));
        }
        Clock.Now = At(28, 10, 31);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(MonitorAsync)));
        await MonitorAsync();

        foreach (var t in tickets)
        {
            Assert.Single(await EventsAsync(t.Id), e => e is { Kind: SlaEventKind.Breach, Target: SlaTargetKind.FirstResponse });
        }
    }

    [Fact]
    public async Task While_nobody_is_assigned_the_group_is_told()
    {
        var ticket = await CreateAsync(Priority.Urgent, "Service Desk");
        Clock.Now = At(28, 10, 31);
        await MonitorAsync();
        var breach = Assert.Single(await EventsAsync(ticket.Id), e => e.Kind == SlaEventKind.Breach);
        Assert.Contains(Agent.Id.ToString(), breach.NotifiedUserIdsJson);
        Assert.Contains("Sam Agent", breach.Summary);
    }

    [Fact]
    public async Task Escalations_email_the_people_who_can_see_the_ticket()
    {
        var ticket = await CreateAsync(Priority.Urgent, "Service Desk", agentId: Agent.Id);
        Clock.Now = At(28, 14, 1);
        await MonitorAsync();
        await Host.ProcessOutboxAsync();

        var mine = CapturingEmailSender.Sent.Where(m => m.To.Contains(Agent.Email!)).ToList();
        Assert.Contains(mine, m => m.Subject.Contains("Escalation level 1") && m.Subject.Contains(ticket.DisplayId));
        Assert.Contains(mine, m => m.Subject.Contains("Missed first response target"));
    }
}
