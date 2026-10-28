using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Modules.Mcp.Security;

/// <param name="Key">OAuth scope (SRS MCP-18).</param>
/// <param name="Title">Short label for lists.</param>
/// <param name="Consent">Plain-language sentence for the consent screen: what the agent may do.</param>
/// <param name="ForRequesters">Requesters can grant it (MCP-17).</param>
/// <param name="RequesterTitle">Label for requesters, when what they can do differs.</param>
/// <param name="RequesterConsent">Consent sentence for requesters, when what they can do differs.</param>
public sealed record McpScopeDefinition(string Key, string Title, string Consent, bool ForRequesters, bool LeavesOrganization = false,
    string? RequesterTitle = null, string? RequesterConsent = null)
{
    public string TitleFor(bool isAgent) => isAgent ? Title : RequesterTitle ?? Title;

    public string ConsentFor(bool isAgent) => isAgent ? Consent : RequesterConsent ?? Consent;
}

/// <summary>The scopes an agent can ask for. There is deliberately no admin:write (MCP-34).</summary>
public static class McpScopes
{
    public const string Profile = "profile";
    public const string TicketsRead = "tickets:read";
    public const string TicketsWrite = "tickets:write";
    public const string TicketsDelete = "tickets:delete";
    public const string TicketsReply = "tickets:reply";
    public const string UsersRead = "users:read";

    public static IReadOnlyList<McpScopeDefinition> All { get; } =
    [
        new(Profile, "Profile", "See your name, roles and workspaces, and check on actions waiting for your confirmation.", true,
            RequesterConsent: "See your name and check on actions waiting for your confirmation."),
        new(TicketsRead, "Read tickets", "Read tickets, their conversations and tasks that you can see.", true,
            RequesterTitle: "Read your requests", RequesterConsent: "Read the requests you can see in the portal, and the replies on them."),
        new(TicketsWrite, "Change tickets", "Create tickets, change their fields, assign them and add private notes.", true,
            RequesterTitle: "Submit requests", RequesterConsent: "Submit new requests in your name."),
        new(TicketsReply, "Send messages", "Send replies, forwards and public notes that people outside your team can read. You confirm every message before it's sent.", true, LeavesOrganization: true,
            RequesterTitle: "Write on your requests", RequesterConsent: "Add messages to your requests in your name. The service desk reads them."),
        new(TicketsDelete, "Delete tickets", "Move tickets to the trash. You confirm every deletion in Switchboard first.", false),
        new(UsersRead, "Read people", "Look up people, groups and departments.", false),
        new("kb:read", "Read knowledge", "Read knowledge articles you can see.", true),
        new("kb:write", "Write knowledge", "Draft and edit knowledge articles.", false),
        new("catalog:read", "Browse catalog", "Browse the service catalog.", true),
        new("catalog:request", "Request items", "Submit service requests from the catalog for you.", true),
        new("approvals:read", "Read approvals", "See approvals waiting for you.", true),
        new("approvals:decide", "Decide approvals", "Approve or reject on your behalf. You confirm every decision in Switchboard first.", true),
        new("itil:read", "Read problems and changes", "Read problems, changes and releases.", false),
        new("itil:write", "Change problems and changes", "Create and update problems, changes and releases.", false),
        new("assets:read", "Read assets", "Read assets and their relationships.", false),
        new("assets:write", "Change assets", "Update and assign assets.", false),
        new("procurement:read", "Read contracts and software", "Read software, contracts, purchase orders and vendors.", false),
        new("ops:read", "Read operations", "Read alerts, on-call schedules and status pages.", false),
        new("ops:write", "Act on operations", "Acknowledge and resolve alerts and post status updates.", false),
        new("projects:read", "Read projects", "Read projects and project tasks.", false),
        new("projects:write", "Change projects", "Create and update project tasks.", false),
        new("analytics:read", "Read reports", "Run reports and read dashboards you can see.", false),
        new("admin:read", "Read configuration", "Read configuration such as automations and the audit log. It can't change anything.", false),
    ];

