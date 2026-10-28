using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Services;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Admin;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + CatalogPrivileges.Manage)]
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => Redirect("/admin/catalog/items");
}
