using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Mcp.Security;
using Switchboard.Modules.Mcp.Server;

namespace Switchboard.Modules.Mcp.Tools;

internal static class Ticketing
{
    public static JsonObject TicketIdArg => new() { ["type"] = "string", ["maxLength"] = 64, ["description"] = "Ticket id or display id, for example INC-1042." };

    public static JsonObject Emails(string description, int max) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["maxItems"] = max,
        ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 254 },
    };

    public static string Quote(string subject) => $"\"{TicketToolSupport.Preview(subject, 200)}\"";
}

public sealed class CreateTicketTool(ITicketService tickets, TicketToolSupport support, IHtmlContent html, SwitchboardDbContext db) : AgentToolBase, IAgentToolClassifier
{
    private static readonly string[] AgentOnly = ["priority", "urgency", "impact", "status", "group", "agent", "requester_email", "category_id", "sub_category_id", "item_category_id", "tags", "cc_emails"];

    public override AgentToolDefinition Definition { get; } = new(
        "create_ticket",
        "Create ticket",
        "Creates a ticket. Requesters create it for themselves with subject, description, type and their custom fields. " +
        "Agents can also set priority, status, group, agent, category, tags and the requester; creating a ticket for someone else, or with CC, " +
        "first asks the user to confirm because Switchboard may email those people. Call describe_fields first for valid values.",
        McpScopes.TicketsWrite,
        AgentActionClass.External,
        Schema(["subject"],
            ("subject", S("Short summary of the problem or request.", 500)),
            ("description", S("Details, as plain text.", 50000)),
            ("type", E("Default incident.", "incident", "service_request")),
            ("workspace_id", Id("Workspace; defaults to the primary one. See list_workspaces.")),
            ("priority", E("Agents only.", "low", "medium", "high", "urgent")),
            ("urgency", E("Agents only.", "low", "medium", "high")),
            ("impact", E("Agents only.", "low", "medium", "high")),
            ("status", S("Agents only. Status name, for example Open.", 100)),
            ("group", S("Agents only. Group name or id.", 200)),
            ("agent", S("Agents only. \"me\", a user id or an email address.", 254)),
            ("requester_email", S("Agents only. The person the ticket is for; defaults to you.", 254)),
            ("category_id", Id("Agents only. See describe_fields.")),
            ("sub_category_id", Id("Agents only.")),
            ("item_category_id", Id("Agents only.")),
            ("tags", Arr("Agents only.", S("Tag.", 64), 20)),
            ("cc_emails", Ticketing.Emails("Agents only. People copied on replies.", 20)),
            ("custom_fields", Obj("Custom field values by key, from describe_fields."))),
        Output(("id", T("string")), ("display_id", T("string")), ("version", T("string"))),
        RequesterAllowed: true);

    public async Task<AgentActionClass> ClassifyAsync(JsonObject arguments, CancellationToken ct)
    {
        if (!await support.IsAgentAsync(ct))
        {
            return AgentActionClass.InternalWrite;
        }
        if (StrList(arguments, "cc_emails") is { Count: > 0 })
        {
            return AgentActionClass.External;
        }
        // A ticket for someone else can email them; that leaves the user's hands, so it's confirmed (MCP-62).
        if (Str(arguments, "requester_email") is { Length: > 0 } email)
        {
            var normalized = email.Trim().ToUpperInvariant();
            var requester = await db.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalized).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
            return requester == support.CurrentUserId ? AgentActionClass.InternalWrite : AgentActionClass.External;
        }
        return AgentActionClass.InternalWrite;
    }

    public override async Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"Create a {(Str(arguments, "type") == "service_request" ? "service request" : "incident")}: {Ticketing.Quote(Str(arguments, "subject") ?? "")}.");
        if (Str(arguments, "requester_email") is { Length: > 0 } requester)
        {
            sb.Append($"\nRequester: {requester}. Switchboard may email them about this ticket.");
        }
        if (StrList(arguments, "cc_emails") is { Count: > 0 } cc)
        {
            sb.Append($"\nCC (will receive replies): {string.Join(", ", cc)}.");
        }
        if (Str(arguments, "description") is { Length: > 0 } description)
        {
            sb.Append($"\n\nDescription:\n{TicketToolSupport.Preview(description)}");
        }
        return sb.ToString();
    }

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var asAgent = await support.IsAgentAsync(ct);
        if (!asAgent && AgentOnly.FirstOrDefault(arguments.ContainsKey) is { } field)
        {
            return Error(McpErrorCodes.ValidationFailed, $"Only agents can set {field}.");
        }
        var workspaceId = Uuid(arguments, "workspace_id");
        var ws = workspaceId ?? await db.Workspaces.Where(w => w.IsPrimary).Select(w => w.Id).FirstAsync(ct);

        var command = new CreateTicketCommand
        {
            WorkspaceId = workspaceId,
            Subject = Str(arguments, "subject") ?? "",
            DescriptionHtml = html.FromPlainText(Str(arguments, "description")),
            Type = Str(arguments, "type") is { } type ? TicketToolSupport.ParseType(type) : TicketType.Incident,
            SourceKey = TicketSourceKeys.Mcp,
            AsRequester = !asAgent,
            CustomFields = TicketToolSupport.CustomFields(arguments, "custom_fields"),
        };
        if (asAgent)
        {
            command = command with
            {
                Priority = Str(arguments, "priority") is { } p ? TicketToolSupport.ParseEnum<Priority>(p, "priority") : null,
                Urgency = Str(arguments, "urgency") is { } u ? TicketToolSupport.ParseEnum<Level>(u, "urgency") : null,
                Impact = Str(arguments, "impact") is { } i ? TicketToolSupport.ParseEnum<Level>(i, "impact") : null,
                StatusId = Str(arguments, "status") is { } s ? (await support.ResolveStatusAsync(s, ws, ct)).Id : null,
                GroupId = await support.ResolveGroupAsync(Str(arguments, "group"), ws, ct),
                AgentId = await support.ResolveUserAsync(Str(arguments, "agent"), "agent", ct),
                RequesterEmail = Str(arguments, "requester_email"),
                CategoryId = Uuid(arguments, "category_id"),
                SubCategoryId = Uuid(arguments, "sub_category_id"),
                ItemCategoryId = Uuid(arguments, "item_category_id"),
                Tags = StrList(arguments, "tags"),
                CcEmails = StrList(arguments, "cc_emails"),
            };
        }

        var ticket = await tickets.CreateAsync(command, ct);
        var detail = await support.DetailAsync(await support.FindAsync(ticket.Id.ToString(), asAgent, ct), asAgent, false, ct);
        return AgentToolResult.Ok(detail, $"Created {ticket.DisplayId}.");
    }
}

