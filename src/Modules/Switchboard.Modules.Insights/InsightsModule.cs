using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Insights.Services;

namespace Switchboard.Modules.Insights;

public static class InsightsPrivileges
{
    /// <summary>Curated reports and saved reports. Agents get it by default (it ends in <c>.view</c>).</summary>
    public const string ReportsView = "reports.view";
    /// <summary>Team dashboards. Seeded onto the service desk supervisor role.</summary>
    public const string Team = "insights.team";
}

/// <summary>Dashboards and reports (SRS §3.26).</summary>
public sealed class InsightsModule : ISwitchboardModule
{
    public string Name => "Insights";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPrivileges(
            new PrivilegeDefinition(InsightsPrivileges.ReportsView, "View reports", "Analytics"),
            new PrivilegeDefinition(InsightsPrivileges.Team, "View team dashboards", "Analytics"));
        services.AddNavItem(new NavItem(NavArea.Agent, "dashboard", "Dashboard", "bi-speedometer2", "/dashboard", Order: 10));
        services.AddNavItem(new NavItem(NavArea.Agent, "reports", "Reports", "bi-bar-chart-line", "/reports", Order: 80, Privilege: InsightsPrivileges.ReportsView));
        services.AddScoped<InsightsQueries>();
        services.AddScoped<SavedReportService>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InsightsModule).Assembly);

    /// <summary>Once per instance: give supervisors the team dashboard. Removing it later sticks.</summary>
    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        const string seeded = "insights.seeded_team_privilege";
        var db = services.GetRequiredService<SwitchboardDbContext>();
        var settings = services.GetRequiredService<ISettingsService>();
        if (await settings.GetAsync(seeded, false, ct: cancellationToken))
        {
            return;
        }
        var supervisor = await db.Roles.FirstOrDefaultAsync(r => r.Key == RoleKeys.Supervisor, cancellationToken);
        if (supervisor is not null && !supervisor.Privileges.Contains(InsightsPrivileges.Team))
        {
            supervisor.Privileges = [.. supervisor.Privileges, InsightsPrivileges.Team];
        }
        await settings.SetAsync(seeded, true, ct: cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
