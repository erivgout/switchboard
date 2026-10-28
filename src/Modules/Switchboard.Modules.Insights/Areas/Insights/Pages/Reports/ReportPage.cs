using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Insights.Services;

namespace Switchboard.Modules.Insights.Areas.Insights.Pages.Reports;

/// <summary>Shared filters for curated reports: a date range, a group, and the query string that reproduces them.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + InsightsPrivileges.ReportsView)]
public abstract class ReportPage(InsightsQueries queries, TimeProvider time) : PageModel
{
    [FromQuery(Name = "period")] public string? Period { get; set; }
    [FromQuery(Name = "from")] public string? From { get; set; }
    [FromQuery(Name = "to")] public string? To { get; set; }
    [FromQuery(Name = "group")] public Guid? Group { get; set; }

    public InsightsPeriod Range { get; private set; } = default!;
    public List<(Guid Id, string Name)> Groups { get; private set; } = [];

    protected InsightsQueries Queries => queries;

    protected async Task LoadFiltersAsync(CancellationToken ct)
    {
        Range = InsightsPeriod.Resolve(Period, From, To, await queries.ViewerZoneAsync(ct), time.GetUtcNow());
        Groups = await queries.GroupsAsync(ct);
        if (Group is { } g && Groups.All(x => x.Id != g))
        {
            Group = null;
        }
    }

    /// <summary>The filters as a query string, for CSV links and saved reports.</summary>
    public virtual string Query => Range.QueryPart + (Group is { } g ? $"&group={g}" : "");

    public string SaveHref(string path) =>
        $"/reports/saved/new?path={Uri.EscapeDataString(path)}&query={Uri.EscapeDataString(Query)}";

    public static string ReportName(string path) => path switch
    {
        "/reports/volume" => "Ticket volume",
        "/reports/agents" => "Agent performance",
        "/reports/sla" => "SLA compliance",
        _ => "Report",
    };
}
