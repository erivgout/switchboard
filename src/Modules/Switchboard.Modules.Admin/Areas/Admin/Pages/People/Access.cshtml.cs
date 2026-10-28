using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.People;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersView)]
public sealed class AccessModel(PeopleService people, AdminAccess access, SwitchboardDbContext db) : AdminPageModel
{
    public sealed class MembershipRow
    {
        public Guid WorkspaceId { get; set; }
        public Guid? RoleId { get; set; }
        public TicketScope Scope { get; set; } = TicketScope.Global;
    }

    public sealed class GroupRow
    {
        public Guid GroupId { get; set; }
        public GroupRole Role { get; set; }
    }

    public sealed class AccessForm
    {
        public List<MembershipRow> Memberships { get; set; } = [];
        public List<GroupRow> Groups { get; set; } = [];
        public string? Skills { get; set; }
    }

    [BindProperty]
    public AccessForm Input { get; set; } = new();

    public PersonDetail Detail { get; private set; } = default!;
    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
    public IReadOnlyList<(Guid Id, string Name, Guid WorkspaceId)> AllGroups { get; private set; } = [];
    public List<SelectListItem> Roles { get; private set; } = [];
    public bool CanEdit { get; private set; }
    public string? ReadOnlyReason { get; private set; }
    public bool ViewerIsOrgAdmin { get; private set; }
    public int OtherActiveOrgAdmins { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadAsync(id))
        {
            return RedirectToPage("Edit", new { id });
        }
        Input = new AccessForm
        {
            Memberships = Workspaces.Select(w => Detail.Memberships.FirstOrDefault(m => m.WorkspaceId == w.Id) is { } m
                ? new MembershipRow { WorkspaceId = w.Id, RoleId = m.RoleId, Scope = m.Scope }
                : new MembershipRow { WorkspaceId = w.Id }).ToList(),
            Groups = AllGroups.Select(g => new GroupRow
            {
                GroupId = g.Id,
                Role = Detail.Groups.FirstOrDefault(x => x.GroupId == g.Id) is { } x ? x.IsObserver ? GroupRole.Observer : GroupRole.Member : GroupRole.None,
            }).ToList(),
            Skills = string.Join(", ", Detail.User.Skills),
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var input = new AccessInput
        {
            Memberships = Input.Memberships.Select(m => new MembershipInput(m.WorkspaceId, m.RoleId, m.Scope)).ToList(),
            Groups = Input.Groups.Select(g => new GroupMembershipInput(g.GroupId, g.Role)).ToList(),
            Skills = (Input.Skills ?? "").Split([',', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        };
        if (await TryAsync(() => people.UpdateAccessAsync(id, input)))
        {
            TempData.Toast("Access saved");
            return RedirectToPage(new { id });
        }
        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostOrgAdminAsync(Guid id, bool grant)
    {
        if (await TryAsync(() => people.SetOrgAdminAsync(id, grant)))
        {
            TempData.Toast(grant ? "Organization admin added" : "Organization admin removed");
            return RedirectToPage(new { id });
        }
        await OnGetAsync(id);
        return Page();
    }

    private async Task<bool> LoadAsync(Guid id)
    {
        Detail = await people.GetAsync(id);
        if (!Detail.User.IsAgent && !Detail.User.IsOrgAdmin)
        {
            return false;
        }
        Workspaces = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).ToListAsync();
        AllGroups = (await db.AgentGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name, g.WorkspaceId }).ToListAsync())
            .Where(g => Workspaces.Any(w => w.Id == g.WorkspaceId))
            .Select(g => (g.Id, g.Name, g.WorkspaceId)).ToList();
        Roles = await AdminSelects.RolesAsync(db, null, "No access");
        ViewerIsOrgAdmin = await access.IsOrgAdminAsync();
        OtherActiveOrgAdmins = await db.Users.CountAsync(u => u.IsOrgAdmin && u.IsActive && u.Id != id);

        CanEdit = false;
        if (!Detail.User.IsActive)
        {
            ReadOnlyReason = "Reactivate this account before changing its access.";
        }
        else if (!await access.HasAsync(Privileges.UsersManage))
        {
            ReadOnlyReason = "Your role lets you view access but not change it.";
        }
        else if (access.ActorUserId == id && !ViewerIsOrgAdmin)
        {
            ReadOnlyReason = "You can't change your own access. Ask another admin to do it.";
        }
        else
        {
            try
            {
                await access.EnsureCanManageUserAsync(id);
                CanEdit = true;
            }
            catch (ForbiddenException ex)
            {
                ReadOnlyReason = ex.Message;
            }
        }
        return true;
    }
}
