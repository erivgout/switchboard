using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Core.Services;

/// <summary>Who is writing custom field values; requesters may only set requester-editable fields.</summary>
public enum FieldAudience
{
    Agent,
    Requester,
}

public interface ICustomFieldService
{
    Task<IReadOnlyList<CustomFieldDefinition>> GetDefinitionsAsync(string entityType, Guid? workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Validates <paramref name="changes"/> (null value clears a field) and merges them into
    /// <paramref name="existingJson"/>. Throws <see cref="ValidationException"/> with <c>custom.&lt;key&gt;</c> errors.
    /// </summary>
    Task<string> ApplyAsync(string entityType, Guid? workspaceId, string existingJson,
        IReadOnlyDictionary<string, JsonElement?> changes, FieldAudience audience, CancellationToken ct = default);

    /// <summary>Errors for required fields that are empty, for the given moment.</summary>
    Task<IReadOnlyList<string>> MissingRequiredAsync(string entityType, Guid? workspaceId, string json, RequiredMoment moment, CancellationToken ct = default);
}

public enum RequiredMoment
{
    AgentSave,
    Resolve,
    RequesterSubmit,
}

public sealed class CustomFieldService(SwitchboardDbContext db) : ICustomFieldService
{
    public async Task<IReadOnlyList<CustomFieldDefinition>> GetDefinitionsAsync(string entityType, Guid? workspaceId, CancellationToken ct = default) =>
        await db.CustomFieldDefinitions.AsNoTracking()
            .Where(d => d.EntityType == entityType && d.IsActive && (d.WorkspaceId == null || d.WorkspaceId == workspaceId))
            .OrderBy(d => d.Order).ThenBy(d => d.Label)
            .ToListAsync(ct);

    public async Task<string> ApplyAsync(string entityType, Guid? workspaceId, string existingJson,
        IReadOnlyDictionary<string, JsonElement?> changes, FieldAudience audience, CancellationToken ct = default)
    {
        if (changes.Count == 0)
        {
            return existingJson;
        }

        var defs = (await GetDefinitionsAsync(entityType, workspaceId, ct)).ToDictionary(d => d.Key, StringComparer.Ordinal);
        var values = Parse(existingJson);
        var errors = new Dictionary<string, string[]>();

        foreach (var (key, value) in changes)
        {
            if (!defs.TryGetValue(key, out var def) || def.FieldType == CustomFieldType.Content)
            {
                errors[$"custom.{key}"] = [$"'{key}' is not a field here."];
                continue;
            }
            if (audience == FieldAudience.Requester && !(def.VisibleToRequesters && def.EditableByRequesters))
            {
                errors[$"custom.{key}"] = [$"{def.RequesterLabel ?? def.Label} can't be changed."];
                continue;
            }

            if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                values.Remove(key);
                continue;
            }

            var error = Validate(def, value.Value, out var normalized);
            if (error is not null)
            {
                errors[$"custom.{key}"] = [error];
                continue;
            }
            values[key] = normalized;
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
        return values.ToJsonString();
    }

    public async Task<IReadOnlyList<string>> MissingRequiredAsync(string entityType, Guid? workspaceId, string json, RequiredMoment moment, CancellationToken ct = default)
    {
        var defs = await GetDefinitionsAsync(entityType, workspaceId, ct);
        var values = Parse(json);
        return defs
            .Where(d => moment switch
            {
                RequiredMoment.AgentSave => d.RequiredForAgents,
                RequiredMoment.Resolve => d.RequiredOnResolve || d.RequiredForAgents,
                _ => d.RequiredForRequesters && d.VisibleToRequesters,
            })
            .Where(d => !values.TryGetPropertyValue(d.Key, out var v) || IsEmpty(v))
            .Select(d => $"{(moment == RequiredMoment.RequesterSubmit ? d.RequesterLabel ?? d.Label : d.Label)} is required.")
            .ToList();
    }

    public static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s),
        _ => false,
    };

    private static string? Validate(CustomFieldDefinition def, JsonElement value, out JsonNode? normalized)
    {
        normalized = null;
        switch (def.FieldType)
        {
            case CustomFieldType.Text or CustomFieldType.Paragraph:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return $"{def.Label} must be text.";
                }
                var text = value.GetString()!.Trim();
                var max = def.FieldType == CustomFieldType.Text ? 500 : 20_000;
                if (text.Length > max)
                {
                    return $"{def.Label} must be at most {max} characters.";
                }
                normalized = JsonValue.Create(text);
                return null;

            case CustomFieldType.Number:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l))
                {
                    normalized = JsonValue.Create(l);
                    return null;
                }
                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                {
                    normalized = JsonValue.Create(l);
                    return null;
                }
                return $"{def.Label} must be a whole number.";

            case CustomFieldType.Decimal:
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d))
                {
                    normalized = JsonValue.Create(d);
                    return null;
                }
                if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out d))
                {
                    normalized = JsonValue.Create(d);
                    return null;
                }
                return $"{def.Label} must be a number.";

            case CustomFieldType.Checkbox:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    normalized = JsonValue.Create(value.GetBoolean());
                    return null;
                }
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var b))
                {
                    normalized = JsonValue.Create(b);
                    return null;
                }
                return $"{def.Label} must be true or false.";

            case CustomFieldType.Date:
                if (value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    normalized = JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    return null;
                }
                return $"{def.Label} must be a date (YYYY-MM-DD).";

            case CustomFieldType.DateTime:
                if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                {
                    normalized = JsonValue.Create(dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    return null;
                }
                return $"{def.Label} must be a date and time.";

            case CustomFieldType.Dropdown:
            {
                var options = Options(def);
                if (value.ValueKind == JsonValueKind.String && options.Contains(value.GetString()!))
                {
                    normalized = JsonValue.Create(value.GetString());
                    return null;
                }
                return $"Choose one of the {def.Label} options.";
            }

            case CustomFieldType.MultiSelect:
            {
                var options = Options(def);
                if (value.ValueKind != JsonValueKind.Array)
                {
                    return $"{def.Label} must be a list.";
                }
                var picked = value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : "").Distinct().ToList();
                if (picked.Any(p => !options.Contains(p)))
                {
                    return $"Choose only {def.Label} options.";
                }
                normalized = new JsonArray(picked.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
                return null;
            }

            case CustomFieldType.DependentDropdown:
            {
                if (value.ValueKind != JsonValueKind.Array)
                {
                    return $"{def.Label} must be a list of up to three levels.";
                }
                var path = value.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
                if (path.Count is 0 or > 3 || !DependentPathValid(def, path))
                {
                    return $"Choose a valid {def.Label}.";
                }
                normalized = new JsonArray(path.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
                return null;
            }

            case CustomFieldType.Lookup:
                if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id))
                {
                    normalized = JsonValue.Create(id.ToString());
                    return null;
                }
                return $"{def.Label} must reference a record.";

            default:
                return $"{def.Label} can't be set.";
        }
    }

    private static HashSet<string> Options(CustomFieldDefinition def)
    {
        try
        {
            var node = JsonNode.Parse(def.OptionsJson);
            return node is JsonArray arr
                ? arr.Select(n => n is JsonObject o ? o["value"]?.GetValue<string>() : n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    private static bool DependentPathValid(CustomFieldDefinition def, IReadOnlyList<string> path)
    {
        try
        {
            var level = JsonNode.Parse(def.OptionsJson) as JsonArray;
            foreach (var segment in path)
            {
                var match = level?.OfType<JsonObject>().FirstOrDefault(o => o["value"]?.GetValue<string>() == segment);
                if (match is null)
                {
                    return false;
                }
                level = match["children"] as JsonArray;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
