using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Pages;

/// <summary>Shared helpers for admin pages: validation errors, workspace selection and look-up lists.</summary>
public abstract class AdminPageModel : PageModel
{
    /// <summary>Copies service validation errors into ModelState. Field keys get <paramref name="prefix"/>; custom field keys stay as is.</summary>
    protected void AddErrors(ValidationException ex, string prefix = "Input")
    {
        foreach (var (key, messages) in ex.Errors)
        {
            var name = key.StartsWith("custom.", StringComparison.Ordinal) || prefix.Length == 0 ? key : $"{prefix}.{key}";
            foreach (var message in messages)
            {
                ModelState.AddModelError(name, message);
            }
        }
    }

    /// <summary>Runs a service call; validation errors land in ModelState and the result is false.</summary>
    protected async Task<bool> TryAsync(Func<Task> action, string prefix = "Input")
    {
        try
        {
            await action();
            return true;
        }
        catch (ValidationException ex)
        {
            AddErrors(ex, prefix);
            return false;
        }
    }

    /// <summary>
    /// Reads a "move them to" choice: an id, or <c>none</c> to clear the reference. With <paramref name="required"/>, an
    /// empty choice adds an error so nobody deletes in-use configuration by accident (SR-DEL-2).
    /// </summary>
    protected bool TryReadReplacement(string? choice, bool required, string message, out Guid? replacement)
    {
        replacement = Guid.TryParse(choice, out var id) ? id : null;
        if (required && replacement is null && choice != "none")
        {
            ModelState.AddModelError("ReplacementId", message);
            return false;
        }
        return true;
    }

    /// <summary>The workspace named by <c>?ws=</c>, or the primary one.</summary>
    protected static async Task<(Workspace Current, IReadOnlyList<Workspace> All)> ResolveWorkspaceAsync(SwitchboardDbContext db, string? key)
    {
        var all = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).ToListAsync();
        var current = all.FirstOrDefault(w => w.Key == key) ?? all.FirstOrDefault(w => w.IsPrimary) ?? all.FirstOrDefault() ?? throw new NotFoundException();
        return (current, all);
    }

    /// <summary>A path plus the non-empty query values, in order.</summary>
    public static string Link(string path, params (string Name, string? Value)[] query)
    {
        var parts = query.Where(q => !string.IsNullOrEmpty(q.Value)).Select(q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value!)}").ToList();
        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    public static string ActorLabel(Core.Domain.AuditEntry e) => e.ActorKind switch
    {
        ActorKind.System => e.ActorName.Length > 0 ? e.ActorName : "System",
        ActorKind.Anonymous => "Not signed in",
        _ => e.ActorName,
    };

    public static string ScopeLabel(TicketScope scope) => scope switch
    {
        TicketScope.Global => "All tickets",
        TicketScope.Group => "Their groups' tickets",
        _ => "Only tickets assigned to them",
    };

    public static string ModeLabel(AssignmentMode mode) => mode switch
    {
        AssignmentMode.RoundRobin => "Round robin",
        AssignmentMode.LoadBased => "Load balanced",
        AssignmentMode.SkillBased => "By skill",
        _ => "Manual",
    };
}

