using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Data;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Import;

/// <summary>Match columns, preview the result, run the import, and download the problems (FR-ADM-7).</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersManage)]
public sealed class DetailModel(RequesterImportService imports) : AdminPageModel
{
    [BindProperty]
    public Dictionary<string, string?> Mapping { get; set; } = [];

    [BindProperty]
    public bool UpdateExisting { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Preview { get; set; }

    public AdmImport Import { get; private set; } = default!;
    public string[] Headers { get; private set; } = [];
    public IReadOnlyList<ImportField> Fields { get; private set; } = [];
    public ImportPreview? Result { get; private set; }

    public async Task OnGetAsync(Guid id)
    {
        await LoadAsync(id, keepInput: false);
        if (Preview && Import.Status == ImportStatus.Uploaded)
        {
            Result = await imports.PreviewAsync(id);
        }
    }

    public async Task<IActionResult> OnPostPreviewAsync(Guid id)
    {
        if (await TryAsync(() => imports.SaveMappingAsync(id, Mapping, UpdateExisting), ""))
        {
            return Redirect($"/admin/import/{id}?preview=true");
        }
        await LoadAsync(id, keepInput: true);
        return Page();
    }

    public async Task<IActionResult> OnPostRunAsync(Guid id)
    {
        ImportPreview? result = null;
        if (await TryAsync(async () => result = await imports.RunAsync(id), ""))
        {
            TempData.Toast($"{result!.Create + result.Update} {(result.Create + result.Update == 1 ? "person" : "people")} imported");
            return Redirect($"/admin/import/{id}");
        }
        await LoadAsync(id, keepInput: false);
        return Page();
    }

    public async Task<IActionResult> OnGetErrorsAsync(Guid id)
    {
        var csv = await imports.ErrorReportAsync(id);
        return File(csv, "text/csv", "import-problems.csv");
    }

    private async Task LoadAsync(Guid id, bool keepInput)
    {
        (Import, Headers, var saved) = await imports.GetAsync(id);
        Fields = await imports.FieldsAsync();
        if (!keepInput)
        {
            Mapping = saved.ToDictionary(m => m.Key, m => (string?)m.Value);
            UpdateExisting = Import.UpdateExisting;
        }
    }
}
