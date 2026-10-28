using Switchboard.Core.Agents;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.Mcp.Data;

/// <summary>How an OAuth client came to be known (MCP-11).</summary>
public enum McpClientSource
{
    /// <summary>Registered itself through Dynamic Client Registration (RFC 7591).</summary>
    Dynamic = 1,
    /// <summary>Its client_id is an HTTPS URL to a Client ID Metadata Document.</summary>
    MetadataDocument = 2,
    /// <summary>Registered by an admin.</summary>
    Admin = 3,
}

/// <summary>
/// Switchboard's view of an OAuth client. The OpenIddict application row (same client_id) holds the
/// protocol settings; this row holds what people see and the admin block switch (MCP-23).
/// </summary>
public class McpClient : Entity
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public McpClientSource Source { get; set; }
    /// <summary>For metadata-document clients, the URL that is also the client_id.</summary>
    public string? MetadataUrl { get; set; }
    public string? ClientUri { get; set; }
    public List<string> RedirectUris { get; set; } = [];
    public bool IsConfidential { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedById { get; set; }
    public string? RegisteredFromIp { get; set; }
    public DateTimeOffset? MetadataFetchedAt { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public Guid? BlockedById { get; set; }
}

/// <summary>
/// A user's consent for one client (MCP-13). Its id is the OpenIddict authorization id, which every
/// token issued under the consent carries as <c>sb:grant_id</c>.
/// </summary>
public class McpGrant
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
    /// <summary>The resource (MCP endpoint URL) tokens of this grant are bound to.</summary>
    public string Resource { get; set; } = "";
    /// <summary>Named configuration slug, or empty for the default endpoint.</summary>
    public string ConfigurationSlug { get; set; } = "";
    public string RedirectHost { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>Why the grant ended: <c>user</c>, <c>admin</c>, <c>refresh_reuse</c>, <c>client_blocked</c>.</summary>
    public string? RevokedReason { get; set; }
    public Guid? RevokedById { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

public enum McpPendingStatus
{
    Pending = 1,
    /// <summary>Approved; the action is running or ran.</summary>
    Approved = 2,
    Declined = 3,
    Expired = 4,
}

/// <summary>
/// A class X/A/D action waiting for, or recording, the user's confirmation (MCP-50..52, INV-MCP-4).
/// Bound to the hash of its exact arguments and usable once.
/// </summary>
public class McpPendingAction : Entity
{
    public Guid UserId { get; set; }
    public Guid? GrantId { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string? SessionId { get; set; }
    public string ConfigurationSlug { get; set; } = "";
    public string Tool { get; set; } = "";
    public string ToolTitle { get; set; } = "";
    public AgentActionClass ActionClass { get; set; }
    /// <summary>The scopes the grant held when the action was requested.</summary>
    public List<string> Scopes { get; set; } = [];
    public string ArgumentsJson { get; set; } = "{}";
    /// <summary>SHA-256 of the canonical arguments (MCP-52).</summary>
    public string ParameterHash { get; set; } = "";
    /// <summary>What will happen, from <see cref="IAgentTool.DescribeAsync"/> at request time.</summary>
    public string Description { get; set; } = "";
    /// <summary><c>out_of_band</c> or <c>in_client</c>.</summary>
    public string Method { get; set; } = "out_of_band";
    public bool RequiresReauthentication { get; set; }
    public McpPendingStatus Status { get; set; } = McpPendingStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Result code of the executed action (<c>ok</c> or an MCP-39 error code).</summary>
    public string? ResultCode { get; set; }
    public string? ResultText { get; set; }
    public string? ResultJson { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One MCP tool call, for usage counts, last use, budgets and the Connected agents page.</summary>
public class McpCallRecord : Entity
{
    public DateTimeOffset At { get; set; }
    public Guid UserId { get; set; }
    public Guid? GrantId { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string Tool { get; set; } = "";
    public AgentActionClass ActionClass { get; set; }
    /// <summary><c>ok</c> or an MCP-39 error code.</summary>
    public string ResultCode { get; set; } = "ok";
    public int LatencyMs { get; set; }
    public Guid? PendingActionId { get; set; }
    public bool ViaApiKey { get; set; }
}

/// <summary>Stored result of a write called with an <c>idempotency_key</c> (MCP-36).</summary>
public class McpIdempotencyRecord : Entity
{
    public Guid UserId { get; set; }
    /// <summary>Only the connection (client, grant, endpoint) that made the first call gets the stored result back.</summary>
    public string ClientId { get; set; } = "";
    public Guid? GrantId { get; set; }
    public string ConfigurationSlug { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Key { get; set; } = "";
    public string ParameterHash { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// An MCP endpoint configuration (MCP-21/22). The default one has an empty slug and serves <c>/mcp</c>;
/// named ones serve <c>/mcp/{slug}</c>.
/// </summary>
public class McpServerConfiguration : Entity
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    /// <summary>Tools turned off for this configuration (everything else is on, including tools added later).</summary>
    public List<string> DisabledTools { get; set; } = [];
    /// <summary>Agent roles allowed to connect; empty means every agent role.</summary>
    public List<Guid> AllowedRoleIds { get; set; } = [];
    public bool AllowRequesters { get; set; } = true;
    /// <summary>Clients allowed on this configuration; empty means every client that isn't blocked.</summary>
    public List<string> AllowedClientIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Token signing and encryption keys of the built-in authorization server, protected with Data Protection.</summary>
public class McpSigningKey : Entity
{
    /// <summary><c>signing</c> or <c>encryption</c>.</summary>
    public string Use { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
