using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Mcp.Security;
using Switchboard.Modules.Mcp.Server;

namespace Switchboard.Modules.Mcp.Tools;

public sealed class SearchTicketsTool(ITicketSearch search, TicketToolSupport support) : AgentToolBase
{
    private static readonly string[] Fields = ["type", "subject", "status", "state", "priority", "group", "agent", "requester", "created_at", "updated_at", "due_by", "waiting_on_agent", "is_major_incident"];

    /// <summary>Filter fields a requester may use: nothing that reveals internal routing.</summary>
    private static readonly HashSet<string> RequesterFields = new(StringComparer.OrdinalIgnoreCase) { "state", "type", "created", "updated", "number", "requester" };

    public override AgentToolDefinition Definition { get; } = new(
        "search_tickets",
        "Search tickets",
        "Searches tickets you can see with Switchboard's filter language and returns one page of summaries. " +
        "Examples: 'state:open agent:me', 'is:lit (agent:me OR (agent:none group:mine))', 'priority>=high created>-7d \"vpn\"', 'requester:dana@example.com'. " +
        "Fields: status, state, priority, urgency, impact, type, source, group, agent, requester, department, category, tag, workspace, number, created, updated, due, resolved, is " +
        "(lit, unassigned, mine, overdue, unresolved, major, deleted, merged). Requesters can filter by state, type, created, updated and number. " +
        "Results are paged: pass next_cursor back as cursor for more. Use get_ticket for details.",
        McpScopes.TicketsRead,
        AgentActionClass.Read,
        Schema([],
            ("query", S("Filter query; empty lists recent tickets.", 2000)),
            ("sort", E("Sort order; '-' means descending. 'lamp' puts tickets waiting on an agent first.", "-updated", "updated", "-created", "created", "due", "-due", "-priority", "priority", "-number", "number", "lamp")),
            ("page_size", I("Tickets per page, default 20.", 1, 100)),
            ("cursor", S("next_cursor from the previous page.", 100)),
            ("fields", Arr("Only return these fields (id and display_id always come back).", E("Field name.", Fields), 20))),
        Output(("tickets", T("array")), ("total", T("integer")), ("next_cursor", T("string"))),
        RequesterAllowed: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var asAgent = await support.IsAgentAsync(ct);
        var query = Str(arguments, "query");
        if (!asAgent && TicketQueryLanguage.Parse(query) is { } node)
        {
            CheckRequesterQuery(node);
        }

        var pageSize = Int(arguments, "page_size") ?? 20;
        var page = DecodeCursor(Str(arguments, "cursor"));
        var result = await search.SearchAsync(new TicketSearchRequest
        {
            Query = query,
            Sort = Str(arguments, "sort"),
            Page = page,
            PageSize = pageSize,
            AsRequester = !asAgent,
        }, ct);

        var showNames = !asAgent && result.Items.Count > 0 && await support.ShowAgentNamesToRequestersAsync(result.Items[0].WorkspaceId, ct);
        var projection = StrList(arguments, "fields")?.ToHashSet(StringComparer.Ordinal);
        var items = result.Items.Select(t =>
        {
            var summary = TicketToolSupport.Summary(t, asAgent, showNames);
            if (projection is { Count: > 0 })
            {
                foreach (var key in summary.Select(p => p.Key).ToList())
                {
                    if (key is not ("id" or "display_id") && !projection.Contains(key))
                    {
                        summary.Remove(key);
                    }
                }
            }
            return (JsonNode)summary;
        }).ToArray();

        var structured = new JsonObject
        {
            ["tickets"] = new JsonArray(items),
            ["total"] = result.Total,
            ["next_cursor"] = result.HasMore ? EncodeCursor(page + 1) : null,
        };
        return AgentToolResult.Ok(structured, $"{items.Length} of {result.Total} matching ticket(s).{(result.HasMore ? " More available with next_cursor." : "")}");
    }

    private static void CheckRequesterQuery(TicketQueryLanguage.Node node)
    {
        switch (node)
        {
            case TicketQueryLanguage.And and:
                and.Items.ToList().ForEach(CheckRequesterQuery);
                break;
            case TicketQueryLanguage.Or or:
                or.Items.ToList().ForEach(CheckRequesterQuery);
                break;
            case TicketQueryLanguage.Not not:
                CheckRequesterQuery(not.Inner);
                break;
            case TicketQueryLanguage.FieldTerm term when !RequesterFields.Contains(term.Field)
                || (term.Field.Equals("requester", StringComparison.OrdinalIgnoreCase) && !term.Value.Equals("me", StringComparison.OrdinalIgnoreCase)):
                throw new ValidationException("query", "You can search your requests by state, type, created, updated, number and text.");
        }
    }

