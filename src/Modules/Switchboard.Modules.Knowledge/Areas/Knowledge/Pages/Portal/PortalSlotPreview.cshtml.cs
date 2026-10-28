using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Hosting;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages.Portal;

/// <summary>Development only: this module's portal slots outside the portal module's pages.</summary>
public sealed class PortalSlotPreviewModel(IHostEnvironment environment) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Q { get; set; } = "password";

    public IActionResult OnGet() => environment.IsDevelopment() ? Page() : NotFound();
}
