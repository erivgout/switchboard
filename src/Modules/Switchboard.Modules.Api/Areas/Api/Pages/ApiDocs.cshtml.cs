using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Switchboard.Core.Api;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Auth;
using Switchboard.Modules.Api.Http;

namespace Switchboard.Modules.Api.Areas.Api.Pages;

public sealed record ApiDocsEndpoint(string Method, string Path, string? Summary, string? Description, string? Scope, bool AgentsOnly);

/// <summary>
/// A small, local API reference built from the live endpoint table, so every module's /api/v1 routes are
/// listed. No third-party docs UI: the CSP allows only same-origin scripts and instances may be offline.
/// </summary>
public sealed class ApiDocsModel(EndpointDataSource endpoints, IPermissionService permissions) : PageModel
{
    public IReadOnlyList<IGrouping<string, ApiDocsEndpoint>> Groups { get; private set; } = [];
    public bool IsAgent { get; private set; }

    public async Task OnGetAsync()
    {
        IsAgent = (await permissions.CurrentAsync(HttpContext.RequestAborted))?.IsAgent == true;
        var prefix = SwitchboardApi.Prefix + "/";
        var list = new List<(string Tag, ApiDocsEndpoint Endpoint)>();
        foreach (var e in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            var path = "/" + (e.RoutePattern.RawText ?? "").TrimStart('/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || e.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription == true)
            {
                continue;
            }
            var tag = e.Metadata.GetMetadata<ITagsMetadata>()?.Tags.FirstOrDefault() ?? "Other";
            foreach (var method in e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                list.Add((tag, new ApiDocsEndpoint(
                    method,
                    Readable(path),
                    e.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary,
                    e.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description,
                    e.Metadata.GetMetadata<ApiScopeMetadata>()?.Scope,
                    e.Metadata.GetMetadata<ApiAgentOnlyMetadata>() is not null)));
            }
        }
        Groups = list
            .OrderBy(x => x.Tag == "Tickets" ? 0 : 1).ThenBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Endpoint.Path, StringComparer.OrdinalIgnoreCase).ThenBy(x => MethodOrder(x.Endpoint.Method))
            .GroupBy(x => x.Tag, x => x.Endpoint)
            .ToList();
    }

    public static IReadOnlyList<ApiScopeDefinition> Scopes => ApiScopes.All;

    /// <summary>Drops route constraints: <c>{id:guid}</c> reads as <c>{id}</c>.</summary>
    private static string Readable(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, @"\{([^:}]+):[^}]+\}", "{$1}").TrimEnd('/');

    private static int MethodOrder(string method) => method switch
    {
        "GET" => 0,
        "POST" => 1,
        "PUT" => 2,
        "PATCH" => 3,
        "DELETE" => 4,
        _ => 5,
    };
}
