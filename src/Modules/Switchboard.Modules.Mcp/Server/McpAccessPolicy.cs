using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Security;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>The MCP-39 error codes.</summary>
public static class McpErrorCodes
{
    public const string PermissionDenied = "permission_denied";
    public const string NotFound = "not_found";
    public const string ValidationFailed = "validation_failed";
    public const string Conflict = "conflict";
    public const string ConfirmationRequired = "confirmation_required";
    public const string ConfirmationDeclined = "confirmation_declined";
    public const string RateLimited = "rate_limited";
    public const string QuotaExceeded = "quota_exceeded";
    public const string DisabledByAdmin = "disabled_by_admin";
    public const string Cancelled = "cancelled";
    public const string Internal = "internal_error";
    public const string Ok = "ok";
}

/// <summary>Why a caller may not use MCP right now. <see cref="Code"/> is an MCP-39 code.</summary>
public sealed record McpDenial(string Code, string Message, bool InvalidatesToken);

/// <summary>What a caller may do on this call: the result of MCP-16 evaluated now.</summary>
public sealed record McpAccess(
    McpCaller Caller,
    UserPermissions Permissions,
    McpServerConfiguration Configuration,
    IReadOnlySet<string> EffectiveScopes)
{
    public bool IsAgent => Permissions.IsAgent;

    /// <summary>The actor tools run as, narrowed to the effective scopes.</summary>
    public Actor Actor => Caller.ToActor(EffectiveScopes);

    /// <summary>Listed and callable: enabled here, scope granted, and requester-safe for requesters (MCP-17, MCP-22).</summary>
    public bool CanUse(AgentToolDefinition tool) =>
        !Configuration.DisabledTools.Contains(tool.Name)
        && EffectiveScopes.Contains(tool.Scope)
        && (IsAgent || tool.RequesterAllowed);
}

