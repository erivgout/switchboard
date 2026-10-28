using Microsoft.AspNetCore.Authorization;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.RequesterGroups;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.GroupsManage)]
public sealed class IndexModel(GroupService groups) : AdminPageModel
{
    public IReadOnlyList<RequesterGroupRow> Groups { get; private set; } = [];

    public async Task OnGetAsync() => Groups = await groups.ListRequesterGroupsAsync();
}
