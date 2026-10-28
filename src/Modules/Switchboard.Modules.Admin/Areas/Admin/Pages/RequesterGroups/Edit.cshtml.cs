using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.RequesterGroups;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.GroupsManage)]
public sealed class EditModel(GroupService groups) : AdminPageModel
{
    public sealed class GroupForm
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
    }

    [BindProperty]
    public GroupForm Input { get; set; } = new();

    [BindProperty]
    public Guid? NewMemberId { get; set; }

    public RequesterGroup? Group { get; private set; }
    public IReadOnlyList<GroupMember> Members { get; private set; } = [];
    public bool IsNew => Group is null;

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        if (Group is not null)
        {
            Input = new GroupForm { Name = Group.Name, Description = Group.Description };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        RequesterGroup? saved = null;
        if (await TryAsync(async () => saved = await groups.SaveRequesterGroupAsync(Group?.Id, Input.Name, Input.Description)))
        {
            TempData.Toast(Group is null ? "Group created" : "Group saved");
            return Redirect($"/admin/requester-groups/{saved!.Id}");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(string id)
    {
        if (!await LoadAsync(id) || Group is null)
        {
            return NotFound();
        }
        if (NewMemberId is not { } member)
        {
            ModelState.AddModelError(nameof(NewMemberId), "Search for the person to add.");
            Input = new GroupForm { Name = Group.Name, Description = Group.Description };
            return Page();
        }
        var added = await groups.AddRequesterMembersAsync(Group.Id, [member]);
        TempData.Toast(added > 0 ? "Member added" : "They're already in this group");
        return Redirect($"/admin/requester-groups/{Group.Id}");
    }

    public async Task<IActionResult> OnPostRemoveAsync(string id, Guid userId)
    {
        if (!await LoadAsync(id) || Group is null)
        {
            return NotFound();
        }
        await groups.RemoveRequesterMemberAsync(Group.Id, userId);
        TempData.Toast("Member removed");
        return Redirect($"/admin/requester-groups/{Group.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (!await LoadAsync(id) || Group is null)
        {
            return NotFound();
        }
        await groups.DeleteRequesterGroupAsync(Group.Id);
        TempData.Toast("Group deleted");
        return Redirect("/admin/requester-groups");
    }

    private async Task<bool> LoadAsync(string id)
    {
        if (id == "new")
        {
            return true;
        }
        if (!Guid.TryParse(id, out var guid))
        {
            return false;
        }
        (Group, Members) = await groups.GetRequesterGroupAsync(guid);
        return true;
    }
}