public sealed class UpdateTicketTool(ITicketService tickets, TicketToolSupport support, IHtmlContent html, SwitchboardDbContext db) : AgentToolBase, IAgentToolClassifier
{
    public override AgentToolDefinition Definition { get; } = new(
        "update_ticket",
        "Update ticket",
        "Changes a ticket's fields. Pass expected_version from get_ticket: if someone changed the ticket since, nothing is saved and you get 'conflict' " +
        "with the current state, so you never overwrite a person's edit. Only the arguments you pass change; null clears group, agent or category. " +
        "Changing cc_emails or the requester changes who receives replies, so the user confirms it first. dry_run: true shows the changes without saving.",
        McpScopes.TicketsWrite,
        AgentActionClass.External,
        Schema(["ticket_id", "expected_version"],
            ("ticket_id", Ticketing.TicketIdArg),
            ("expected_version", S("The 'version' from get_ticket.", 100)),
            ("subject", S("New subject.", 500)),
            ("description", S("New description, as plain text. Replaces the old one.", 50000)),
            ("type", E("Ticket type.", "incident", "service_request")),
            ("priority", E("Priority.", "low", "medium", "high", "urgent")),
            ("urgency", E("Urgency.", "low", "medium", "high")),
            ("impact", E("Impact.", "low", "medium", "high")),
            ("status", S("Status name or id. Resolving may require fields; see describe_fields.", 100)),
            ("group", Nullable(S("Group name or id; null to clear.", 200))),
            ("agent", Nullable(S("\"me\", a user id or an email address; null to unassign.", 254))),
            ("category_id", Nullable(Id("Category; null to clear."))),
            ("sub_category_id", Nullable(Id("Sub-category."))),
            ("item_category_id", Nullable(Id("Item category."))),
            ("tags", Arr("Replace all tags with these.", S("Tag.", 64), 50)),
            ("add_tags", Arr("Tags to add.", S("Tag.", 64), 20)),
            ("remove_tags", Arr("Tags to remove.", S("Tag.", 64), 20)),
            ("custom_fields", Obj("Custom field values by key; null clears a field.")),
            ("is_major_incident", B("Mark or unmark as a major incident.")),
            ("cc_emails", Ticketing.Emails("Replace the CC list. Needs the user's confirmation.", 20)),
            ("requester_email", S("Change the requester to this existing person. Needs the user's confirmation.", 254)),
            ("dry_run", B("Show what would change without saving."))),
        Output(("id", T("string")), ("version", T("string")), ("changes", T("array"))));

    public Task<AgentActionClass> ClassifyAsync(JsonObject arguments, CancellationToken ct) =>
        Task.FromResult(arguments.ContainsKey("cc_emails") || arguments.ContainsKey("requester_email") ? AgentActionClass.External : AgentActionClass.InternalWrite);

    public override async Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var (requester, cc) = await support.RecipientsAsync(ticket, ct);
        var sb = new StringBuilder($"Update {ticket.DisplayId} {Ticketing.Quote(ticket.Subject)}:");
        foreach (var change in await PlanAsync(ticket, arguments, ct))
        {
            sb.Append($"\n{change.Field}: {change.From ?? "(none)"} → {change.To ?? "(none)"}");
        }
        if (StrList(arguments, "cc_emails") is { } newCc)
        {
            var added = newCc.Where(e => !cc.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
            if (added.Count > 0)
            {
                sb.Append($"\n\nNew people who will receive replies: {string.Join(", ", added)}.");
            }
        }
        if (Str(arguments, "requester_email") is { } r)
        {
            sb.Append($"\n\nThe requester changes from {requester} to {r}; replies will go to them.");
        }
        return sb.ToString();
    }

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var expected = Str(arguments, "expected_version") ?? "";
        if (TicketToolSupport.Version(ticket) != expected)
        {
            return Conflict(await support.DetailAsync(ticket, true, false, ct));
        }

        var plan = await PlanAsync(ticket, arguments, ct);
        if (context.DryRun)
        {
            return AgentToolResult.Ok(new JsonObject
            {
                ["dry_run"] = true,
                ["id"] = ticket.Id.ToString(),
                ["display_id"] = ticket.DisplayId,
                ["changes"] = ToNode(plan.Select(p => new { field = p.Field, from = p.From, to = p.To })),
            }, $"{plan.Count} change(s) planned on {ticket.DisplayId}; nothing saved.");
        }

