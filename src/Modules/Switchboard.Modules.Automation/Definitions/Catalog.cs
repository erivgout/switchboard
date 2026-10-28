using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Modules.Automation.Engine;

namespace Switchboard.Modules.Automation.Definitions;

[JsonConverter(typeof(JsonStringEnumConverter<FieldKind>))]
public enum FieldKind
{
    Text,
    Choice,
    Ordinal,
    Bool,
    Tags,
    Number,
}

public sealed record CatalogOption(string Value, string Label);

/// <param name="ChangeName">Name the ticket service uses for this field in <c>TicketUpdated</c> changes.</param>
/// <param name="Negated">For yes/no fields phrased as a statement ("Requester is a VIP"), the "no" phrasing.</param>
public sealed record CatalogField(
    string Key,
    string Label,
    string Group,
    FieldKind Kind,
    IReadOnlyList<CatalogOption> Options,
    bool Nullable = false,
    string? ChangeName = null,
    string? Hint = null,
    string? Negated = null);

/// <param name="Kind"><c>select</c>, <c>text</c>, <c>textarea</c>, <c>number</c>, <c>secret</c>.</param>
public sealed record CatalogParam(
    string Key,
    string Label,
    string Kind,
    IReadOnlyList<CatalogOption>? Options = null,
    bool Required = false,
    string? Help = null,
    string? ShowWhen = null,
    IReadOnlyList<string>? ShowWhenIn = null,
    bool Placeholders = false,
    string? Default = null);

public sealed record CatalogAction(string Type, string Label, string Group, IReadOnlyList<CatalogParam> Params, bool External = false);

public sealed record CatalogTrigger(string Key, string Label, string Hint);

/// <summary>Everything the builder needs to show choices, and the describer needs to name them.</summary>
public sealed class BuilderCatalog
{
    public required IReadOnlyList<CatalogTrigger> Triggers { get; init; }
    public required IReadOnlyList<CatalogOption> Clocks { get; init; }
    public required IReadOnlyList<CatalogOption> ConversationKinds { get; init; }
    public required IReadOnlyList<CatalogField> Fields { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<CatalogOption>> Operators { get; init; }
    public required IReadOnlyList<CatalogAction> Actions { get; init; }
    public required IReadOnlyList<CatalogOption> Placeholders { get; init; }

    public CatalogField? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);

    public CatalogAction? Action(string type) => Actions.FirstOrDefault(a => a.Type == type);

    public string OptionLabel(string fieldKey, string? value)
    {
        if (value is null)
        {
            return "";
        }
        var field = Field(fieldKey);
        return field?.Options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase))?.Label ?? value;
    }

    public static IReadOnlyList<CatalogOption> OperatorsFor(CatalogField field)
    {
        var ops = field.Kind switch
        {
            FieldKind.Text => [Definitions.Operators.Contains, Definitions.Operators.NotContains, Definitions.Operators.Is, Definitions.Operators.IsNot,
                Definitions.Operators.StartsWith, Definitions.Operators.EndsWith, Definitions.Operators.Empty, Definitions.Operators.NotEmpty],
            FieldKind.Choice => field.Nullable
                ? [Definitions.Operators.Is, Definitions.Operators.IsNot, Definitions.Operators.Empty, Definitions.Operators.NotEmpty]
                : new[] { Definitions.Operators.Is, Definitions.Operators.IsNot },
            FieldKind.Ordinal => [Definitions.Operators.Is, Definitions.Operators.IsNot, Definitions.Operators.AtLeast, Definitions.Operators.AtMost],
            FieldKind.Bool => [Definitions.Operators.Is],
            FieldKind.Tags => [Definitions.Operators.Has, Definitions.Operators.NotHas, Definitions.Operators.Empty, Definitions.Operators.NotEmpty],
            FieldKind.Number => [Definitions.Operators.Is, Definitions.Operators.IsNot, Definitions.Operators.GreaterThan, Definitions.Operators.LessThan,
                Definitions.Operators.AtLeast, Definitions.Operators.AtMost, Definitions.Operators.Empty, Definitions.Operators.NotEmpty],
            _ => Array.Empty<string>(),
        };
        return ops.Select(o => new CatalogOption(o, Definitions.Operators.Label(o))).ToList();
    }
}

/// <summary>Builds the <see cref="BuilderCatalog"/> for a workspace from its statuses, groups, agents and fields.</summary>
public sealed class CatalogLoader(SwitchboardDbContext db, AutomationTriggerRegistry triggers)
{
    private readonly Dictionary<Guid, BuilderCatalog> _cache = [];

    public static readonly IReadOnlyList<CatalogOption> Priorities =
        [new("Low", "Low"), new("Medium", "Medium"), new("High", "High"), new("Urgent", "Urgent")];

