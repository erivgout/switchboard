using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.People;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersView)]
public sealed class AccountModel(PeopleService people, PersonLifecycleService lifecycle, InvitationService invitations, AdminAccess access) : AdminPageModel
{
    public PersonDetail Detail { get; private set; } = default!;
    public bool CanManage { get; private set; }
    public string? ReadOnlyReason { get; private set; }
    public bool ViewerIsOrgAdmin { get; private set; }
    public bool IsSelf { get; private set; }

    public async Task OnGetAsync(Guid id) => await LoadAsync(id);

    public async Task<IActionResult> OnPostInviteAsync(Guid id)
    {
        if (await TryAsync(() => invitations.InviteAsync(id), ""))
        {
            TempData.Toast("Invitation sent");
            return RedirectToPage(new { id });
        }
        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostReactivateAsync(Guid id)
    {
        if (await TryAsync(() => lifecycle.ReactivateAsync(id), ""))
        {
            TempData.Toast("Account reactivated");
            return RedirectToPage(new { id });
        }
        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostExportAsync(Guid id)
    {
        var json = await lifecycle.ExportAsync(id);
        return File(json, "application/json", $"person-{id:N}.json");
    }

    private async Task LoadAsync(Guid id)
    {
        Detail = await people.GetAsync(id);
        ViewerIsOrgAdmin = await access.IsOrgAdminAsync();
        IsSelf = access.ActorUserId == id;
        CanManage = false;
        if (!await access.HasAsync(Privileges.UsersManage))
        {
            ReadOnlyReason = "Your role lets you view people but not change their accounts.";
            return;
        }
        try
        {
            await access.EnsureCanManageUserAsync(id);
            CanManage = true;
        }
        catch (ForbiddenException ex)
        {
            ReadOnlyReason = ex.Message;
        }
    }
}