/// <summary>Select lists shared by admin pages.</summary>
public static class AdminSelects
{
    public static async Task<List<SelectListItem>> DepartmentsAsync(SwitchboardDbContext db, Guid? selected, string none = "No department") =>
        (await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync())
        .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == selected))
        .Prepend(new SelectListItem(none, "", selected is null))
        .ToList();

    /// <summary>Locations indented by depth (parents first).</summary>
    public static async Task<List<SelectListItem>> LocationsAsync(SwitchboardDbContext db, Guid? selected, Guid? exclude = null, string none = "No location")
    {
        var all = await db.Locations.AsNoTracking().OrderBy(l => l.Name).Select(l => new { l.Id, l.Name, l.ParentId }).ToListAsync();
        var list = new List<SelectListItem> { new(none, "", selected is null) };
        var ids = all.Select(l => l.Id).ToHashSet();
        void Walk(Guid? parent, int depth)
        {
            foreach (var l in all.Where(l => l.ParentId == parent || (parent is null && l.ParentId is { } p && !ids.Contains(p))))
            {
                if (l.Id == exclude || list.Any(i => i.Value == l.Id.ToString()))
                {
                    continue;
                }
                list.Add(new SelectListItem(new string(' ', depth * 3) + l.Name, l.Id.ToString(), l.Id == selected));
                Walk(l.Id, depth + 1);
            }
        }
        Walk(null, 0);
        return list;
    }

    public static IEnumerable<SelectListItem> TimeZones(string? selected, string none = "Organization default") =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(z => new SelectListItem(z.DisplayName, z.Id, z.Id == selected))
            .Prepend(new SelectListItem("UTC", "UTC", selected == "UTC"))
            .Prepend(new SelectListItem(none, "", string.IsNullOrEmpty(selected)))
            .DistinctBy(i => i.Value)
            .ToList();

    public static async Task<List<SelectListItem>> RolesAsync(SwitchboardDbContext db, Guid? selected, string? none = null)
    {
        var roles = (await db.Roles.AsNoTracking().OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name).Select(r => new { r.Id, r.Name }).ToListAsync())
            .Select(r => new SelectListItem(r.Name, r.Id.ToString(), r.Id == selected)).ToList();
        if (none is not null)
        {
            roles.Insert(0, new SelectListItem(none, "", selected is null));
        }
        return roles;
    }
}

public sealed record PagerModel(int Page, int Pages, int Total, Func<int, string> Url);

public sealed record TabModel(string Href, string Label, bool Current);

public sealed record CustomFieldsModel(IReadOnlyList<CustomFieldDefinition> Definitions, JsonObject Values, bool Disabled)
{
    /// <summary>Choices for lookups of departments, locations and groups, by target.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(Guid Id, string Name)>> Lookups { get; init; } = new Dictionary<string, IReadOnlyList<(Guid, string)>>();

    /// <summary>Names of people currently selected in person lookups.</summary>
    public IReadOnlyDictionary<Guid, PersonOption> People { get; init; } = new Dictionary<Guid, PersonOption>();