    internal static string EncodeCursor(int page) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"p:{page}"));

    internal static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 1;
        }
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (raw.StartsWith("p:", StringComparison.Ordinal) && int.TryParse(raw[2..], out var page) && page is > 0 and < 100_000)
            {
                return page;
            }
        }
        catch (FormatException)
        {
        }
        throw new ValidationException("cursor", "That cursor isn't valid. Start again without one.");
    }
}

public sealed class GetTicketTool(TicketToolSupport support) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "get_ticket",
        "Get ticket",
        "Returns one ticket you can see: subject, description, status, priority, people, custom fields, tags, links and attachments, plus 'version' " +
        "to pass as expected_version when updating. detail 'full' returns a longer description. Use list_conversations for replies and notes.",
        McpScopes.TicketsRead,
        AgentActionClass.Read,
        Schema(["ticket_id"],
            ("ticket_id", S("Ticket id or display id, for example INC-1042.", 64)),
            ("detail", E("'summary' (default) or 'full'.", "summary", "full"))),
        Output(("id", T("string")), ("display_id", T("string")), ("version", T("string")), ("subject", T("object")), ("description", T("object"))),
        RequesterAllowed: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var asAgent = await support.IsAgentAsync(ct);
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent, ct);
        var detail = await support.DetailAsync(ticket, asAgent, Str(arguments, "detail") == "full", ct);
        return AgentToolResult.Ok(detail, $"{ticket.DisplayId}, {detail["state"]}.");
    }
}

public sealed class ListConversationsTool(SwitchboardDbContext db, TicketToolSupport support) : AgentToolBase
{
    private const int MaxBodyChars = 4000;

    public override AgentToolDefinition Definition { get; } = new(
        "list_conversations",
        "List conversations",
        "Returns a page of a ticket's conversation: requester messages, replies, forwards and notes (private notes only for agents), oldest first by default. " +
        "Long messages are cut at 4000 characters and marked truncated. Attachments are listed as links. Pass next_cursor back as cursor for more.",
        McpScopes.TicketsRead,
        AgentActionClass.Read,
        Schema(["ticket_id"],
            ("ticket_id", S("Ticket id or display id, for example INC-1042.", 64)),
            ("order", E("'oldest' (default) or 'newest' first.", "oldest", "newest")),
            ("page_size", I("Messages per page, default 20.", 1, 50)),
            ("cursor", S("next_cursor from the previous page.", 100)),
            ("include_private", B("Agents only: include private notes and forwards (default true)."))),
        Output(("ticket_id", T("string")), ("conversations", T("array")), ("next_cursor", T("string"))),
        RequesterAllowed: true);

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var asAgent = await support.IsAgentAsync(ct);
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent, ct);
        var includePrivate = asAgent && (Bool(arguments, "include_private") ?? true);
        var pageSize = Int(arguments, "page_size") ?? 20;
        var page = SearchTicketsTool.DecodeCursor(Str(arguments, "cursor"));

        var query = db.TicketConversations.AsNoTracking().Where(c => c.TicketId == ticket.Id && c.DeletedAt == null);
        if (!includePrivate)
        {
            // Requesters only ever see what the portal shows (SR-AUTHZ-7, INV-TKT-7).
            query = query.Where(c => !c.IsPrivate && c.Kind != ConversationKind.Forward);
        }
        var total = await query.CountAsync(ct);
        query = Str(arguments, "order") == "newest" ? query.OrderByDescending(c => c.CreatedAt) : query.OrderBy(c => c.CreatedAt);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new { c.Id, c.Kind, c.IsPrivate, c.AuthorId, AuthorName = c.Author != null ? c.Author.DisplayName : null, c.FromEmail, c.To, c.Cc, c.BodyHtml, c.CreatedAt, c.Channel })
            .ToListAsync(ct);

        var ids = rows.Select(r => (Guid?)r.Id).ToList();
        var files = await db.Attachments.AsNoTracking()
            .Where(a => a.OwnerType == AttachmentOwners.Conversation && ids.Contains(a.OwnerId) && !a.IsInline)
            .Select(a => new { a.OwnerId, a.Id, a.FileName, a.ContentType, a.Size })
            .ToListAsync(ct);
        var showNames = asAgent || await support.ShowAgentNamesToRequestersAsync(ticket.WorkspaceId, ct);

        var items = rows.Select(r =>
        {
            var authorIsStaff = r.Kind is ConversationKind.AgentReply or ConversationKind.Note or ConversationKind.Forward;
            var author = r.Kind == ConversationKind.System ? "Switchboard" : !showNames && authorIsStaff ? "Support team" : r.AuthorName ?? r.FromEmail ?? "Unknown";
            var o = new JsonObject
            {
                ["id"] = r.Id.ToString(),
                ["kind"] = r.Kind switch
                {
                    ConversationKind.RequesterMessage => "requester_message",
                    ConversationKind.AgentReply => "reply",
                    ConversationKind.Forward => "forward",
                    ConversationKind.Note => r.IsPrivate ? "private_note" : "public_note",
                    _ => "system",
                },
                ["author"] = TicketToolSupport.Untrusted(author, "user_profile"),
                ["created_at"] = r.CreatedAt.ToString("O"),
                ["body"] = TicketToolSupport.UntrustedHtml(r.BodyHtml, "conversation", author, MaxBodyChars),
                ["attachments"] = new JsonArray(files.Where(f => f.OwnerId == r.Id).Select(f => (JsonNode)TicketToolSupport.Attachment(f.Id, f.FileName, f.ContentType, f.Size)).ToArray()),
            };
            if (asAgent)
            {
                o["is_private"] = r.IsPrivate;
                o["channel"] = r.Channel.ToString().ToLowerInvariant();
                if (r.To.Count > 0)
                {
                    o["to"] = new JsonArray(r.To.Select(x => (JsonNode)x).ToArray());
                }
                if (r.Cc.Count > 0)
                {
                    o["cc"] = new JsonArray(r.Cc.Select(x => (JsonNode)x).ToArray());
                }
            }
            return (JsonNode)o;
        }).ToArray();

        var hasMore = page * pageSize < total;
        var structured = new JsonObject
        {
            ["ticket_id"] = ticket.Id.ToString(),
            ["display_id"] = ticket.DisplayId,
            ["conversations"] = new JsonArray(items),
            ["total"] = total,
            ["next_cursor"] = hasMore ? SearchTicketsTool.EncodeCursor(page + 1) : null,
        };
        return AgentToolResult.Ok(structured, $"{items.Length} of {total} message(s) on {ticket.DisplayId}.");
    }
}

