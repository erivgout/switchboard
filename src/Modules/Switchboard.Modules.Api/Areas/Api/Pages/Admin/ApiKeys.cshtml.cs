using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Auth;
using Switchboard.UI;

namespace Switchboard.Modules.Api.Areas.Api.Pages.Admin;

/// <summary>Every personal API key on the instance, with revoke (FR-IAM-20, SR-AUD-1).</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.SettingsManage)]
public sealed class ApiKeysModel(IApiKeyService keys, TimeProvider time) : PageModel
{
    public const int PageSize = 50;

    public static readonly (string Key, string Title)[] States = [("active", "Active"), ("expired", "Expired"), ("revoked", "Revoked"), ("all", "All")];
    public static readonly int[] LifetimeChoices = [30, 90, 180, 365, 730];

    [BindProperty(SupportsGet = true)]
    public string State { get; set; } = "active";

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    [BindProperty]
    public string? MaxLifetime { get; set; }

    public IReadOnlyList<ApiKeySummary> Keys { get; private set; } = [];
    public int Total { get; private set; }
    public int? MaxLifetimeDays { get; private set; }
    public DateTimeOffset Now { get; private set; }
    public string? LifetimeError { get; private set; }

    public int PageCount => (int)Math.Ceiling(Total / (double)PageSize);

    public string Link(string? state = null, int? page = null)
    {
        var q = string.IsNullOrWhiteSpace(Q) ? "" : "&q=" + Uri.EscapeDataString(Q);
        return $"?state={state ?? State}{q}{(page is > 1 ? $"&page={page}" : "")}";
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostRevokeAsync(Guid id)
    {
        await keys.RevokeAsync(id, HttpContext.RequestAborted);
        TempData.Toast("Key revoked.");
        return Redirect("/admin/api-keys" + Link(page: PageNumber));
    }

    public async Task<IActionResult> OnPostLifetimeAsync()
    {
        int? days = MaxLifetime is null or "" or "none" ? null : int.TryParse(MaxLifetime, out var d) ? d : -1;
        try
        {
            await keys.SetMaxLifetimeDaysAsync(days, HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            LifetimeError = ex.Message;
            await LoadAsync();
            return Page();
        }
        TempData.Toast("Key lifetime saved.");
        return Redirect("/admin/api-keys" + Link());
    }

    private async Task LoadAsync()
    {
        Now = time.GetUtcNow();
        PageNumber = Math.Clamp(PageNumber, 1, 10_000);
        if (!States.Any(s => s.Key == State))
        {
            State = "active";
        }
        ApiKeyState? state = State switch
        {
            "expired" => ApiKeyState.Expired,
            "revoked" => ApiKeyState.Revoked,
            "all" => null,
            _ => ApiKeyState.Active,
        };
        var search = Q?.Trim() is { Length: > 0 and <= 100 } s ? s : null;
        (Keys, Total) = await keys.ListAllAsync(state, search, PageNumber, PageSize, HttpContext.RequestAborted);
        MaxLifetimeDays = await keys.GetMaxLifetimeDaysAsync(HttpContext.RequestAborted);
    }
}
