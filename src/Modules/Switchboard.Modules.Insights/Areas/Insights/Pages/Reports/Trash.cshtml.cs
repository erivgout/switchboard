using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Insights.Domain;
using Switchboard.Modules.Insights.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Insights.Areas.Insights.Pages.Reports;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + InsightsPrivileges.ReportsView)]
public sealed class TrashModel(SavedReportService saved) : PageModel
{
    public List<InsSavedReport> Reports { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) => Reports = await saved.TrashAsync(ct);

    public async Task<IActionResult> OnPostRestoreAsync(Guid id, CancellationToken ct)
    {
        await saved.RestoreAsync(id, ct);
        TempData.Toast("Report restored");
        return Redirect("/reports/trash");
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        await saved.DeleteForeverAsync(id, ct);
        TempData.Toast("Report deleted");
        return Redirect("/reports/trash");
    }
}