    public static async Task<CustomFieldsModel> LoadAsync(SwitchboardDbContext db, IReadOnlyList<CustomFieldDefinition> definitions, string json, bool disabled)
    {
        var values = Core.Services.CustomFieldService.Parse(json);
        var targets = definitions.Where(d => d.FieldType == CustomFieldType.Lookup).Select(FieldAdminService.LookupTarget).OfType<string>().ToHashSet();
        var lookups = new Dictionary<string, IReadOnlyList<(Guid, string)>>();
        if (targets.Contains("department"))
        {
            lookups["department"] = (await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync()).Select(d => (d.Id, d.Name)).ToList();
        }
        if (targets.Contains("location"))
        {
            lookups["location"] = (await db.Locations.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync()).Select(d => (d.Id, d.Name)).ToList();
        }
        if (targets.Contains("group"))
        {
            lookups["group"] = (await db.AgentGroups.AsNoTracking().OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync()).Select(d => (d.Id, d.Name)).ToList();
        }
        var personIds = definitions.Where(d => FieldAdminService.LookupTarget(d) is "user" or "agent")
            .Select(d => Guid.TryParse(CustomFieldForm.Text(d, values), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToList();
        var people = await db.Users.AsNoTracking().Where(u => personIds.Contains(u.Id))
            .Select(u => new PersonOption(u.Id, u.DisplayName, u.Email, u.IsAgent || u.IsOrgAdmin)).ToDictionaryAsync(p => p.Id);
        return new CustomFieldsModel(definitions, values, disabled) { Lookups = lookups, People = people };
    }
}

public sealed record WorkspacePickerModel(Workspace Current, IReadOnlyList<Workspace> Workspaces, IReadOnlyList<(string Name, string Value)> Keep);

public sealed record PersonPickerModel(string Id, string Name, string Label, string Kind, PersonOption? Selected)
{
    /// <summary>Label of the empty choice; null makes a choice required.</summary>
    public string? NoneLabel { get; init; } = "No one";
    public string? Help { get; init; }
    public bool Disabled { get; init; }
}

/// <summary>Reads and renders custom field values as form controls named <c>custom.&lt;key&gt;</c>.</summary>
public static class CustomFieldForm
{
    public static string Name(CustomFieldDefinition d) => $"custom.{d.Key}";

    /// <summary>The submitted values of every editable field, as the JSON the custom field service validates.</summary>
    public static Dictionary<string, JsonElement?> Read(IFormCollection form, IEnumerable<CustomFieldDefinition> definitions)
    {
        var values = new Dictionary<string, JsonElement?>();
        foreach (var d in definitions.Where(d => d.FieldType != CustomFieldType.Content))
        {
            var raw = form[Name(d)];
            JsonElement? value = d.FieldType switch
            {
                CustomFieldType.Checkbox => JsonSerializer.SerializeToElement(raw.Contains("true")),
                CustomFieldType.MultiSelect => raw.Count == 0 ? null : JsonSerializer.SerializeToElement(raw.Where(v => !string.IsNullOrEmpty(v)).ToArray()),
                CustomFieldType.DependentDropdown => string.IsNullOrWhiteSpace(raw.ToString()) ? null : Path(raw.ToString()),
                _ => string.IsNullOrWhiteSpace(raw.ToString()) ? null : JsonSerializer.SerializeToElement(raw.ToString().Trim()),
            };
            if (d.FieldType == CustomFieldType.MultiSelect && value is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() == 0)
            {
                value = null;
            }
            values[d.Key] = value;
        }
        return values;
    }

    /// <summary>Submitted values as a JSON object, to re-show the form after a validation error.</summary>
    public static string ToJson(IReadOnlyDictionary<string, JsonElement?> values) =>
        JsonSerializer.Serialize(values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value!.Value));

    private static JsonElement? Path(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) is { Length: > 0 } path ? JsonSerializer.SerializeToElement(path) : null;
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new[] { json });
        }
    }

    /// <summary>Current value as text for single-value controls.</summary>
    public static string? Text(CustomFieldDefinition d, JsonObject values)
    {
        if (!values.TryGetPropertyValue(d.Key, out var node) || node is null)
        {
            return d.DefaultValue;
        }
        return d.FieldType switch
        {
            CustomFieldType.DependentDropdown => node.ToJsonString(),
            CustomFieldType.DateTime => DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
                ? dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) : null,
            _ => node is JsonValue v ? v.ToString() : node.ToJsonString(),
        };
    }

    public static IReadOnlyList<string> List(CustomFieldDefinition d, JsonObject values) =>
        values.TryGetPropertyValue(d.Key, out var node) && node is JsonArray a ? a.Select(n => n?.ToString() ?? "").ToList() : [];

    public static IReadOnlyList<string> Options(CustomFieldDefinition d)
    {
        try
        {
            return JsonNode.Parse(d.OptionsJson) is JsonArray a ? a.Select(n => n is JsonObject o ? o["value"]?.ToString() ?? "" : n?.ToString() ?? "").ToList() : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Every valid path of a dependent dropdown, as (label, JSON value).</summary>
    public static IReadOnlyList<(string Label, string Value)> Paths(CustomFieldDefinition d)
    {
        var result = new List<(string, string)>();
        void Walk(JsonArray? level, List<string> prefix)
        {
            foreach (var o in level?.OfType<JsonObject>() ?? [])
            {
                var path = new List<string>(prefix) { o["value"]?.ToString() ?? "" };
                result.Add((string.Join(" › ", path), JsonSerializer.Serialize(path)));
                Walk(o["children"] as JsonArray, path);
            }
        }
        try
        {
            Walk(JsonNode.Parse(d.OptionsJson) as JsonArray, []);
        }
        catch (JsonException)
        {
        }
        return result;
    }
}
