using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Security;
using Switchboard.Modules.Mcp.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Switchboard.Modules.Mcp.OAuth;

/// <summary>Protected Resource Metadata (RFC 9728), Dynamic Client Registration (RFC 7591) and the token endpoint.</summary>
internal static class OAuthEndpoints
{
    public const string RegisterRateLimit = "mcp-register";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(McpPaths.ProtectedResourceMetadata, (Func<HttpContext, Task<IResult>>)(http => ProtectedResourceAsync(http, "")))
            .AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(McpPaths.ProtectedResourceMetadata + McpPaths.Mcp, (Func<HttpContext, Task<IResult>>)(http => ProtectedResourceAsync(http, "")))
            .AllowAnonymous().ExcludeFromDescription();
        endpoints.MapGet(McpPaths.ProtectedResourceMetadata + McpPaths.Mcp + "/{slug}", (HttpContext http, string slug) => ProtectedResourceAsync(http, slug))
            .AllowAnonymous().ExcludeFromDescription();
        endpoints.MapPost(McpPaths.Register, (Func<HttpContext, Task<IResult>>)RegisterAsync)
            .AllowAnonymous().DisableAntiforgery().RequireRateLimiting(RegisterRateLimit).ExcludeFromDescription();
        endpoints.MapPost(McpPaths.Token, (Func<HttpContext, Task<IResult>>)ExchangeAsync)
            .AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
    }

    public static string Issuer(string baseUrl) => new Uri(baseUrl.TrimEnd('/') + "/").AbsoluteUri;

    private static async Task<IResult> ProtectedResourceAsync(HttpContext http, string slug)
    {
        slug = slug.ToLowerInvariant();
        var services = http.RequestServices;
        var db = services.GetRequiredService<SwitchboardDbContext>();
        if (!await db.Set<McpServerConfiguration>().AnyAsync(c => c.Slug == slug, http.RequestAborted))
        {
            return Results.NotFound();
        }
        var baseUrl = services.GetRequiredService<IOptions<SwitchboardOptions>>().Value.BaseUrl;
        var toolScopes = services.GetRequiredService<McpToolCatalog>().All.Select(t => t.Definition.Scope).ToHashSet(StringComparer.Ordinal);
        var scopes = McpScopes.All.Where(s => s.Key == McpScopes.Profile || toolScopes.Contains(s.Key)).Select(s => s.Key).ToArray();
        var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => o.Name).FirstOrDefaultAsync(http.RequestAborted);
        return Results.Json(new Dictionary<string, object?>
        {
            ["resource"] = McpPaths.Resource(baseUrl, slug),
            ["authorization_servers"] = new[] { Issuer(baseUrl) },
            ["scopes_supported"] = scopes,
            ["bearer_methods_supported"] = new[] { "header" },
            ["resource_name"] = org is null ? "Switchboard" : $"Switchboard ({org})",
        });
    }

    private static async Task<IResult> RegisterAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var settings = services.GetRequiredService<ISettingsService>();
        if (!await settings.GetAsync(McpSettings.Enabled, true, ct: http.RequestAborted))
        {
            return RegistrationError(StatusCodes.Status403Forbidden, "access_denied", "AI agent access is turned off on this Switchboard.");
        }
        if (!await settings.GetAsync(McpSettings.OpenRegistration, true, ct: http.RequestAborted))
        {
            return RegistrationError(StatusCodes.Status403Forbidden, "access_denied", "This Switchboard only accepts agents your administrator has registered.");
        }

        JsonObject body;
        try
        {
            if (http.Request.ContentLength > 16 * 1024)
            {
                return RegistrationError(StatusCodes.Status413PayloadTooLarge, "invalid_client_metadata", "The registration request is too large.");
            }
            body = await JsonSerializer.DeserializeAsync<JsonObject>(http.Request.Body, cancellationToken: http.RequestAborted)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return RegistrationError(StatusCodes.Status400BadRequest, "invalid_client_metadata", "Send the client metadata as a JSON object.");
        }

        static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static List<string>? List(JsonObject o, string name) => o[name] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList() : null;

        var authMethod = Text(body, "token_endpoint_auth_method") ?? "none";
        if (authMethod is not ("none" or "client_secret_basic" or "client_secret_post"))
        {
            return RegistrationError(StatusCodes.Status400BadRequest, "invalid_client_metadata", "Use token_endpoint_auth_method none, client_secret_basic or client_secret_post.");
        }
        var grantTypes = List(body, "grant_types") ?? [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken];
        if (grantTypes.Any(g => g is not (GrantTypes.AuthorizationCode or GrantTypes.RefreshToken)))
        {
            return RegistrationError(StatusCodes.Status400BadRequest, "invalid_client_metadata", "Only the authorization_code and refresh_token grant types are supported.");
        }
        var responseTypes = List(body, "response_types") ?? [ResponseTypes.Code];
        if (responseTypes.Any(r => r != ResponseTypes.Code))
        {
            return RegistrationError(StatusCodes.Status400BadRequest, "invalid_client_metadata", "Only the code response type is supported.");
        }

        var registry = services.GetRequiredService<McpClientRegistry>();
        var actors = services.GetRequiredService<IActorAccessor>();
        RegisteredClient registered;
        try
        {
            using var _ = actors.BeginScope(Actor.System("Dynamic client registration", Core.Domain.Channel.Mcp));
            var name = Text(body, "client_name") is { Length: > 0 } n ? n.Trim() : "Unnamed agent";
            registered = await registry.RegisterAsync(
                new ClientRegistration(name.Length > 200 ? name[..200] : name, List(body, "redirect_uris") ?? [], Text(body, "client_uri"), authMethod != "none", McpClientSource.Dynamic),
                http.Connection.RemoteIpAddress?.ToString(),
                http.RequestAborted);
        }
        catch (ValidationException ex)
        {
            return RegistrationError(StatusCodes.Status400BadRequest, ex.Errors.ContainsKey("redirect_uris") ? "invalid_redirect_uri" : "invalid_client_metadata", ex.Message);
        }

        var client = registered.Client;
        var response = new Dictionary<string, object?>
        {
            ["client_id"] = client.ClientId,
            ["client_id_issued_at"] = client.CreatedAt.ToUnixTimeSeconds(),
            ["client_name"] = client.Name,
            ["redirect_uris"] = client.RedirectUris,
            ["grant_types"] = new[] { GrantTypes.AuthorizationCode, GrantTypes.RefreshToken },
            ["response_types"] = new[] { ResponseTypes.Code },
            ["token_endpoint_auth_method"] = authMethod,
        };
        if (client.ClientUri is not null)
        {
            response["client_uri"] = client.ClientUri;
        }
        if (registered.ClientSecret is not null)
        {
            response["client_secret"] = registered.ClientSecret;
            response["client_secret_expires_at"] = 0;
        }
        return Results.Json(response, statusCode: StatusCodes.Status201Created);
    }

    private static IResult RegistrationError(int status, string error, string description) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, statusCode: status);

    /// <summary>
    /// Token endpoint (passthrough): re-checks the grant on every code exchange and refresh, so revoked or
    /// expired grants, blocked clients and deactivated users get no new tokens (INV-MCP-5).
    /// </summary>
    private static async Task<IResult> ExchangeAsync(HttpContext http)
    {
        var request = http.GetOpenIddictServerRequest();
        if (request is null || !(request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType()))
        {
            return Deny(Errors.UnsupportedGrantType, "Only authorization_code and refresh_token are supported.");
        }

        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (result.Principal is not { } principal
            || !Guid.TryParse(principal.GetAuthorizationId(), out var grantId)
            || !Guid.TryParse(principal.GetClaim(Claims.Subject), out var userId))
        {
            return Deny(Errors.InvalidGrant, "The code or refresh token is no longer valid.");
        }

        var services = http.RequestServices;
        var db = services.GetRequiredService<SwitchboardDbContext>();
        var settings = services.GetRequiredService<ISettingsService>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var grant = await db.Set<McpGrant>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId, http.RequestAborted);
        var userActive = await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, http.RequestAborted);
        var blocked = grant is not null && await db.Set<McpClient>().AnyAsync(c => c.ClientId == grant.ClientId && c.BlockedAt != null, http.RequestAborted);
        if (grant is null || !grant.IsActive(now) || grant.UserId != userId || !userActive || blocked
            || !await settings.GetAsync(McpSettings.Enabled, true, ct: http.RequestAborted))
        {
            return Deny(Errors.InvalidGrant, "This connection was revoked or has expired. Connect the agent again.");
        }

        // RFC 8707: a token request may narrow the resource, never widen it.
        var granted = principal.GetResources().ToList();
        var requested = request.GetResources().Select(r => McpPaths.NormalizeResource(services.GetRequiredService<IOptions<SwitchboardOptions>>().Value.BaseUrl, r)).ToList();
        if (requested.Any(r => r is null || !granted.Contains(r, StringComparer.Ordinal)))
        {
            return Deny(Errors.InvalidTarget, "That resource wasn't part of this grant.");
        }

        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, userId.ToString());
        identity.SetScopes(principal.GetScopes().Where(s => s == Scopes.OfflineAccess || grant.Scopes.Contains(s)));
        identity.SetResources(requested.Count > 0 ? requested.OfType<string>() : granted);
        identity.SetAuthorizationId(grantId.ToString());
        identity.SetDestinations(_ => [Destinations.AccessToken]);
        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Deny(string error, string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
