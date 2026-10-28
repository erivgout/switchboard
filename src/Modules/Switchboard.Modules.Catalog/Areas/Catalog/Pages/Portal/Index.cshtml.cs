using Switchboard.Modules.Catalog.Services;
using Switchboard.Modules.Catalog.Web;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Portal;

public sealed class IndexModel(ICatalogService catalog) : CatalogBrowsePageBase(catalog)
{
    protected override bool ForAgent => false;
    protected override string BasePath => "/portal/catalog";
}
