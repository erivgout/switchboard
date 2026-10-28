using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Roles;

/// <summary><c>/admin/roles/new</c> or <c>/admin/roles/{id}</c>.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.RolesManage)]
public sealed class EditModel(RoleService roles, SwitchboardDbContext db) : AdminPageModel
{
    public sealed class RoleForm
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public List<string> Privileges { get; set; } = [];
    }

    [BindProperty]
    public RoleForm Input { get; set; } = new();

    [BindProperty]
    public Guid? ReplacementId { get; set; }

    public Role? Role { get; private set; }
    public bool IsNew => Role is null;
    public IReadOnlySet<string> Grantable { get; private set; } = new HashSet<string>();
    public bool IsOwnRole { get; private set; }
    public IReadOnlyList<RoleUser> Users { get; private set; } = [];
    public List<SelectListItem> Replacements { get; private set; } = [];
    public IReadOnlyList<IGrouping<string, Core.Modules.PrivilegeDefinition>> Groups => roles.Grouped;

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        if (Role is not null)
        {
            Input = new RoleForm { Name = Role.Name, Description = Role.Description, Privileges = [.. Role.Privileges] };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }
        // Privileges the actor can't change keep their current state, whatever the (disabled) checkbox sent.
        var current = Role?.Privileges ?? [];
        var submitted = Input.Privileges.Where(Grantable.Contains).Concat(current.Where(p => !Grantable.Contains(p))).Distinct().ToList();
        var input = new RoleInput(Input.Name, Input.Description, submitted);
        Role? saved = Role;
        if (await TryAsync(async () =>
            {
                if (Role is null)
                {
                    saved = await roles.CreateAsync(input);
                }
                else
                {
                    await roles.UpdateAsync(Role.Id, input);
                }
            }))
        {
            TempData.Toast(Role is null ? "Role created" : "Role saved");
            return Redirect($"/admin/roles/{saved!.Id}");
        }
        Input.Privileges = submitted;
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (!await LoadAsync(id) || Role is null)
        {
            return NotFound();
        }
        if (await TryAsync(() => roles.DeleteAsync(Role.Id, ReplacementId), ""))
        {
            TempData.Toast("Role deleted");
            return Redirect("/admin/roles");
        }
        Input = new RoleForm { Name = Role.Name, Description = Role.Description, Privileges = [.. Role.Privileges] };
        return Page();
    }

    private async Task<bool> LoadAsync(string id)
    {
        if (id != "new")
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return false;
            }
            Role = await roles.GetAsync(guid);
            Users = await roles.UsersAsync(guid);
            IsOwnRole = await roles.IsOwnRoleAsync(guid);
            Replacements = (await AdminSelects.RolesAsync(db, ReplacementId, "Choose a role")).Where(r => r.Value != guid.ToString()).ToList();
        }
        Grantable = IsOwnRole ? new HashSet<string>() : await roles.GrantableAsync(Role?.Id);
        return true;
    }
}
