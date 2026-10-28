using System.Globalization;
using Switchboard.Modules.Automation.Definitions;

namespace Switchboard.Modules.Automation.Engine;

public sealed record RuleResult(Condition Rule, bool Matched, string Description, string Actual);

public sealed record GroupResult(string Match, bool Matched, IReadOnlyList<RuleResult> Rules);

public sealed record EvaluationResult(bool Matched, string Match, IReadOnlyList<GroupResult> Groups)
{
    public static EvaluationResult Always { get; } = new(true, "all", []);
}

/// <summary>Evaluates AND/OR condition groups against <see cref="TicketFacts"/>. Pure: no I/O.</summary>
public static class ConditionEvaluator
{
    public static EvaluationResult Evaluate(ConditionSet set, TicketFacts facts, BuilderCatalog catalog)
    {
        var groups = new List<GroupResult>();
        foreach (var group in set.Groups.Where(g => g.Rules.Count > 0))
        {
            var rules = group.Rules.Select(r => EvaluateRule(r, facts, catalog)).ToList();
            var matched = IsAny(group.Match) ? rules.Any(r => r.Matched) : rules.All(r => r.Matched);
            groups.Add(new GroupResult(group.Match, matched, rules));
        }
        if (groups.Count == 0)
        {
            return EvaluationResult.Always;
        }
        var all = IsAny(set.Match) ? groups.Any(g => g.Matched) : groups.All(g => g.Matched);
        return new EvaluationResult(all, set.Match, groups);
    }

    public static bool IsAny(string? match) => string.Equals(match, "any", StringComparison.OrdinalIgnoreCase);

    public static RuleResult EvaluateRule(Condition rule, TicketFacts facts, BuilderCatalog catalog)
    {
        var field = catalog.Field(rule.Field);
        var description = Describer.Rule(rule, catalog, html: false);
        if (field is null)
        {
            return new RuleResult(rule, false, description, "This field no longer exists");
        }
        if (!facts.Values.TryGetValue(rule.Field, out var actual))
        {
            actual = null;
        }
        var matched = Matches(field, rule.Op, rule.Value, actual);
        return new RuleResult(rule, matched, description, Display(field, actual));
    }

    public static bool Matches(CatalogField field, string op, string? expected, object? actual)
    {
        expected = expected?.Trim();
        switch (field.Kind)
        {
            case FieldKind.Text:
            {
                var a = actual switch
                {
                    null => "",
                    string s => s,
                    IEnumerable<string> list => string.Join(", ", list),
                    _ => Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "",
                };
                var e = expected ?? "";
                return op switch
                {
                    Operators.Contains => a.Contains(e, StringComparison.OrdinalIgnoreCase),
                    Operators.NotContains => !a.Contains(e, StringComparison.OrdinalIgnoreCase),
                    Operators.Is => string.Equals(a.Trim(), e, StringComparison.OrdinalIgnoreCase),
                    Operators.IsNot => !string.Equals(a.Trim(), e, StringComparison.OrdinalIgnoreCase),
                    Operators.StartsWith => a.TrimStart().StartsWith(e, StringComparison.OrdinalIgnoreCase),
                    Operators.EndsWith => a.TrimEnd().EndsWith(e, StringComparison.OrdinalIgnoreCase),
                    Operators.Empty => string.IsNullOrWhiteSpace(a),
                    Operators.NotEmpty => !string.IsNullOrWhiteSpace(a),
                    _ => false,
                };
            }

            case FieldKind.Choice:
            {
                var values = actual switch
                {
                    null => [],
                    string s when s.Length == 0 => [],
                    string s => [s],
                    IEnumerable<string> list => list.Where(x => !string.IsNullOrEmpty(x)).ToList(),
                    _ => new List<string> { Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "" },
                };
                var has = expected is not null && values.Any(x => string.Equals(x, expected, StringComparison.OrdinalIgnoreCase));
                return op switch
                {
                    Operators.Is => has,
                    Operators.IsNot => !has,
                    Operators.Empty => values.Count == 0,
                    Operators.NotEmpty => values.Count > 0,
                    _ => false,
                };
            }

            case FieldKind.Ordinal:
            {
                var a = Rank(field, actual as string);
                var e = Rank(field, expected);
                if (a < 0 || e < 0)
                {
                    return false;
                }
                return op switch
                {
                    Operators.Is => a == e,
                    Operators.IsNot => a != e,
                    Operators.AtLeast => a >= e,
                    Operators.AtMost => a <= e,
                    _ => false,
                };
            }

            case FieldKind.Bool:
            {
                var a = actual is true;
                var e = bool.TryParse(expected, out var parsed) && parsed;
                return op == Operators.Is && a == e;
            }

            case FieldKind.Tags:
            {
                var list = actual as IEnumerable<string> ?? [];
                var has = expected is not null && list.Any(t => string.Equals(t.Trim(), expected, StringComparison.OrdinalIgnoreCase));
                return op switch
                {
                    Operators.Has => has,
                    Operators.NotHas => !has,
                    Operators.Empty => !list.Any(),
                    Operators.NotEmpty => list.Any(),
                    _ => false,
                };
            }

            case FieldKind.Number:
            {
                var a = actual as decimal?;
                if (op == Operators.Empty)
                {
                    return a is null;
                }
                if (op == Operators.NotEmpty)
                {
                    return a is not null;
                }
                if (a is null || !decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var e))
                {
                    return false;
                }
                return op switch
                {
                    Operators.Is => a == e,
                    Operators.IsNot => a != e,
                    Operators.GreaterThan => a > e,
                    Operators.LessThan => a < e,
                    Operators.AtLeast => a >= e,
                    Operators.AtMost => a <= e,
                    _ => false,
                };
            }
        }
        return false;
    }

    private static int Rank(CatalogField field, string? value)
    {
        if (value is null)
        {
            return -1;
        }
        for (var i = 0; i < field.Options.Count; i++)
        {
            if (string.Equals(field.Options[i].Value, value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    public static string Display(CatalogField field, object? actual) => actual switch
    {
        null => "empty",
        bool b => b ? "Yes" : "No",
        string s when s.Length == 0 => "empty",
        string s => field.Options.FirstOrDefault(o => string.Equals(o.Value, s, StringComparison.OrdinalIgnoreCase))?.Label ?? Truncate(s, 120),
        IEnumerable<string> list => list.Any()
            ? string.Join(", ", list.Select(x => field.Options.FirstOrDefault(o => o.Value == x)?.Label ?? x))
            : "empty",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(actual, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