    private static readonly Dictionary<string, McpScopeDefinition> ByKey = All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    public static bool IsKnown(string scope) => ByKey.ContainsKey(scope);

    public static McpScopeDefinition? Find(string scope) => ByKey.GetValueOrDefault(scope);

    /// <summary>Known scopes from a space-separated or listed request, in catalog order, filtered for requesters.</summary>
    public static List<string> Normalize(IEnumerable<string> requested, bool isAgent)
    {
        var set = requested.Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        return All.Where(d => set.Contains(d.Key) && (isAgent || d.ForRequesters)).Select(d => d.Key).ToList();
    }

    /// <summary>
    /// The scope that lets an MCP caller use a privilege, or null when no scope can (MCP-34: permanent
    /// deletion, exports, admin configuration). Follows the area.verb privilege convention for modules.
    /// </summary>
    public static IReadOnlyList<string> ScopesFor(string privilege)
    {
        switch (privilege)
        {
            // Every ticket write reads the ticket first, so any ticket scope can see it; read tools still need tickets:read.
            case Privileges.TicketsView:
                return [TicketsRead, TicketsWrite, TicketsReply, TicketsDelete];
            case Privileges.TicketsCreate:
            case Privileges.TicketsEdit:
            case Privileges.TicketsNote:
            case Privileges.TicketsAssign:
            case Privileges.TicketsMerge:
            case Privileges.TicketsBulk:
                return [TicketsWrite];
            case Privileges.TicketsReply:
                return [TicketsReply];
            case Privileges.TicketsDelete:
                return [TicketsDelete];
            case Privileges.UsersView:
                return [UsersRead];
            case Privileges.AuditView:
                return ["admin:read"];
            case Privileges.TicketsDeletePermanently:
            case Privileges.TicketsExport:
                return [];
        }

        var dot = privilege.IndexOf('.');
        if (dot <= 0)
        {
            return [];
        }
        var area = privilege[..dot];
        var verb = privilege[(dot + 1)..];
        var read = verb is "view" or "read";
        string? scope = area switch
        {
            "kb" or "knowledge" => read ? "kb:read" : verb is "edit" or "write" or "create" or "publish" or "manage" ? "kb:write" : null,
            "catalog" => read ? "catalog:read" : verb is "use" or "request" ? "catalog:request" : null,
            "approvals" => read ? "approvals:read" : verb is "use" or "decide" ? "approvals:decide" : null,
            "problems" or "changes" or "releases" or "itil" => read ? "itil:read" : verb is "edit" or "write" or "create" or "manage" ? "itil:write" : null,
            "assets" or "cmdb" => read ? "assets:read" : verb is "edit" or "write" or "assign" or "manage" ? "assets:write" : null,
            "procurement" or "contracts" or "software" or "vendors" or "purchasing" => read ? "procurement:read" : null,
            "ops" or "alerts" or "oncall" or "statuspages" => read ? "ops:read" : verb is "edit" or "write" or "acknowledge" or "resolve" or "post" ? "ops:write" : null,
            "projects" => read ? "projects:read" : verb is "edit" or "write" or "create" ? "projects:write" : null,
            "analytics" or "reports" or "insights" or "dashboards" => read ? "analytics:read" : null,
            _ => null,
        };
        return scope is null ? [] : [scope];
    }
}

/// <summary>
/// Narrows every privilege check made by an MCP caller to the scopes its grant holds (MCP-16, INV-MCP-1).
/// Other channels are left to their own guards.
/// </summary>
internal sealed class McpScopeGuard : IPrivilegeScopeGuard
{
    public bool Allows(Actor actor, string privilege)
    {
        if (actor.Channel != Channel.Mcp)
        {
            return true;
        }
        var scopes = actor.Scopes;
        if (scopes is null)
        {
            return false;
        }
        return McpScopes.ScopesFor(privilege).Any(scopes.Contains);
    }
}
