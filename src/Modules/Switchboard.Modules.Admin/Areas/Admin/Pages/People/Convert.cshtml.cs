using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.People;

/// <summary>Requester to agent (choose a first role) or agent to requester (hand off open work first).</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersManage)]
public sealed class ConvertModel(PeopleService people, PersonLifecycleService lifecycle, SwitchboardDbContext db) : AdminPageModel
{
    public sealed class AgentForm
    {
        public Guid WorkspaceId { get; set; }
        public Guid? RoleId { get; set; }
        public TicketScope Scope { get; set; } = TicketScope.Global;
    }

    [BindProperty]
    public AgentForm Agent { get; set; } = new();

    [BindProperty]
    public HandOffInput Input { get; set; } = new();

    public PersonDetail Detail { get; private set; } = default!;
    public bool ToAgent { get; private set; }
    public HandOffModel? HandOff { get; private set; }
    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
    public List<SelectListItem> Roles { get; private set; } = [];

    public async Task OnGetAsync(Guid id) => await LoadAsync(id);

    public async Task<IActionResult> OnPostAgentAsync(Guid id)
    {
        var ok = Agent.RoleId is { } role
            ? await TryAsync(() => lifecycle.ConvertToAgentAsync(id, new NewAgentAccess(Agent.WorkspaceId, role, Agent.Scope)), "Agent")
            : Fail("Agent.RoleId", "Choose the role they get.");
        if (ok)
        {
            TempData.Toast("Converted to agent");
            return Redirect($"/admin/people/{id}/access");
        }
        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostRequesterAsync(Guid id)
    {
        if (await TryAsync(() => lifecycle.ConvertToRequesterAsync(id, Input.ToPlan())))
        {
            TempData.Toast("Converted to requester");
            return RedirectToPage("Account", new { id });
        }
        await LoadAsync(id);
        return Page();
    }

    private bool Fail(string key, string message)
    {
        ModelState.AddModelError(key, message);
        return false;
    }

    private async Task LoadAsync(Guid id)
    {
        Detail = await people.GetAsync(id);
        ToAgent = !Detail.User.IsAgent && !Detail.User.IsOrgAdmin;
        (_, Workspaces) = await ResolveWorkspaceAsync(db, null);
        if (Agent.WorkspaceId == Guid.Empty)
        {
            Agent.WorkspaceId = Workspaces[0].Id;
        }
        Roles = await AdminSelects.RolesAsync(db, Agent.RoleId, "Choose a role");
        if (!ToAgent)
        {
            HandOff = await HandOffLoader.LoadAsync(db, people, lifecycle, Input, Detail);
        }
    }
}
