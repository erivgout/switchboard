using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Data;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages.Import;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.UsersManage)]
[RequestSizeLimit(RequesterImportService.MaxBytes + 64 * 1024)]
public sealed class IndexModel(RequesterImportService imports) : AdminPageModel
{
    [BindProperty]
    public IFormFile? Upload { get; set; }

    public IReadOnlyList<AdmImport> Recent { get; private set; } = [];

    public async Task OnGetAsync() => Recent = await imports.RecentAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (Upload is null || Upload.Length == 0)
        {
            ModelState.AddModelError(nameof(Upload), "Choose a CSV file to upload.");
        }
        else
        {
            AdmImport? import = null;
            await using var stream = Upload.OpenReadStream();
            if (await TryAsync(async () => import = await imports.UploadAsync(Upload.FileName, stream), ""))
            {
                return Redirect($"/admin/import/{import!.Id}");
            }
        }
        Recent = await imports.RecentAsync();
        return Page();
    }
}
