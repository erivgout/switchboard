using Microsoft.AspNetCore.Authorization;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.Departments;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.DepartmentsManage)]
public sealed class IndexModel(OrgStructureService org) : AdminPageModel
{
    public IReadOnlyList<DepartmentRow> Departments { get; private set; } = [];

    public async Task OnGetAsync() => Departments = await org.ListDepartmentsAsync();
}
