using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Insights.Domain;
using Switchboard.Modules.Insights.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Insights.Tests;

/// <summary>Dashboards and reports count only what the viewer may see (FR-ANL-8, SR-AUTHZ-2); saved reports follow their sharing.</summary>
public sealed class InsightsTests : IAsyncLifetime
{
    private SwitchboardTestHost _host = default!;
    private User _supervisor = default!;
    private User _hardware = default!;
    private User _requester = default!;

    public async Task InitializeAsync()
    {
        _host = await SwitchboardTestHost.CreateAsync(new InsightsModule());
        _supervisor = await _host.CreateAgentAsync("Priya Supervisor", RoleKeys.Supervisor, TicketScope.Global, "Service Desk");
        _hardware = await _host.CreateAgentAsync("Jordan Hardware", RoleKeys.Agent, TicketScope.Group, "Hardware");
        _requester = await _host.CreateRequesterAsync("Dana Requester");
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<Ticket> CreateAsync(string group, Guid? agent = null, Priority priority = Priority.Medium) =>
        _host.RunAsync(SwitchboardTestHost.As(_supervisor), async sp =>
        {
            var groupId = await sp.GetRequiredService<SwitchboardDbContext>().AgentGroups.Where(g => g.Name == group).Select(g => g.Id).FirstAsync();
            return await sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
            {
                Subject = $"{group} problem",
                RequesterId = _requester.Id,
                GroupId = groupId,
                AgentId = agent,
                Priority = priority,
                SourceKey = TicketSourceKeys.Email,
            });
        });

    private Task<T> As<T>(User user, Func<InsightsQueries, Task<T>> run) =>
        _host.RunAsync(SwitchboardTestHost.As(user), sp => run(sp.GetRequiredService<InsightsQueries>()));

    [Fact]
    public async Task Dashboard_numbers_only_count_tickets_the_viewer_can_see()
    {
        await CreateAsync("Hardware");
        await CreateAsync("Hardware", _hardware.Id);
        await CreateAsync("Network");
        await CreateAsync("Service Desk");

        var jordan = await As(_hardware, q => q.DashboardAsync(TimeZoneInfo.Utc, default));
        Assert.Equal(1, jordan.Metrics.Single(m => m.Label == "Unassigned in your groups").Value);
        Assert.Equal(2, jordan.Trend.Sum(d => d.Created));
        Assert.Equal(["Hardware"], jordan.Groups.Select(g => g.Name));
        Assert.Equal(2, jordan.Groups.Single().Open);
        Assert.All(jordan.Queue, t => Assert.Equal("Hardware", t.GroupName));

        var priya = await As(_supervisor, q => q.DashboardAsync(TimeZoneInfo.Utc, default));
        Assert.Equal(4, priya.Trend.Sum(d => d.Created));
        Assert.Equal(14, priya.Trend.Count);
        Assert.Equal(1, priya.Metrics.Single(m => m.Label == "Unassigned in your groups").Value);
    }

    [Fact]
    public async Task Reports_only_count_visible_tickets_and_never_trash_or_spam()
    {
        await CreateAsync("Hardware", priority: Priority.High);
        await CreateAsync("Network", priority: Priority.High);
        var trashed = await CreateAsync("Hardware", priority: Priority.Low);
        var spam = await CreateAsync("Hardware", priority: Priority.Low);
        await _host.RunAsync(SwitchboardTestHost.As(_supervisor), async sp =>
        {
            var tickets = sp.GetRequiredService<ITicketService>();
            await tickets.MoveToTrashAsync(trashed.Id);
            await tickets.SetSpamAsync(spam.Id, true);
        });
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddDays(1);

        var jordan = await As(_hardware, q => q.VolumeAsync(from, to, "priority", null, default));
        Assert.Equal(1, jordan.Created);
        Assert.Equal(("High", 1), (jordan.Rows.Single().Label, jordan.Rows.Single().Created));

        var priya = await As(_supervisor, q => q.VolumeAsync(from, to, "group", null, default));
        Assert.Equal(2, priya.Created);
        Assert.Equal(["Hardware", "Network"], priya.Rows.Select(r => r.Label).Order());

        var agents = await As(_hardware, q => q.AgentsAsync(from, to, null, default));
        Assert.Empty(agents);
    }

    [Fact]
    public async Task Team_dashboard_belongs_to_supervisors_and_counts_visible_work()
    {
        await CreateAsync("Hardware", _hardware.Id);
        await CreateAsync("Hardware", _hardware.Id);
        await CreateAsync("Network");

        var can = await _host.RunAsync(SwitchboardTestHost.As(_supervisor), sp => sp.GetRequiredService<IPermissionService>().HasAsync(InsightsPrivileges.Team));
        var cannot = await _host.RunAsync(SwitchboardTestHost.As(_hardware), sp => sp.GetRequiredService<IPermissionService>().HasAsync(InsightsPrivileges.Team));
        Assert.True(can);
        Assert.False(cannot);

        var team = await As(_supervisor, q => q.TeamAsync(InsightsPrivileges.Team, default));
        var hardware = team.Single(g => g.Name == "Hardware");
        Assert.Equal(2, hardware.Open);
        Assert.Equal(("Jordan Hardware", 2), (hardware.Agents.Single().Name, hardware.Agents.Single().Open));
        Assert.Empty(await As(_hardware, q => q.TeamAsync(InsightsPrivileges.Team, default)));
    }

