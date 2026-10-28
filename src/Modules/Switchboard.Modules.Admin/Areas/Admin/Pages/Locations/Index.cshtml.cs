using Microsoft.AspNetCore.Authorization;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.Locations;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.LocationsManage)]
public sealed class IndexModel(OrgStructureService org) : AdminPageModel
{
    public IReadOnlyList<LocationRow> Locations { get; private set; } = [];

    public async Task OnGetAsync() => Locations = await org.ListLocationsAsync();
}
