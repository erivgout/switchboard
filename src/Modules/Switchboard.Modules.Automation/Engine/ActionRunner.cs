using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Automation.Assignment;
using Switchboard.Modules.Automation.Definitions;

namespace Switchboard.Modules.Automation.Engine;

/// <summary>An email ready to send (stored on the run step until sent).</summary>
public sealed record EmailPayload(List<string> To, string Subject, string Html, string Text, string Workflow, string TicketDisplayId);

/// <summary>A web request ready to send. <see cref="AuthValue"/> is encrypted.</summary>
public sealed record WebRequestPayload(string Method, string Url, string? AuthHeader, string? AuthValue, string? Body, int TimeoutSeconds);

/// <summary>What one action will do, resolved against the current ticket. Building it never writes.</summary>
public sealed class PlannedStep
{
    public required ActionDefinition Action { get; init; }
    /// <summary>Plain sentence, e.g. "Set priority to Urgent (was Medium)".</summary>
    public required string Title { get; set; }
    public bool External => ActionTypes.IsExternal(Action.Type);
    /// <summary>Nothing to do (value already set, nobody to email). Still logged.</summary>
    public string? NoChange { get; set; }
    public TicketPatch? Patch { get; set; }
    public NoteCommand? Note { get; set; }
    public TaskCommand? Task { get; set; }
    public EmailPayload? Email { get; set; }
    public WebRequestPayload? Web { get; set; }
    /// <summary>Round-robin or load-based assignment to claim when applied.</summary>
    public (Guid GroupId, AssignmentMode Mode, bool SetGroup)? Assign { get; set; }
    /// <summary>Displayable inputs (secrets masked).</summary>
    public Dictionary<string, object?> Input { get; } = [];
}

