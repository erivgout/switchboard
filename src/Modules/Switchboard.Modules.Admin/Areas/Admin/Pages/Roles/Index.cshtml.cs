using Microsoft.AspNetCore.Authorization;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.Roles;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.RolesManage)]
public sealed class IndexModel(RoleService roles) : AdminPageModel
{
    public IReadOnlyList<RoleRow> Roles { get; private set; } = [];

    public async Task OnGetAsync() => Roles = await roles.ListAsync();
}