        var patch = await BuildPatchAsync(ticket, arguments, ct);
        byte[] version;
        try
        {
            version = Convert.FromBase64String(expected);
        }
        catch (FormatException)
        {
            return Error(McpErrorCodes.ValidationFailed, "expected_version isn't valid. Use the version from get_ticket.");
        }
        try
        {
            await tickets.UpdateAsync(ticket.Id, patch, version, ct);
        }
        catch (ConflictException)
        {
            return Conflict(await support.DetailAsync(await support.FindAsync(ticket.Id.ToString(), true, ct), true, false, ct));
        }

        var updated = await support.DetailAsync(await support.FindAsync(ticket.Id.ToString(), true, ct), true, false, ct);
        updated["changes"] = ToNode(plan.Select(p => new { field = p.Field, from = p.From, to = p.To }));
        return AgentToolResult.Ok(updated, $"Updated {ticket.DisplayId}: {plan.Count} change(s).");
    }

    private static AgentToolResult Conflict(JsonObject current) =>
        Error(McpErrorCodes.Conflict, "Someone changed this ticket since you read it. Nothing was saved. Review the current state and try again with its version.",
            new JsonObject { ["current_version"] = current["version"]?.DeepClone(), ["current"] = current });

    private async Task<TicketPatch> BuildPatchAsync(Ticket ticket, JsonObject a, CancellationToken ct)
    {
        var ws = ticket.WorkspaceId;
        var patch = new TicketPatch();
        if (Str(a, "subject") is { } subject)
        {
            patch = patch with { Subject = subject };
        }
        if (Has(a, "description"))
        {
            patch = patch with { DescriptionHtml = html.FromPlainText(Str(a, "description")) };
        }
        if (Str(a, "type") is { } type)
        {
            patch = patch with { Type = TicketToolSupport.ParseType(type) };
        }
        if (Str(a, "priority") is { } priority)
        {
            patch = patch with { Priority = TicketToolSupport.ParseEnum<Priority>(priority, "priority") };
        }
        if (Str(a, "urgency") is { } urgency)
        {
            patch = patch with { Urgency = TicketToolSupport.ParseEnum<Level>(urgency, "urgency") };
        }
        if (Str(a, "impact") is { } impact)
        {
            patch = patch with { Impact = TicketToolSupport.ParseEnum<Level>(impact, "impact") };
        }
        if (Str(a, "status") is { } status)
        {
            patch = patch with { StatusId = (await support.ResolveStatusAsync(status, ws, ct)).Id };
        }
        if (Has(a, "group"))
        {
            patch = patch with { GroupId = await support.ResolveGroupAsync(Str(a, "group"), ws, ct) };
        }
        if (Has(a, "agent"))
        {
            patch = patch with { AgentId = await support.ResolveUserAsync(Str(a, "agent"), "agent", ct) };
        }
        if (Has(a, "category_id"))
        {
            patch = patch with { CategoryId = Uuid(a, "category_id") };
        }
        if (Has(a, "sub_category_id"))
        {
            patch = patch with { SubCategoryId = Uuid(a, "sub_category_id") };
        }
        if (Has(a, "item_category_id"))
        {
            patch = patch with { ItemCategoryId = Uuid(a, "item_category_id") };
        }
        var tags = await NewTagsAsync(ticket, a, ct);
        if (tags is not null)
        {
            patch = patch with { Tags = tags };
        }
        if (TicketToolSupport.CustomFields(a, "custom_fields") is { } custom)
        {
            patch = patch with { CustomFields = custom };
        }
        if (Bool(a, "is_major_incident") is { } major)
        {
            patch = patch with { IsMajorIncident = major };
        }
        if (StrList(a, "cc_emails") is { } cc)
        {
            patch = patch with { CcEmails = cc.ToArray() };
        }
        if (Str(a, "requester_email") is { } requester)
        {
            patch = patch with { RequesterId = (await support.ResolveUserAsync(requester, "requester_email", ct))!.Value };
        }
        return patch;
    }

    private async Task<string[]?> NewTagsAsync(Ticket ticket, JsonObject a, CancellationToken ct)
    {
        var replace = StrList(a, "tags");
        var add = StrList(a, "add_tags");
        var remove = StrList(a, "remove_tags");
        if (replace is null && add is null && remove is null)
        {
            return null;
        }
        var current = replace ?? await db.TicketTags.AsNoTracking().Where(t => t.TicketId == ticket.Id).Select(t => t.Tag).ToListAsync(ct);
        return current.Concat(add ?? [])
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => !(remove ?? []).Contains(t, StringComparer.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();
    }

    /// <summary>The changes the arguments ask for, as before/after text (dry runs and confirmations).</summary>
    private async Task<List<FieldChange>> PlanAsync(Ticket ticket, JsonObject a, CancellationToken ct)
    {
        var changes = new List<FieldChange>();
        void Add(string field, string? from, string? to)
        {
            if (!string.Equals(from, to, StringComparison.Ordinal))
            {
                changes.Add(new FieldChange(field, from, to));
            }
        }
        if (Str(a, "subject") is { } subject)
        {
            Add("Subject", ticket.Subject, subject.Trim());
        }
        if (Has(a, "description"))
        {
            Add("Description", "(current text)", "(new text)");
        }
        if (Str(a, "type") is { } type)
        {
            Add("Type", TicketToolSupport.TypeKey(ticket.Type), type);
        }
        if (Str(a, "priority") is { } priority)
        {
            Add("Priority", ticket.Priority.ToString().ToLowerInvariant(), priority);
        }
        if (Str(a, "urgency") is { } urgency)
        {
            Add("Urgency", ticket.Urgency.ToString().ToLowerInvariant(), urgency);
        }
        if (Str(a, "impact") is { } impact)
        {
            Add("Impact", ticket.Impact.ToString().ToLowerInvariant(), impact);
        }
        if (Str(a, "status") is { } status)
        {
            var target = await support.ResolveStatusAsync(status, ticket.WorkspaceId, ct);
            Add("Status", ticket.Status?.Name, target.Name);
        }
        if (Has(a, "group"))
        {
            var id = await support.ResolveGroupAsync(Str(a, "group"), ticket.WorkspaceId, ct);
            Add("Group", await GroupNameAsync(ticket.GroupId, ct), await GroupNameAsync(id, ct));
        }
        if (Has(a, "agent"))
        {
            var id = await support.ResolveUserAsync(Str(a, "agent"), "agent", ct);
            Add("Agent", await UserNameAsync(ticket.AgentId, ct), await UserNameAsync(id, ct));
        }
        if (Has(a, "category_id") || Has(a, "sub_category_id") || Has(a, "item_category_id"))
        {
            Add("Category", "(current)", "(new)");
        }
        if (await NewTagsAsync(ticket, a, ct) is { } tags)
        {
            var current = await db.TicketTags.AsNoTracking().Where(t => t.TicketId == ticket.Id).Select(t => t.Tag).OrderBy(t => t).ToListAsync(ct);
            Add("Tags", string.Join(", ", current), string.Join(", ", tags.Order()));
        }
        if (a["custom_fields"] is JsonObject custom)
        {
            var values = CustomFieldService.Parse(ticket.CustomFieldsJson);
            foreach (var (key, value) in custom)
            {
                Add(key, values[key]?.ToJsonString(), value?.ToJsonString());
            }
        }
        if (Bool(a, "is_major_incident") is { } major)
        {
            Add("Major incident", ticket.IsMajorIncident ? "yes" : "no", major ? "yes" : "no");
        }
        if (StrList(a, "cc_emails") is { } cc)
        {
            Add("CC", string.Join(", ", ticket.CcEmails), string.Join(", ", cc));
        }
        if (Str(a, "requester_email") is { } requester)
        {
            Add("Requester", await UserNameAsync(ticket.RequesterId, ct), requester);
        }
        return changes;
    }

    private async Task<string?> GroupNameAsync(Guid? id, CancellationToken ct) =>
        id is null ? null : await db.AgentGroups.AsNoTracking().Where(g => g.Id == id).Select(g => g.Name).FirstOrDefaultAsync(ct);

    private async Task<string?> UserNameAsync(Guid? id, CancellationToken ct) =>
        id is null ? null : await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
}

