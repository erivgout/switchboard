using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>
/// Checks tool arguments against the subset of JSON Schema that tool input schemas use (type, required,
/// properties, additionalProperties, enum, lengths, ranges, items). Errors are keyed by argument path.
/// </summary>
public static class SchemaValidator
{
    public static Dictionary<string, string[]> Validate(JsonObject schema, JsonNode? value)
    {
        var errors = new Dictionary<string, List<string>>();
        Check(schema, value, "", errors);
        return errors.ToDictionary(e => e.Key.Length == 0 ? "arguments" : e.Key, e => e.Value.ToArray());
    }

    private static void Check(JsonObject schema, JsonNode? value, string path, Dictionary<string, List<string>> errors)
    {
        void Add(string message)
        {
            if (!errors.TryGetValue(path, out var list))
            {
                errors[path] = list = [];
            }
            list.Add(message);
        }

        var types = schema["type"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var t) => [t],
            JsonArray a => a.Select(x => x?.GetValue<string>() ?? "").ToArray(),
            _ => Array.Empty<string>(),
        };
        if (types.Length > 0 && !types.Any(t => Matches(t, value)))
        {
            Add($"Expected {string.Join(" or ", types)}.");
            return;
        }

        if (schema["enum"] is JsonArray allowed && value is not null
            && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
        {
            Add($"Use one of: {string.Join(", ", allowed.Select(a => a?.ToJsonString()))}.");
        }

        switch (value)
        {
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
            {
                var s = v.GetValue<string>();
                if (schema["maxLength"] is JsonValue max && s.Length > max.GetValue<int>())
                {
                    Add($"Use at most {max.GetValue<int>()} characters.");
                }
                if (schema["minLength"] is JsonValue min && s.Length < min.GetValue<int>())
                {
                    Add(min.GetValue<int>() == 1 ? "Can't be empty." : $"Use at least {min.GetValue<int>()} characters.");
                }
                if (schema["format"] is JsonValue f && f.GetValue<string>() == "uuid" && !Guid.TryParse(s, out _))
                {
                    Add("Expected an id (UUID).");
                }
                break;
            }
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
            {
                var d = Number(v);
                if (schema["minimum"] is JsonValue min && d < Number(min))
                {
                    Add($"Must be at least {min}.");
                }
                if (schema["maximum"] is JsonValue max && d > Number(max))
                {
                    Add($"Must be at most {max}.");
                }
                break;
            }
            case JsonArray array:
            {
                if (schema["maxItems"] is JsonValue max && array.Count > max.GetValue<int>())
                {
                    Add($"Use at most {max.GetValue<int>()} items.");
                }
                if (schema["minItems"] is JsonValue min && array.Count < min.GetValue<int>())
                {
                    Add($"Use at least {min.GetValue<int>()} items.");
                }
                if (schema["items"] is JsonObject itemSchema)
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        Check(itemSchema, array[i], $"{path}[{i}]", errors);
                    }
                }
                break;
            }
            case JsonObject obj:
            {
                var properties = schema["properties"] as JsonObject;
                if (schema["required"] is JsonArray required)
                {
                    foreach (var name in required.Select(r => r?.GetValue<string>()).OfType<string>())
                    {
                        if (obj[name] is null)
                        {
                            AddAt(Join(path, name), "Required.");
                        }
                    }
                }
                foreach (var (name, child) in obj)
                {
                    if (properties?[name] is JsonObject propertySchema)
                    {
                        if (child is not null)
                        {
                            Check(propertySchema, child, Join(path, name), errors);
                        }
                    }
                    else if (schema["additionalProperties"] is JsonValue ap && ap.GetValueKind() == JsonValueKind.False)
                    {
                        AddAt(Join(path, name), "Not an argument of this tool.");
                    }
                    else if (schema["additionalProperties"] is JsonObject apSchema && child is not null)
                    {
                        Check(apSchema, child, Join(path, name), errors);
                    }
                }
                break;
            }
        }

        void AddAt(string at, string message)
        {
            if (!errors.TryGetValue(at, out var list))
            {
                errors[at] = list = [];
            }
            list.Add(message);
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static bool Matches(string type, JsonNode? value) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && IsInteger(v),
        _ => true,
    };

    private static bool IsInteger(JsonValue v) => decimal.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d);

    /// <summary>The numeric value whether the node was parsed from JSON or built from a CLR number.</summary>
    private static double Number(JsonValue v) => double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
}
