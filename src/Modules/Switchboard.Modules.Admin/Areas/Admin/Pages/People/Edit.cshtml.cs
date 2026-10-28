using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.People;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersView)]
public sealed class EditModel(PeopleService people, AdminAccess access, ICustomFieldService fields, SwitchboardDbContext db) : AdminPageModel
{
    [BindProperty]
    public ProfileInput Input { get; set; } = new();

    public PersonDetail Detail { get; private set; } = default!;
    public ProfileFormModel Form { get; private set; } = default!;
    public bool CanEdit { get; private set; }
    public string? ReadOnlyReason { get; private set; }

    public async Task OnGetAsync(Guid id)
    {
        Detail = await people.GetAsync(id);
        Input = ProfileInput.From(Detail.User, Detail.SecondaryEmails);
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var definitions = await fields.GetDefinitionsAsync(FieldEntities.User, null);
        var custom = CustomFieldForm.Read(Request.Form, definitions);
        if (await TryAsync(() => people.UpdateProfileAsync(id, Input.ToInput(custom))))
        {
            TempData.Toast("Profile saved");
            return RedirectToPage(new { id });
        }
        Detail = await people.GetAsync(id);
        await LoadAsync(CustomFieldForm.ToJson(custom));
        return Page();
    }

    private async Task LoadAsync(string? customJson = null)
    {
        CanEdit = false;
        if (Detail.User.IsForgotten)
        {
            ReadOnlyReason = "Erased profiles can't be edited.";
        }
        else if (!await access.HasAsync(Privileges.UsersManage))
        {
            ReadOnlyReason = "Your role lets you view people but not change them.";
        }
        else
        {
            try
            {
                await access.EnsureCanManageUserAsync(Detail.User.Id);
                CanEdit = true;
            }
            catch (ForbiddenException ex)
            {
                ReadOnlyReason = ex.Message;
            }
        }
        Form = await ProfileFormModel.LoadAsync(db, fields, people, Input, customJson ?? Detail.User.CustomFieldsJson, !CanEdit);
    }
}