public sealed class AssignTicketTool(ITicketService tickets, TicketToolSupport support) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "assign_ticket",
        "Assign ticket",
        "Assigns a ticket to a group, an agent, or both. Pass null to clear one. \"me\" assigns it to you. " +
        "The agent must work in the ticket's workspace; use search_users or search_groups to find ids.",
        McpScopes.TicketsWrite,
        AgentActionClass.InternalWrite,
        Schema(["ticket_id"],
            ("ticket_id", Ticketing.TicketIdArg),
            ("group", Nullable(S("Group name or id; null to clear.", 200))),
            ("agent", Nullable(S("\"me\", a user id or an email address; null to unassign.", 254))),
            ("expected_version", S("Optional 'version' from get_ticket, to avoid overwriting a change.", 100))),
        Output(("id", T("string")), ("group", T("object")), ("agent", T("object"))),
        Idempotent: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        if (!Has(arguments, "group") && !Has(arguments, "agent"))
        {
            return Error(McpErrorCodes.ValidationFailed, "Pass group, agent or both.");
        }
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        byte[]? version = null;
        if (Str(arguments, "expected_version") is { } expected)
        {
            if (TicketToolSupport.Version(ticket) != expected)
            {
                return Error(McpErrorCodes.Conflict, "Someone changed this ticket since you read it. Nothing was saved.",
                    new JsonObject { ["current_version"] = TicketToolSupport.Version(ticket) });
            }
            version = Convert.FromBase64String(expected);
        }
        var patch = new TicketPatch();
        if (Has(arguments, "group"))
        {
            patch = patch with { GroupId = await support.ResolveGroupAsync(Str(arguments, "group"), ticket.WorkspaceId, ct) };
        }
        if (Has(arguments, "agent"))
        {
            patch = patch with { AgentId = await support.ResolveUserAsync(Str(arguments, "agent"), "agent", ct) };
        }
        await tickets.UpdateAsync(ticket.Id, patch, version, ct);
        var detail = await support.DetailAsync(await support.FindAsync(ticket.Id.ToString(), true, ct), true, false, ct);
        return AgentToolResult.Ok(new JsonObject
        {
            ["id"] = ticket.Id.ToString(),
            ["display_id"] = ticket.DisplayId,
            ["version"] = detail["version"]?.DeepClone(),
            ["group"] = detail["group"]?.DeepClone(),
            ["agent"] = detail["agent"]?.DeepClone(),
        }, $"Assigned {ticket.DisplayId}.");
    }
}

