using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Security;
using Switchboard.Modules.Mcp.Server;

namespace Switchboard.Modules.Mcp.Tools;

public sealed class WhoAmITool(SwitchboardDbContext db, IPermissionService permissions, IActorAccessor actors, McpToolCatalog catalog) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "whoami",
        "Who am I",
        "Returns the Switchboard user you act as: name, email, whether they are an agent, their roles per workspace, the scopes this connection was granted, " +
        "and the tools you may call. Call it first to learn what you can do.",
        McpScopes.Profile,
        AgentActionClass.Read,
        Schema([]),
        Output(("user", Obj("The signed-in user.")), ("roles", T("array")), ("scopes", T("array")), ("tools", T("array")), ("connection", Obj("This connection."))),
        RequesterAllowed: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var actor = actors.Current;
        var userId = actor.UserId ?? throw new ForbiddenException();
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.JobTitle, u.IsAgent, u.IsOrgAdmin, u.TimeZone }).FirstAsync(ct);
        var perms = await permissions.ForUserAsync(userId, ct);
        var workspaces = await db.Workspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);

        var access = Call;
        var tools = access is null
            ? []
            : catalog.All.Select(t => t.Definition).Where(access.CanUse).Select(d => (JsonNode)d.Name).ToArray();
        var result = new JsonObject
        {
            ["user"] = new JsonObject
            {
                ["id"] = user.Id.ToString(),
                ["name"] = AgentContent.Untrusted(user.DisplayName, "user_profile"),
                ["email"] = user.Email,
                ["job_title"] = user.JobTitle is null ? null : AgentContent.Untrusted(user.JobTitle, "user_profile"),
                ["time_zone"] = user.TimeZone,
                ["is_agent"] = perms?.IsAgent == true,
                ["is_org_admin"] = user.IsOrgAdmin,
            },
            ["roles"] = new JsonArray((perms?.Workspaces.Values ?? []).Select(w => (JsonNode)new JsonObject
            {
                ["workspace_id"] = w.WorkspaceId.ToString(),
                ["workspace"] = workspaces.GetValueOrDefault(w.WorkspaceId),
                ["role"] = w.RoleName,
                ["ticket_scope"] = w.Scope.ToString().ToLowerInvariant(),
            }).ToArray()),
            ["scopes"] = new JsonArray((actor.Scopes ?? new HashSet<string>()).Order(StringComparer.Ordinal).Select(s => (JsonNode)s).ToArray()),
            ["tools"] = new JsonArray(tools),
            ["connection"] = new JsonObject
            {
                ["client_id"] = actor.ClientId,
                ["client_name"] = AgentContent.Untrusted(actor.ClientName, "client_registration"),
                ["grant_id"] = actor.GrantId?.ToString(),
                ["via_api_key"] = access?.Caller.ViaApiKey == true,
            },
        };
        if (actor.GrantId is { } grantId)
        {
            var grant = await db.Set<McpGrant>().AsNoTracking().Where(g => g.Id == grantId).Select(g => new { g.ExpiresAt, g.CreatedAt }).FirstOrDefaultAsync(ct);
            ((JsonObject)result["connection"]!)["expires_at"] = grant?.ExpiresAt.ToString("O");
        }
        return AgentToolResult.Ok(result, $"You are acting as {(perms?.IsAgent == true ? "an agent" : "a requester")} with {tools.Length} tools available.");
    }
}

public sealed class ListWorkspacesTool(SwitchboardDbContext db, IPermissionService permissions, IActorAccessor actors) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "list_workspaces",
        "List workspaces",
        "Lists the service desk workspaces (for example IT or HR) you can work in or submit requests to, with their ids. " +
        "Pass a workspace_id to create_ticket or describe_fields to target one; the primary workspace is used otherwise.",
        McpScopes.Profile,
        AgentActionClass.Read,
        Schema([]),
        Output(("workspaces", T("array"))),
        RequesterAllowed: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var userId = actors.Current.UserId ?? throw new ForbiddenException();
        var perms = await permissions.ForUserAsync(userId, ct) ?? throw new ForbiddenException();
        var all = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).ToListAsync(ct);
        var list = perms.IsAgent && !perms.IsOrgAdmin ? all.Where(w => perms.Workspaces.ContainsKey(w.Id)).ToList() : all;
        var items = list.Select(w => (JsonNode)new JsonObject
        {
            ["id"] = w.Id.ToString(),
            ["key"] = w.Key,
            ["name"] = w.Name,
            ["is_primary"] = w.IsPrimary,
            ["role"] = perms.Workspaces.TryGetValue(w.Id, out var access) ? access.RoleName : null,
        }).ToArray();
        return AgentToolResult.Ok(new JsonObject { ["workspaces"] = new JsonArray(items) }, $"{items.Length} workspace(s).");
    }
}

