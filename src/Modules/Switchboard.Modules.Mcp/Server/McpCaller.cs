using System.Security.Claims;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>
/// Exactly one (user, client, grant) behind an MCP request (INV-MCP-1), as established by the
/// <see cref="McpBearerHandler"/> for the current HTTP request.
/// </summary>
public sealed record McpCaller(
    Guid UserId,
    string UserName,
    string ClientId,
    string ClientName,
    Guid? GrantId,
    IReadOnlySet<string> Scopes,
    string Slug,
    bool ViaApiKey)
{
    public const string Issuer = "Switchboard.Mcp";
    public const string SlugClaim = "sb:mcp_slug";
    public const string AuthClaim = "sb:mcp_auth";

    /// <summary>The actor tools run as: the user, through the MCP channel, narrowed to the grant's scopes.</summary>
    public Actor ToActor(IReadOnlySet<string>? scopes = null) =>
        new(ActorKind.McpClient, UserId, UserName, Channel.Mcp, ClientId, ClientName, GrantId, scopes ?? Scopes);

    public ClaimsPrincipal ToPrincipal(string scheme)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, UserId.ToString(), ClaimValueTypes.String, Issuer),
            new(SwitchboardClaims.DisplayName, UserName, ClaimValueTypes.String, Issuer),
            new(SwitchboardClaims.Channel, nameof(Channel.Mcp), ClaimValueTypes.String, Issuer),
            new(SwitchboardClaims.ClientId, ClientId, ClaimValueTypes.String, Issuer),
            new(SwitchboardClaims.ClientName, ClientName, ClaimValueTypes.String, Issuer),
            new(SwitchboardClaims.Scope, string.Join(' ', Scopes), ClaimValueTypes.String, Issuer),
            new(SlugClaim, Slug, ClaimValueTypes.String, Issuer),
            new(AuthClaim, ViaApiKey ? "api_key" : "oauth", ClaimValueTypes.String, Issuer),
        };
        if (GrantId is { } grant)
        {
            claims.Add(new Claim(SwitchboardClaims.GrantId, grant.ToString(), ClaimValueTypes.String, Issuer));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, SwitchboardClaims.DisplayName, ClaimTypes.Role));
    }

    /// <summary>Reads a principal built by <see cref="ToPrincipal"/>; null for anything else (cookies, API tokens).</summary>
    public static McpCaller? FromPrincipal(ClaimsPrincipal? principal)
    {
        var id = principal?.FindFirst(ClaimTypes.NameIdentifier);
        if (principal is null || id is null || id.Issuer != Issuer || !Guid.TryParse(id.Value, out var userId)
            || principal.FindFirstValue(SwitchboardClaims.Channel) != nameof(Channel.Mcp))
        {
            return null;
        }
        Guid? grant = Guid.TryParse(principal.FindFirstValue(SwitchboardClaims.GrantId), out var g) ? g : null;
        var scopes = (principal.FindFirstValue(SwitchboardClaims.Scope) ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        return new McpCaller(
            userId,
            principal.FindFirstValue(SwitchboardClaims.DisplayName) ?? "User",
            principal.FindFirstValue(SwitchboardClaims.ClientId) ?? "",
            principal.FindFirstValue(SwitchboardClaims.ClientName) ?? "",
            grant,
            scopes,
            principal.FindFirstValue(SlugClaim) ?? "",
            principal.FindFirstValue(AuthClaim) == "api_key");
    }
}
