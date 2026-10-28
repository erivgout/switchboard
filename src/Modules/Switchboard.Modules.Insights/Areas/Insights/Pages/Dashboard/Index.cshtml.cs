using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Modules.Insights.Services;

namespace Switchboard.Modules.Insights.Areas.Insights.Pages.Dashboard;

/// <summary>The agent dashboard (FR-ANL-1): where agents land.</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class IndexModel(InsightsQueries queries, IPermissionService permissions, IEnumerable<SlotRegistration> slots, TimeProvider time) : PageModel
{
    public DashboardData Data { get; private set; } = default!;
    public TrendChartModel Trend { get; private set; } = default!;
    public bool CanSeeTeam { get; private set; }
    public bool HasWidgets { get; private set; }
    public DateTimeOffset Now { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Now = time.GetUtcNow();
        var zone = await queries.ViewerZoneAsync(ct);
        Data = await queries.DashboardAsync(zone, ct);
        Trend = new TrendChartModel("dashTrend", Data.Trend);
        CanSeeTeam = await permissions.HasAsync(InsightsPrivileges.Team, ct: ct);
        HasWidgets = slots.Any(s => s.Slot == "dashboard.widgets");
    }

    public static string QueueHref(string query) => "/tickets?q=" + Uri.EscapeDataString(query);
}
