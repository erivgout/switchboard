using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Auth;

namespace Switchboard.Modules.Api.Pages;

public sealed class ApiKeyForm
{
    public string? Name { get; set; }
    /// <summary><c>full</c> or <c>limited</c>.</summary>
    public string Access { get; set; } = "full";
    public List<string> Scopes { get; set; } = [];
    /// <summary>Days as a number, or <c>never</c>.</summary>
    public string Expires { get; set; } = "90";

    public int? ExpiresInDays => int.TryParse(Expires, out var days) ? days : null;
}

public sealed record ApiKeysSectionModel(
    IReadOnlyList<ApiKeySummary> Keys,
    IReadOnlyList<ApiScopeDefinition> Scopes,
    int? MaxLifetimeDays,
    DateTimeOffset Now,
    ApiKeyForm Form,
    CreatedApiKey? Created = null,
    IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public const string ViewPath = "/Areas/Api/Pages/Shared/_ApiKeysSection.cshtml";

    public bool FormOpen => Errors is { Count: > 0 };

    public string? Error(string field) => Errors?.TryGetValue(field, out var e) == true ? string.Join(" ", e) : null;

    public IEnumerable<int> LifetimeChoices =>
        ApiKeyService.LifetimeChoices.Where(d => MaxLifetimeDays is null || d <= MaxLifetimeDays).DefaultIfEmpty(MaxLifetimeDays ?? 90).Distinct();

    public static string Lifetime(int days) => days switch
    {
        365 => "1 year",
        _ when days % 365 == 0 => $"{days / 365} years",
        _ => $"{days} days",
    };

    public static async Task<ApiKeysSectionModel> BuildAsync(
        IApiKeyService keys,
        IPermissionService permissions,
        TimeProvider time,
        ApiKeyForm? form = null,
        CreatedApiKey? created = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        CancellationToken ct = default)
    {
        var isAgent = (await permissions.CurrentAsync(ct))?.IsAgent == true;
        var max = await keys.GetMaxLifetimeDaysAsync(ct);
        var list = (await keys.ListMineAsync(ct))
            .OrderBy(k => k.State(time.GetUtcNow()))
            .ThenByDescending(k => k.CreatedAt)
            .Take(50)
            .ToList();
        form ??= new ApiKeyForm();
        if (max is { } limit && (form.ExpiresInDays is null || form.ExpiresInDays > limit))
        {
            form.Expires = Math.Min(limit, 90).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return new ApiKeysSectionModel(
            list,
            ApiScopes.All.Where(s => isAgent || s.ForRequesters).ToList(),
            max,
            time.GetUtcNow(),
            form,
            created,
            errors);
    }
}

/// <summary><c>profile.sections</c>: the signed-in user's personal API keys (FR-IAM-20).</summary>
/// <remarks>
/// The parameter is typed on purpose: with an <c>object</c> parameter MVC passes the slot's whole
/// <c>new { model = … }</c> argument object instead of the model. The section always shows the
/// signed-in user's own keys, whatever id the page passes.
/// </remarks>
public sealed class ApiKeysViewComponent(IApiKeyService keys, IPermissionService permissions, TimeProvider time) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid model) =>
        View("/Areas/Api/Pages/Shared/_ApiKeysSlot.cshtml", await ApiKeysSectionModel.BuildAsync(keys, permissions, time, ct: HttpContext.RequestAborted));
}
