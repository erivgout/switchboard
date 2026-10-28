using Switchboard.Core.Services;

namespace Switchboard.Core;

/// <summary>Bound from the <c>Switchboard</c> configuration section.</summary>
public sealed class SwitchboardOptions
{
    public const string Section = "Switchboard";

    /// <summary>Public URL of the instance (emails, OAuth/MCP metadata).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Seed demo people and tickets on startup (development only).</summary>
    public bool DemoData { get; set; }

    /// <summary>When true and the instance is unconfigured, development creates the demo admin instead of requiring setup.</summary>
    public bool AutoSetup { get; set; }

    /// <summary>Marks a non-production instance: outbound email/SMS/webhooks are redirected or disabled (SR-DEP-6).</summary>
    public bool NonProduction { get; set; }

    public StorageOptions Storage { get; set; } = new();
}

/// <summary>Credentials of the demo accounts (development only; documented in docs/MODULE_GUIDE.md).</summary>
public static class DemoAccounts
{
    public const string Password = "Switchboard!2026";
    public const string Admin = "admin@switchboard.local";
    public const string Agent = "sam@switchboard.local";
    public const string Supervisor = "priya@switchboard.local";
    public const string GroupAgent = "jordan@switchboard.local";
    public const string Requester = "dana@switchboard.local";
}
