using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Insights.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Insights.Areas.Insights.Pages.Reports;

/// <summary>The reports catalog and saved reports (FR-ANL-2, FR-ANL-4).</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + InsightsPrivileges.ReportsView)]
public sealed class IndexModel(SavedReportService saved) : PageModel
{
    public List<SavedReportView> Starred { get; private set; } = [];
    public List<IGrouping<string, SavedReportView>> Folders { get; private set; } = [];
    public int SavedCount { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var all = await saved.ListAsync(ct);
        SavedCount = all.Count;
        Starred = all.Where(r => r.IsFavorite).ToList();
        Folders = all.GroupBy(r => r.Report.Folder ?? "")
            .OrderBy(g => g.Key.Length == 0 ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IActionResult> OnPostFavoriteAsync(Guid id, CancellationToken ct)
    {
        var on = await saved.ToggleFavoriteAsync(id, ct);
        TempData.Toast(on ? "Added to starred" : "Removed from starred");
        return Redirect("/reports");
    }

    public async Task<IActionResult> OnPostTrashAsync(Guid id, CancellationToken ct)
    {
        await saved.MoveToTrashAsync(id, ct);
        TempData.Toast("Report moved to trash");
        return Redirect("/reports");
    }

    public static string Sharing(SavedReportView v) => v.Report.Sharing switch
    {
        Domain.ReportSharing.Everyone => "Shared with everyone",
        Domain.ReportSharing.Groups => "Shared with groups",
        _ => "Only you",
    };
}
