using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.ServiceDesk.Services;

public sealed record FieldChoice(string Value, string Label);

/// <summary>Converts custom field values between form posts, stored JSON and display text.</summary>
public static class CustomFieldForm
{
    /// <summary>Separator between levels of a dependent dropdown value in a form post.</summary>
    public const string PathSeparator = "\u001f";

    /// <summary>Form inputs for custom fields are named <c>cf.&lt;key&gt;</c>.</summary>
    public const string InputPrefix = "cf.";

    public static bool IsEditable(CustomFieldDefinition def) =>
        def.FieldType is not (CustomFieldType.Content or CustomFieldType.Lookup);

    /// <summary>The choices of a dropdown or multi-select, or the flattened paths of a dependent dropdown.</summary>
    public static IReadOnlyList<FieldChoice> Choices(CustomFieldDefinition def)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(def.OptionsJson) ? "[]" : def.OptionsJson);
        }
        catch (JsonException)
        {
            return [];
        }
        if (root is not JsonArray array)
        {
            return [];
        }
        if (def.FieldType == CustomFieldType.DependentDropdown)
        {
            var list = new List<FieldChoice>();
            Flatten(array, [], list);
            return list;
        }
        return array.Select(n => n switch
        {
            JsonObject o when o["value"]?.GetValue<string>() is { } v => new FieldChoice(v, o["label"]?.GetValue<string>() ?? v),
            JsonValue v when v.TryGetValue<string>(out var s) => new FieldChoice(s, s),
            _ => null,
        }).OfType<FieldChoice>().ToList();
    }

    private static void Flatten(JsonArray level, List<string> path, List<FieldChoice> output)
    {
        foreach (var node in level.OfType<JsonObject>())
        {
            if (node["value"]?.GetValue<string>() is not { } value)
            {
                continue;
            }
            var here = new List<string>(path) { value };
            output.Add(new FieldChoice(string.Join(PathSeparator, here), string.Join(" / ", here)));
            if (node["children"] is JsonArray children && here.Count < 3)
            {
                Flatten(children, here, output);
            }
        }
    }

    /// <summary>
    /// Turns posted strings into the JSON value the custom field service validates. Empty input clears the field.
    /// </summary>
    public static JsonElement? FromForm(CustomFieldDefinition def, IReadOnlyList<string?> values)
    {
        var first = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        switch (def.FieldType)
        {
            case CustomFieldType.Checkbox:
                return JsonSerializer.SerializeToElement(values.Any(v => v is "true" or "on"));
            case CustomFieldType.MultiSelect:
            {
                var picked = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct().ToArray();
                return picked.Length == 0 ? null : JsonSerializer.SerializeToElement(picked);
            }
            case CustomFieldType.DependentDropdown:
                return first is null ? null : JsonSerializer.SerializeToElement(first.Split(PathSeparator));
            case CustomFieldType.Number:
                return first is null ? null
                    : long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? JsonSerializer.SerializeToElement(l)
                    : JsonSerializer.SerializeToElement(first);
            case CustomFieldType.Decimal:
                return first is null ? null
                    : decimal.TryParse(first, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? JsonSerializer.SerializeToElement(d)
                    : JsonSerializer.SerializeToElement(first);
            default:
                return first is null ? null : JsonSerializer.SerializeToElement(first);
        }
    }

    /// <summary>The stored value as form strings (for selected options and input values).</summary>
    public static IReadOnlyList<string> ToForm(CustomFieldDefinition def, JsonNode? node)
    {
        switch (node)
        {
            case null:
                return [];
            case JsonArray array when def.FieldType == CustomFieldType.DependentDropdown:
                return [string.Join(PathSeparator, array.Select(n => n?.ToString() ?? ""))];
            case JsonArray array:
                return array.Select(n => n?.ToString() ?? "").ToList();
            case JsonValue value when def.FieldType == CustomFieldType.DateTime && value.TryGetValue<string>(out var s)
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt):
                return [dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)];
            default:
                return [node.ToString()];
        }
    }

    /// <summary>Readable value for read-only display.</summary>
    public static string? Display(CustomFieldDefinition def, JsonNode? node) => node switch
    {
        null => null,
        JsonArray array when def.FieldType == CustomFieldType.DependentDropdown => string.Join(" / ", array.Select(n => n?.ToString())),
        JsonArray array => string.Join(", ", array.Select(n => n?.ToString())),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
        _ => node.ToString(),
    };
}