public sealed class AddPrivateNoteTool(ITicketService tickets, TicketToolSupport support, IHtmlContent html) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "add_private_note",
        "Add private note",
        "Adds a private note to a ticket. Only agents can read private notes; the requester never sees them. Optionally notifies agents by id.",
        McpScopes.TicketsWrite,
        AgentActionClass.InternalWrite,
        Schema(["ticket_id", "body"],
            ("ticket_id", Ticketing.TicketIdArg),
            ("body", S("The note, as plain text.", 20000)),
            ("notify_agent_ids", Arr("Agents to notify about the note.", Id("Agent id."), 20))),
        Output(("conversation_id", T("string")), ("ticket_id", T("string"))));

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var notify = (StrList(arguments, "notify_agent_ids") ?? []).Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null).OfType<Guid>().ToList();
        var note = await tickets.AddNoteAsync(ticket.Id, new NoteCommand
        {
            BodyHtml = html.FromPlainText(Str(arguments, "body")),
            IsPrivate = true,
            NotifyUserIds = notify,
        }, ct);
        return AgentToolResult.Ok(new JsonObject { ["conversation_id"] = note.Id.ToString(), ["ticket_id"] = ticket.Id.ToString(), ["display_id"] = ticket.DisplayId },
            $"Added a private note to {ticket.DisplayId}.");
    }
}

public sealed class AddPublicNoteTool(ITicketService tickets, TicketToolSupport support, IHtmlContent html) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "add_public_note",
        "Add public note",
        "Adds a public note to a ticket. The requester can read public notes, so the user confirms each one before it's added. " +
        "Use add_private_note for anything only agents should see.",
        McpScopes.TicketsReply,
        AgentActionClass.External,
        Schema(["ticket_id", "body"],
            ("ticket_id", Ticketing.TicketIdArg),
            ("body", S("The note, as plain text.", 20000))),
        Output(("conversation_id", T("string")), ("ticket_id", T("string"))));

    public override async Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var (requester, cc) = await support.RecipientsAsync(ticket, ct);
        return $"Add a public note to {ticket.DisplayId} {Ticketing.Quote(ticket.Subject)}.\n" +
            $"The requester {requester}{(cc.Count > 0 ? $" and the people copied ({string.Join(", ", cc)})" : "")} can read it.\n\n" +
            $"Note:\n{TicketToolSupport.Preview(Str(arguments, "body") ?? "")}";
    }

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var note = await tickets.AddNoteAsync(ticket.Id, new NoteCommand { BodyHtml = html.FromPlainText(Str(arguments, "body")), IsPrivate = false }, ct);
        return AgentToolResult.Ok(new JsonObject { ["conversation_id"] = note.Id.ToString(), ["ticket_id"] = ticket.Id.ToString(), ["display_id"] = ticket.DisplayId },
            $"Added a public note to {ticket.DisplayId}.");
    }
}

public sealed class ReplyToTicketTool(ITicketService tickets, TicketToolSupport support, IHtmlContent html) : AgentToolBase, IAgentToolClassifier
{
    public override AgentToolDefinition Definition { get; } = new(
        "reply_to_ticket",
        "Reply to ticket",
        "Sends a reply on a ticket. For agents the reply goes only to the ticket's requester and the people already copied; the user confirms each reply " +
        "before it is sent, and adding anyone with additional_cc shows them as new recipients. Optionally sets the status in the same step. " +
        "For requesters this adds a message to their own request.",
        McpScopes.TicketsReply,
        AgentActionClass.External,
        Schema(["ticket_id", "body"],
            ("ticket_id", Ticketing.TicketIdArg),
            ("body", S("The message, as plain text.", 20000)),
            ("status", S("Agents only. Set this status after sending, for example Pending.", 100)),
            ("additional_cc", Ticketing.Emails("Agents only. People to copy who aren't on the ticket yet.", 10))),
        Output(("conversation_id", T("string")), ("ticket_id", T("string")), ("recipients", T("object"))),
        RequesterAllowed: true);

    public async Task<AgentActionClass> ClassifyAsync(JsonObject arguments, CancellationToken ct) =>
        // A requester writing on their own request reaches only the service desk: an internal write (Appendix B).
        await support.IsAgentAsync(ct) ? AgentActionClass.External : AgentActionClass.InternalWrite;

