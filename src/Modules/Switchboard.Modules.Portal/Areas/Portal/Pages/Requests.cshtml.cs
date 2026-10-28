using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Modules.Portal.Services;

namespace Switchboard.Modules.Portal.Areas.Portal.Pages;

public sealed class RequestsModel(PortalRequests portal) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "tab")]
    public string? TabValue { get; set; }

    [BindProperty(SupportsGet = true, Name = "state")]
    public string? StateValue { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public RequestTab Tab { get; private set; }
    public RequestState State { get; private set; }
    public PortalTabCounts Counts { get; private set; } = new(0, 0, 0, false);
    public PortalRequestPage Results { get; private set; } = new([], 0, 1, 20);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Tab = TabValue?.ToLowerInvariant() switch
        {
            "shared" => RequestTab.Shared,
            "department" => RequestTab.Department,
            _ => RequestTab.Mine,
        };
        State = StateValue?.ToLowerInvariant() switch
        {
            "closed" => RequestState.Closed,
            "all" => RequestState.All,
            _ => RequestState.Open,
        };
        Counts = await portal.CountsAsync(State, ct);
        if (Tab == RequestTab.Department && !Counts.IsDepartmentLead)
        {
            return NotFound();
        }
        Results = await portal.ListAsync(Tab, State, PageNumber, 20, ct);
        return Page();
    }

    public string Link(RequestTab? tab = null, RequestState? state = null, int page = 1)
    {
        var t = tab ?? Tab;
        var s = state ?? State;
        var query = new List<string>();
        if (t != RequestTab.Mine)
        {
            query.Add($"tab={t.ToString().ToLowerInvariant()}");
        }
        if (s != RequestState.Open)
        {
            query.Add($"state={s.ToString().ToLowerInvariant()}");
        }
        if (page > 1)
        {
            query.Add($"page={page}");
        }
        return "/portal/requests" + (query.Count == 0 ? "" : "?" + string.Join('&', query));
    }

    public string StateWord => State switch
    {
        RequestState.Closed => "closed",
        RequestState.All => "",
        _ => "open",
    };
}
