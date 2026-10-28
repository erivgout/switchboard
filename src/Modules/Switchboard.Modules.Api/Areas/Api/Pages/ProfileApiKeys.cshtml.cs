using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Auth;
using Switchboard.Modules.Api.Pages;
using Switchboard.UI;

namespace Switchboard.Modules.Api.Areas.Api.Pages;

/// <summary>
/// Handlers behind the API keys section on /profile. htmx posts get the section back in place; without
/// JavaScript the same handlers render this page. A new key is shown in that response only, never stored.
/// </summary>
public sealed class ProfileApiKeysModel(IApiKeyService keys, IPermissionService permissions, TimeProvider time) : PageModel
{
    [BindProperty]
    public ApiKeyForm Form { get; set; } = new();

    public ApiKeysSectionModel Section { get; private set; } = default!;

    public bool IsAgent { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        CreatedApiKey? created = null;
        IReadOnlyDictionary<string, string[]>? errors = null;
        try
        {
            var scopes = Form.Access == "limited" ? Form.Scopes : [];
            if (Form.Access == "limited" && scopes.Count == 0)
            {
                throw new ValidationException("scopes", "Choose at least one thing the key may do, or give it full access.");
            }
            created = await keys.CreateAsync(new CreateApiKeyRequest(Form.Name ?? "", scopes, Form.ExpiresInDays), HttpContext.RequestAborted);
            Form = new ApiKeyForm();
        }
        catch (ValidationException ex)
        {
            errors = ex.Errors;
        }
        await LoadAsync(created, errors);
        if (created is not null)
        {
            Response.Headers.CacheControl = "no-store";
        }
        return Request.IsHtmx() ? Partial("/Areas/Api/Pages/Shared/_ApiKeysSection.cshtml", Section) : Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id)
    {
        await keys.RevokeAsync(id, HttpContext.RequestAborted);
        if (!Request.IsHtmx())
        {
            TempData.Toast("Key revoked.");
            return Redirect("/profile#apiKeys");
        }
        Response.HxToast("Key revoked.");
        await LoadAsync();
        return Partial("/Areas/Api/Pages/Shared/_ApiKeysSection.cshtml", Section);
    }

    private async Task LoadAsync(CreatedApiKey? created = null, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        IsAgent = (await permissions.CurrentAsync(HttpContext.RequestAborted))?.IsAgent == true;
        Section = await ApiKeysSectionModel.BuildAsync(keys, permissions, time, created is null ? Form : null, created, errors, HttpContext.RequestAborted);
    }
}
