using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.People;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersManage)]
public sealed class MergeModel(PeopleService people, PersonLifecycleService lifecycle) : AdminPageModel
{
    [BindProperty]
    public Guid? SurvivorId { get; set; }

    public PersonDetail Detail { get; private set; } = default!;
    public PersonOption? Survivor { get; private set; }

    public async Task OnGetAsync(Guid id) => await LoadAsync(id);

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        if (SurvivorId is not { } survivor)
        {
            ModelState.AddModelError(nameof(SurvivorId), "Choose the person to keep.");
        }
        else if (await TryAsync(() => lifecycle.MergeAsync(id, survivor), ""))
        {
            TempData.Toast("Requesters merged");
            return Redirect($"/admin/people/{survivor}");
        }
        await LoadAsync(id);
        return Page();
    }

    private async Task LoadAsync(Guid id)
    {
        Detail = await people.GetAsync(id);
        Survivor = await people.OptionAsync(SurvivorId);
    }
}
