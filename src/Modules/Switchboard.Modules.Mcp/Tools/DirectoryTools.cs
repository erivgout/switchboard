using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Mcp.Security;

namespace Switchboard.Modules.Mcp.Tools;

public sealed class SearchUsersTool(SwitchboardDbContext db, IPermissionService permissions) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "search_users",
        "Search people",
        "Finds people (requesters and agents) by name or email. Returns ids, names, emails, job titles and departments. " +
        "Use agents_only to find someone to assign work to.",
        McpScopes.UsersRead,
        AgentActionClass.Read,
        Schema(["query"],
            ("query", S("Part of a name or email address.", 200)),
            ("agents_only", B("Only agents.")),
            ("page_size", I("Results per page, default 20.", 1, 100)),
            ("cursor", S("next_cursor from the previous page.", 100))),
        Output(("users", T("array")), ("next_cursor", T("string"))));

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        await permissions.DemandAsync(Privileges.UsersView, ct: ct);
        var query = (Str(arguments, "query") ?? "").Trim();
        if (query.Length == 0)
        {
            return Error(Server.McpErrorCodes.ValidationFailed, "Type part of a name or email address.");
        }
        var size = Int(arguments, "page_size") ?? 20;
        var page = SearchTicketsTool.DecodeCursor(Str(arguments, "cursor"));
        var users = db.Users.AsNoTracking().Where(u => !u.IsForgotten && (u.DisplayName.Contains(query) || (u.Email != null && u.Email.Contains(query))));
        if (Bool(arguments, "agents_only") == true)
        {
            users = users.Where(u => u.IsAgent || u.IsOrgAdmin);
        }
        var rows = await users.OrderBy(u => u.DisplayName).Skip((page - 1) * size).Take(size + 1)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.JobTitle, Department = u.Department != null ? u.Department.Name : null, u.IsAgent, u.IsActive, u.IsVip })
            .ToListAsync(ct);
        var items = rows.Take(size).Select(u => (JsonNode)new JsonObject
        {
            ["id"] = u.Id.ToString(),
            ["name"] = AgentContent.Untrusted(u.DisplayName, "user_profile"),
            ["email"] = u.Email,
            ["job_title"] = u.JobTitle is null ? null : AgentContent.Untrusted(u.JobTitle, "user_profile"),
            ["department"] = u.Department,
            ["is_agent"] = u.IsAgent,
            ["is_active"] = u.IsActive,
            ["is_vip"] = u.IsVip,
        }).ToArray();
        return AgentToolResult.Ok(new JsonObject
        {
            ["users"] = new JsonArray(items),
            ["next_cursor"] = rows.Count > size ? SearchTicketsTool.EncodeCursor(page + 1) : null,
        }, $"{items.Length} person(s).");
    }
}

