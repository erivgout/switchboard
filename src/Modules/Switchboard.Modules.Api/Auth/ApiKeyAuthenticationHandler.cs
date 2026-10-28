using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Switchboard.Core.Api;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Http;

namespace Switchboard.Modules.Api.Auth;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// Scheme that validates bearer tokens which are not personal API keys (OAuth access tokens).
    /// Used only when a module registers a scheme with this name.
    /// </summary>
    public string OAuthScheme { get; set; } = ApiAuthentication.OAuthScheme;
}

public static class ApiAuthentication
{
    /// <summary>Name an OAuth module gives its token scheme so REST calls with OAuth bearer tokens are accepted.</summary>
    public const string OAuthScheme = "SwitchboardOAuth";

    internal const string FailureItem = "sb.api.auth_failure";
}

/// <summary>
/// The <see cref="SwitchboardApi.Scheme"/> handler: personal API keys sent as <c>Authorization: Bearer sb_…</c>
/// or as the Basic-auth user name. Rejects revoked and expired keys and deactivated users, and never redirects.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SwitchboardDbContext db,
    IAuthenticationSchemeProvider schemes,
    TimeProvider time) : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return AuthenticateResult.NoResult();
        }

        var credential = ReadCredential(header);
        if (credential is null)
        {
            return Fail("Use Authorization: Bearer <api key>.");
        }

        if (!credential.StartsWith(ApiKeyFormat.Marker, StringComparison.Ordinal))
        {
            // Not a personal key: an OAuth access token, if a module validates those.
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && await schemes.GetSchemeAsync(Options.OAuthScheme) is not null)
            {
                return await Context.AuthenticateAsync(Options.OAuthScheme);
            }
            return Fail("That isn't a Switchboard API key.");
        }

        if (!ApiKeyFormat.IsWellFormed(credential))
        {
            return Fail("That isn't a valid API key.");
        }

        var hash = ApiKeyFormat.Hash(credential);
        var now = time.GetUtcNow();
        var match = await db.ApiKeys.AsNoTracking()
            .Where(k => k.KeyHash == hash)
            .Join(db.Users, k => k.UserId, u => u.Id, (k, u) => new
            {
                k.Id,
                k.UserId,
                k.Scopes,
                k.ExpiresAt,
                k.RevokedAt,
                k.LastUsedAt,
                u.DisplayName,
                u.IsActive,
                u.IsForgotten,
            })
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (match is null || match.RevokedAt is not null || match.ExpiresAt <= now || !match.IsActive || match.IsForgotten)
        {
            Logger.LogInformation("API key {Prefix} rejected: {Reason}", ApiKeyFormat.PrefixOf(credential),
                match is null ? "unknown" : match.RevokedAt is not null ? "revoked" : match.ExpiresAt <= now ? "expired" : "inactive user");
            return Fail("The API key is invalid, expired or revoked.");
        }

        if (match.LastUsedAt is null || match.LastUsedAt < now - LastUsedResolution)
        {
            var cutoff = now - LastUsedResolution;
            await db.ApiKeys
                .Where(k => k.Id == match.Id && (k.LastUsedAt == null || k.LastUsedAt < cutoff))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, match.UserId.ToString()),
            new(SwitchboardClaims.DisplayName, match.DisplayName),
            new(SwitchboardClaims.Channel, nameof(Channel.Api)),
        };
        if (match.Scopes is { Count: > 0 })
        {
            claims.Add(new Claim(SwitchboardClaims.Scope, string.Join(' ', match.Scopes)));
        }
        var identity = new ClaimsIdentity(claims, Scheme.Name, SwitchboardClaims.DisplayName, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private AuthenticateResult Fail(string message)
    {
        Context.Items[ApiAuthentication.FailureItem] = message;
        return AuthenticateResult.Fail(message);
    }

    /// <summary>Bearer token, or the user name of Basic credentials (Freshservice style: <c>key:X</c>).</summary>
    internal static string? ReadCredential(string header)
    {
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header[7..].Trim();
            return token.Length is > 0 and <= 4096 ? token : null;
        }
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = header[6..].Trim();
            if (encoded.Length is 0 or > 4096)
            {
                return null;
            }
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var colon = decoded.IndexOf(':');
                var user = colon >= 0 ? decoded[..colon] : decoded;
                return user.Length > 0 ? user : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Bearer only: advertising Basic would make browsers prompt and then replay credentials cross-site.
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer realm=\"Switchboard API\"";
        var detail = Context.Items[ApiAuthentication.FailureItem] as string
            ?? "Send an API key: Authorization: Bearer sb_…. Create one on your profile page.";
        await ApiProblems.WriteAsync(Context, StatusCodes.Status401Unauthorized, ApiProblems.TitleFor(401), detail);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblems.WriteAsync(Context, StatusCodes.Status403Forbidden, ApiProblems.TitleFor(403), new ForbiddenException().Message);
}