    public static readonly IReadOnlyList<CatalogOption> Levels =
        [new("Low", "Low"), new("Medium", "Medium"), new("High", "High")];

    public static readonly IReadOnlyList<CatalogOption> Types =
        [new(nameof(TicketType.Incident), "Incident"), new(nameof(TicketType.ServiceRequest), "Service request")];

    public static readonly IReadOnlyList<CatalogOption> YesNo = [new("true", "Yes"), new("false", "No")];

    public static readonly IReadOnlyList<CatalogOption> Placeholders =
    [
        new("ticket.id", "Ticket ID, like INC-1042"),
        new("ticket.subject", "Subject"),
        new("ticket.description", "Description (plain text)"),
        new("ticket.status", "Status"),
        new("ticket.priority", "Priority"),
        new("ticket.type", "Type"),
        new("ticket.category", "Category"),
        new("ticket.group", "Group"),
        new("ticket.tags", "Tags"),
        new("ticket.source", "Source"),
        new("ticket.url", "Link to the ticket"),
        new("ticket.created_at", "Created at (UTC)"),
        new("ticket.due_by", "Due by (UTC)"),
        new("requester.name", "Requester name"),
        new("requester.email", "Requester email"),
        new("requester.department", "Requester department"),
        new("agent.name", "Agent name"),
        new("agent.email", "Agent email"),
        new("workflow.name", "Workflow name"),
        new("ticket.cf.KEY", "Custom field (replace KEY)"),
    ];

    public async Task<BuilderCatalog> LoadAsync(Guid workspaceId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(workspaceId, out var cached))
        {
            return cached;
        }

