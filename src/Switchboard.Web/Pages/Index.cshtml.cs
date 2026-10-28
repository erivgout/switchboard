using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.UI;

namespace Switchboard.Web.Pages;

/// <summary>Sends agents to their console and requesters to the portal.</summary>
[AllowAnonymous]
public sealed class IndexModel(IShellContext shell) : PageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Redirect("/portal");
        }
        var model = await shell.GetAsync(HttpContext.RequestAborted);
        if (model.User?.IsAgent == true)
        {
            return Redirect(model.AgentNav.FirstOrDefault()?.Href ?? "/profile");
        }
        return Redirect("/portal");
    }
}
