using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceDesk.Data;
using Switchboard.Modules.ServiceDesk.Services;

namespace Switchboard.Modules.ServiceDesk.Areas.ServiceDesk.Pages.Tickets;

/// <summary>Progress and per-record results of a bulk action (SR-AUT-5).</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class JobModel(BulkActionService bulk) : PageModel
{
    public SdBulkJob Job { get; private set; } = default!;
    public IReadOnlyList<SdBulkJobItem> Items { get; private set; } = [];
    public string Back { get; private set; } = "/tickets";

    public async Task<IActionResult> OnGetAsync(Guid id, string? back, CancellationToken ct)
    {
        await LoadAsync(id, back, ct);
        ViewData["Title"] = Job.Summary;
        return Page();
    }

    public async Task<IActionResult> OnGetProgressAsync(Guid id, string? back, CancellationToken ct)
    {
        await LoadAsync(id, back, ct);
        return Partial("_JobProgress", this);
    }

    private async Task LoadAsync(Guid id, string? back, CancellationToken ct)
    {
        Job = await bulk.GetJobAsync(id, ct);
        Items = await bulk.GetItemsAsync(id, ct);
        Back = !string.IsNullOrEmpty(back) && back.StartsWith("/tickets", StringComparison.Ordinal) && !back.StartsWith("//", StringComparison.Ordinal) ? back : "/tickets";
    }
}
