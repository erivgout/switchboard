using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Modules;
using Switchboard.Modules.Portal.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Portal.Areas.Portal.Pages;

public sealed class IndexModel(PortalRequests portal, IShellContext shell, IEnumerable<NavItem> nav, IEnumerable<SlotRegistration> slots) : PageModel
{
    public string FirstName { get; private set; } = "there";
    public PortalRequestPage Open { get; private set; } = new([], 0, 1, 5);
    public int WaitingCount { get; private set; }
    public int? WaitingNumber { get; private set; }

    /// <summary>The catalog when it's installed; otherwise a plain service request form.</summary>
    public string RequestHref { get; private set; } = "/portal/new?type=request";

    /// <summary>Link to approvals when the approvals page exists but nothing fills the attention slot.</summary>
    public bool ShowApprovalsLink { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var model = await shell.GetAsync(ct);
        FirstName = PortalText.FirstName(model.User?.Name);
        Open = await portal.ListAsync(RequestTab.Mine, RequestState.Open, 1, 5, ct);
        (WaitingCount, WaitingNumber) = await portal.WaitingOnMeAsync(ct);

        var portalNav = nav.Where(n => n.Area == NavArea.Portal).ToList();
        if (portalNav.Any(n => n.Href.Equals("/portal/catalog", StringComparison.OrdinalIgnoreCase)))
        {
            RequestHref = "/portal/catalog";
        }
        ShowApprovalsLink = portalNav.Any(n => n.Href.StartsWith("/portal/approvals", StringComparison.OrdinalIgnoreCase))
            && !slots.Any(s => s.Slot == PortalModule.HomeAttentionSlot);
    }
}
