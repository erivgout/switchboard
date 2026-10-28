namespace Switchboard.Modules.Mcp;

/// <summary>Instance settings of the MCP server (<see cref="Switchboard.Core.Services.ISettingsService"/> keys).</summary>
public static class McpSettings
{
    /// <summary>bool, default true (MCP-20): the instance-wide switch.</summary>
    public const string Enabled = "mcp.enabled";

    /// <summary>bool, default true: clients may register themselves (DCR) or identify by metadata URL (MCP-11).</summary>
    public const string OpenRegistration = "mcp.open_registration";

    /// <summary>bool, default false: personal API keys may be used as MCP bearer tokens (MCP-14).</summary>
    public const string ApiKeysAllowed = "mcp.api_keys_allowed";

    /// <summary>string[]: extra browser origins allowed to call /mcp (MCP-5). The instance's own origin is always allowed.</summary>
    public const string AllowedOrigins = "mcp.allowed_origins";

    /// <summary><c>out_of_band</c> (default) or <c>in_client</c>: how authority (class A) actions are confirmed (MCP-26).</summary>
    public const string AuthorityConfirmation = "mcp.authority_confirmation";

    /// <summary>int, default 200: internal writes per grant per hour before each write needs confirmation (MCP-53).</summary>
    public const string WriteBudgetPerHour = "mcp.write_budget_per_hour";

    /// <summary>int, default 50: records one call may change (MCP-53).</summary>
    public const string BulkCeiling = "mcp.bulk_ceiling";

    public const int DefaultWriteBudget = 200;
    public const int DefaultBulkCeiling = 50;

    /// <summary>Confirmations expire after this long (MCP-52).</summary>
    public static readonly TimeSpan PendingActionLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Access tokens live this long (MCP-12).</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Refresh tokens expire after this long unused (MCP-12).</summary>
    public static readonly TimeSpan RefreshTokenIdleLifetime = TimeSpan.FromDays(30);

    /// <summary>Structured content cap (MCP-81).</summary>
    public const int MaxStructuredBytes = 256 * 1024;

    /// <summary>A sign-in this recent counts as re-authentication for destructive actions.</summary>
    public static readonly TimeSpan RecentSignIn = TimeSpan.FromMinutes(5);
}