    public override async Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var (requester, cc) = await support.RecipientsAsync(ticket, ct);
        var added = (StrList(arguments, "additional_cc") ?? []).Where(e => !cc.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        var sb = new StringBuilder($"Send a reply on {ticket.DisplayId} {Ticketing.Quote(ticket.Subject)}.\nTo: {requester}");
        if (cc.Count > 0)
        {
            sb.Append($"\nCC: {string.Join(", ", cc)}");
        }
        if (added.Count > 0)
        {
            sb.Append($"\nNew recipients, not on the ticket before: {string.Join(", ", added)}");
        }
        if (Str(arguments, "status") is { } status)
        {
            sb.Append($"\nThen set the status to {status}.");
        }
        sb.Append($"\n\nMessage:\n{TicketToolSupport.Preview(Str(arguments, "body") ?? "", 4000)}");
        return sb.ToString();
    }

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var asAgent = await support.IsAgentAsync(ct);
        var body = html.FromPlainText(Str(arguments, "body"));
        if (!asAgent)
        {
            if (Has(arguments, "status") || Has(arguments, "additional_cc"))
            {
                return Error(McpErrorCodes.ValidationFailed, "Only agents can set status or additional_cc.");
            }
            var own = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: false, ct);
            var message = await tickets.AddRequesterMessageAsync(own.Id, new RequesterMessageCommand { BodyHtml = body }, ct);
            return AgentToolResult.Ok(new JsonObject
            {
                ["conversation_id"] = message.Conversation.Id.ToString(),
                ["ticket_id"] = message.Ticket.Id.ToString(),
                ["display_id"] = message.Ticket.DisplayId,
                ["created_follow_up"] = message.CreatedFollowUp,
            }, message.CreatedFollowUp ? $"That request was closed, so {message.Ticket.DisplayId} was opened as a follow-up." : $"Added your message to {own.DisplayId}.");
        }

        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        Guid? statusId = Str(arguments, "status") is { } s ? (await support.ResolveStatusAsync(s, ticket.WorkspaceId, ct)).Id : null;
        var reply = await tickets.ReplyAsync(ticket.Id, new ReplyCommand
        {
            BodyHtml = body,
            Cc = StrList(arguments, "additional_cc"),
            StatusId = statusId,
        }, ct);
        return AgentToolResult.Ok(new JsonObject
        {
            ["conversation_id"] = reply.Id.ToString(),
            ["ticket_id"] = ticket.Id.ToString(),
            ["display_id"] = ticket.DisplayId,
            ["recipients"] = new JsonObject
            {
                ["to"] = new JsonArray(reply.To.Select(x => (JsonNode)x).ToArray()),
                ["cc"] = new JsonArray(reply.Cc.Select(x => (JsonNode)x).ToArray()),
            },
        }, $"Reply sent on {ticket.DisplayId}.");
    }
}

public sealed class ForwardTicketTool(ITicketService tickets, TicketToolSupport support, IHtmlContent html) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "forward_ticket",
        "Forward ticket",
        "Forwards a ticket to people outside it, for example a vendor. Every recipient is new, so the user confirms the recipients and message first. " +
        "The requester doesn't see forwards.",
        McpScopes.TicketsReply,
        AgentActionClass.External,
        Schema(["ticket_id", "to"],
            ("ticket_id", Ticketing.TicketIdArg),
            ("to", Ticketing.Emails("Recipients.", 10)),
            ("cc", Ticketing.Emails("People to copy.", 10)),
            ("body", S("A message to go with the ticket, as plain text.", 20000))),
        Output(("conversation_id", T("string")), ("ticket_id", T("string"))));

    public override async Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var sb = new StringBuilder($"Forward {ticket.DisplayId} {Ticketing.Quote(ticket.Subject)}, with its details, to people outside the ticket.");
        sb.Append($"\nTo: {string.Join(", ", StrList(arguments, "to") ?? [])}");
        if (StrList(arguments, "cc") is { Count: > 0 } cc)
        {
            sb.Append($"\nCC: {string.Join(", ", cc)}");
        }
        sb.Append($"\n\nMessage:\n{TicketToolSupport.Preview(Str(arguments, "body") ?? "(none)")}");
        return sb.ToString();
    }

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var forward = await tickets.ForwardAsync(ticket.Id, new ForwardCommand
        {
            To = StrList(arguments, "to") ?? [],
            Cc = StrList(arguments, "cc"),
            BodyHtml = html.FromPlainText(Str(arguments, "body")),
        }, ct);
        return AgentToolResult.Ok(new JsonObject { ["conversation_id"] = forward.Id.ToString(), ["ticket_id"] = ticket.Id.ToString(), ["display_id"] = ticket.DisplayId },
            $"Forwarded {ticket.DisplayId}.");
    }
}

public sealed class MergeTicketsTool(ITicketService tickets, TicketToolSupport support, ISettingsService settings) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "merge_tickets",
        "Merge tickets",
        "Merges duplicate tickets into a primary ticket: the duplicates close, point to the primary, and their requesters are copied on it. " +
        "This can't be undone, so the user confirms it in Switchboard first. dry_run: true shows what would happen.",
        McpScopes.TicketsWrite,
        AgentActionClass.Destructive,
        Schema(["primary_ticket_id", "ticket_ids"],
            ("primary_ticket_id", Ticketing.TicketIdArg),
            ("ticket_ids", Arr("Tickets to merge into the primary.", S("Ticket id or display id.", 64), McpSettings.DefaultBulkCeiling)),
            ("dry_run", B("Show the plan without merging."))),
        Output(("primary", T("object")), ("merged", T("array"))));

    public override async Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct)
    {
        var (primary, others) = await LoadAsync(arguments, ct);
        return $"Merge {others.Count} ticket(s) into {primary.DisplayId} {Ticketing.Quote(primary.Subject)}:\n" +
            string.Join("\n", others.Select(o => $"{o.DisplayId} {Ticketing.Quote(o.Subject)}")) +
            "\n\nThe merged tickets close and can't be reopened separately. Their requesters and CCs are copied on the primary ticket.";
    }

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var ceiling = await settings.GetAsync(McpSettings.BulkCeiling, McpSettings.DefaultBulkCeiling, ct: ct);
        if ((StrList(arguments, "ticket_ids")?.Count ?? 0) > ceiling)
        {
            return Error(McpErrorCodes.ValidationFailed, $"Merge at most {ceiling} tickets in one call.");
        }
        var (primary, others) = await LoadAsync(arguments, ct);
        var plan = new JsonObject
        {
            ["primary"] = new JsonObject { ["id"] = primary.Id.ToString(), ["display_id"] = primary.DisplayId },
            ["merged"] = new JsonArray(others.Select(o => (JsonNode)new JsonObject { ["id"] = o.Id.ToString(), ["display_id"] = o.DisplayId }).ToArray()),
            ["count"] = others.Count,
        };
        if (context.DryRun)
        {
            plan["dry_run"] = true;
            return AgentToolResult.Ok(plan, $"Would merge {others.Count} ticket(s) into {primary.DisplayId}; nothing changed.");
        }
        await tickets.MergeAsync(primary.Id, others.Select(o => o.Id).ToList(), ct);
        return AgentToolResult.Ok(plan, $"Merged {others.Count} ticket(s) into {primary.DisplayId}.");
    }

    private async Task<(Ticket Primary, List<Ticket> Others)> LoadAsync(JsonObject arguments, CancellationToken ct)
    {
        var primary = await support.FindAsync(Str(arguments, "primary_ticket_id"), asAgent: true, ct);
        var others = new List<Ticket>();
        foreach (var reference in StrList(arguments, "ticket_ids") ?? [])
        {
            var t = await support.FindAsync(reference, asAgent: true, ct);
            if (t.Id != primary.Id && others.All(o => o.Id != t.Id))
            {
                others.Add(t);
            }
        }
        if (others.Count == 0)
        {
            throw new ValidationException("ticket_ids", "Choose at least one other ticket to merge.");
        }
        return (primary, others);
    }
}

