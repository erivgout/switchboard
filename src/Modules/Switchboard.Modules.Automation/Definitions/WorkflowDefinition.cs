using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switchboard.Modules.Automation.Definitions;

/// <summary>When (trigger), If (conditions), Then (actions). Stored as JSON on workflows and versions.</summary>
public sealed class WorkflowDefinition
{
    public TriggerDefinition Trigger { get; set; } = new();
    public ConditionSet Conditions { get; set; } = new();
    public List<ActionDefinition> Actions { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };

    public static WorkflowDefinition Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new WorkflowDefinition();
        }
        var parsed = JsonSerializer.Deserialize<WorkflowDefinition>(json, Json) ?? new WorkflowDefinition();
        parsed.Normalize();
        return parsed;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public WorkflowDefinition Clone() => Parse(ToJson());

    /// <summary>Fills nulls left by hand-written or imported JSON.</summary>
    public void Normalize()
    {
        Trigger ??= new TriggerDefinition();
        Trigger.Changes ??= [];
        Conditions ??= new ConditionSet();
        Conditions.Groups ??= [];
        foreach (var g in Conditions.Groups)
        {
            g.Rules ??= [];
        }
        Conditions.Groups.RemoveAll(g => g is null);
        Actions ??= [];
        Actions.RemoveAll(a => a is null);
        foreach (var a in Actions)
        {
            a.Params ??= [];
            if (string.IsNullOrWhiteSpace(a.Id))
            {
                a.Id = ActionDefinition.NewId();
            }
        }
    }
}

public sealed class TriggerDefinition
{
    /// <summary>See <see cref="TriggerKeys"/>.</summary>
    public string Event { get; set; } = TriggerKeys.TicketCreated;

    /// <summary>For <see cref="TriggerKeys.TicketUpdated"/>: run only when one of these fields changes (empty: any change).</summary>
    public List<FieldChangeFilter> Changes { get; set; } = [];

    /// <summary>For <see cref="TriggerKeys.Conversation"/>: <c>any</c>, <c>requester</c>, <c>agent_reply</c> or <c>note</c>.</summary>
    public string? Conversation { get; set; }

    /// <summary>For <see cref="TriggerKeys.Time"/>: <c>created</c>, <c>updated</c>, <c>status</c> or <c>due</c>.</summary>
    public string? Clock { get; set; }

    /// <summary>For <see cref="TriggerKeys.Time"/>: hours since the clock started, or hours before the due time.</summary>
    public double? Hours { get; set; }
}

/// <summary>"Field changes", optionally "to a value".</summary>
public sealed class FieldChangeFilter
{
    public string Field { get; set; } = "";
    public string? To { get; set; }
}

public sealed class ConditionSet
{
    /// <summary><c>all</c> or <c>any</c> across groups.</summary>
    public string Match { get; set; } = "all";
    public List<ConditionGroup> Groups { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Groups.All(g => g.Rules.Count == 0);
}

public sealed class ConditionGroup
{
    /// <summary><c>all</c> or <c>any</c> across rules.</summary>
    public string Match { get; set; } = "all";
    public List<Condition> Rules { get; set; } = [];
}

public sealed class Condition
{
    /// <summary>Field key from <see cref="FieldCatalog"/>, e.g. <c>ticket.priority</c> or <c>ticket.cf.asset_tag</c>.</summary>
    public string Field { get; set; } = "";
    /// <summary>Operator key from <see cref="Operators"/>.</summary>
    public string Op { get; set; } = Operators.Is;
    public string? Value { get; set; }
}

public sealed class ActionDefinition
{
    /// <summary>Stable id within the definition (keeps secrets attached when actions are reordered).</summary>
    public string Id { get; set; } = NewId();
    /// <summary>See <see cref="ActionTypes"/>.</summary>
    public string Type { get; set; } = "";
    public Dictionary<string, string?> Params { get; set; } = [];

    public string? Get(string key) => Params.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public static string NewId() => Guid.NewGuid().ToString("N")[..10];
}

public static class TriggerKeys
{
    public const string TicketCreated = "ticket.created";
    public const string TicketUpdated = "ticket.updated";
    public const string Conversation = "ticket.conversation";
    public const string StatusChanged = "ticket.status";
    public const string Assigned = "ticket.assigned";
    /// <summary>Supervisor rule evaluated by the automation clock.</summary>
    public const string Time = "ticket.time";
    /// <summary>Raised by the service-levels module when it is installed.</summary>
    public const string SlaBreached = "sla.breached";
    public const string SlaEscalated = "sla.escalated";

    public static readonly IReadOnlyList<string> All = [TicketCreated, TicketUpdated, Conversation, StatusChanged, Assigned, Time, SlaBreached, SlaEscalated];
}

public static class TimeClocks
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string InStatus = "status";
    public const string Due = "due";

    public static readonly IReadOnlyList<string> All = [Created, Updated, InStatus, Due];
}

public static class Operators
{
    public const string Is = "is";
    public const string IsNot = "is_not";
    public const string Contains = "contains";
    public const string NotContains = "not_contains";
    public const string StartsWith = "starts_with";
    public const string EndsWith = "ends_with";
    public const string Empty = "empty";
    public const string NotEmpty = "not_empty";
    public const string AtLeast = "gte";
    public const string AtMost = "lte";
    public const string GreaterThan = "gt";
    public const string LessThan = "lt";
    public const string Has = "has";
    public const string NotHas = "not_has";

    public static bool NeedsValue(string op) => op is not (Empty or NotEmpty);

    public static string Label(string op) => op switch
    {
        Is => "is",
        IsNot => "is not",
        Contains => "contains",
        NotContains => "doesn't contain",
        StartsWith => "starts with",
        EndsWith => "ends with",
        Empty => "is empty",
        NotEmpty => "is not empty",
        AtLeast => "is at least",
        AtMost => "is at most",
        GreaterThan => "is more than",
        LessThan => "is less than",
        Has => "includes",
        NotHas => "doesn't include",
        _ => op,
    };
}

public static class ActionTypes
{
    public const string SetStatus = "set_status";
    public const string SetPriority = "set_priority";
    public const string SetType = "set_type";
    public const string SetCategory = "set_category";
    public const string SetGroup = "set_group";
    public const string SetAgent = "set_agent";
    public const string AssignRoundRobin = "assign_round_robin";
    public const string AssignByLoad = "assign_by_load";
    public const string AddTags = "add_tags";
    public const string RemoveTags = "remove_tags";
    public const string SetCustomField = "set_custom_field";
    public const string AddNote = "add_note";
    public const string SendEmail = "send_email";
    public const string CreateTask = "create_task";
    public const string WebRequest = "web_request";

    /// <summary>Actions with effects outside the database; they run after the ticket changes commit.</summary>
    public static bool IsExternal(string type) => type is SendEmail or WebRequest;

    /// <summary>Actions agents may put in scenarios (they run as the agent, through the ticket service).</summary>
    public static readonly IReadOnlySet<string> ScenarioAllowed = new HashSet<string>(StringComparer.Ordinal)
    {
        SetStatus, SetPriority, SetType, SetCategory, SetGroup, SetAgent, AssignRoundRobin, AssignByLoad,
        AddTags, RemoveTags, SetCustomField, AddNote, CreateTask,
    };
}
