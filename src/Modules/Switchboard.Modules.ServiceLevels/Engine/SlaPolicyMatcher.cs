using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Switchboard.Core.Domain;
using Switchboard.Modules.ServiceLevels.Domain;

namespace Switchboard.Modules.ServiceLevels.Engine;

/// <summary>What policy conditions look at, gathered from a ticket.</summary>
public sealed record SlaTicketFacts(
    TicketType Type,
    Guid SourceId,
    IReadOnlySet<Guid> RequesterGroupIds,
    Guid? DepartmentId,
    IReadOnlyList<Guid> CategoryIds,
    Guid? GroupId,
    JsonObject CustomFields);

/// <summary>Pure policy selection: the first active policy, in order, whose conditions all hold (FR-SLA-2, INV-SLA-1).</summary>
public static class SlaPolicyMatcher
{
    public static SlaPolicy? Select(IEnumerable<SlaPolicy> policies, SlaTicketFacts facts) =>
        policies
            .Where(p => p.IsActive && p.DeletedAt is null)
            .OrderBy(p => p.Order)
            .ThenBy(p => p.CreatedAt)
            .FirstOrDefault(p => Matches(p, facts));

    public static bool Matches(SlaPolicy policy, SlaTicketFacts facts) => policy.Conditions.All(c => Matches(c, facts));

    /// <summary>A condition with no values is ignored. Unknown fields never match (fail closed).</summary>
    public static bool Matches(SlaCondition condition, SlaTicketFacts facts)
    {
        var values = condition.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (values.Count == 0)
        {
            return true;
        }

        switch (condition.Field)
        {
            case SlaConditionFields.Type:
                var type = facts.Type == TicketType.Incident ? "incident" : "request";
                return values.Any(v => string.Equals(v, type, StringComparison.OrdinalIgnoreCase));
            case SlaConditionFields.Source:
                return Ids(values).Contains(facts.SourceId);
            case SlaConditionFields.RequesterGroup:
                return Ids(values).Overlaps(facts.RequesterGroupIds);
            case SlaConditionFields.Department:
                return facts.DepartmentId is { } d && Ids(values).Contains(d);
            case SlaConditionFields.Category:
                return Ids(values).Overlaps(facts.CategoryIds);
            case SlaConditionFields.Group:
                return facts.GroupId is { } g && Ids(values).Contains(g);
        }

        if (condition.Field.StartsWith(SlaConditionFields.CustomPrefix, StringComparison.Ordinal))
        {
            var key = condition.Field[SlaConditionFields.CustomPrefix.Length..];
            return CustomValues(facts.CustomFields[key])
                .Any(actual => values.Any(v => string.Equals(v.Trim(), actual, StringComparison.OrdinalIgnoreCase)));
        }
        return false;
    }

    private static HashSet<Guid> Ids(IEnumerable<string> values) =>
        values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();

    private static IEnumerable<string> CustomValues(JsonNode? node)
    {
        switch (node)
        {
            case null:
                yield break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var v in CustomValues(item))
                    {
                        yield return v;
                    }
                }
                yield break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                yield return text;
                yield break;
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                yield return flag ? "true" : "false";
                yield break;
            case JsonValue value when value.TryGetValue<decimal>(out var number):
                yield return number.ToString(CultureInfo.InvariantCulture);
                yield break;
            default:
                yield return node.ToJsonString();
                yield break;
        }
    }
}