    [Fact]
    public async Task Saved_reports_follow_their_sharing_and_only_the_owner_changes_them()
    {
        var other = await _host.CreateAgentAsync("Ola Other", RoleKeys.Agent, TicketScope.Global, "Network");
        var networkId = await _host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().AgentGroups.Where(g => g.Name == "Network").Select(g => g.Id).FirstAsync());
        var (mine, everyone, network) = await _host.RunAsync(SwitchboardTestHost.As(_supervisor), async sp =>
        {
            var saved = sp.GetRequiredService<SavedReportService>();
            var a = await saved.SaveAsync(new SavedReportInput(null, "Mine", null, "/reports/volume", "period=7&by=status", ReportSharing.Private, []));
            var b = await saved.SaveAsync(new SavedReportInput(null, "Everyone", "Weekly", "/reports/agents", "period=30", ReportSharing.Everyone, []));
            var c = await saved.SaveAsync(new SavedReportInput(null, "Network only", null, "/reports/sla", "period=7", ReportSharing.Groups, [networkId]));
            return (a, b, c);
        });

        var seenByOther = await _host.RunAsync(SwitchboardTestHost.As(other), sp => sp.GetRequiredService<SavedReportService>().ListAsync());
        Assert.Equal(["Everyone", "Network only"], seenByOther.Select(v => v.Report.Name).Order());
        var seenByJordan = await _host.RunAsync(SwitchboardTestHost.As(_hardware), sp => sp.GetRequiredService<SavedReportService>().ListAsync());
        Assert.Equal(["Everyone"], seenByJordan.Select(v => v.Report.Name));

        await _host.RunAsync(SwitchboardTestHost.As(other), async sp =>
        {
            var saved = sp.GetRequiredService<SavedReportService>();
            await Assert.ThrowsAsync<NotFoundException>(() => saved.MoveToTrashAsync(everyone.Id));
            await Assert.ThrowsAsync<NotFoundException>(() => saved.ToggleFavoriteAsync(mine.Id));
            await Assert.ThrowsAsync<NotFoundException>(() => saved.SaveAsync(new SavedReportInput(network.Id, "Hijacked", null, "/reports/sla", "", ReportSharing.Everyone, [])));
            Assert.True(await saved.ToggleFavoriteAsync(everyone.Id));
        });

        await _host.RunAsync(SwitchboardTestHost.As(_supervisor), async sp =>
        {
            var saved = sp.GetRequiredService<SavedReportService>();
            await saved.MoveToTrashAsync(everyone.Id);
            Assert.DoesNotContain(await saved.ListAsync(), v => v.Report.Id == everyone.Id);
            Assert.Equal(everyone.Id, (await saved.TrashAsync()).Single().Id);
            await saved.RestoreAsync(everyone.Id);
            Assert.Contains(await saved.ListAsync(), v => v.Report.Id == everyone.Id);
        });
    }

    [Theory]
    [InlineData("https://evil.example/reports/volume", "period=7")]
    [InlineData("/reports/saved/new", "")]
    [InlineData("//evil.example", "")]
    [InlineData("/reports/volume", "period=7\"><script>")]
    public async Task Saved_reports_only_point_at_reports(string path, string query)
    {
        var error = await _host.RunAsync(SwitchboardTestHost.As(_supervisor), sp => Assert.ThrowsAsync<ValidationException>(() =>
            sp.GetRequiredService<SavedReportService>().SaveAsync(new SavedReportInput(null, "Sneaky", null, path, query, ReportSharing.Private, []))));
        Assert.True(error.Errors.ContainsKey("path") || error.Errors.ContainsKey("query"));
    }

    [Fact]
    public async Task Requesters_cannot_use_reports()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => _host.RunAsync(SwitchboardTestHost.As(_requester, Channel.Portal), sp =>
            sp.GetRequiredService<SavedReportService>().ListAsync()));
        var dashboard = await As(_requester, q => q.DashboardAsync(TimeZoneInfo.Utc, default));
        Assert.All(dashboard.Metrics, m => Assert.Equal(0, m.Value));
        Assert.Equal(0, dashboard.Trend.Sum(d => d.Created));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Plain, with comma", "\"Plain, with comma\"")]
    [InlineData("Line\nbreak", "\"Line\nbreak\"")]
    [InlineData("", "")]
    public void Csv_cells_cannot_become_formulas(string value, string expected) =>
        Assert.Equal(expected, InsightsCsv.Cell(value));

    [Fact]
    public void Chart_scales_use_round_ticks()
    {
        Assert.Equal([0, 2, 4], ChartScale.For(3).Ticks);
        Assert.Equal([0, 5, 10, 15], ChartScale.For(12).Ticks);
        Assert.Equal(200, ChartScale.For(170).Max);
        Assert.Equal(1.5, InsightsQueries.Median([3, 1, 2, 0]));
        Assert.Null(InsightsQueries.Median([]));
    }

    [Fact]
    public void Local_days_follow_the_viewers_timezone_across_dst()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var range = InsightsPeriod.Resolve("custom", "2026-10-31", "2026-11-01", zone, DateTimeOffset.UtcNow);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 4, 0, 0, TimeSpan.Zero), range.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), range.To);
        Assert.Equal("7", InsightsPeriod.Resolve("custom", "2026-11-02", "2026-11-01", zone, DateTimeOffset.UtcNow).Key);
    }
}