public sealed class DeleteTicketTool(ITicketService tickets, TicketToolSupport support, ISettingsService settings) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "delete_ticket",
        "Delete ticket",
        "Moves a ticket to the trash, where it can be restored for a while. It never deletes permanently. The user confirms it in Switchboard first. " +
        "dry_run: true shows what would happen.",
        McpScopes.TicketsDelete,
        AgentActionClass.Destructive,
        Schema(["ticket_id"],
            ("ticket_id", Ticketing.TicketIdArg),
            ("dry_run", B("Show the plan without deleting."))),
        Output(("id", T("string")), ("in_trash", T("boolean"))));

    public override async Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var days = await settings.GetAsync(SettingKeys.TrashRetentionDays, 30, ticket.WorkspaceId, ct);
        return $"Move {ticket.DisplayId} {Ticketing.Quote(ticket.Subject)} to the trash. It can be restored from the trash for {days} days.";
    }

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var result = new JsonObject { ["id"] = ticket.Id.ToString(), ["display_id"] = ticket.DisplayId };
        if (context.DryRun)
        {
            result["dry_run"] = true;
            result["in_trash"] = ticket.DeletedAt is not null;
            return AgentToolResult.Ok(result, $"Would move {ticket.DisplayId} to the trash; nothing changed.");
        }
        await tickets.MoveToTrashAsync(ticket.Id, ct);
        result["in_trash"] = true;
        return AgentToolResult.Ok(result, $"Moved {ticket.DisplayId} to the trash.");
    }
}

public sealed class CreateChildTicketTool(ITicketService tickets, TicketToolSupport support, IHtmlContent html) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "create_child_ticket",
        "Create child ticket",
        "Creates a child ticket under a parent, for the parent's requester and in its workspace, for example to hand part of the work to another group. " +
        "A parent can't be resolved while its children are open.",
        McpScopes.TicketsWrite,
        AgentActionClass.InternalWrite,
        Schema(["parent_ticket_id", "subject"],
            ("parent_ticket_id", Ticketing.TicketIdArg),
            ("subject", S("Summary of the child's work.", 500)),
            ("description", S("Details, as plain text.", 50000)),
            ("type", E("Default: the parent's type.", "incident", "service_request")),
            ("priority", E("Priority.", "low", "medium", "high", "urgent")),
            ("group", S("Group name or id.", 200)),
            ("agent", S("\"me\", a user id or an email address.", 254))),
        Output(("id", T("string")), ("display_id", T("string")), ("parent_id", T("string"))));

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var parent = await support.FindAsync(Str(arguments, "parent_ticket_id"), asAgent: true, ct);
        var child = await tickets.CreateAsync(new CreateTicketCommand
        {
            WorkspaceId = parent.WorkspaceId,
            ParentId = parent.Id,
            RequesterId = parent.RequesterId,
            Subject = Str(arguments, "subject") ?? "",
            DescriptionHtml = html.FromPlainText(Str(arguments, "description")),
            Type = Str(arguments, "type") is { } type ? TicketToolSupport.ParseType(type) : parent.Type,
            Priority = Str(arguments, "priority") is { } p ? TicketToolSupport.ParseEnum<Priority>(p, "priority") : null,
            GroupId = await support.ResolveGroupAsync(Str(arguments, "group"), parent.WorkspaceId, ct),
            AgentId = await support.ResolveUserAsync(Str(arguments, "agent"), "agent", ct),
            SourceKey = TicketSourceKeys.Mcp,
        }, ct);
        return AgentToolResult.Ok(new JsonObject
        {
            ["id"] = child.Id.ToString(),
            ["display_id"] = child.DisplayId,
            ["parent_id"] = parent.Id.ToString(),
            ["version"] = TicketToolSupport.Version(child),
        }, $"Created {child.DisplayId} under {parent.DisplayId}.");
    }
}

