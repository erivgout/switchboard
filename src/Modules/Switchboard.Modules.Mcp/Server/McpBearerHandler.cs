using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Security;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>
/// Authenticates MCP requests: OAuth access tokens issued by the built-in server for exactly this endpoint
/// (audience check, MCP-15), or personal API keys when the admin allows them (MCP-14). Session cookies are
/// never accepted. A 401 points the client at the Protected Resource Metadata (MCP-10).
/// </summary>
public sealed class McpBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SwitchboardDbContext db,
    ISettingsService settings,
    IOptions<SwitchboardOptions> switchboard,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SwitchboardMcp";
    private const string ApiKeyMarker = "sb_";
    private const string FailureItem = "sb.mcp.auth_failure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }
        var token = header[7..].Trim();
        var slug = McpPaths.SlugOf(Request.Path) ?? "";

        var caller = token.StartsWith(ApiKeyMarker, StringComparison.Ordinal)
            ? await FromApiKeyAsync(token, slug)
            : await FromAccessTokenAsync(slug);
        if (caller is null)
        {
            return Fail("The access token is invalid, expired, revoked, or not for this endpoint.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(caller.ToPrincipal(Scheme.Name), Scheme.Name));
    }

    private async Task<McpCaller?> FromAccessTokenAsync(string slug)
    {
        var result = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is not { } principal)
        {
            return null;
        }

        // MCP-15: only tokens issued for this exact endpoint are accepted; no passthrough.
        var resource = McpPaths.Resource(switchboard.Value.BaseUrl, slug);
        if (!principal.GetAudiences().Contains(resource, StringComparer.Ordinal))
        {
            Logger.LogInformation("Rejected an MCP token for another audience at {Path}", Request.Path);
            return null;
        }

        if (!Guid.TryParse(principal.GetClaim(Claims.Subject), out var userId)
            || !Guid.TryParse(principal.GetAuthorizationId(), out var grantId)
            || principal.GetClaim(Claims.ClientId) is not { Length: > 0 } clientId)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var grant = await db.Set<McpGrant>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId, Context.RequestAborted);
        if (grant is null || !grant.IsActive(now) || grant.UserId != userId || grant.ClientId != clientId || grant.ConfigurationSlug != slug)
        {
            return null;
        }
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.DisplayName, u.IsActive }).FirstOrDefaultAsync(Context.RequestAborted);
        var blocked = await db.Set<McpClient>().AsNoTracking().Where(c => c.ClientId == clientId).Select(c => c.BlockedAt != null).FirstOrDefaultAsync(Context.RequestAborted);
        if (user is not { IsActive: true } || blocked)
        {
            return null;
        }

        var scopes = principal.GetScopes().Where(grant.Scopes.Contains).ToHashSet(StringComparer.Ordinal);
        if (scopes.Count == 0)
        {
            return null;
        }
        return new McpCaller(userId, user.DisplayName, clientId, grant.ClientName, grantId, scopes, slug, ViaApiKey: false);
    }

    private async Task<McpCaller?> FromApiKeyAsync(string key, string slug)
    {
        if (!await settings.GetAsync(McpSettings.ApiKeysAllowed, false, ct: Context.RequestAborted) || key.Length > 200)
        {
            return null;
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var now = time.GetUtcNow();
        var match = await db.ApiKeys.AsNoTracking()
            .Where(k => k.KeyHash == hash && k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now))
            .Join(db.Users, k => k.UserId, u => u.Id, (k, u) => new { k.Id, k.UserId, k.Prefix, k.Scopes, u.DisplayName, u.IsActive, u.IsForgotten })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (match is null || !match.IsActive || match.IsForgotten)
        {
            return null;
        }
        // MCP-14: the user's full permissions (no consent narrowing), flagged as an API-key session in every audit
        // entry. A key the user limited to some scopes stays limited.
        var scopes = match.Scopes is { Count: > 0 }
            ? match.Scopes.Where(McpScopes.IsKnown).ToHashSet(StringComparer.Ordinal)
            : McpScopes.All.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        return scopes.Count == 0 ? null : new McpCaller(match.UserId, match.DisplayName, $"{McpAccessPolicy.ApiKeyClientPrefix}{match.Id:N}", $"API key {match.Prefix}", null, scopes, slug, ViaApiKey: true);
    }

    private AuthenticateResult Fail(string message)
    {
        Context.Items[FailureItem] = message;
        return AuthenticateResult.Fail(message);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var slug = McpPaths.SlugOf(Request.Path) ?? "";
        var baseUrl = switchboard.Value.BaseUrl.TrimEnd('/');
        var metadata = $"{baseUrl}{McpPaths.ProtectedResourceMetadata}{McpPaths.Mcp}{(slug.Length == 0 ? "" : "/" + slug)}";
        var value = new StringBuilder($"Bearer resource_metadata=\"{metadata}\"");
        if (Context.Items.ContainsKey(FailureItem))
        {
            value.Append(", error=\"invalid_token\", error_description=\"The access token is invalid, expired or revoked.\"");
        }
        // A bare 401 for API clients, not the HTML status page.
        if (Context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages)
        {
            pages.Enabled = false;
        }
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = value.ToString();
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (Context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages)
        {
            pages.Enabled = false;
        }
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
