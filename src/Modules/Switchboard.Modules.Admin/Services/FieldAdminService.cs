using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Admin.Services;

/// <summary>Entity types whose custom fields this module manages (FR-TKT-3, FR-ADM-2).</summary>
public static class FieldEntities
{
    public const string Ticket = "ticket";
    public const string User = "user";
    public const string Department = "department";

    public static readonly IReadOnlyList<(string Key, string Title)> All = [(Ticket, "Tickets"), (User, "People"), (Department, "Departments")];

    public static bool IsKnown(string? entity) => All.Any(e => e.Key == entity);

    /// <summary>Ticket fields belong to a workspace; people and department fields are instance-wide.</summary>
    public static bool IsWorkspaceScoped(string entity) => entity == Ticket;

    /// <summary>Requester visibility applies to ticket forms and the requester's own profile.</summary>
    public static bool HasRequesterSettings(string entity) => entity is Ticket or User;
}

/// <summary>Lookup targets for <see cref="CustomFieldType.Lookup"/> fields. Stored as <c>{"target":"user"}</c> in <c>OptionsJson</c>.</summary>
public static class LookupTargets
{
    public static readonly IReadOnlyList<(string Key, string Title)> All =
    [
        ("user", "Person"), ("agent", "Agent"), ("group", "Agent group"), ("department", "Department"),
        ("location", "Location"), ("asset", "Asset"), ("record", "Custom object record"),
    ];
}

public sealed record FieldRow(CustomFieldDefinition Field, bool HasData);

public sealed record FieldInput
{
    public string Label { get; init; } = "";
    /// <summary>Only used when creating; generated from the label when empty.</summary>
    public string? Key { get; init; }
    /// <summary>Only used when creating; the type can't change afterwards.</summary>
    public CustomFieldType Type { get; init; } = CustomFieldType.Text;
    public string? RequesterLabel { get; init; }
    /// <summary>One option per line; for dependent dropdowns, indent two spaces per level.</summary>
    public string? Options { get; init; }
    public string? LookupTarget { get; init; }
    public bool RequiredForAgents { get; init; }
    public bool RequiredOnResolve { get; init; }
    public bool RequiredForRequesters { get; init; }
    public bool VisibleToRequesters { get; init; }
    public bool EditableByRequesters { get; init; }
    public string? HelpText { get; init; }
    public string? DefaultValue { get; init; }
    public string? Section { get; init; }
}

/// <summary>The custom field manager for tickets, people and departments.</summary>
public sealed partial class FieldAdminService(SwitchboardDbContext db, AdminAccess access, IAuditLog audit)
{
    public const int MaxFieldsPerEntity = 200;
    public const int MaxOptions = 500;

    public static readonly IReadOnlyList<(CustomFieldType Type, string Title, string Hint)> Types =
    [
        (CustomFieldType.Text, "Single line of text", "Short answers such as an asset tag."),
        (CustomFieldType.Paragraph, "Paragraph", "Longer text over several lines."),
        (CustomFieldType.Number, "Whole number", "Counts, such as the number of people affected."),
        (CustomFieldType.Decimal, "Decimal number", "Amounts with a fractional part."),
        (CustomFieldType.Checkbox, "Checkbox", "Yes or no."),
        (CustomFieldType.Date, "Date", "A calendar day."),
        (CustomFieldType.DateTime, "Date and time", "A moment, shown in each viewer's time zone."),
        (CustomFieldType.Dropdown, "Dropdown", "Pick one option from a list."),
        (CustomFieldType.MultiSelect, "Multi-select", "Pick any number of options from a list."),
        (CustomFieldType.DependentDropdown, "Dependent dropdown", "Up to three linked lists, such as country, region, city."),
        (CustomFieldType.Lookup, "Lookup", "A link to a person, group, department, location or asset."),
        (CustomFieldType.Content, "Display text", "Guidance shown on the form. People can't type into it."),
    ];

    public async Task<IReadOnlyList<FieldRow>> ListAsync(string entity, Guid? workspaceId, CancellationToken ct = default)
    {
        var ws = await DemandAsync(entity, workspaceId, ct);
        var fields = await db.CustomFieldDefinitions.AsNoTracking()
            .Where(f => f.EntityType == entity && f.WorkspaceId == ws)
            .OrderBy(f => f.Order).ThenBy(f => f.Label)
            .ToListAsync(ct);
        var rows = new List<FieldRow>();
        foreach (var f in fields)
        {
            rows.Add(new FieldRow(f, await HasDataAsync(f, ct)));
        }
        return rows;
    }

