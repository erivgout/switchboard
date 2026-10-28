using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Domain;
using Switchboard.Modules.Catalog.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Admin;

/// <summary>Shared fields (FR-SRM-3): questions defined once and reused across items.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + CatalogPrivileges.Manage)]
public sealed class FieldsModel(ICatalogAdmin admin) : PageModel
{
    public IReadOnlyList<(CatSharedField Field, int UsedBy)> Fields { get; private set; } = [];

    public async Task OnGetAsync() => Fields = await admin.ListSharedFieldsAsync();

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, string? label, CatFieldType fieldType, string? helpText, string? options, string? key)
    {
        try
        {
            await admin.SaveSharedFieldAsync(id, new FieldInput
            {
                Label = label ?? "",
                FieldType = fieldType,
                HelpText = helpText,
                Key = key,
                Options = (options ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            });
            TempData.Toast(id is null ? "Shared field added" : "Shared field saved");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, bool archived)
    {
        await admin.ArchiveSharedFieldAsync(id, archived);
        TempData.Toast(archived ? "Shared field archived" : "Shared field restored");
        return RedirectToPage();
    }
}
