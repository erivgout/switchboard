using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceDesk.Services;

namespace Switchboard.Modules.ServiceDesk.Areas.ServiceDesk.Pages.Search;

/// <summary>Global agent search: ticket references jump to the ticket; otherwise tickets, people and other modules' results.</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class IndexModel(AgentSearch search) : PageModel
{
    [FromQuery(Name = "q")] public string? Q { get; set; }
    [FromQuery(Name = "p")] public int PageNumber { get; set; } = 1;

    public AgentSearchResult Result { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Result = await search.SearchAsync(Q, Math.Max(1, PageNumber), ct);
        if (Result.RedirectNumber is { } number)
        {
            return Redirect($"/tickets/{number}");
        }
        ViewData["Title"] = string.IsNullOrEmpty(Result.Query) ? "Search" : $"Search: {Result.Query}";
        return Page();
    }
}
