using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Groups;

/// <summary><c>/admin/groups/new?ws=</c> or <c>/admin/groups/{id}</c>.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.GroupsManage)]
public sealed class EditModel(GroupService groups, PeopleService people, SwitchboardDbContext db) : AdminPageModel
{
    public sealed class GroupForm
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public AssignmentMode Mode { get; set; }
        public int? MaxOpenPerAgent { get; set; }
        public int? EscalationMinutes { get; set; }
        public Guid? EscalateToUserId { get; set; }
        public Dictionary<Guid, GroupRole> Members { get; set; } = [];
    }

    [BindProperty]
    public GroupForm Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Ws { get; set; }

    /// <summary>A group id, or <c>none</c>.</summary>
    [BindProperty]
    public string? Replacement { get; set; }

    public AgentGroup? Group { get; private set; }
    public bool IsNew => Group is null;
    public Workspace Workspace { get; private set; } = default!;
    public IReadOnlyList<PersonOption> Candidates { get; private set; } = [];
    public IReadOnlyList<GroupMember> FormerMembers { get; private set; } = [];
    public PersonOption? EscalateTo { get; private set; }
    public int Tickets { get; private set; }
    public List<SelectListItem> Replacements { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        if (Group is not null)
        {
            var (_, members, _) = await groups.GetAsync(Group.Id);
            Input = new GroupForm
            {
                Name = Group.Name,
                Description = Group.Description,
                Mode = Group.AssignmentMode,
                MaxOpenPerAgent = Group.MaxOpenPerAgent,
                EscalationMinutes = Group.UnassignedEscalationMinutes,
                EscalateToUserId = Group.EscalateToUserId,
                Members = members.ToDictionary(m => m.UserId, m => m.IsObserver ? GroupRole.Observer : GroupRole.Member),
            };
            await LoadAsync(id);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        var input = new GroupInput(Input.Name, Input.Description, Input.Mode, Input.MaxOpenPerAgent, Input.EscalationMinutes, Input.EscalateToUserId,
            Input.Members.Select(m => new MemberInput(m.Key, m.Value)).ToList());
        var saved = Group;
        if (await TryAsync(async () =>
            {
                if (Group is null)
                {
                    saved = await groups.CreateAsync(Workspace.Id, input);
                }
                else
                {
                    await groups.UpdateAsync(Group.Id, input);
                }
            }))
        {
            TempData.Toast(Group is null ? "Group created" : "Group saved");
            return Redirect($"/admin/groups/{saved!.Id}");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (!await LoadAsync(id) || Group is null)
        {
            return NotFound();
        }
        if (TryReadReplacement(Replacement, Tickets > 0, "Choose where the group's tickets go.", out var replacement)
            && await TryAsync(() => groups.DeleteAsync(Group.Id, replacement), ""))
        {
            TempData.Toast("Group deleted");
            return Redirect($"/admin/groups?ws={Workspace.Key}");
        }
        return await OnGetAsync(id);
    }

    private async Task<bool> LoadAsync(string id)
    {
        if (id != "new")
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return false;
            }
            var (group, members, tickets) = await groups.GetAsync(guid);
            Group = group;
            Tickets = tickets;
            Workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == group.WorkspaceId);
            Replacements = (await db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == group.WorkspaceId && g.Id != guid).OrderBy(g => g.Name)
                    .Select(g => new { g.Id, g.Name }).ToListAsync())
                .Select(g => new SelectListItem(g.Name, g.Id.ToString(), g.Id.ToString() == Replacement))
                .Prepend(new SelectListItem("Choose where they go", "", string.IsNullOrEmpty(Replacement)))
                .Append(new SelectListItem("Leave them without a group", "none", Replacement == "none")).ToList();
            Candidates = await groups.CandidatesAsync(group.WorkspaceId);
            FormerMembers = members.Where(m => Candidates.All(c => c.Id != m.UserId)).ToList();
        }
        else
        {
            (Workspace, _) = await ResolveWorkspaceAsync(db, Ws);
            Candidates = await groups.CandidatesAsync(Workspace.Id);
        }
        EscalateTo = await people.OptionAsync(Input.EscalateToUserId);
        return true;
    }
}