public sealed class LinkRecordsTool(ITicketService tickets, TicketToolSupport support) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "link_records",
        "Link records",
        "Links a ticket to another record: a related ticket, or a problem, change, release or asset by id.",
        McpScopes.TicketsWrite,
        AgentActionClass.InternalWrite,
        Schema(["ticket_id", "target_type", "target_id"],
            ("ticket_id", Ticketing.TicketIdArg),
            ("target_type", E("What to link to.", "ticket", "problem", "change", "release", "asset")),
            ("target_id", S("The record's id; for tickets a display id like INC-7 also works.", 64)),
            ("link_type", E("Default related. For changes: change_causing or change_requested.", "related", "problem", "change_causing", "change_requested", "asset"))),
        Output(("link_id", T("string"))),
        Idempotent: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var targetType = Str(arguments, "target_type")!;
        Guid targetId;
        if (targetType == "ticket")
        {
            targetId = (await support.FindAsync(Str(arguments, "target_id"), asAgent: true, ct)).Id;
        }
        else if (!Guid.TryParse(Str(arguments, "target_id"), out targetId))
        {
            return Error(McpErrorCodes.ValidationFailed, "target_id must be the record's id.");
        }
        var linkType = Str(arguments, "link_type") ?? targetType switch
        {
            "problem" => TicketLinkTypes.Problem,
            "asset" => TicketLinkTypes.Asset,
            _ => TicketLinkTypes.Related,
        };
        var link = await tickets.LinkAsync(ticket.Id, targetType, targetId, linkType, ct);
        return AgentToolResult.Ok(new JsonObject { ["link_id"] = link.Id.ToString(), ["ticket_id"] = ticket.Id.ToString() }, $"Linked {ticket.DisplayId}.");
    }
}

public sealed class CreateTaskTool(ITicketService tickets, TicketToolSupport support) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "create_task",
        "Create task",
        "Adds a task to a ticket, optionally assigned to an agent or group and due at a time (ISO 8601).",
        McpScopes.TicketsWrite,
        AgentActionClass.InternalWrite,
        Schema(["ticket_id", "title"],
            ("ticket_id", Ticketing.TicketIdArg),
            ("title", S("What needs doing.", 300)),
            ("description", S("Details.", 5000)),
            ("assignee", S("\"me\", a user id or an email address.", 254)),
            ("group", S("Group name or id.", 200)),
            ("due_at", new JsonObject { ["type"] = "string", ["format"] = "date-time", ["description"] = "When it's due, ISO 8601." })),
        Output(("task_id", T("string"))));

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var task = await tickets.AddTaskAsync(ticket.Id, new TaskCommand
        {
            Title = Str(arguments, "title") ?? "",
            Description = Str(arguments, "description"),
            AssigneeId = await support.ResolveUserAsync(Str(arguments, "assignee"), "assignee", ct),
            GroupId = await support.ResolveGroupAsync(Str(arguments, "group"), ticket.WorkspaceId, ct),
            DueAt = ParseTime(Str(arguments, "due_at")),
        }, ct);
        return AgentToolResult.Ok(new JsonObject { ["task_id"] = task.Id.ToString(), ["ticket_id"] = ticket.Id.ToString() }, $"Added a task to {ticket.DisplayId}.");
    }

    internal static DateTimeOffset? ParseTime(string? value) =>
        value is null ? null : DateTimeOffset.TryParse(value, out var t) ? t.ToUniversalTime() : throw new ValidationException("due_at", "Use an ISO 8601 date and time.");
}

public sealed class UpdateTaskTool(ITicketService tickets, TicketToolSupport support) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "update_task",
        "Update task",
        "Changes a task: title, description, status (open, in_progress, completed), assignee or due time. Get task ids from list_tasks.",
        McpScopes.TicketsWrite,
        AgentActionClass.InternalWrite,
        Schema(["task_id"],
            ("task_id", Id("Task id from list_tasks.")),
            ("title", S("New title.", 300)),
            ("description", Nullable(S("New description; null clears it.", 5000))),
            ("status", E("New status.", "open", "in_progress", "completed")),
            ("assignee", Nullable(S("\"me\", a user id or an email address; null to unassign.", 254))),
            ("due_at", Nullable(new JsonObject { ["type"] = "string", ["format"] = "date-time", ["description"] = "When it's due, ISO 8601; null clears it." }))),
        Output(("task_id", T("string")), ("status", T("string"))),
        Idempotent: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var patch = new TaskPatch();
        if (Str(arguments, "title") is { } title)
        {
            patch = patch with { Title = title };
        }
        if (Has(arguments, "description"))
        {
            patch = patch with { Description = Str(arguments, "description") };
        }
        if (Str(arguments, "status") is { } status)
        {
            patch = patch with { Status = status switch { "in_progress" => TicketTaskStatus.InProgress, "completed" => TicketTaskStatus.Completed, _ => TicketTaskStatus.Open } };
        }
        if (Has(arguments, "assignee"))
        {
            patch = patch with { AssigneeId = await support.ResolveUserAsync(Str(arguments, "assignee"), "assignee", ct) };
        }
        if (Has(arguments, "due_at"))
        {
            patch = patch with { DueAt = CreateTaskTool.ParseTime(Str(arguments, "due_at")) };
        }
        var task = await tickets.UpdateTaskAsync(Uuid(arguments, "task_id")!.Value, patch, ct);
        return AgentToolResult.Ok(new JsonObject { ["task_id"] = task.Id.ToString(), ["status"] = task.Status.ToString().ToLowerInvariant() }, "Updated the task.");
    }
}
