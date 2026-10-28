using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Modules.Api.Data;
using Switchboard.Modules.Api.Webhooks;

namespace Switchboard.Modules.Api.Pages;

public sealed class WebhookForm
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    /// <summary>A workspace id, or <c>all</c>.</summary>
    public string? Workspace { get; set; }
    public List<string> Events { get; set; } = [];
    public bool IsActive { get; set; } = true;

    public Guid? WorkspaceId => Guid.TryParse(Workspace, out var id) ? id : null;

    public WebhookInput ToInput() => new(Name, Url, Events, WorkspaceId, IsActive);

    public static WebhookForm From(WebhookSummary hook) => new()
    {
        Name = hook.Name,
        Url = hook.Url,
        Workspace = hook.WorkspaceId?.ToString() ?? "all",
        Events = [.. hook.Events],
        IsActive = hook.IsActive,
    };
}

public sealed record WorkspaceChoice(string Value, string Name);

public sealed record WebhookFormModel(WebhookForm Form, IReadOnlyList<WorkspaceChoice> Workspaces, IReadOnlyDictionary<string, string[]>? Errors, string SubmitLabel)
{
    public string? Error(string field) => Errors?.TryGetValue(field, out var e) == true ? string.Join(" ", e) : null;
}

public static class WebhookUi
{
    public const string FormView = "/Areas/Api/Pages/Shared/_WebhookForm.cshtml";

    /// <summary>Workspaces the user may point a webhook at; "Every workspace" only for org admins.</summary>
    public static async Task<IReadOnlyList<WorkspaceChoice>> WorkspacesAsync(SwitchboardDbContext db, WebhookService webhooks, CancellationToken ct)
    {
        var manageable = await webhooks.ManageableAsync(ct);
        var rows = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).OrderBy(w => !w.IsPrimary).ThenBy(w => w.Name)
            .Select(w => new { w.Id, w.Name }).ToListAsync(ct);
        var choices = rows.Where(w => manageable is null || manageable.Contains(w.Id)).Select(w => new WorkspaceChoice(w.Id.ToString(), w.Name)).ToList();
        if (manageable is null)
        {
            choices.Insert(0, new WorkspaceChoice("all", "Every workspace"));
        }
        return choices;
    }

    public static (string Icon, string Label, string Tone) State(WebhookDeliveryStatus status, int attempts) => status switch
    {
        WebhookDeliveryStatus.Succeeded => ("bi-check-circle", "Delivered", "ok"),
        WebhookDeliveryStatus.Retrying => ("bi-arrow-repeat", $"Retrying after {attempts} {(attempts == 1 ? "try" : "tries")}", "wait"),
        WebhookDeliveryStatus.Dead => ("bi-x-octagon", "Failed", "bad"),
        _ => ("bi-hourglass-split", "Queued", "wait"),
    };

    public static string EventsSummary(IReadOnlyList<string> events) =>
        events.Count == WebhookEvents.All.Count ? "All ticket events"
        : events.Count == 1 ? WebhookEvents.Title(events[0])
        : $"{events.Count} events";

    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>Pretty-printed JSON for the delivery log; raw text when it isn't JSON.</summary>
    public static string Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }
}
