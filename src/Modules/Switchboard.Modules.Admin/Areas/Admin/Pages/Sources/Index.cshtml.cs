using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Sources;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.FieldsManage)]
public sealed class IndexModel(TicketConfigService config) : AdminPageModel
{
    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string Icon { get; set; } = TicketConfigService.SourceIcons[0];

    public IReadOnlyList<SourceRow> Sources { get; private set; } = [];

    public async Task OnGetAsync() => Sources = await config.ListSourcesAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (await TryAsync(() => config.CreateSourceAsync(Name ?? "", Icon), ""))
        {
            TempData.Toast("Source added");
            return Redirect("/admin/sources");
        }
        Sources = await config.ListSourcesAsync();
        return Page();
    }
}