/// <summary>
/// Plans and applies actions. Every ticket change goes through <see cref="ITicketService"/> as the
/// current actor (the workflow's automation actor, or the agent for scenarios).
/// </summary>
public sealed class ActionRunner(
    SwitchboardDbContext db,
    ITicketService tickets,
    AssignmentService assignment,
    IHtmlContent html,
    TimeProvider time)
{
    public async Task<PlannedStep> PlanAsync(ActionDefinition action, TicketFacts facts, BuilderCatalog catalog, string workflowName, CancellationToken ct)
    {
        var meta = catalog.Action(action.Type) ?? throw new AutomationActionException("This action type isn't available any more.");
        var step = new PlannedStep { Action = action, Title = meta.Label };
        string Label(string param) => meta.Params.FirstOrDefault(p => p.Key == param)?.Options?
            .FirstOrDefault(o => string.Equals(o.Value, action.Get(param), StringComparison.OrdinalIgnoreCase))?.Label ?? "a deleted choice";
        var placeholders = new Dictionary<string, string>(facts.Placeholders, StringComparer.OrdinalIgnoreCase) { ["workflow.name"] = workflowName };

        switch (action.Type)
        {
            case ActionTypes.SetStatus:
            {
                var id = RequireGuid(action, "statusId", "status");
                step.Title = $"Set status to {Label("statusId")}";
                if (id == facts.StatusId)
                {
                    step.NoChange = "The status is already set.";
                }
                step.Patch = new TicketPatch { StatusId = id };
                step.Input["status"] = Label("statusId");
                break;
            }
            case ActionTypes.SetPriority:
            {
                var priority = Enum.TryParse<Priority>(action.Get("priority"), out var p) ? p : throw new AutomationActionException("Choose a priority.");
                step.Title = $"Set priority to {priority}";
                if (facts.Get("ticket.priority") as string == priority.ToString())
                {
                    step.NoChange = "The priority is already set.";
                }
                step.Patch = new TicketPatch { Priority = priority };
                step.Input["priority"] = priority.ToString();
                break;
            }
            case ActionTypes.SetType:
            {
                var type = Enum.TryParse<TicketType>(action.Get("type"), out var t) ? t : throw new AutomationActionException("Choose a type.");
                step.Title = $"Change type to {Label("type")}";
                if (type == facts.Type)
                {
                    step.NoChange = "The type is already set.";
                }
                step.Patch = new TicketPatch { Type = type };
                step.Input["type"] = Label("type");
                break;
            }
            case ActionTypes.SetCategory:
            {
                var id = RequireGuid(action, "categoryId", "category");
                var chain = await CategoryChainAsync(id, facts.WorkspaceId, ct);
                step.Title = $"Set category to {Label("categoryId")}";
                if (chain[0] == facts.CategoryId && chain[1] == facts.SubCategoryId && chain[2] == facts.ItemCategoryId)
                {
                    step.NoChange = "The category is already set.";
                }
                step.Patch = new TicketPatch { CategoryId = chain[0], SubCategoryId = chain[1], ItemCategoryId = chain[2] };
                step.Input["category"] = Label("categoryId");
                break;
            }
            case ActionTypes.SetGroup:
            {
                var id = RequireGuid(action, "groupId", "group");
                step.Title = $"Assign to {Label("groupId")}";
                if (id == facts.GroupId)
                {
                    step.NoChange = "The ticket is already in that group.";
                }
                step.Patch = new TicketPatch { GroupId = id };
                step.Input["group"] = Label("groupId");
                break;
            }
            case ActionTypes.SetAgent:
            {
                var raw = action.Get("agentId");
                Guid? id = raw is null or "none" ? null : Guid.TryParse(raw, out var g) ? g : throw new AutomationActionException("Choose an agent.");
                step.Title = id is null ? "Unassign the agent" : $"Assign to {Label("agentId")}";
                if (id == facts.AgentId)
                {
                    step.NoChange = id is null ? "Nobody is assigned." : "That agent is already assigned.";
                }
                step.Patch = new TicketPatch { AgentId = id };
                step.Input["agent"] = id is null ? "Nobody" : Label("agentId");
                break;
            }
            case ActionTypes.AssignRoundRobin or ActionTypes.AssignByLoad:
            {
                var mode = action.Type == ActionTypes.AssignByLoad ? AssignmentMode.LoadBased : AssignmentMode.RoundRobin;
                var explicitGroup = action.Get("groupId") is { } raw ? Guid.TryParse(raw, out var g) ? g : throw new AutomationActionException("Choose a group.") : (Guid?)null;
                var groupId = explicitGroup ?? facts.GroupId;
                var how = mode == AssignmentMode.LoadBased ? "least busy agent" : "round robin";
                if (groupId is not { } gid)
                {
                    step.Title = $"Assign by {how}";
                    step.NoChange = "The ticket isn't in a group, so there's nobody to choose from.";
                    break;
                }
                var choice = await assignment.ChooseAsync(gid, mode, ct);
                var groupName = await db.AgentGroups.AsNoTracking().Where(x => x.Id == gid).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "the group";
                step.Title = mode == AssignmentMode.LoadBased ? $"Assign the least busy agent in {groupName}" : $"Assign by round robin in {groupName}";
                step.Input["group"] = groupName;
                step.Input["choice"] = choice.AgentName ?? "Nobody";
                step.Input["reason"] = choice.Reason;
                if (choice.AgentId is null)
                {
                    step.NoChange = choice.Reason;
                    break;
                }
                step.Assign = (gid, mode, explicitGroup is not null && explicitGroup != facts.GroupId);
                break;
            }
            case ActionTypes.AddTags or ActionTypes.RemoveTags:
            {
                var change = Describer.SplitTags(action.Get("tags"));
                var adding = action.Type == ActionTypes.AddTags;
                var next = adding ? facts.Tags.Union(change, StringComparer.Ordinal).ToList() : facts.Tags.Except(change, StringComparer.Ordinal).ToList();
                step.Title = $"{(adding ? "Add" : "Remove")} {(change.Count == 1 ? "tag" : "tags")} {string.Join(", ", change)}";
                if (next.Order(StringComparer.Ordinal).SequenceEqual(facts.Tags.Order(StringComparer.Ordinal)))
                {
                    step.NoChange = adding ? "The ticket already has those tags." : "The ticket doesn't have those tags.";
                }
                step.Patch = new TicketPatch { Tags = next.ToArray() };
                step.Input["tags"] = change;
                break;
            }
            case ActionTypes.SetCustomField:
            {
                var key = action.Get("key") ?? throw new AutomationActionException("Choose a field.");
                var def = await db.CustomFieldDefinitions.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.EntityType == "ticket" && d.Key == key && d.IsActive && (d.WorkspaceId == null || d.WorkspaceId == facts.WorkspaceId), ct)
                    ?? throw new AutomationActionException("That custom field no longer exists.");
                var value = action.Get("value");
                step.Title = value is null ? $"Clear {def.Label}" : $"Set {def.Label} to {value}";
                step.Patch = new TicketPatch { CustomFields = new Dictionary<string, JsonElement?> { [key] = value is null ? null : ToJson(def.FieldType, value) } };
                step.Input["field"] = def.Label;
                step.Input["value"] = value;
                break;
            }
            case ActionTypes.AddNote:
            {
                var text = PlaceholderRenderer.Render(action.Get("body"), placeholders, PlaceholderEncoding.Text);
                step.Title = "Add a private note";
                step.Note = new NoteCommand { BodyHtml = html.FromPlainText(text), IsPrivate = true };
                step.Input["note"] = text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    step.NoChange = "The note would be empty.";
                }
                break;
            }
            case ActionTypes.CreateTask:
            {
                var title = PlaceholderRenderer.Render(action.Get("title"), placeholders, PlaceholderEncoding.SingleLine).Trim();
                var description = PlaceholderRenderer.Render(action.Get("description"), placeholders, PlaceholderEncoding.Text);
                Guid? assignee = action.Get("assigneeId") is { } a && Guid.TryParse(a, out var ag) ? ag : null;
                DateTimeOffset? due = action.Get("dueInHours") is { } h && double.TryParse(h, NumberStyles.Number, CultureInfo.InvariantCulture, out var hours)
                    ? time.GetUtcNow().AddHours(hours) : null;
                step.Title = $"Create the task \"{Truncate(title, 80)}\"";
                step.Task = new TaskCommand { Title = title.Length > 300 ? title[..300] : title, Description = description.Length == 0 ? null : description, AssigneeId = assignee, DueAt = due };
                step.Input["title"] = title;
                step.Input["assignee"] = assignee is null ? null : Label("assigneeId");
                step.Input["due"] = due?.ToString("O");
                break;
            }
            case ActionTypes.SendEmail:
            {
                var (to, who) = await RecipientsAsync(action, facts, ct);
                var subject = PlaceholderRenderer.Render(action.Get("subject"), placeholders, PlaceholderEncoding.SingleLine).Trim();
                var text = PlaceholderRenderer.Render(action.Get("body"), placeholders, PlaceholderEncoding.Text);
                step.Title = $"Email {who}";
                step.Email = new EmailPayload(to, subject.Length > 300 ? subject[..300] : subject, html.FromPlainText(text), text, workflowName, facts.DisplayId);
                step.Input["to"] = to;
                step.Input["subject"] = step.Email.Subject;
                step.Input["body"] = text;
                if (to.Count == 0)
                {
                    step.NoChange = $"There's no email address for {who}.";
                }
                break;
            }
            case ActionTypes.WebRequest:
            {
                var method = (action.Get("method") ?? "POST").ToUpperInvariant();
                var url = PlaceholderRenderer.Render(action.Get("url"), placeholders, PlaceholderEncoding.Url);
                var body = method is "POST" or "PUT" or "PATCH" ? PlaceholderRenderer.Render(action.Get("body"), placeholders, PlaceholderEncoding.Json) : null;
                var timeout = int.TryParse(action.Get("timeoutSeconds"), out var s) ? Math.Clamp(s, 1, 30) : 10;
                step.Title = $"Send a {method} request to {(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "an invalid address")}";
                step.Web = new WebRequestPayload(method, url, action.Get("authHeader"), action.Get("authValue"), string.IsNullOrWhiteSpace(body) ? null : body, timeout);
                step.Input["method"] = method;
                step.Input["url"] = url;
                if (action.Get("authHeader") is { } header)
                {
                    step.Input["headers"] = new Dictionary<string, string> { [header] = string.IsNullOrEmpty(action.Get("authValue")) ? "(not set)" : "••••••" };
                }
                step.Input["body"] = step.Web.Body;
                step.Input["timeoutSeconds"] = timeout;
                break;
            }
            default:
                throw new AutomationActionException("This action type isn't available any more.");
        }
        return step;
    }

    /// <summary>Applies a database step through the ticket service. External steps are never applied here.</summary>
    public async Task<object?> ApplyAsync(PlannedStep step, TicketFacts facts, CancellationToken ct)
    {
        if (step.External)
        {
            throw new InvalidOperationException("External steps are sent after commit.");
        }
        if (step.NoChange is not null)
        {
            return new { skipped = step.NoChange };
        }

        if (step.Assign is { } assign)
        {
            var choice = await assignment.ClaimAsync(assign.GroupId, assign.Mode, ct);
            if (choice.AgentId is not { } agentId)
            {
                return new { skipped = choice.Reason };
            }
            var patch = assign.SetGroup ? new TicketPatch { GroupId = assign.GroupId, AgentId = agentId } : new TicketPatch { AgentId = agentId };
            await tickets.UpdateAsync(facts.TicketId, patch, ct: ct);
            return new { assigned = choice.AgentName, reason = choice.Reason };
        }
        if (step.Patch is { } p)
        {
            var ticket = await tickets.UpdateAsync(facts.TicketId, p, ct: ct);
            return new { updated = true, status = ticket.StatusId };
        }
        if (step.Note is { } note)
        {
            var conversation = await tickets.AddNoteAsync(facts.TicketId, note, ct);
            return new { noteId = conversation.Id };
        }
        if (step.Task is { } task)
        {
            var created = await tickets.AddTaskAsync(facts.TicketId, task, ct);
            return new { taskId = created.Id };
        }
        return null;
    }

    /// <summary>Updates the in-memory facts as if the step ran, so later steps in a test run see it.</summary>
    public static void Simulate(PlannedStep step, TicketFacts facts, BuilderCatalog catalog)
    {
        if (step.NoChange is not null || step.External)
        {
            return;
        }
        if (step.Patch is { } p)
        {
            if (p.StatusId.HasValue)
            {
                facts.StatusId = p.StatusId.Value;
                facts.Values["ticket.status"] = p.StatusId.Value.ToString();
                facts.Placeholders["ticket.status"] = catalog.OptionLabel("ticket.status", p.StatusId.Value.ToString());
            }
            if (p.Priority.HasValue)
            {
                facts.Values["ticket.priority"] = p.Priority.Value.ToString();
                facts.Placeholders["ticket.priority"] = p.Priority.Value.ToString();
            }
            if (p.Type.HasValue)
            {
                facts.Type = p.Type.Value;
                facts.Values["ticket.type"] = p.Type.Value.ToString();
            }
            if (p.GroupId.HasValue)
            {
                facts.GroupId = p.GroupId.Value;
                facts.Values["ticket.group"] = p.GroupId.Value?.ToString();
                facts.Placeholders["ticket.group"] = catalog.OptionLabel("ticket.group", p.GroupId.Value?.ToString());
            }
            if (p.AgentId.HasValue)
            {
                facts.AgentId = p.AgentId.Value;
                facts.Values["ticket.agent"] = p.AgentId.Value?.ToString();
                facts.Placeholders["agent.name"] = p.AgentId.Value is null ? "" : catalog.OptionLabel("ticket.agent", p.AgentId.Value.ToString());
            }
            if (p.Tags.HasValue)
            {
                facts.Tags = p.Tags.Value.ToList();
                facts.Values["ticket.tags"] = facts.Tags;
                facts.Placeholders["ticket.tags"] = string.Join(", ", facts.Tags);
            }
            if (p.CategoryId.HasValue)
            {
                facts.CategoryId = p.CategoryId.Value;
                facts.SubCategoryId = p.SubCategoryId.HasValue ? p.SubCategoryId.Value : null;
                facts.ItemCategoryId = p.ItemCategoryId.HasValue ? p.ItemCategoryId.Value : null;
                facts.Values["ticket.category"] = new[] { facts.CategoryId, facts.SubCategoryId, facts.ItemCategoryId }
                    .Where(x => x is not null).Select(x => x!.Value.ToString()).ToList();
            }
        }
        if (step.Assign is { } a && step.Input.GetValueOrDefault("choice") is string name)
        {
            if (a.SetGroup)
            {
                facts.GroupId = a.GroupId;
                facts.Values["ticket.group"] = a.GroupId.ToString();
            }
            facts.Placeholders["agent.name"] = name;
        }
    }

    private async Task<(List<string> To, string Who)> RecipientsAsync(ActionDefinition action, TicketFacts facts, CancellationToken ct)
    {
        switch (action.Get("recipient"))
        {
            case "agent":
            {
                if (facts.AgentId is not { } agentId)
                {
                    return ([], "the assigned agent");
                }
                var agent = await db.Users.AsNoTracking().Where(u => u.Id == agentId && u.IsActive).Select(u => new { u.Email, u.DisplayName }).FirstOrDefaultAsync(ct);
                return (agent?.Email is { } e ? [e] : [], agent?.DisplayName ?? "the assigned agent");
            }
            case "group":
            {
                var groupId = action.Get("groupId") is { } raw && Guid.TryParse(raw, out var g) ? g : throw new AutomationActionException("Choose the group to email.");
                var group = await db.AgentGroups.AsNoTracking().Where(x => x.Id == groupId).Select(x => x.Name).FirstOrDefaultAsync(ct)
                    ?? throw new AutomationActionException("That group no longer exists.");
                var emails = await db.AgentGroupMembers.AsNoTracking()
                    .Where(m => m.GroupId == groupId && m.User!.IsActive && m.User.Email != null)
                    .Select(m => m.User!.Email!).Distinct().OrderBy(e => e).Take(100).ToListAsync(ct);
                return (emails, $"everyone in {group}");
            }
            case "address":
            {
                var address = action.Get("address");
                return (DefinitionValidator.IsEmail(address) ? [address!.Trim()] : [], address ?? "an address");
            }
            default:
            {
                var requester = await db.Users.AsNoTracking().Where(u => u.Id == facts.RequesterId)
                    .Select(u => new { u.Email, u.IsForgotten, u.IsActive }).FirstOrDefaultAsync(ct);
                return (requester is { IsForgotten: false, IsActive: true, Email: { } e } ? [e] : [], "the requester");
            }
        }
    }

    private async Task<Guid?[]> CategoryChainAsync(Guid id, Guid workspaceId, CancellationToken ct)
    {
        var chain = new List<Guid>();
        Guid? current = id;
        while (current is { } c && chain.Count < 3)
        {
            var row = await db.TicketCategories.AsNoTracking().Where(x => x.Id == c && x.WorkspaceId == workspaceId)
                .Select(x => new { x.Id, x.ParentId }).FirstOrDefaultAsync(ct)
                ?? throw new AutomationActionException("That category no longer exists.");
            chain.Insert(0, row.Id);
            current = row.ParentId;
        }
        return [chain.ElementAtOrDefault(0), chain.Count > 1 ? chain[1] : null, chain.Count > 2 ? chain[2] : null];
    }

    private static Guid RequireGuid(ActionDefinition action, string key, string what) =>
        Guid.TryParse(action.Get(key), out var id) ? id : throw new AutomationActionException($"Choose a {what}.");

    private static JsonElement ToJson(CustomFieldType type, string value) => type switch
    {
        CustomFieldType.Number when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => JsonSerializer.SerializeToElement(n),
        CustomFieldType.Decimal when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => JsonSerializer.SerializeToElement(d),
        CustomFieldType.Checkbox when bool.TryParse(value, out var b) => JsonSerializer.SerializeToElement(b),
        CustomFieldType.MultiSelect or CustomFieldType.DependentDropdown =>
            JsonSerializer.SerializeToElement(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        _ => JsonSerializer.SerializeToElement(value),
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

/// <summary>An action couldn't run for a reason the admin can fix (missing group, deleted field).</summary>
public sealed class AutomationActionException(string message) : Exception(message);