        // Ids are formatted in memory: SQL Server would upper-case them.
        var statuses = (await db.TicketStatuses.AsNoTracking().Where(s => s.WorkspaceId == workspaceId).OrderBy(s => s.Order)
            .Select(s => new { s.Id, s.Name }).ToListAsync(ct)).Select(s => new CatalogOption(s.Id.ToString(), s.Name)).ToList();
        var sources = await db.TicketSources.AsNoTracking().OrderBy(s => s.Order)
            .Select(s => new CatalogOption(s.Key, s.Name)).ToListAsync(ct);
        var categoryRows = await db.TicketCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId)
            .Select(c => new { c.Id, c.ParentId, c.Name, c.Level, c.Order }).ToListAsync(ct);
        var groups = (await db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == workspaceId).OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name }).ToListAsync(ct)).Select(g => new CatalogOption(g.Id.ToString(), g.Name)).ToList();
        var agents = (await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.IsOrgAdmin || u.Memberships.Any(m => m.WorkspaceId == workspaceId)))
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName }).ToListAsync(ct)).Select(u => new CatalogOption(u.Id.ToString(), u.DisplayName)).ToList();
        var departments = (await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct))
            .Select(d => new CatalogOption(d.Id.ToString(), d.Name)).ToList();
        var locations = (await db.Locations.AsNoTracking().OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }).ToListAsync(ct))
            .Select(l => new CatalogOption(l.Id.ToString(), l.Name)).ToList();
        var customFields = await db.CustomFieldDefinitions.AsNoTracking()
            .Where(d => d.EntityType == "ticket" && d.IsActive && (d.WorkspaceId == null || d.WorkspaceId == workspaceId) && d.FieldType != CustomFieldType.Content)
            .OrderBy(d => d.Order).ThenBy(d => d.Label)
            .ToListAsync(ct);

        // Category options show the full path; a category condition matches any level.
        var byId = categoryRows.ToDictionary(c => c.Id);
        string Path(Guid id)
        {
            var parts = new List<string>();
            for (var cur = byId.GetValueOrDefault(id); cur is not null; cur = cur.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
            {
                parts.Insert(0, cur.Name);
                if (parts.Count > 3)
                {
                    break;
                }
            }
            return string.Join(" / ", parts);
        }
        var categories = categoryRows
            .Select(c => new CatalogOption(c.Id.ToString(), Path(c.Id)))
            .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var fields = new List<CatalogField>
        {
            new("ticket.subject", "Subject", "Ticket", FieldKind.Text, [], ChangeName: "Subject"),
            new("ticket.description", "Description", "Ticket", FieldKind.Text, [], ChangeName: "Description"),
            new("ticket.type", "Type", "Ticket", FieldKind.Choice, Types, ChangeName: "Type"),
            new("ticket.status", "Status", "Ticket", FieldKind.Choice, statuses, ChangeName: "Status"),
            new("ticket.status_category", "Status stage", "Ticket", FieldKind.Choice,
                [new("Open", "Open"), new("Pending", "Pending"), new("Resolved", "Resolved"), new("Closed", "Closed")],
                Hint: "Every status belongs to one of these stages."),
            new("ticket.priority", "Priority", "Ticket", FieldKind.Ordinal, Priorities, ChangeName: "Priority"),
            new("ticket.urgency", "Urgency", "Ticket", FieldKind.Ordinal, Levels, ChangeName: "Urgency"),
            new("ticket.impact", "Impact", "Ticket", FieldKind.Ordinal, Levels, ChangeName: "Impact"),
            new("ticket.source", "Source", "Ticket", FieldKind.Choice, sources, ChangeName: "Source"),
            new("ticket.category", "Category", "Ticket", FieldKind.Choice, categories, Nullable: true, ChangeName: "Category",
                Hint: "Matches the category at any level."),
            new("ticket.group", "Group", "Ticket", FieldKind.Choice, groups, Nullable: true, ChangeName: "Group"),
            new("ticket.agent", "Agent", "Ticket", FieldKind.Choice, agents, Nullable: true, ChangeName: "Agent"),
            new("ticket.department", "Department", "Ticket", FieldKind.Choice, departments, Nullable: true, ChangeName: "Department"),
            new("ticket.tags", "Tags", "Ticket", FieldKind.Tags, [], ChangeName: "Tags"),
            new("ticket.major_incident", "Ticket is a major incident", "Ticket", FieldKind.Bool, YesNo, ChangeName: "Major incident",
                Negated: "Ticket isn't a major incident"),
        };

        foreach (var cf in customFields)
        {
            var (kind, options) = cf.FieldType switch
            {
                CustomFieldType.Number or CustomFieldType.Decimal => (FieldKind.Number, (IReadOnlyList<CatalogOption>)[]),
                CustomFieldType.Checkbox => (FieldKind.Bool, YesNo),
                CustomFieldType.Dropdown => (FieldKind.Choice, DropdownOptions(cf.OptionsJson)),
                CustomFieldType.MultiSelect => (FieldKind.Tags, DropdownOptions(cf.OptionsJson)),
                _ => (FieldKind.Text, []),
            };
            fields.Add(new CatalogField($"ticket.cf.{cf.Key}", cf.Label, "Custom fields", kind, options, Nullable: true, ChangeName: cf.Key));
        }

        fields.AddRange(
        [
            new("requester.name", "Requester name", "Requester", FieldKind.Text, []),
            new("requester.email", "Requester email", "Requester", FieldKind.Text, [], Hint: "Use \"ends with\" to match a domain."),
            new("requester.department", "Requester department", "Requester", FieldKind.Choice, departments, Nullable: true),
            new("requester.location", "Requester location", "Requester", FieldKind.Choice, locations, Nullable: true),
            new("requester.job_title", "Requester job title", "Requester", FieldKind.Text, []),
            new("requester.vip", "Requester is a VIP", "Requester", FieldKind.Bool, YesNo, Negated: "Requester isn't a VIP"),
            new("conversation.kind", "Message type", "New message", FieldKind.Choice,
                [new("requester", "Requester message"), new("agent_reply", "Agent reply"), new("note", "Note"), new("forward", "Forward")],
                Hint: "Only set when the trigger is a new reply or note."),
            new("conversation.private", "Message is private", "New message", FieldKind.Bool, YesNo, Negated: "Message is public"),
            new("conversation.body", "Message text", "New message", FieldKind.Text, []),
        ]);

        var agentOrNobody = new List<CatalogOption> { new("none", "Nobody (unassign)") };
        agentOrNobody.AddRange(agents);
        var groupOrCurrent = new List<CatalogOption> { new("", "The ticket's current group") };
        groupOrCurrent.AddRange(groups);
        var customFieldOptions = customFields.Select(c => new CatalogOption(c.Key, c.Label)).ToList();

        var actions = new List<CatalogAction>
        {
            new(ActionTypes.SetStatus, "Set status", "Update the ticket", [new("statusId", "Status", "select", statuses, Required: true)]),
            new(ActionTypes.SetPriority, "Set priority", "Update the ticket", [new("priority", "Priority", "select", Priorities, Required: true)]),
            new(ActionTypes.SetType, "Set type", "Update the ticket", [new("type", "Type", "select", Types, Required: true)]),
            new(ActionTypes.SetCategory, "Set category", "Update the ticket", [new("categoryId", "Category", "select", categories, Required: true)]),
            new(ActionTypes.AddTags, "Add tags", "Update the ticket", [new("tags", "Tags", "text", Required: true, Help: "Separate tags with commas.")]),
            new(ActionTypes.RemoveTags, "Remove tags", "Update the ticket", [new("tags", "Tags", "text", Required: true, Help: "Separate tags with commas.")]),
            new(ActionTypes.SetCustomField, "Set a custom field", "Update the ticket",
            [
                new("key", "Field", "select", customFieldOptions, Required: true),
                new("value", "Value", "text", Help: "Leave empty to clear the field. Use true or false for checkboxes."),
            ]),
            new(ActionTypes.SetGroup, "Assign to a group", "Assign", [new("groupId", "Group", "select", groups, Required: true)]),
            new(ActionTypes.SetAgent, "Assign to an agent", "Assign", [new("agentId", "Agent", "select", agentOrNobody, Required: true)]),
            new(ActionTypes.AssignRoundRobin, "Assign by round robin", "Assign",
                [new("groupId", "Group", "select", groupOrCurrent, Help: "Takes turns among available agents in the group.")]),
            new(ActionTypes.AssignByLoad, "Assign to the least busy agent", "Assign",
                [new("groupId", "Group", "select", groupOrCurrent, Help: "Picks the available agent with the fewest unresolved tickets.")]),
            new(ActionTypes.AddNote, "Add a private note", "Communicate",
                [new("body", "Note", "textarea", Required: true, Placeholders: true)]),
            new(ActionTypes.SendEmail, "Send an email", "Communicate",
            [
                new("recipient", "To", "select",
                    [new("requester", "The requester"), new("agent", "The assigned agent"), new("group", "Everyone in a group"), new("address", "An email address")],
                    Required: true, Default: "requester"),
                new("groupId", "Group", "select", groups, ShowWhen: "recipient", ShowWhenIn: ["group"]),
                new("address", "Email address", "text", ShowWhen: "recipient", ShowWhenIn: ["address"]),
                new("subject", "Subject", "text", Required: true, Placeholders: true),
                new("body", "Message", "textarea", Required: true, Placeholders: true),
            ], External: true),
            new(ActionTypes.CreateTask, "Create a task", "Communicate",
            [
                new("title", "Task", "text", Required: true, Placeholders: true),
                new("description", "Details", "textarea", Placeholders: true),
                new("assigneeId", "Assign to", "select", [new CatalogOption("", "Nobody yet"), .. agents]),
                new("dueInHours", "Due in (hours)", "number", Help: "Leave empty for no due date."),
            ]),
            new(ActionTypes.WebRequest, "Send a web request", "Connect",
            [
                new("method", "Method", "select", [new("POST", "POST"), new("PUT", "PUT"), new("PATCH", "PATCH"), new("GET", "GET"), new("DELETE", "DELETE")],
                    Required: true, Default: "POST"),
                new("url", "URL", "text", Required: true, Placeholders: true, Help: "Public https:// addresses only. Internal and private addresses are blocked."),
                new("authHeader", "Auth header name", "text", Help: "For example Authorization or X-Api-Key."),
                new("authValue", "Auth header value", "secret", Help: "Stored encrypted. Never shown again or included in exports."),
                new("body", "JSON body", "textarea", Placeholders: true, ShowWhen: "method", ShowWhenIn: ["POST", "PUT", "PATCH"],
                    Help: "Placeholders are inserted as JSON text, so put them inside quotes."),
                new("timeoutSeconds", "Timeout (seconds)", "number", Default: "10"),
            ], External: true),
        };

        var catalog = new BuilderCatalog
        {
            Triggers = triggers.Available,
            Clocks =
            [
                new(TimeClocks.Created, "since the ticket was created"),
                new(TimeClocks.Updated, "since the ticket was last updated"),
                new(TimeClocks.InStatus, "in its current status"),
                new(TimeClocks.Due, "before the ticket is due"),
            ],
            ConversationKinds =
            [
                new("any", "Any reply or note"),
                new("requester", "The requester replies"),
                new("agent_reply", "An agent replies"),
                new("note", "A note is added"),
            ],
            Fields = fields,
            Operators = fields.Select(f => f.Kind).Distinct()
                .ToDictionary(k => k.ToString(), k => BuilderCatalog.OperatorsFor(new CatalogField("", "", "", k, [], Nullable: true))),
            Actions = actions,
            Placeholders = Placeholders,
        };
        _cache[workspaceId] = catalog;
        return catalog;
    }

    private static IReadOnlyList<CatalogOption> DropdownOptions(string optionsJson)
    {
        try
        {
            return JsonNode.Parse(optionsJson) is JsonArray arr
                ? arr.Select(n => n is JsonObject o ? o["value"]?.GetValue<string>() : n?.GetValue<string>())
                    .OfType<string>().Select(v => new CatalogOption(v, v)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }
}
