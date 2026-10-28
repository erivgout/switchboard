using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Admin;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + CatalogPrivileges.Manage)]
public sealed class CategoriesModel(ICatalogAdmin admin) : PageModel
{
    public IReadOnlyList<CatalogCategoryView> Categories { get; private set; } = [];

    public async Task OnGetAsync() => Categories = await admin.ListCategoriesAsync();

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, string? name, string? description, string? icon)
    {
        try
        {
            await admin.SaveCategoryAsync(id, name ?? "", description, icon ?? "bi-grid");
            TempData.Toast(id is null ? "Category added" : "Category saved");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMoveAsync(Guid id, int direction)
    {
        await admin.MoveCategoryAsync(id, direction);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        await admin.DeleteCategoryAsync(id);
        TempData.Toast("Category deleted");
        return RedirectToPage();
    }
}