    public async Task<FieldRow> GetAsync(Guid id, CancellationToken ct = default)
    {
        var field = await db.CustomFieldDefinitions.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
        await DemandAsync(field.EntityType, field.WorkspaceId, ct);
        return new FieldRow(field, await HasDataAsync(field, ct));
    }

    public async Task<CustomFieldDefinition> CreateAsync(string entity, Guid? workspaceId, FieldInput input, CancellationToken ct = default)
    {
        var ws = await DemandAsync(entity, workspaceId, ct);
        if (await db.CustomFieldDefinitions.CountAsync(f => f.EntityType == entity && f.WorkspaceId == ws, ct) >= MaxFieldsPerEntity)
        {
            throw new ValidationException("Label", $"There are already {MaxFieldsPerEntity} fields here. Deactivate or delete unused fields first.");
        }
        if (!Types.Any(t => t.Type == input.Type))
        {
            throw new ValidationException("Type", "Choose a field type.");
        }

        var key = string.IsNullOrWhiteSpace(input.Key) ? KeyFromLabel(input.Label) : input.Key.Trim();
        if (!KeyPattern().IsMatch(key))
        {
            throw new ValidationException("Key", "Use lowercase letters, digits and underscores, starting with a letter (for example asset_tag).");
        }
        if (await db.CustomFieldDefinitions.AnyAsync(f => f.EntityType == entity && f.WorkspaceId == ws && f.Key == key, ct))
        {
            throw new ValidationException("Key", $"Another field already uses the key {key}. Choose a different key.");
        }

        var field = new CustomFieldDefinition
        {
            EntityType = entity,
            WorkspaceId = ws,
            Key = key,
            FieldType = input.Type,
            Order = (await db.CustomFieldDefinitions.Where(f => f.EntityType == entity && f.WorkspaceId == ws).MaxAsync(f => (int?)f.Order, ct) ?? 0) + 1,
        };
        Apply(field, input);
        db.CustomFieldDefinitions.Add(field);
        audit.Record("fields.created", "custom_field", field.Id.ToString(), new { entity, key, type = input.Type.ToString(), workspaceId = ws });
        await db.SaveChangesAsync(ct);
        return field;
    }