public sealed class GetPendingActionTool(SwitchboardDbContext db, IActorAccessor actors, TimeProvider time) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "get_pending_action",
        "Get pending action",
        "Returns the status of an action that needed the user's confirmation (a tool returned confirmation_required with a pending_action id): " +
        "pending, declined, expired, or done with the action's result. Check back after the user has had time to confirm; don't poll more than every few seconds.",
        McpScopes.Profile,
        AgentActionClass.Read,
        Schema(["id"], ("id", Id("The pending_action id from the confirmation_required result."))),
        Output(("id", T("string")), ("status", T("string")), ("result", T("object"))),
        RequesterAllowed: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var id = Uuid(arguments, "id")!.Value;
        var actor = actors.Current;
        var userId = actor.UserId ?? throw new ForbiddenException();
        // Only the user's own actions, and only from the client that asked (a second agent app can't read another's results).
        var action = await db.Set<McpPendingAction>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId && p.ClientId == actor.ClientId, ct)
            ?? throw new NotFoundException();

        var status = action.Status switch
        {
            McpPendingStatus.Pending when action.ExpiresAt <= time.GetUtcNow() => "expired",
            McpPendingStatus.Pending => "pending",
            McpPendingStatus.Declined => "declined",
            McpPendingStatus.Expired => "expired",
            _ when action.CompletedAt is null => "running",
            _ when action.ResultCode == McpErrorCodes.Ok => "done",
            _ => "failed",
        };
        var result = new JsonObject
        {
            ["id"] = action.Id.ToString(),
            ["tool"] = action.Tool,
            ["status"] = status,
            ["created_at"] = action.CreatedAt.ToString("O"),
            ["expires_at"] = action.ExpiresAt.ToString("O"),
            ["decided_at"] = action.DecidedAt?.ToString("O"),
            ["completed_at"] = action.CompletedAt?.ToString("O"),
            ["result_code"] = action.ResultCode,
            ["result"] = action.ResultJson is null ? null : JsonNode.Parse(action.ResultJson),
        };
        var text = status switch
        {
            "pending" => "Still waiting for the user to confirm.",
            "declined" => "The user declined. Don't retry unless they ask.",
            "expired" => "Nobody confirmed in time, so it didn't run.",
            "running" => "Approved and running.",
            "done" => "Approved and done.",
            _ => "Approved, but the action failed. See result.",
        };
        return AgentToolResult.Ok(result, text);
    }
}

