using System.Globalization;
using System.Net;
using System.Text;

namespace Switchboard.Modules.Automation.Definitions;

/// <summary>
/// Turns a definition into one plain-language sentence: "When a ticket is created, if priority is
/// Urgent, then assign it to Network and email the requester." HTML output wraps chosen values in
/// <c>&lt;strong&gt;</c> and encodes everything.
/// </summary>
public static class Describer
{
    public static string Sentence(WorkflowDefinition def, BuilderCatalog catalog, bool html = true)
    {
        var sb = new StringBuilder();
        sb.Append(Trigger(def.Trigger, catalog, html));
        var conditions = Conditions(def.Conditions, catalog, html);
        if (conditions.Length > 0)
        {
            sb.Append(", if ").Append(conditions);
        }
        sb.Append(", then ");
        sb.Append(def.Actions.Count == 0 ? "do nothing yet" : Join(def.Actions.Select(a => Action(a, catalog, html)).ToList(), "and"));
        sb.Append('.');
        return sb.ToString();
    }

    public static string Trigger(TriggerDefinition trigger, BuilderCatalog catalog, bool html = true)
    {
        string V(string s) => Value(s, html);
        switch (trigger.Event)
        {
            case TriggerKeys.TicketCreated:
                return "When a ticket is created";
            case TriggerKeys.TicketUpdated:
                if (trigger.Changes.Count == 0)
                {
                    return "When a ticket is updated";
                }
                var parts = trigger.Changes.Select((c, i) => ChangeClause(c, catalog, first: i == 0, html)).ToList();
                return "When " + Join(parts, "or");
            case TriggerKeys.Conversation:
                return trigger.Conversation switch
                {
                    "requester" => "When the requester replies",
                    "agent_reply" => "When an agent replies",
                    "note" => "When a note is added",
                    _ => "When a reply or note is added",
                };
            case TriggerKeys.StatusChanged:
                return "When a ticket's status changes";
            case TriggerKeys.Assigned:
                return "When a ticket is assigned or reassigned";
            case TriggerKeys.Time:
                var hours = V(Hours(trigger.Hours ?? 0));
                return trigger.Clock switch
                {
                    TimeClocks.Created => $"When {hours} have passed since a ticket was created",
                    TimeClocks.Updated => $"When a ticket hasn't been updated for {hours}",
                    TimeClocks.InStatus => $"When a ticket has been in the same status for {hours}",
                    TimeClocks.Due => $"When a ticket is due within {hours}",
                    _ => "When time passes",
                };
            default:
                var known = catalog.Triggers.FirstOrDefault(t => t.Key == trigger.Event);
                return known is null ? "When an unknown event happens" : "When " + Lower(known.Label);
        }
    }

    /// <summary>"a ticket's priority changes to Urgent", "it becomes a major incident", "its Approved is checked".</summary>
    private static string ChangeClause(FieldChangeFilter change, BuilderCatalog catalog, bool first, bool html)
    {
        var field = catalog.Field(change.Field);
        var possessive = first ? "a ticket's " : "its ";
        if (field?.Kind == FieldKind.Bool && !string.IsNullOrWhiteSpace(change.To))
        {
            var yes = change.To == "true";
            if (field.Key == "ticket.major_incident")
            {
                return (first ? "a ticket " : "it ") + (yes ? "becomes a major incident" : "stops being a major incident");
            }
            return possessive + Enc(field.Label, html) + (yes ? " is checked" : " is unchecked");
        }
        var label = Enc(Lower(field?.Label ?? change.Field), html);
        return string.IsNullOrWhiteSpace(change.To)
            ? $"{possessive}{label} changes"
            : field?.Kind == FieldKind.Tags
                ? $"{(first ? "a ticket" : "it")} gets the tag {Quote(change.To, html)}"
                : $"{possessive}{label} changes to {Value(catalog.OptionLabel(change.Field, change.To), html)}";
    }

    public static string Conditions(ConditionSet set, BuilderCatalog catalog, bool html = true)
    {
        var groups = set.Groups.Where(g => g.Rules.Count > 0).ToList();
        if (groups.Count == 0)
        {
            return "";
        }
        var outer = ConditionWord(set.Match);
        var rendered = groups.Select(g =>
        {
            var inner = Join(g.Rules.Select(r => Rule(r, catalog, html)).ToList(), ConditionWord(g.Match));
            return groups.Count > 1 && g.Rules.Count > 1 ? $"({inner})" : inner;
        }).ToList();
        return Join(rendered, outer);
    }

