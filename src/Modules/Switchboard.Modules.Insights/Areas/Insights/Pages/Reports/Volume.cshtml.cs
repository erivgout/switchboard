using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Modules.Insights.Services;

namespace Switchboard.Modules.Insights.Areas.Insights.Pages.Reports;

/// <summary>Ticket volume by status, priority, group, category, source or type (FR-ANL-2).</summary>
public sealed class VolumeModel(InsightsQueries queries, TimeProvider time) : ReportPage(queries, time)
{
    [FromQuery(Name = "by")] public string? By { get; set; }

    public VolumeReport Report { get; private set; } = default!;

    public override string Query => base.Query + $"&by={Report.By}";

    public string DimensionLabel => InsightsQueries.VolumeDimensions.First(d => d.Key == Report.By).Label;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadFiltersAsync(ct);
        Report = await Queries.VolumeAsync(Range.From, Range.To, By, Group, ct);
    }

    public async Task<IActionResult> OnGetCsvAsync(CancellationToken ct)
    {
        await OnGetAsync(ct);
        var rows = new List<IReadOnlyList<string?>> { new[] { DimensionLabel, "Created", "Resolved", "Created and still open" } };
        rows.AddRange(Report.Rows.Select(r => (IReadOnlyList<string?>)new[]
        {
            r.Label, r.Created.ToString(CultureInfo.InvariantCulture), r.Resolved.ToString(CultureInfo.InvariantCulture), r.StillOpen.ToString(CultureInfo.InvariantCulture),
        }));
        rows.Add(new[] { "Total", Report.Created.ToString(CultureInfo.InvariantCulture), Report.Resolved.ToString(CultureInfo.InvariantCulture), Report.StillOpen.ToString(CultureInfo.InvariantCulture) });
        return File(InsightsCsv.Write(rows), "text/csv; charset=utf-8", $"ticket-volume-by-{Report.By}-{Range.FirstDay:yyyy-MM-dd}-to-{Range.LastDay:yyyy-MM-dd}.csv");
    }
}
