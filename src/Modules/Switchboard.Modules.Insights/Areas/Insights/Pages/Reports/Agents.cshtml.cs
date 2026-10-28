using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Modules.Insights.Areas.Insights.Pages.Dashboard;
using Switchboard.Modules.Insights.Services;

namespace Switchboard.Modules.Insights.Areas.Insights.Pages.Reports;

/// <summary>Agent performance: assigned, resolved, open now, median first response (FR-ANL-2).</summary>
public sealed class AgentsModel(InsightsQueries queries, TimeProvider time) : ReportPage(queries, time)
{
    public List<AgentRow> Rows { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadFiltersAsync(ct);
        Rows = await Queries.AgentsAsync(Range.From, Range.To, Group, ct);
    }

    public async Task<IActionResult> OnGetCsvAsync(CancellationToken ct)
    {
        await OnGetAsync(ct);
        var rows = new List<IReadOnlyList<string?>>
        {
            new[] { "Agent", "Assigned (created in period)", "Resolved in period", "Open now", "Median first response (minutes)", "First responses counted" },
        };
        rows.AddRange(Rows.Select(r => (IReadOnlyList<string?>)new[]
        {
            r.Name, r.Assigned.ToString(CultureInfo.InvariantCulture), r.Resolved.ToString(CultureInfo.InvariantCulture),
            r.OpenNow.ToString(CultureInfo.InvariantCulture), InsightsCsv.Number(r.MedianFirstResponseMinutes), r.Responded.ToString(CultureInfo.InvariantCulture),
        }));
        return File(InsightsCsv.Write(rows), "text/csv; charset=utf-8", $"agent-performance-{Range.FirstDay:yyyy-MM-dd}-to-{Range.LastDay:yyyy-MM-dd}.csv");
    }

    public static string Minutes(double? m) => TeamModel.Minutes(m);
}