    public async Task UpdateAsync(Guid id, FieldInput input, CancellationToken ct = default)
    {
        var field = await db.CustomFieldDefinitions.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
        await DemandAsync(field.EntityType, field.WorkspaceId, ct);
        var before = JsonSerializer.Serialize(field, JsonDefaults.Options);
        Apply(field, input);
        if (JsonSerializer.Serialize(field, JsonDefaults.Options) == before)
        {
            return;
        }
        audit.Record("fields.updated", "custom_field", id.ToString(), new { entity = field.EntityType, key = field.Key });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var field = await db.CustomFieldDefinitions.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
        await DemandAsync(field.EntityType, field.WorkspaceId, ct);
        if (field.IsActive == active)
        {
            return;
        }
        field.IsActive = active;
        audit.Record(active ? "fields.activated" : "fields.deactivated", "custom_field", id.ToString(), new { entity = field.EntityType, key = field.Key });
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveAsync(Guid id, int delta, CancellationToken ct = default)
    {
        var field = await db.CustomFieldDefinitions.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
        await DemandAsync(field.EntityType, field.WorkspaceId, ct);
        var all = await db.CustomFieldDefinitions.Where(f => f.EntityType == field.EntityType && f.WorkspaceId == field.WorkspaceId)
            .OrderBy(f => f.Order).ThenBy(f => f.Label).ToListAsync(ct);
        if (TicketConfigService.Reorder(all, field, delta, (f, o) => f.Order = o))
        {
            audit.Record("fields.reordered", "custom_field", id.ToString(), new { entity = field.EntityType });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Deletes a field nobody has filled in. Fields with data are deactivated instead, so values are never lost.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var field = await db.CustomFieldDefinitions.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
        await DemandAsync(field.EntityType, field.WorkspaceId, ct);
        if (await HasDataAsync(field, ct))
        {
            throw new ValidationException("Field", $"Some records have a value in {field.Label}. Deactivate the field instead, so their data stays.");
        }
        db.CustomFieldDefinitions.Remove(field);
        audit.Record("fields.deleted", "custom_field", id.ToString(), new { entity = field.EntityType, key = field.Key });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>True when at least one record of the entity type stores a value under the field's key.</summary>
    public async Task<bool> HasDataAsync(CustomFieldDefinition field, CancellationToken ct = default)
    {
        if (field.FieldType == CustomFieldType.Content)
        {
            return false;
        }
        var pattern = $"%\"{AdminData.LikeLiteral(field.Key)}\":%";
        return field.EntityType switch
        {
            FieldEntities.Ticket => await db.Tickets.AnyAsync(t => (field.WorkspaceId == null || t.WorkspaceId == field.WorkspaceId) && EF.Functions.Like(t.CustomFieldsJson, pattern), ct),
            FieldEntities.User => await db.Users.AnyAsync(u => EF.Functions.Like(u.CustomFieldsJson, pattern), ct),
            FieldEntities.Department => await db.Departments.AnyAsync(d => EF.Functions.Like(d.CustomFieldsJson, pattern), ct),
            _ => true,
        };
    }

    /// <summary>The field's options as the editor shows them: one per line, two spaces of indent per level.</summary>
    public static string OptionsText(CustomFieldDefinition field)
    {
        if (field.FieldType is not (CustomFieldType.Dropdown or CustomFieldType.MultiSelect or CustomFieldType.DependentDropdown))
        {
            return "";
        }
        var sb = new StringBuilder();
        void Walk(JsonArray? level, int depth)
        {
            foreach (var node in level ?? [])
            {
                var value = node is JsonObject o ? o["value"]?.GetValue<string>() : node?.GetValue<string>();
                if (value is null)
                {
                    continue;
                }
                sb.Append(' ', depth * 2).Append(value).Append('\n');
                if (node is JsonObject obj)
                {
                    Walk(obj["children"] as JsonArray, depth + 1);
                }
            }
        }
        try
        {
            Walk(JsonNode.Parse(field.OptionsJson) as JsonArray, 0);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return "";
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static string? LookupTarget(CustomFieldDefinition field)
    {
        try
        {
            return field.FieldType == CustomFieldType.Lookup && JsonNode.Parse(field.OptionsJson) is JsonObject o ? o["target"]?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private void Apply(CustomFieldDefinition field, FieldInput input)
    {
        var errors = new Dictionary<string, string[]>();
        var label = (input.Label ?? "").Trim();
        if (label.Length is 0 or > 200)
        {
            errors["Label"] = [label.Length == 0 ? "Give the field a label." : "Keep the label under 200 characters."];
        }
        string? Optional(string key, string? value, int max)
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v is { Length: var n } && n > max)
            {
                errors.TryAdd(key, [$"Keep this under {max:N0} characters."]);
            }
            return v;
        }
        var requesterLabel = Optional(nameof(FieldInput.RequesterLabel), input.RequesterLabel, 200);
        var help = Optional(nameof(FieldInput.HelpText), input.HelpText, 1000);
        var section = Optional(nameof(FieldInput.Section), input.Section, 200);
        var defaultValue = Optional(nameof(FieldInput.DefaultValue), input.DefaultValue, 1000);

        var optionsJson = field.OptionsJson;
        switch (field.FieldType)
        {
            case CustomFieldType.Dropdown or CustomFieldType.MultiSelect:
            {
                var options = (input.Options ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (options.Count == 0)
                {
                    errors["Options"] = ["Add at least one option, one per line."];
                }
                else if (options.Count > MaxOptions || options.Any(o => o.Length > 200))
                {
                    errors["Options"] = [$"Use up to {MaxOptions} options of at most 200 characters each."];
                }
                else if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
                {
                    errors["Options"] = ["Each option can appear once."];
                }
                optionsJson = new JsonArray(options.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()).ToJsonString();
                break;
            }
            case CustomFieldType.DependentDropdown:
            {
                var (tree, error) = ParseTree(input.Options ?? "");
                if (error is not null)
                {
                    errors["Options"] = [error];
                }
                optionsJson = tree.ToJsonString();
                break;
            }
            case CustomFieldType.Lookup:
            {
                var target = input.LookupTarget?.Trim();
                if (!LookupTargets.All.Any(t => t.Key == target))
                {
                    errors["LookupTarget"] = ["Choose what the field links to."];
                }
                optionsJson = new JsonObject { ["target"] = target }.ToJsonString();
                break;
            }
            default:
                optionsJson = "[]";
                break;
        }

        var content = field.FieldType == CustomFieldType.Content;
        if (content && help is null)
        {
            errors["HelpText"] = ["Write the text to show on the form."];
        }
        if (defaultValue is not null && DefaultError(field.FieldType, defaultValue, optionsJson) is { } defaultError)
        {
            errors["DefaultValue"] = [defaultError];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var requester = FieldEntities.HasRequesterSettings(field.EntityType) && !content;
        field.Label = label;
        field.RequesterLabel = requester ? requesterLabel : null;
        field.OptionsJson = optionsJson;
        field.HelpText = help;
        field.Section = section;
        field.DefaultValue = content ? null : defaultValue;
        field.RequiredForAgents = !content && input.RequiredForAgents;
        field.RequiredOnResolve = !content && field.EntityType == FieldEntities.Ticket && input.RequiredOnResolve;
        // Requesters can only be required to fill in what they can see and edit.
        field.RequiredForRequesters = requester && input.RequiredForRequesters;
        field.EditableByRequesters = requester && (input.EditableByRequesters || field.RequiredForRequesters);
        field.VisibleToRequesters = (FieldEntities.HasRequesterSettings(field.EntityType) && input.VisibleToRequesters) || field.EditableByRequesters;
    }

    /// <summary>Parses indented lines into <c>[{value, children}]</c>, at most three levels.</summary>
    internal static (JsonArray Tree, string? Error) ParseTree(string text)
    {
        var root = new JsonArray();
        var stack = new List<JsonArray> { root };
        var count = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0)
            {
                continue;
            }
            var indent = 0;
            foreach (var c in raw)
            {
                if (c == ' ')
                {
                    indent++;
                }
                else if (c == '\t')
                {
                    indent += 2;
                }
                else
                {
                    break;
                }
            }
            var depth = indent / 2;
            var value = raw.Trim();
            if (depth > 2)
            {
                return (root, $"\"{value}\" is more than three levels deep. Dependent dropdowns have at most three levels.");
            }
            if (depth > stack.Count - 1)
            {
                return (root, $"\"{value}\" is indented more than the line above it. Indent one level (two spaces) at a time.");
            }
            if (value.Length > 200 || ++count > MaxOptions * 4)
            {
                return (root, "That's too many options, or an option is longer than 200 characters.");
            }
            stack.RemoveRange(depth + 1, stack.Count - depth - 1);
            var siblings = stack[depth];
            if (siblings.OfType<JsonObject>().Any(o => string.Equals(o["value"]?.GetValue<string>(), value, StringComparison.OrdinalIgnoreCase)))
            {
                return (root, $"\"{value}\" appears twice in the same list.");
            }
            var children = new JsonArray();
            siblings.Add(new JsonObject { ["value"] = value, ["children"] = children });
            stack.Add(children);
        }
        return root.Count == 0 ? (root, "Add at least one option, one per line.") : (root, null);
    }

    private static string? DefaultError(CustomFieldType type, string value, string optionsJson) => type switch
    {
        CustomFieldType.Text when value.Length > 500 => "Keep the default under 500 characters.",
        CustomFieldType.Number when !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => "The default must be a whole number.",
        CustomFieldType.Decimal when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) => "The default must be a number, like 12.5.",
        CustomFieldType.Checkbox when !bool.TryParse(value, out _) => "The default must be true or false.",
        CustomFieldType.Date when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "The default must be a date like 2026-01-31.",
        CustomFieldType.DateTime when !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _) => "The default must be a date and time.",
        CustomFieldType.Dropdown when !(JsonNode.Parse(optionsJson) as JsonArray ?? []).Any(n => n?.GetValue<string>() == value) => "The default must be one of the options.",
        CustomFieldType.MultiSelect or CustomFieldType.DependentDropdown or CustomFieldType.Lookup => "This kind of field can't have a default.",
        _ => null,
    };

    private async Task<Guid?> DemandAsync(string entity, Guid? workspaceId, CancellationToken ct)
    {
        if (!FieldEntities.IsKnown(entity))
        {
            throw new NotFoundException();
        }
        if (FieldEntities.IsWorkspaceScoped(entity))
        {
            if (workspaceId is not { } ws)
            {
                throw new ValidationException("WorkspaceId", "Choose a workspace.");
            }
            await access.DemandAsync(Privileges.FieldsManage, ws, ct);
            return ws;
        }
        await access.DemandAsync(Privileges.FieldsManage, ct: ct);
        return null;
    }

    internal static string KeyFromLabel(string? label)
    {
        var slug = NonKey().Replace((label ?? "").Trim().ToLowerInvariant(), "_").Trim('_');
        if (slug.Length == 0 || !char.IsAsciiLetterLower(slug[0]))
        {
            slug = "field_" + slug;
        }
        return slug.Length > 64 ? slug[..64].TrimEnd('_') : slug;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonKey();
}
