using Microsoft.AspNetCore.Authorization;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Services;
using Switchboard.Modules.Catalog.Web;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Agent;

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class IndexModel(ICatalogService catalog, IPermissionService permissions) : CatalogBrowsePageBase(catalog)
{
    protected override bool ForAgent => true;
    protected override string BasePath => "/catalog";

    public bool CanManage { get; private set; }

    public override async Task OnGetAsync(string? q, Guid? category)
    {
        await base.OnGetAsync(q, category);
        CanManage = await permissions.HasAsync(CatalogPrivileges.Manage);
    }
}
