using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using Switchboard.Modules.Automation.Engine;
using Switchboard.Modules.Automation.Net;

namespace Switchboard.Modules.Automation.Definitions;

/// <summary>Checks a definition is complete and only references things that exist. Messages are written for admins.</summary>
public static class DefinitionValidator
{
    public const int MaxGroups = 10;
    public const int MaxRulesPerGroup = 20;
    public const int MaxActions = 25;

    public static List<string> ValidateWorkflow(WorkflowDefinition def, BuilderCatalog catalog, OutboundNetworkPolicy? network = null)
    {
        var errors = new List<string>();
        ValidateTrigger(def.Trigger, catalog, errors);
        ValidateConditions(def.Conditions, catalog, errors);
        if (def.Actions.Count == 0)
        {
            errors.Add("Then: add at least one action.");
        }
        ValidateActions(def.Actions, catalog, errors, scenario: false, network);
        return errors;
    }

    public static List<string> ValidateScenario(IReadOnlyList<ActionDefinition> actions, BuilderCatalog catalog)
    {
        var errors = new List<string>();
        if (actions.Count == 0)
        {
            errors.Add("Add at least one action.");
        }
        ValidateActions(actions, catalog, errors, scenario: true);
        return errors;
    }

    private static void ValidateTrigger(TriggerDefinition t, BuilderCatalog catalog, List<string> errors)
    {
        if (catalog.Triggers.All(x => x.Key != t.Event))
        {
            errors.Add("When: choose what starts this workflow.");
            return;
        }
        switch (t.Event)
        {
            case TriggerKeys.TicketUpdated:
                if (t.Changes.Count > 10)
                {
                    errors.Add("When: use at most 10 field changes.");
                }
                foreach (var change in t.Changes)
                {
                    var field = catalog.Field(change.Field);
                    if (field?.ChangeName is null)
                    {
                        errors.Add("When: choose which field change starts the workflow.");
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(change.To) && field.Options.Count > 0 && !HasOption(field.Options, change.To))
                    {
                        errors.Add($"When: \"{field.Label}\" can't change to that value. Pick one from the list.");
                    }
                }
                break;
            case TriggerKeys.Conversation:
                if (t.Conversation is not (null or "any" or "requester" or "agent_reply" or "note"))
                {
                    errors.Add("When: choose which kind of message starts the workflow.");
                }
                break;
            case TriggerKeys.Time:
                if (t.Clock is null || !TimeClocks.All.Contains(t.Clock))
                {
                    errors.Add("When: choose which clock to measure.");
                }
                if (t.Hours is not (> 0 and <= 8760))
                {
                    errors.Add("When: enter a number of hours between 1 and 8760.");
                }
                else if (t.Hours < 1)
                {
                    errors.Add("When: time-based workflows are checked every few minutes, so use at least 1 hour.");
                }
                break;
        }
    }

    private static void ValidateConditions(ConditionSet set, BuilderCatalog catalog, List<string> errors)
    {
        if (set.Match is not ("all" or "any"))
        {
            errors.Add("If: choose whether all or any groups must match.");
        }
        if (set.Groups.Count > MaxGroups)
        {
            errors.Add($"If: use at most {MaxGroups} groups.");
        }
        var groupNumber = 0;
        foreach (var group in set.Groups)
        {
            groupNumber++;
            if (group.Match is not ("all" or "any"))
            {
                errors.Add($"If, group {groupNumber}: choose whether all or any conditions must match.");
            }
            if (group.Rules.Count > MaxRulesPerGroup)
            {
                errors.Add($"If, group {groupNumber}: use at most {MaxRulesPerGroup} conditions.");
            }
            foreach (var rule in group.Rules)
            {
                var where = set.Groups.Count > 1 ? $"If, group {groupNumber}" : "If";
                var field = catalog.Field(rule.Field);
                if (field is null)
                {
                    errors.Add($"{where}: a condition uses a field that doesn't exist. Choose another field.");
                    continue;
                }
                var ops = BuilderCatalog.OperatorsFor(field);
                if (ops.All(o => o.Value != rule.Op))
                {
                    errors.Add($"{where}: choose how to compare \"{field.Label}\".");
                    continue;
                }
                if (!Operators.NeedsValue(rule.Op))
                {
                    continue;
                }
                var value = rule.Value?.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    errors.Add($"{where}: enter a value for \"{field.Label}\".");
                    continue;
                }
                if (value.Length > 500)
                {
                    errors.Add($"{where}: keep the value for \"{field.Label}\" under 500 characters.");
                }
                if (field.Kind is FieldKind.Choice or FieldKind.Ordinal or FieldKind.Bool && !HasOption(field.Options, value))
                {
                    errors.Add($"{where}: \"{field.Label}\" uses a value that no longer exists. Pick one from the list.");
                }
                if (field.Kind == FieldKind.Number && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                {
                    errors.Add($"{where}: \"{field.Label}\" needs a number.");
                }
            }
        }
    }

    private static void ValidateActions(IReadOnlyList<ActionDefinition> actions, BuilderCatalog catalog, List<string> errors, bool scenario,
        OutboundNetworkPolicy? network = null)
    {
        if (actions.Count > MaxActions)
        {
            errors.Add($"Then: use at most {MaxActions} actions.");
        }
        var known = catalog.Placeholders.Select(p => p.Value).Where(p => p != "ticket.cf.KEY").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in catalog.Fields.Where(f => f.Key.StartsWith("ticket.cf.", StringComparison.Ordinal)))
        {
            known.Add(f.Key);
        }

