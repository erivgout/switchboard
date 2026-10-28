using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Switchboard.Core.Api;

/// <summary>Shared REST API conventions (FR-INT-1). The API module implements the schemes and the rate limiter.</summary>
public static class SwitchboardApi
{
    /// <summary>Authentication scheme for personal API keys and OAuth bearer tokens, registered by the API module.</summary>
    public const string Scheme = "SwitchboardApi";

    /// <summary>
    /// Scheme name reserved for an OAuth bearer-token handler. The API key handler forwards non-<c>sb_</c> bearer
    /// tokens here when it is registered. It must reject tokens whose audience is not the REST API (MCP-15).
    /// </summary>
    public const string OAuthScheme = "SwitchboardOAuth";

    /// <summary>Rate-limiter policy applied to every API route (per key and per instance).</summary>
    public const string RateLimitPolicy = "api";

    public const string Prefix = "/api/v1";

    /// <summary>
    /// Creates a route group under <c>/api/v1/{segment}</c> that authenticates with API keys or bearer tokens,
    /// applies the API rate limit, and is described in the OpenAPI document.
    /// </summary>
    /// <remarks>
    /// Browser session cookies are deliberately not accepted here: API routes skip antiforgery, so cookie
    /// auth would allow cross-site request forgery. Pages use Razor Page handlers instead.
    /// </remarks>
    /// <example><c>var kb = endpoints.MapSwitchboardApi("kb", "Knowledge"); kb.MapGet("/articles", ...);</c></example>
    public static RouteGroupBuilder MapSwitchboardApi(this IEndpointRouteBuilder endpoints, string segment, string tag) =>
        endpoints.MapGroup($"{Prefix}/{segment.Trim('/')}")
            .WithTags(tag)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = Scheme })
            .RequireRateLimiting(RateLimitPolicy)
            .DisableAntiforgery();
}
