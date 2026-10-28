using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.Groups;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.GroupsManage)]
public sealed class IndexModel(GroupService groups, SwitchboardDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Ws { get; set; }

    public Workspace Workspace { get; private set; } = default!;
    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
    public IReadOnlyList<GroupRow> Groups { get; private set; } = [];

    public async Task OnGetAsync()
    {
        (Workspace, Workspaces) = await ResolveWorkspaceAsync(db, Ws);
        Groups = await groups.ListAsync(Workspace.Id);
    }
}
