using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Modules.Portal.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Portal.Areas.Portal.Pages;

/// <summary>Confirmation after filing a request: what happens next, in plain language.</summary>
public sealed class SentModel(PortalRequests portal, IShellContext shell) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Number { get; set; }

    public PortalRequestView View { get; private set; } = default!;
    public string FirstName { get; private set; } = "there";

    public async Task OnGetAsync(CancellationToken ct)
    {
        View = await portal.GetAsync(Number, ct);
        FirstName = PortalText.FirstName((await shell.GetAsync(ct)).User?.Name);
    }
}
