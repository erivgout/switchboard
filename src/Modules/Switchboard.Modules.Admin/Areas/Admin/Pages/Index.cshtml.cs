using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages;

/// <summary>The admin console home: every admin area the user may open, grouped by outcome (FR-ADM-1).</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class IndexModel(IShellContext shell) : PageModel
{
    public IReadOnlyList<IGrouping<string, NavItem>> Groups { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var model = await shell.GetAsync();
        if (!model.HasAdmin)
        {
            return Forbid();
        }
        // The shell already orders groups by outcome, so the home matches the admin menu.
        Groups = model.AdminNav;
        return Page();
    }
}
