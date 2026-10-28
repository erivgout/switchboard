using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Services;

namespace Switchboard.Modules.ServiceLevels.Areas.ServiceLevels.Pages.Admin;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + SlaAdminService.Privilege)]
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet(string? ws) =>
        Redirect(string.IsNullOrEmpty(ws) ? "/admin/service-levels/policies" : $"/admin/service-levels/policies?ws={Uri.EscapeDataString(ws)}");
}
