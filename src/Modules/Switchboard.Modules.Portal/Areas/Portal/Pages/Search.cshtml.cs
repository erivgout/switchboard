using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Modules.Portal.Services;

namespace Switchboard.Modules.Portal.Areas.Portal.Pages;

public sealed class SearchModel(PortalRequests portal) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    public string Text => (Query ?? "").Trim();

    public IReadOnlyList<PortalRequestRow> Requests { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (Text.Length > 200)
        {
            Query = Text[..200];
        }
        if (Text.Length > 0)
        {
            Requests = await portal.SearchAsync(Text, 20, ct);
        }
    }
}
