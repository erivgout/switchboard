using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.People;

/// <summary>Erase a person's personal data (SR-PRIV-3). Organization admins only, with typed confirmation.</summary>
[Authorize(Policy = SwitchboardPolicies.OrgAdmin)]
public sealed class ForgetModel(PeopleService people, PersonLifecycleService lifecycle) : AdminPageModel
{
    [BindProperty]
    public string? Confirmation { get; set; }

    public PersonDetail Detail { get; private set; } = default!;
    public string Expected => Detail.User.Email ?? Detail.User.DisplayName;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Detail = await people.GetAsync(id);
        return Detail.User.IsForgotten ? RedirectToPage("Account", new { id }) : Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (await TryAsync(() => lifecycle.ForgetAsync(id, Confirmation ?? ""), ""))
        {
            TempData.Toast("Personal data erased");
            return RedirectToPage("Account", new { id });
        }
        Detail = await people.GetAsync(id);
        return Page();
    }
}