public sealed class ListTasksTool(SwitchboardDbContext db, TicketToolSupport support) : AgentToolBase
{
    public override AgentToolDefinition Definition { get; } = new(
        "list_tasks",
        "List tasks",
        "Lists the tasks on a ticket with their status, assignee and due date.",
        McpScopes.TicketsRead,
        AgentActionClass.Read,
        Schema(["ticket_id"], ("ticket_id", S("Ticket id or display id, for example INC-1042.", 64))),
        Output(("tasks", T("array"))));

    public override async Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct)
    {
        var ticket = await support.FindAsync(Str(arguments, "ticket_id"), asAgent: true, ct);
        var tasks = await db.TicketTasks.AsNoTracking().Where(t => t.TicketId == ticket.Id).OrderBy(t => t.Order)
            .Select(t => new { t.Id, t.Title, t.Description, t.Status, t.AssigneeId, Assignee = t.Assignee != null ? t.Assignee.DisplayName : null, t.DueAt, t.CompletedAt })
            .ToListAsync(ct);
        var items = tasks.Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.Id.ToString(),
            ["title"] = TicketToolSupport.Untrusted(t.Title, "task"),
            ["description"] = t.Description is null ? null : TicketToolSupport.Untrusted(t.Description, "task"),
            ["status"] = t.Status switch { TicketTaskStatus.InProgress => "in_progress", TicketTaskStatus.Completed => "completed", _ => "open" },
            ["assignee"] = t.AssigneeId is null ? null : TicketToolSupport.Person(t.AssigneeId, t.Assignee),
            ["due_at"] = t.DueAt?.ToString("O"),
            ["completed_at"] = t.CompletedAt?.ToString("O"),
        }).ToArray();
        return AgentToolResult.Ok(new JsonObject { ["ticket_id"] = ticket.Id.ToString(), ["tasks"] = new JsonArray(items) }, $"{items.Length} task(s) on {ticket.DisplayId}.");
    }
}
