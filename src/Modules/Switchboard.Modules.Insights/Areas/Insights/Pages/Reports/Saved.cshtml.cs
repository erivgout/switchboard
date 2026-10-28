using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Insights.Domain;
using Switchboard.Modules.Insights.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Insights.Areas.Insights.Pages.Reports;

/// <summary>Save a report with its filters, or edit a saved one (FR-ANL-4).</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + InsightsPrivileges.ReportsView)]
public sealed class SavedModel(SavedReportService saved, InsightsQueries queries) : PageModel
{
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Folder { get; set; }
    [BindProperty] public string Path { get; set; } = "";
    [BindProperty] public string? Query { get; set; }
    [BindProperty] public ReportSharing Sharing { get; set; }
    [BindProperty] public List<Guid> GroupIds { get; set; } = [];

    public bool IsNew { get; private set; }
    public List<(Guid Id, string Name)> Groups { get; private set; } = [];
    public List<string> KnownFolders { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string id, string? path, string? query, CancellationToken ct)
    {
        IsNew = id == "new";
        if (IsNew)
        {
            if (!SavedReportService.IsReportPath(path))
            {
                return NotFound();
            }
            Path = path!;
            Query = query;
            Name = ReportPage.ReportName(Path);
        }
        else
        {
            if (!Guid.TryParse(id, out var reportId))
            {
                return NotFound();
            }
            var report = await saved.GetOwnAsync(reportId, ct);
            Name = report.Name;
            Folder = report.Folder;
            Path = report.Path;
            Query = report.Query;
            Sharing = report.Sharing;
            GroupIds = report.SharedGroupIds;
        }
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id, CancellationToken ct)
    {
        IsNew = id == "new";
        Guid? reportId = null;
        if (!IsNew)
        {
            if (!Guid.TryParse(id, out var g))
            {
                return NotFound();
            }
            reportId = g;
        }
        try
        {
            var report = await saved.SaveAsync(new SavedReportInput(reportId, Name, Folder, Path, Query, Sharing, GroupIds), ct);
            TempData.Toast(IsNew ? $"Report “{report.Name}” saved" : "Report updated");
            return Redirect("/reports");
        }
        catch (ValidationException ex)
        {
            foreach (var (key, messages) in ex.Errors)
            {
                foreach (var m in messages)
                {
                    ModelState.AddModelError(key, m);
                }
            }
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Groups = await queries.GroupsAsync(ct);
        KnownFolders = (await saved.ListAsync(ct)).Where(v => v.IsMine && v.Report.Folder is not null)
            .Select(v => v.Report.Folder!).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
    }
}
