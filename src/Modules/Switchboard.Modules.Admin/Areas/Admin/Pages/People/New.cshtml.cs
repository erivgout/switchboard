using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.People;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersManage)]
public sealed class NewModel(PeopleService people, InvitationService invitations, ICustomFieldService fields, SwitchboardDbContext db) : AdminPageModel
{
    [BindProperty]
    public ProfileInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string Kind { get; set; } = "requester";

    [BindProperty]
    public Guid WorkspaceId { get; set; }

    [BindProperty]
    public Guid? RoleId { get; set; }

    [BindProperty]
    public TicketScope Scope { get; set; } = TicketScope.Global;

    [BindProperty]
    public bool Invite { get; set; } = true;

    public bool IsAgent => Kind == "agent";
    public ProfileFormModel Form { get; private set; } = default!;
    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
    public List<SelectListItem> Roles { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Invite = IsAgent;
        await LoadAsync("{}");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var definitions = await fields.GetDefinitionsAsync(FieldEntities.User, null);
        var custom = CustomFieldForm.Read(Request.Form, definitions);
        NewAgentAccess? agent = null;
        if (IsAgent)
        {
            if (RoleId is { } role)
            {
                agent = new NewAgentAccess(WorkspaceId, role, Scope);
            }
            else
            {
                ModelState.AddModelError(nameof(RoleId), "Choose the agent's role.");
            }
        }

        User? created = null;
        if ((!IsAgent || agent is not null) && await TryAsync(async () => created = await people.CreateAsync(Input.ToInput(custom), agent)))
        {
            var message = IsAgent ? "Agent added" : "Requester added";
            if (Invite)
            {
                try
                {
                    await invitations.InviteAsync(created!.Id);
                    message += ". Invitation sent";
                }
                catch (ValidationException ex)
                {
                    TempData.Toast($"{message}, but the invitation wasn't sent: {ex.Message}", ToastKind.Error);
                    return Redirect($"/admin/people/{created!.Id}");
                }
            }
            TempData.Toast(message);
            return Redirect($"/admin/people/{created!.Id}");
        }
        await LoadAsync(CustomFieldForm.ToJson(custom));
        return Page();
    }

    private async Task LoadAsync(string customJson)
    {
        Kind = Kind == "agent" ? "agent" : "requester";
        Form = await ProfileFormModel.LoadAsync(db, fields, people, Input, customJson, disabled: false);
        (var current, Workspaces) = await ResolveWorkspaceAsync(db, null);
        if (WorkspaceId == Guid.Empty)
        {
            WorkspaceId = current.Id;
        }
        Roles = await AdminSelects.RolesAsync(db, RoleId, "Choose a role");
    }
}
