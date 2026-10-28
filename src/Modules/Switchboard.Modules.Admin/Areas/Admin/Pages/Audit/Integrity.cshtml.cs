using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.Audit;

/// <summary>Hash chain status (SR-AUD-2), loaded after the page so a long log doesn't slow it down.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.AuditView)]
public sealed class IntegrityModel(AuditBrowser audit) : PageModel
{
    public ChainStatus Status { get; private set; } = default!;

    public async Task OnGetAsync() => Status = await audit.VerifyChainAsync(HttpContext.RequestAborted);
}