public sealed class DescribeFieldsTool(
    SwitchboardDbContext db,
    IPermissionService permissions,
    ICustomFieldService customFields,
    IActorAccessor actors) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "describe_fields",
        "Describe fields",
        "Returns the fields of a record type exactly as you see them in a workspace: names, types, allowed values and which are required " +
        "when creating, updating or resolving. Call it before create_ticket or update_ticket so your values are valid.",
        McpScopes.TicketsRead,
        AgentActionClass.Read,
        Schema(["entity"],
            ("entity", E("Record type to describe.", "ticket")),
            ("workspace_id", Id("Workspace to describe; defaults to the primary workspace."))),
        Output(("entity", T("string")), ("fields", T("array")), ("custom_fields", T("array"))),
        RequesterAllowed: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var userId = actors.Current.UserId ?? throw new ForbiddenException();
        var perms = await permissions.ForUserAsync(userId, ct) ?? throw new ForbiddenException();
        var asAgent = perms.IsAgent;
        var workspaceId = Uuid(arguments, "workspace_id");
        var workspace = workspaceId is { } wsId
            ? await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wsId && !w.IsArchived, ct)
            : await db.Workspaces.AsNoTracking().FirstAsync(w => w.IsPrimary, ct);
        if (workspace is null || (asAgent && !perms.IsOrgAdmin && !perms.Workspaces.ContainsKey(workspace.Id)))
        {
            throw new NotFoundException();
        }

        var fields = new JsonArray
        {
            Field("subject", "Subject", "string", required: true, note: "Up to 500 characters."),
            Field("description", "Description", "string", note: "Plain text."),
            Field("type", "Type", "enum", values: ["incident", "service_request"]),
        };
        if (asAgent)
        {
            var statuses = await db.TicketStatuses.AsNoTracking().Where(s => s.WorkspaceId == workspace.Id).OrderBy(s => s.Order).Select(s => s.Name).ToListAsync(ct);
            var groups = await db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == workspace.Id).OrderBy(g => g.Name).Select(g => g.Name).ToListAsync(ct);
            var categories = await db.TicketCategories.AsNoTracking().Where(c => c.WorkspaceId == workspace.Id).OrderBy(c => c.Level).ThenBy(c => c.Order)
                .Select(c => new { c.Id, c.Name, c.ParentId, c.Level }).ToListAsync(ct);
            fields.Add(Field("priority", "Priority", "enum", values: ["low", "medium", "high", "urgent"]));
            fields.Add(Field("urgency", "Urgency", "enum", values: ["low", "medium", "high"]));
            fields.Add(Field("impact", "Impact", "enum", values: ["low", "medium", "high"], note: "Urgency and impact set the priority when it isn't given."));
            fields.Add(Field("status", "Status", "enum", values: [.. statuses], note: "Resolving or closing can require more fields; see required_on_resolve."));
            fields.Add(Field("group", "Group", "enum", values: [.. groups]));
            fields.Add(Field("agent", "Agent", "user", note: "\"me\", a user id or an email address. Use search_users to find agents."));
            fields.Add(Field("requester_email", "Requester", "email", note: "Creating a ticket for someone else needs their confirmation of the new requester in your client."));
            fields.Add(Field("tags", "Tags", "string[]", note: "Lower case, up to 64 characters each."));
            fields.Add(Field("cc_emails", "CC", "email[]", note: "Changing who receives replies needs the user's confirmation."));
            var categoryTree = new JsonArray(categories.Where(c => c.Level == 1).Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id.ToString(),
                ["name"] = c.Name,
                ["sub_categories"] = new JsonArray(categories.Where(s => s.ParentId == c.Id).Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id.ToString(),
                    ["name"] = s.Name,
                    ["items"] = new JsonArray(categories.Where(i => i.ParentId == s.Id).Select(i => (JsonNode)new JsonObject { ["id"] = i.Id.ToString(), ["name"] = i.Name }).ToArray()),
                }).ToArray()),
            }).ToArray());
            var categoryField = Field("category_id", "Category", "category", note: "Pick the category, then optionally sub_category_id and item_category_id from the same branch.");
            categoryField["tree"] = categoryTree;
            fields.Add(categoryField);
        }

        var definitions = await customFields.GetDefinitionsAsync("ticket", workspace.Id, ct);
        var custom = new JsonArray();
        foreach (var d in definitions.Where(d => d.FieldType != CustomFieldType.Content && (asAgent || d.VisibleToRequesters)))
        {
            var f = new JsonObject
            {
                ["key"] = d.Key,
                ["label"] = asAgent ? d.Label : d.RequesterLabel ?? d.Label,
                ["type"] = d.FieldType.ToString().ToLowerInvariant(),
                ["required_on_create"] = asAgent ? d.RequiredForAgents : d.RequiredForRequesters,
                ["required_on_resolve"] = asAgent && d.RequiredOnResolve,
                ["editable"] = asAgent || d.EditableByRequesters,
            };
            if (d.HelpText is { Length: > 0 } help)
            {
                f["help"] = help;
            }
            if (d.FieldType is CustomFieldType.Dropdown or CustomFieldType.MultiSelect or CustomFieldType.DependentDropdown)
            {
                f["values"] = JsonNode.Parse(d.OptionsJson);
            }
            custom.Add(f);
        }

        var result = new JsonObject
        {
            ["entity"] = "ticket",
            ["workspace"] = new JsonObject { ["id"] = workspace.Id.ToString(), ["name"] = workspace.Name },
            ["audience"] = asAgent ? "agent" : "requester",
            ["fields"] = fields,
            ["custom_fields"] = custom,
            ["custom_fields_argument"] = "Pass custom fields to create_ticket and update_ticket as custom_fields: { \"<key>\": value }.",
        };
        return AgentToolResult.Ok(result, $"{fields.Count} standard and {custom.Count} custom ticket fields.");
    }

    private static JsonObject Field(string key, string label, string type, bool required = false, string[]? values = null, string? note = null)
    {
        var f = new JsonObject { ["key"] = key, ["label"] = label, ["type"] = type, ["required_on_create"] = required };
        if (values is not null)
        {
            f["values"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray());
        }
        if (note is not null)
        {
            f["note"] = note;
        }
        return f;
    }
}