    public static string Rule(Condition rule, BuilderCatalog catalog, bool html = true)
    {
        var field = catalog.Field(rule.Field);
        var label = Enc(Lower(field?.Label ?? rule.Field), html);
        var op = Operators.Label(rule.Op);
        if (field?.Kind == FieldKind.Bool)
        {
            var yes = rule.Value == "true";
            return field.Negated is { } negated
                ? Enc(Lower(yes ? field.Label : negated), html)
                : $"{label} {(yes ? "is checked" : "isn't checked")}";
        }
        if (!Operators.NeedsValue(rule.Op))
        {
            return $"{label} {op}";
        }
        var value = field?.Kind switch
        {
            FieldKind.Text or FieldKind.Tags => Quote(rule.Value ?? "", html),
            _ => Value(field is null ? rule.Value ?? "" : catalog.OptionLabel(rule.Field, rule.Value), html),
        };
        return $"{label} {op} {value}";
    }

    public static string Action(ActionDefinition action, BuilderCatalog catalog, bool html = true)
    {
        string V(string? s) => Value(s ?? "", html);
        string Opt(string param) => OptionLabel(catalog, action.Type, param, action.Get(param));
        switch (action.Type)
        {
            case ActionTypes.SetStatus:
                return $"set the status to {V(Opt("statusId"))}";
            case ActionTypes.SetPriority:
                return $"set the priority to {V(action.Get("priority"))}";
            case ActionTypes.SetType:
                return $"change the type to {V(Opt("type"))}";
            case ActionTypes.SetCategory:
                return $"set the category to {V(Opt("categoryId"))}";
            case ActionTypes.SetGroup:
                return $"assign it to {V(Opt("groupId"))}";
            case ActionTypes.SetAgent:
                return action.Get("agentId") is null or "none" ? "unassign the agent" : $"assign it to {V(Opt("agentId"))}";
            case ActionTypes.AssignRoundRobin:
                return action.Get("groupId") is null
                    ? "assign an agent in its group by round robin"
                    : $"assign an agent in {V(Opt("groupId"))} by round robin";
            case ActionTypes.AssignByLoad:
                return action.Get("groupId") is null
                    ? "assign the least busy agent in its group"
                    : $"assign the least busy agent in {V(Opt("groupId"))}";
            case ActionTypes.AddTags:
                return $"add {TagList(action.Get("tags"), html)}";
            case ActionTypes.RemoveTags:
                return $"remove {TagList(action.Get("tags"), html)}";
            case ActionTypes.SetCustomField:
                var fieldLabel = Opt("key");
                return action.Get("value") is { } value
                    ? $"set {Enc(fieldLabel, html)} to {Quote(value, html)}"
                    : $"clear {Enc(fieldLabel, html)}";
            case ActionTypes.AddNote:
                return "add a private note";
            case ActionTypes.SendEmail:
                return action.Get("recipient") switch
                {
                    "agent" => "email the assigned agent",
                    "group" => $"email everyone in {V(Opt("groupId"))}",
                    "address" => $"email {V(action.Get("address"))}",
                    _ => "email the requester",
                };
            case ActionTypes.CreateTask:
                return $"create the task {Quote(action.Get("title") ?? "", html)}";
            case ActionTypes.WebRequest:
                var host = Uri.TryCreate(action.Get("url") ?? "", UriKind.Absolute, out var uri) ? uri.Host : "an address";
                return $"send a {Enc(action.Get("method") ?? "POST", html)} request to {V(host)}";
            default:
                return "do something this version doesn't know";
        }
    }

    public static string Hours(double hours) =>
        hours == 1 ? "1 hour" : $"{hours.ToString("0.##", CultureInfo.InvariantCulture)} hours";

    private static string OptionLabel(BuilderCatalog catalog, string type, string param, string? value)
    {
        if (value is null)
        {
            return "nothing";
        }
        var options = catalog.Action(type)?.Params.FirstOrDefault(p => p.Key == param)?.Options;
        return options?.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase))?.Label ?? "a deleted choice";
    }

    private static string TagList(string? tags, bool html)
    {
        var list = SplitTags(tags);
        var label = list.Count == 1 ? "the tag " : "the tags ";
        return label + Join(list.Select(t => Quote(t, html)).ToList(), "and");
    }

    public static List<string> SplitTags(string? tags) =>
        (tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length is > 0 and <= 64)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string ConditionWord(string? match) => string.Equals(match, "any", StringComparison.OrdinalIgnoreCase) ? "or" : "and";

    private static string Join(IReadOnlyList<string> parts, string word) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        2 => $"{parts[0]} {word} {parts[1]}",
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + $", {word} {parts[^1]}",
    };

    private static string Value(string text, bool html) => html ? $"<strong>{WebUtility.HtmlEncode(text)}</strong>" : text;

    private static string Quote(string text, bool html) => html ? $"<strong>“{WebUtility.HtmlEncode(text)}”</strong>" : $"“{text}”";

    private static string Enc(string text, bool html) => html ? WebUtility.HtmlEncode(text) : text;

    /// <summary>Lower-cases the first letter unless the word looks like an acronym ("VPN region").</summary>
    public static string Lower(string label) =>
        label.Length > 1 && char.IsUpper(label[0]) && !char.IsUpper(label[1]) ? char.ToLowerInvariant(label[0]) + label[1..] : label;
}
