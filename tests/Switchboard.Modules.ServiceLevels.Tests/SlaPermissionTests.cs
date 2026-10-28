using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Reports;
using Switchboard.Modules.ServiceLevels.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.ServiceLevels.Tests;

/// <summary>Who may configure service levels, and what SLA reports show to whom (FR-ANL-8, SR-AUTHZ-2).</summary>
public sealed class SlaPermissionTests : SlaTestBase
{
    [Fact]
    public async Task Only_people_with_sla_manage_can_change_policies_and_calendars()
    {
        var agent = await Host.CreateAgentAsync("Ann Agent", RoleKeys.Agent);
        var admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        var policy = await Host.RunAsync(SwitchboardTestHost.As(admin), async sp => (await sp.GetRequiredService<SlaAdminService>().PoliciesAsync(Host.Workspace.Id)).Single());
        var calendar = await Host.RunAsync(SwitchboardTestHost.As(admin), async sp => (await sp.GetRequiredService<SlaAdminService>().CalendarsAsync(Host.Workspace.Id)).Single());

        foreach (var who in new[] { agent, Agent })
        {
            await Host.RunAsync(SwitchboardTestHost.As(who), async sp =>
            {
                var service = sp.GetRequiredService<SlaAdminService>();
                await Assert.ThrowsAsync<ForbiddenException>(() => service.PoliciesAsync(Host.Workspace.Id));
                await Assert.ThrowsAsync<ForbiddenException>(() => service.SavePolicyAsync(new PolicyInput
                {
                    WorkspaceId = Host.Workspace.Id,
                    Name = "Sneaky",
                    Targets = [new SlaTarget(Priority.Low, 1, null, 1)],
                }));
                // Records in a workspace you can't manage look like they don't exist (SR-AUTHZ-6).
                await Assert.ThrowsAsync<NotFoundException>(() => service.PolicyAsync(policy.Id));
                await Assert.ThrowsAsync<NotFoundException>(() => service.DeletePolicyAsync(policy.Id));
                await Assert.ThrowsAsync<NotFoundException>(() => service.ImportHolidaysAsync(calendar.Id, "2026-12-25, Christmas Day"));
            });
        }
    }

    [Fact]
    public async Task Policy_input_is_validated_in_plain_language()
    {
        var admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        var error = await Host.RunAsync(SwitchboardTestHost.As(admin), sp => Assert.ThrowsAsync<ValidationException>(() =>
            sp.GetRequiredService<SlaAdminService>().SavePolicyAsync(new PolicyInput
            {
                WorkspaceId = Host.Workspace.Id,
                Name = "",
                Conditions = [new SlaCondition(SlaConditionFields.Group, [Guid.NewGuid().ToString()])],
                Escalations = new SlaEscalationPlan
                {
                    Resolution = [new SlaEscalationLevel(1, 60, new SlaNotifyTargets()), new SlaEscalationLevel(2, 30, new SlaNotifyTargets())],
                },
            })));
        Assert.Contains("name", error.Errors.Keys);
        Assert.Contains("targets", error.Errors.Keys);
        Assert.Contains("conditions", error.Errors.Keys);
        Assert.Contains("Level 2 can't fire before level 1.", error.Errors["escalations"]);
    }

    [Fact]
    public async Task Holiday_lists_import_from_pasted_text()
    {
        var admin = await Host.CreateAgentAsync("Ada Admin", RoleKeys.WorkspaceAdmin);
        var result = await Host.RunAsync(SwitchboardTestHost.As(admin), async sp =>
        {
            var service = sp.GetRequiredService<SlaAdminService>();
            var calendar = (await service.CalendarsAsync(Host.Workspace.Id)).Single();
            await service.ImportHolidaysAsync(calendar.Id, "2026-12-25,Christmas Day");
            var second = await service.ImportHolidaysAsync(calendar.Id, "Date,Name\n2026-12-25,Christmas\n\"Jan 1, 2027\",New Year's Day\nNew Year's Day 2027\n2027-07-05;Independence Day (observed)");
            var reloaded = await service.CalendarAsync(calendar.Id);
            return (second, reloaded.Holidays.Select(h => (h.Date, h.Name)).ToList());
        });
        Assert.Equal(2, result.second.Added);
        Assert.Equal(1, result.second.Updated);
        Assert.Equal("Line 4: couldn't read a date in “New Year's Day 2027”. Use a date like 2026-12-25.", Assert.Single(result.second.Errors));
        Assert.Equal(
            [(new DateOnly(2026, 12, 25), "Christmas"), (new DateOnly(2027, 1, 1), "New Year's Day"), (new DateOnly(2027, 7, 5), "Independence Day (observed)")],
            result.Item2);
    }

    [Fact]
    public async Task Sla_reports_only_count_tickets_the_viewer_can_see()
    {
        var jordan = await Host.CreateAgentAsync("Jordan Group", RoleKeys.Agent, TicketScope.Group, "Hardware");
        var hardware = await CreateAsync(Priority.Urgent, "Hardware");
        await CreateAsync(Priority.Urgent, "Network");
        await CreateAsync(Priority.Urgent, "Service Desk");
        Clock.Now = At(28, 15);

        var seen = await Host.RunAsync(SwitchboardTestHost.As(jordan), async sp =>
        {
            var reports = sp.GetRequiredService<SlaReportService>();
            var compliance = await reports.ComplianceAsync(At(28, 0), At(29, 0), "group", null, null, default);
            var performance = await reports.PerformanceAsync(At(28, 0), Clock.Now, default);
            return (compliance, performance);
        });
        Assert.Equal(1, seen.compliance.Total.Tickets);
        Assert.Equal(hardware.Id, Assert.Single(seen.compliance.Exceptions).Row.TicketId);
        Assert.Equal(1, seen.performance.ResolutionTotal);

        var everything = await AsAgent(sp => sp.GetRequiredService<SlaReportService>().ComplianceAsync(At(28, 0), At(29, 0), "priority", null, null, default));
        Assert.Equal(3, everything.Total.Tickets);
        Assert.Equal(3, everything.Total.ResolutionMissed);
    }

    [Fact]
    public async Task Reports_show_the_original_target_beside_a_manual_override()
    {
        var ticket = await CreateAsync(Priority.High);
        await UpdateAsync(ticket.Id, new Core.Tickets.TicketPatch { DueBy = At(30, 12), DueOverrideReason = "Parts on order" });
        Clock.Now = At(29, 12);

        var report = await AsAgent(sp => sp.GetRequiredService<SlaReportService>().ComplianceAsync(At(28, 0), At(29, 0), "priority", null, null, default));
        var row = Assert.Single(report.Exceptions);
        Assert.Equal(At(29, 10), row.Row.ResolutionTargetAt);
        Assert.Equal(At(30, 12), row.Row.DueBy);
        Assert.Equal(SlaOutcome.Pending, row.Resolution);
        Assert.Equal(SlaOutcome.Missed, row.ResolutionAgainstOriginal);
        Assert.Equal("Parts on order", row.Row.OverrideReason);
    }
}