        var step = 0;
        foreach (var action in actions)
        {
            step++;
            var meta = catalog.Action(action.Type);
            var where = scenario ? $"Action {step}" : $"Then, step {step}";
            if (meta is null || (scenario && !ActionTypes.ScenarioAllowed.Contains(action.Type)))
            {
                errors.Add($"{where}: choose an action from the list.");
                continue;
            }
            where += $" ({meta.Label})";

            foreach (var p in meta.Params)
            {
                if (p.ShowWhen is { } dependsOn && p.ShowWhenIn is { } allowed && !allowed.Contains(action.Get(dependsOn) ?? ""))
                {
                    continue;
                }
                var value = action.Get(p.Key);
                if (p.Required && value is null)
                {
                    errors.Add($"{where}: fill in {p.Label.ToLowerInvariant()}.");
                    continue;
                }
                if (value is null)
                {
                    continue;
                }
                if (value.Length > 20_000)
                {
                    errors.Add($"{where}: {p.Label.ToLowerInvariant()} is too long.");
                }
                if (p.Kind == "select" && p.Options is { } options && !HasOption(options, value))
                {
                    errors.Add($"{where}: {p.Label.ToLowerInvariant()} uses a choice that no longer exists. Pick one from the list.");
                }
                if (p.Placeholders)
                {
                    foreach (var token in PlaceholderRenderer.Tokens(value).Where(t => !known.Contains(t)).Distinct())
                    {
                        errors.Add($"{where}: {{{{{token}}}}} isn't a placeholder. Check the spelling against the list.");
                    }
                }
            }

            switch (action.Type)
            {
                case ActionTypes.AddTags or ActionTypes.RemoveTags when Describer.SplitTags(action.Get("tags")).Count == 0:
                    errors.Add($"{where}: enter at least one tag.");
                    break;
                case ActionTypes.SendEmail:
                    if (action.Get("recipient") == "group" && action.Get("groupId") is null)
                    {
                        errors.Add($"{where}: choose the group to email.");
                    }
                    if (action.Get("recipient") == "address" && !IsEmail(action.Get("address")))
                    {
                        errors.Add($"{where}: enter a valid email address.");
                    }
                    if ((action.Get("subject")?.Length ?? 0) > 300)
                    {
                        errors.Add($"{where}: keep the subject under 300 characters.");
                    }
                    break;
                case ActionTypes.CreateTask:
                    if ((action.Get("title")?.Length ?? 0) > 300)
                    {
                        errors.Add($"{where}: keep the task title under 300 characters.");
                    }
                    if (action.Get("dueInHours") is { } due && !(double.TryParse(due, NumberStyles.Number, CultureInfo.InvariantCulture, out var h) && h is > 0 and <= 8760))
                    {
                        errors.Add($"{where}: enter the due time as hours between 1 and 8760.");
                    }
                    break;
                case ActionTypes.WebRequest:
                    ValidateWebRequest(action, where, errors, network);
                    break;
            }
        }
    }

    private static void ValidateWebRequest(ActionDefinition action, string where, List<string> errors, OutboundNetworkPolicy? network)
    {
        var url = action.Get("url") ?? "";
        // The host must be fixed: placeholders are allowed only in the path and query.
        var sample = PlaceholderRenderer.Render(url, new Dictionary<string, string>(), PlaceholderEncoding.Url);
        var hostPart = sample.Contains("://", StringComparison.Ordinal) ? sample[(sample.IndexOf("://", StringComparison.Ordinal) + 3)..] : sample;
        var authority = hostPart.Split('/', '?', '#')[0];
        if (authority.Contains("{{", StringComparison.Ordinal))
        {
            errors.Add($"{where}: the address must have a fixed host name. Placeholders can go in the path or query only.");
            return;
        }
        var probe = PlaceholderRenderer.Tokens(url).Aggregate(url, (current, t) => current.Replace("{{" + t + "}}", "x", StringComparison.Ordinal));
        var problem = OutboundUrlPolicy.CheckStatic(probe, network);
        if (problem is not null)
        {
            errors.Add($"{where}: {problem}");
        }

        if (action.Get("authHeader") is { } header && !IsHeaderName(header))
        {
            errors.Add($"{where}: the auth header name can only use letters, numbers and dashes.");
        }

        var method = action.Get("method") ?? "POST";
        if (method is "POST" or "PUT" or "PATCH" && action.Get("body") is { } body)
        {
            var substituted = PlaceholderRenderer.Tokens(body).Aggregate(body, (current, t) => current.Replace("{{" + t + "}}", "x", StringComparison.Ordinal));
            try
            {
                using var _ = JsonDocument.Parse(substituted);
            }
            catch (JsonException)
            {
                errors.Add($"{where}: the body isn't valid JSON. Check quotes and commas, and put placeholders inside quotes.");
            }
        }

        if (action.Get("timeoutSeconds") is { } timeout && !(int.TryParse(timeout, out var seconds) && seconds is >= 1 and <= 30))
        {
            errors.Add($"{where}: the timeout must be between 1 and 30 seconds.");
        }
    }

    private static bool HasOption(IReadOnlyList<CatalogOption> options, string value) =>
        options.Any(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase));

    public static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254)
        {
            return false;
        }
        try
        {
            var parsed = new MailAddress(value.Trim());
            return parsed.Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase) && parsed.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsHeaderName(string value) =>
        value.Length is > 0 and <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
}
