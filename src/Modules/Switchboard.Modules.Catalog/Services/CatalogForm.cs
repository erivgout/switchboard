using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Switchboard.Core.Data;
using Switchboard.Modules.Catalog.Domain;

namespace Switchboard.Modules.Catalog.Services;

/// <summary>A question as the requester sees it, with shared-field definitions already applied.</summary>
public sealed record FormField(string Key, string Label, CatFieldType Type, bool Required, string? HelpText, IReadOnlyList<string> Options)
{
    public static FormField From(CatItemField f) => f.SharedField is { } shared
        ? new FormField(f.Key, shared.Label, shared.FieldType, f.Required, shared.HelpText, shared.Options)
        : new FormField(f.Key, f.Label, f.FieldType, f.Required, f.HelpText, f.Options);
}

public sealed record FormResult(IReadOnlyList<FieldAnswer> Answers, IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Server-side validation of an item's request form. Pure: user lookups are checked against
/// <c>knownUsers</c>, which the caller loads (active people only).
/// </summary>
public static partial class CatalogForm
{
    public const int MaxText = 500;
    public const int MaxParagraph = 5000;
    public const int MaxOptions = 100;
    public const string FieldPrefix = "f.";

    public static string ErrorKey(string fieldKey) => FieldPrefix + fieldKey;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex KeyPattern();

    public static bool IsValidKey(string key) => KeyPattern().IsMatch(key);

    /// <summary>Makes a snake_case key from a label, e.g. "Cost centre" becomes <c>cost_centre</c>.</summary>
    public static string KeyFrom(string label)
    {
        var chars = (label ?? "").Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        var key = Regex.Replace(new string(chars), "_+", "_").Trim('_');
        if (key.Length == 0 || !char.IsAsciiLetter(key[0]))
        {
            key = "field_" + key;
        }
        return key.Length > 63 ? key[..63].TrimEnd('_') : key;
    }

    public static bool UsesOptions(CatFieldType type) => type is CatFieldType.Dropdown or CatFieldType.MultiSelect;

    /// <summary>Validates raw submitted values (every value as text, several for multi-select) against the form.</summary>
    /// <param name="strict">Reject keys that aren't on the form (API and agent calls); web forms ignore extras.</param>
    public static FormResult Validate(
        IReadOnlyList<FormField> fields,
        IReadOnlyDictionary<string, IReadOnlyList<string>> input,
        IReadOnlyDictionary<Guid, string> knownUsers,
        bool strict = false)
    {
        var answers = new List<FieldAnswer>();
        var errors = new Dictionary<string, string[]>();

        if (strict)
        {
            foreach (var key in input.Keys.Where(k => fields.All(f => f.Key != k)))
            {
                errors[ErrorKey(key)] = [$"'{key}' isn't a question on this form."];
            }
        }

        foreach (var field in fields)
        {
            var raw = (input.GetValueOrDefault(field.Key) ?? [])
                .Select(v => (v ?? "").Trim())
                .Where(v => v.Length > 0)
                .ToList();
            var error = Check(field, raw, knownUsers, out var answer);
            if (error is not null)
            {
                errors[ErrorKey(field.Key)] = [error];
            }
            else if (answer is not null)
            {
                answers.Add(answer);
            }
        }
        return new FormResult(answers, errors);
    }

    private static string? Check(FormField f, List<string> raw, IReadOnlyDictionary<Guid, string> users, out FieldAnswer? answer)
    {
        answer = null;
        if (f.Type == CatFieldType.Checkbox)
        {
            var on = raw.Any(v => v is "true" or "on" or "1" or "yes");
            if (raw.Any(v => v is not ("true" or "on" or "1" or "yes" or "false" or "0" or "no")))
            {
                return $"{f.Label} must be yes or no.";
            }
            if (f.Required && !on)
            {
                return $"Tick \"{f.Label}\" to continue.";
            }
            answer = new FieldAnswer(f.Key, f.Label, f.Type, JsonValue.Create(on), on ? "Yes" : "No");
            return null;
        }

        if (raw.Count == 0)
        {
            return f.Required ? $"{f.Label} is required." : null;
        }
        if (f.Type != CatFieldType.MultiSelect && raw.Count > 1)
        {
            return $"Give one value for {f.Label}.";
        }
        var value = raw[0];

        switch (f.Type)
        {
            case CatFieldType.Text:
            case CatFieldType.Paragraph:
            {
                var max = f.Type == CatFieldType.Text ? MaxText : MaxParagraph;
                if (value.Length > max)
                {
                    return $"Keep {f.Label} under {max} characters.";
                }
                answer = new FieldAnswer(f.Key, f.Label, f.Type, JsonValue.Create(value), value);
                return null;
            }
            case CatFieldType.Number:
            {
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) || Math.Abs(number) > 1_000_000_000_000m)
                {
                    return $"{f.Label} must be a number, like 12 or 3.5.";
                }
                answer = new FieldAnswer(f.Key, f.Label, f.Type, JsonValue.Create(number), number.ToString("0.##########", CultureInfo.InvariantCulture));
                return null;
            }
            case CatFieldType.Dropdown:
            {
                var match = f.Options.FirstOrDefault(o => string.Equals(o, value, StringComparison.Ordinal));
                if (match is null)
                {
                    return $"Choose one of the options for {f.Label}.";
                }
                answer = new FieldAnswer(f.Key, f.Label, f.Type, JsonValue.Create(match), match);
                return null;
            }
            case CatFieldType.MultiSelect:
            {
                var chosen = raw.Distinct(StringComparer.Ordinal).ToList();
                if (chosen.Any(c => !f.Options.Contains(c, StringComparer.Ordinal)))
                {
                    return $"Choose only listed options for {f.Label}.";
                }
                // Keep the form's order, not the click order.
                var ordered = f.Options.Where(chosen.Contains).ToList();
                answer = new FieldAnswer(f.Key, f.Label, f.Type,
                    new JsonArray(ordered.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()), string.Join(", ", ordered));
                return null;
            }
            case CatFieldType.Date:
            {
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return $"{f.Label} must be a date.";
                }
                answer = new FieldAnswer(f.Key, f.Label, f.Type, JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture));
                return null;
            }
            case CatFieldType.UserLookup:
            {
                if (!Guid.TryParse(value, out var id) || !users.TryGetValue(id, out var name))
                {
                    return $"Choose a person for {f.Label}.";
                }
                answer = new FieldAnswer(f.Key, f.Label, f.Type, new JsonObject { ["id"] = id.ToString(), ["name"] = name }, name);
                return null;
            }
            default:
                return $"{f.Label} can't be answered.";
        }
    }

    /// <summary>User ids typed into user lookup fields, so the caller can check they exist.</summary>
    public static IReadOnlyList<Guid> LookupIds(IReadOnlyList<FormField> fields, IReadOnlyDictionary<string, IReadOnlyList<string>> input) =>
        fields.Where(f => f.Type == CatFieldType.UserLookup)
            .SelectMany(f => input.GetValueOrDefault(f.Key) ?? [])
            .Select(v => Guid.TryParse(v?.Trim(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();

    public static string Serialize(IReadOnlyList<FieldAnswer> answers) => JsonSerializer.Serialize(answers, JsonDefaults.Options);

    public static IReadOnlyList<FieldAnswer> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<FieldAnswer>>(json, JsonDefaults.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Answers turned back into raw form input, to pre-fill a form when a request is edited and resubmitted.</summary>
    public static Dictionary<string, IReadOnlyList<string>> ToInput(IReadOnlyList<FieldAnswer> answers)
    {
        var input = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var a in answers)
        {
            input[a.Key] = a.Value switch
            {
                JsonArray arr => arr.Select(n => n?.GetValue<string>() ?? "").ToList(),
                JsonObject obj => [obj["id"]?.GetValue<string>() ?? ""],
                JsonValue v when v.TryGetValue<bool>(out var b) => [b ? "true" : "false"],
                JsonValue v when v.TryGetValue<decimal>(out var d) => [d.ToString(CultureInfo.InvariantCulture)],
                JsonValue v => [v.ToString()],
                _ => [],
            };
        }
        return input;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Symbols = new(StringComparer.Ordinal);

    /// <summary>Formats minor units for display, e.g. 129900 USD as "$1,299.00".</summary>
    public static string Money(long minor, string currency)
    {
        var symbol = Symbols.GetOrAdd(currency, code =>
            CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .Select(c => { try { return new RegionInfo(c.Name); } catch (ArgumentException) { return null; } })
                .FirstOrDefault(r => r?.ISOCurrencySymbol == code)?.CurrencySymbol ?? "");
        var amount = (minor / 100m).ToString("N2", CultureInfo.InvariantCulture);
        return symbol.Length is > 0 and <= 2 ? $"{symbol}{amount}" : $"{amount} {currency}";
    }

    /// <summary>"Same day", "1 working day", "5 working days".</summary>
    public static string Delivery(int? days) => days switch
    {
        null => "",
        0 => "Same day",
        1 => "1 working day",
        _ => $"{days} working days",
    };
}
