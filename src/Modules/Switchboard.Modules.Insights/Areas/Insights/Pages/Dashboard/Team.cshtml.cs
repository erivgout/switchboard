using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Insights.Services;

namespace Switchboard.Modules.Insights.Areas.Insights.Pages.Dashboard;

/// <summary>Team dashboard for supervisors (FR-ANL-1): per group, over the tickets the viewer can see.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + InsightsPrivileges.Team)]
public sealed class TeamModel(InsightsQueries queries) : PageModel
{
    public List<TeamGroup> Groups { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) => Groups = await queries.TeamAsync(InsightsPrivileges.Team, ct);

    public static string Minutes(double? minutes)
    {
        if (minutes is not { } m)
        {
            return "–";
        }
        var span = TimeSpan.FromMinutes(Math.Round(m));
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{Math.Max(0, span.Minutes)}m";
    }
}
