using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Modules.Api.Auth;

public sealed record ApiScopeDefinition(string Key, string Title, string Group, bool ForRequesters = false);

/// <summary>
/// Scopes a personal API key can be limited to (SRS MCP-18, the same vocabulary as MCP grants). A key
/// without scopes acts with all of its owner's permissions; a key with scopes never exceeds them.
/// </summary>
public static class ApiScopes
{
    public const string Profile = "profile";
    public const string TicketsRead = "tickets:read";
    public const string TicketsWrite = "tickets:write";
    public const string TicketsDelete = "tickets:delete";
    public const string TicketsReply = "tickets:reply";
    public const string UsersRead = "users:read";

    public static IReadOnlyList<ApiScopeDefinition> All { get; } =
    [
        new(Profile, "Profile and workspaces", "Account", ForRequesters: true),
        new(TicketsRead, "Read tickets, tasks and attachments", "Tickets", ForRequesters: true),
        new(TicketsWrite, "Create and change tickets, tasks and private notes", "Tickets", ForRequesters: true),
        new(TicketsReply, "Send replies, forwards and public notes", "Tickets"),
        new(TicketsDelete, "Move to trash, restore, mark spam", "Tickets"),
        new(UsersRead, "Read people, groups, departments and locations", "People"),
        new("kb:read", "Read articles", "Knowledge", ForRequesters: true),
        new("kb:write", "Write articles", "Knowledge"),
        new("catalog:read", "Browse the catalog", "Service catalog", ForRequesters: true),
        new("catalog:request", "Submit requests", "Service catalog", ForRequesters: true),
        new("approvals:read", "Read approvals", "Approvals", ForRequesters: true),
        new("approvals:decide", "Approve and reject", "Approvals", ForRequesters: true),
        new("itil:read", "Read problems, changes and releases", "Problems and changes"),
        new("itil:write", "Change problems, changes and releases", "Problems and changes"),
        new("assets:read", "Read assets", "Assets"),
        new("assets:write", "Change and assign assets", "Assets"),
        new("procurement:read", "Read software, contracts and vendors", "Procurement"),
        new("ops:read", "Read alerts, on-call and status pages", "Operations"),
        new("ops:write", "Act on alerts and post status updates", "Operations"),
        new("projects:read", "Read projects", "Projects"),
        new("projects:write", "Change projects", "Projects"),
        new("analytics:read", "Run reports and read dashboards", "Analytics"),
        new("admin:read", "Read configuration and the audit log", "Administration"),
    ];

    public static ApiScopeDefinition? Find(string scope) => All.FirstOrDefault(s => s.Key == scope);

    /// <summary>
    /// The scope a privilege needs; null when no scope can grant it, so scoped keys never get it (people,
    /// roles and settings management, permanent deletion, exports: there is deliberately no admin:write).
    /// Kept in line with the MCP module's mapping so keys and MCP grants mean the same thing.
    /// </summary>
    public static string? For(string privilege)
    {
        switch (privilege)
        {
            case Privileges.TicketsView:
                return TicketsRead;
            case Privileges.TicketsCreate:
            case Privileges.TicketsEdit:
            case Privileges.TicketsNote:
            case Privileges.TicketsAssign:
            case Privileges.TicketsMerge:
            case Privileges.TicketsBulk:
                return TicketsWrite;
            case Privileges.TicketsReply:
                return TicketsReply;
            case Privileges.TicketsDelete:
                return TicketsDelete;
            case Privileges.UsersView:
                return UsersRead;
            case Privileges.AuditView:
                return "admin:read";
            case Privileges.TicketsDeletePermanently:
            case Privileges.TicketsExport:
                return null;
        }

        // Module privileges follow the area.verb convention.
        var dot = privilege.IndexOf('.');
        if (dot <= 0)
        {
            return null;
        }
        var area = privilege[..dot];
        var verb = privilege[(dot + 1)..];
        var read = verb is "view" or "read";
        return area switch
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
    }

    /// <summary>
    /// Cleans a requested scope list: drops blanks, collects unknown scopes (and agent-only scopes asked for
    /// by requesters), and adds tickets:read to any ticket write scope, since every ticket write reads it first.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? requested, bool isAgent, out List<string> rejected)
    {
        rejected = [];
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in requested ?? [])
        {
            var scope = raw.Trim();
            if (scope.Length == 0)
            {
                continue;
            }
            if (Find(scope) is not { } def || (!isAgent && !def.ForRequesters))
            {
                rejected.Add(scope);
                continue;
            }
            set.Add(scope);
        }
        if (set.Overlaps([TicketsWrite, TicketsReply, TicketsDelete]))
        {
            set.Add(TicketsRead);
        }
        return All.Where(s => set.Contains(s.Key)).Select(s => s.Key).ToList();
    }

    /// <summary>True unless the actor is a scoped API caller without <paramref name="scope"/>.</summary>
    public static bool Allows(Actor actor, string scope) =>
        actor.Channel != Channel.Api || actor.Scopes is null || actor.Scopes.Contains(scope);
}

/// <summary>Narrows every privilege check of a scoped API key to its scopes (SR-AUTHN-7). MCP has its own guard.</summary>
internal sealed class ApiKeyScopeGuard : IPrivilegeScopeGuard
{
    public bool Allows(Actor actor, string privilege) =>
        actor.Channel != Channel.Api || actor.Scopes is null
        || (ApiScopes.For(privilege) is { } scope && actor.Scopes.Contains(scope));
}