public sealed class GetUserTool(SwitchboardDbContext db, IPermissionService permissions) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "get_user",
        "Get person",
        "Returns one person by id or email: name, email, job title, department, location, manager, VIP flag, and for agents their groups and roles.",
        McpScopes.UsersRead,
        AgentActionClass.Read,
        Schema(["user"], ("user", S("A user id or email address.", 254))),
        Output(("id", T("string")), ("name", T("object")), ("email", T("string"))));

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        await permissions.DemandAsync(Privileges.UsersView, ct: ct);
        var value = (Str(arguments, "user") ?? "").Trim();
        var users = db.Users.AsNoTracking().Where(u => !u.IsForgotten);
        users = Guid.TryParse(value, out var id) ? users.Where(u => u.Id == id) : users.Where(u => u.NormalizedEmail == value.ToUpperInvariant());
        var user = await users.Select(u => new
        {
            u.Id, u.DisplayName, u.Email, u.JobTitle, u.IsAgent, u.IsActive, u.IsVip, u.TimeZone,
            Department = u.Department != null ? u.Department.Name : null,
            Location = u.Location != null ? u.Location.Name : null,
            u.ManagerId,
            Manager = u.Manager != null ? u.Manager.DisplayName : null,
        }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException();

        var result = new JsonObject
        {
            ["id"] = user.Id.ToString(),
            ["name"] = AgentContent.Untrusted(user.DisplayName, "user_profile"),
            ["email"] = user.Email,
            ["job_title"] = user.JobTitle is null ? null : AgentContent.Untrusted(user.JobTitle, "user_profile"),
            ["department"] = user.Department,
            ["location"] = user.Location,
            ["manager"] = user.ManagerId is null ? null : new JsonObject { ["id"] = user.ManagerId.ToString(), ["name"] = AgentContent.Untrusted(user.Manager, "user_profile") },
            ["time_zone"] = user.TimeZone,
            ["is_agent"] = user.IsAgent,
            ["is_active"] = user.IsActive,
            ["is_vip"] = user.IsVip,
        };
        if (user.IsAgent)
        {
            var groups = await db.AgentGroupMembers.AsNoTracking().Where(m => m.UserId == user.Id)
                .Select(m => new { m.GroupId, m.Group!.Name, m.IsObserver }).ToListAsync(ct);
            result["groups"] = new JsonArray(groups.Select(g => (JsonNode)new JsonObject { ["id"] = g.GroupId.ToString(), ["name"] = g.Name, ["observer"] = g.IsObserver }).ToArray());
            var roles = await db.WorkspaceMemberships.AsNoTracking().Where(m => m.UserId == user.Id)
                .Select(m => new { Workspace = m.Workspace!.Name, Role = m.Role!.Name }).ToListAsync(ct);
            result["roles"] = new JsonArray(roles.Select(r => (JsonNode)new JsonObject { ["workspace"] = r.Workspace, ["role"] = r.Role }).ToArray());
        }
        return AgentToolResult.Ok(result, "Found the person.");
    }
}

public sealed class SearchGroupsTool(SwitchboardDbContext db, IPermissionService permissions, IActorAccessor actors) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "search_groups",
        "Search groups",
        "Lists agent groups in your workspaces, optionally filtered by name, with their members when include_members is true.",
        McpScopes.UsersRead,
        AgentActionClass.Read,
        Schema([],
            ("query", S("Part of a group name.", 200)),
            ("workspace_id", Id("Only this workspace.")),
            ("include_members", B("Include each group's members."))),
        Output(("groups", T("array"))));

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        await permissions.DemandAsync(Privileges.UsersView, ct: ct);
        var perms = await permissions.ForUserAsync(actors.Current.UserId ?? throw new ForbiddenException(), ct) ?? throw new ForbiddenException();
        var groups = db.AgentGroups.AsNoTracking();
        if (!perms.IsOrgAdmin)
        {
            var mine = perms.Workspaces.Keys.ToList();
            groups = groups.Where(g => mine.Contains(g.WorkspaceId));
        }
        if (Uuid(arguments, "workspace_id") is { } ws)
        {
            groups = groups.Where(g => g.WorkspaceId == ws);
        }
        if (Str(arguments, "query") is { Length: > 0 } q)
        {
            groups = groups.Where(g => g.Name.Contains(q));
        }
        var includeMembers = Bool(arguments, "include_members") == true;
        var rows = await groups.OrderBy(g => g.Name).Take(100)
            .Select(g => new
            {
                g.Id, g.Name, g.Description, g.WorkspaceId,
                Members = g.Members.Where(m => !m.IsObserver).Select(m => new { m.UserId, m.User!.DisplayName }).ToList(),
            })
            .ToListAsync(ct);
        var items = rows.Select(g =>
        {
            var o = new JsonObject
            {
                ["id"] = g.Id.ToString(),
                ["name"] = g.Name,
                ["description"] = g.Description,
                ["workspace_id"] = g.WorkspaceId.ToString(),
                ["member_count"] = g.Members.Count,
            };
            if (includeMembers)
            {
                o["members"] = new JsonArray(g.Members.Select(m => (JsonNode)new JsonObject
                {
                    ["id"] = m.UserId.ToString(),
                    ["name"] = AgentContent.Untrusted(m.DisplayName, "user_profile"),
                }).ToArray());
            }
            return (JsonNode)o;
        }).ToArray();
        return AgentToolResult.Ok(new JsonObject { ["groups"] = new JsonArray(items) }, $"{items.Length} group(s).");
    }
}