/// <summary>
/// Kill switches and effective permission (MCP-16, MCP-21, MCP-73, INV-MCP-5). Reads current state on
/// every call, so revocations and admin changes apply to the very next request.
/// </summary>
public sealed class McpAccessPolicy(
    SwitchboardDbContext db,
    IPermissionService permissions,
    ISettingsService settings,
    TimeProvider time,
    ILogger<McpAccessPolicy> logger)
{
    /// <summary>Client id given to API-key sessions: <c>api_key:{key id}</c>.</summary>
    public const string ApiKeyClientPrefix = "api_key:";

    public async Task<(McpAccess? Access, McpDenial? Denial)> EvaluateAsync(McpCaller caller, CancellationToken ct)
    {
        if (!await settings.GetAsync(McpSettings.Enabled, true, ct: ct))
        {
            return Deny(McpErrorCodes.DisabledByAdmin, "Your administrator has turned off AI agent access.", invalidates: false);
        }

        var configuration = await db.Set<McpServerConfiguration>().AsNoTracking().FirstOrDefaultAsync(c => c.Slug == caller.Slug, ct);
        if (configuration is null || !configuration.IsEnabled)
        {
            return Deny(McpErrorCodes.DisabledByAdmin, "This AI agent connection has been turned off by your administrator.", invalidates: false);
        }

        var now = time.GetUtcNow();
        IReadOnlySet<string> grantScopes;
        if (caller.GrantId is { } grantId)
        {
            var grant = await db.Set<McpGrant>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId, ct);
            if (grant is null || !grant.IsActive(now) || grant.UserId != caller.UserId || grant.ClientId != caller.ClientId)
            {
                return Deny(McpErrorCodes.PermissionDenied, "This connection was revoked or has expired. Connect the agent again.", invalidates: true);
            }
            if (!string.Equals(grant.ConfigurationSlug, caller.Slug, StringComparison.Ordinal))
            {
                return Deny(McpErrorCodes.PermissionDenied, "This connection was approved for a different endpoint.", invalidates: true);
            }
            grantScopes = grant.Scopes.ToHashSet(StringComparer.Ordinal);

            var client = await db.Set<McpClient>().AsNoTracking().Where(c => c.ClientId == caller.ClientId)
                .Select(c => new { c.BlockedAt }).FirstOrDefaultAsync(ct);
            if (client is null || client.BlockedAt is not null)
            {
                return Deny(McpErrorCodes.DisabledByAdmin, "Your administrator has blocked this AI agent.", invalidates: true);
            }
        }
        else if (caller.ViaApiKey)
        {
            if (!await settings.GetAsync(McpSettings.ApiKeysAllowed, false, ct: ct))
            {
                return Deny(McpErrorCodes.DisabledByAdmin, "Your administrator doesn't allow API keys for AI agents.", invalidates: true);
            }
            // The key itself must still be valid: approved pending actions re-run this check long after the call.
            var keyId = Guid.TryParse(caller.ClientId.StartsWith(ApiKeyClientPrefix, StringComparison.Ordinal) ? caller.ClientId[ApiKeyClientPrefix.Length..] : "", out var k) ? k : Guid.Empty;
            var key = await db.ApiKeys.AsNoTracking().Where(x => x.Id == keyId && x.UserId == caller.UserId)
                .Select(x => new { x.RevokedAt, x.ExpiresAt, x.Scopes }).FirstOrDefaultAsync(ct);
            if (key is null || key.RevokedAt is not null || key.ExpiresAt <= now)
            {
                return Deny(McpErrorCodes.PermissionDenied, "This API key was revoked or has expired.", invalidates: true);
            }
            // MCP-14: an API-key session carries the user's full permissions (no consent narrowing); a key the user limited stays limited.
            grantScopes = (key.Scopes.Count > 0 ? key.Scopes : McpScopes.All.Select(s => s.Key)).ToHashSet(StringComparer.Ordinal);
        }
        else
        {
            return Deny(McpErrorCodes.PermissionDenied, "This connection has no grant.", invalidates: true);
        }

        if (configuration.AllowedClientIds.Count > 0 && !configuration.AllowedClientIds.Contains(caller.ClientId))
        {
            return Deny(McpErrorCodes.DisabledByAdmin, "This AI agent isn't allowed on this endpoint.", invalidates: false);
        }

        var perms = await permissions.ForUserAsync(caller.UserId, ct);
        if (perms is null)
        {
            // Deactivated or deleted users hold nothing (INV-ID-4).
            return Deny(McpErrorCodes.PermissionDenied, "This account is inactive.", invalidates: true);
        }

        if (!RoleAllowed(configuration, perms))
        {
            return Deny(McpErrorCodes.PermissionDenied, "Your role can't use AI agents here. Ask your administrator if you need it.", invalidates: false);
        }

        // MCP-16: the token's scopes, cut down to what the grant holds now and to what this kind of user may hold.
        var effective = McpScopes.Normalize(caller.Scopes.Where(grantScopes.Contains), perms.IsAgent).ToHashSet(StringComparer.Ordinal);

        return (new McpAccess(caller with { Scopes = effective }, perms, configuration, effective), null);

        (McpAccess?, McpDenial?) Deny(string code, string message, bool invalidates)
        {
            logger.LogInformation("MCP access denied for user {User} client {Client}: {Code}", caller.UserId, caller.ClientId, code);
            return (null, new McpDenial(code, message, invalidates));
        }
    }

    internal static bool RoleAllowed(McpServerConfiguration configuration, UserPermissions perms)
    {
        if (perms.IsOrgAdmin)
        {
            return true;
        }
        if (!perms.IsAgent)
        {
            return configuration.AllowRequesters;
        }
        return configuration.AllowedRoleIds.Count == 0
            || perms.Workspaces.Values.Any(w => configuration.AllowedRoleIds.Contains(w.RoleId));
    }
}

/// <summary>Every <see cref="IAgentTool"/> registered by any module, by name.</summary>
public sealed class McpToolCatalog(IServiceProvider services, ILogger<McpToolCatalog> logger)
{
    private Dictionary<string, IAgentTool>? _byName;

    public IReadOnlyDictionary<string, IAgentTool> ByName => _byName ??= Build();

    public IEnumerable<IAgentTool> All => ByName.Values.OrderBy(t => t.Definition.Name, StringComparer.Ordinal);

    public IAgentTool? Find(string name) => ByName.GetValueOrDefault(name);

    private Dictionary<string, IAgentTool> Build()
    {
        // Resolved on first use, not in the constructor: tools such as whoami depend on the catalog.
        var map = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in services.GetServices<IAgentTool>())
        {
            var name = tool.Definition.Name;
            if (!IsValidName(name))
            {
                logger.LogWarning("Ignoring agent tool {Type}: '{Name}' isn't a snake_case name", tool.GetType().FullName, name);
                continue;
            }
            if (!map.TryAdd(name, tool))
            {
                logger.LogWarning("Two agent tools are named {Name}; keeping {Kept}, ignoring {Ignored}", name, map[name].GetType().FullName, tool.GetType().FullName);
            }
        }
        return map;
    }

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') && char.IsAsciiLetterLower(name[0]);
}
