using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Domain;
using Switchboard.Modules.Catalog.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Admin;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + CatalogPrivileges.Manage)]
public sealed class ItemsModel(ICatalogAdmin admin) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    public IReadOnlyList<CatalogItemSummary> Items { get; private set; } = [];
    public IReadOnlyList<CatalogItemSummary> All { get; private set; } = [];
    public IReadOnlyList<CatalogCategoryView> Categories { get; private set; } = [];

    public async Task OnGetAsync()
    {
        All = await admin.ListItemsAsync(null);
        Categories = await admin.ListCategoriesAsync();
        Items = Enum.TryParse<CatItemState>(State, true, out var s) ? All.Where(i => i.State == s).ToList() : All.Where(i => i.State != CatItemState.Archived).ToList();
    }

    public async Task<IActionResult> OnPostCreateAsync(string? name, Guid? categoryId)
    {
        try
        {
            var item = await admin.CreateItemAsync(name ?? "", categoryId);
            TempData.Toast("Draft created. Add a summary and the questions, then publish.");
            return Redirect($"/admin/catalog/items/{item.Id}");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return RedirectToPage();
        }
    }
}
