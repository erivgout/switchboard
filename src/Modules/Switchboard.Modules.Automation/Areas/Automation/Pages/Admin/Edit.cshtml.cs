using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;
using Switchboard.Modules.Automation.Web;
using Switchboard.Modules.Automation.Workflows;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Admin;

public sealed partial class EditModel(
    WorkflowService workflows,
    CatalogLoader catalogs,
    WorkflowTestRunner tester,
    IPermissionService permissions) : AutomationAdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = "";

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string? Description { get; set; }

    [BindProperty]
    public string DefinitionJson { get; set; } = "{}";

    [BindProperty]
    public string? TestTicket { get; set; }

    public AutWorkflow? Workflow { get; private set; }
    public bool IsNew => Workflow is null;
    public string CatalogJson { get; private set; } = "{}";
    public string EditorJson { get; private set; } = "{}";
    public string SentenceHtml { get; private set; } = "";
    public List<AutWorkflowVersion> Versions { get; private set; } = [];
    public List<AutRun> RecentRuns { get; private set; } = [];
    public List<string> Errors { get; private set; } = [];
    public string? ErrorIntro { get; private set; }

    public string PageUrl => $"/admin/automations/{(Workflow?.Id.ToString() ?? "new")}";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        WorkflowDefinition def;
        if (Workflow is null)
        {
            def = new WorkflowDefinition();
        }
        else
        {
            Name = Workflow.Name;
            Description = Workflow.Description;
            def = WorkflowService.ForEditor(Workflow.DraftJson);
        }
        await PrepareAsync(def, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        var (saved, def) = await TrySaveAsync(ct);
        if (saved is null)
        {
            await PrepareAsync(def, ct);
            return Page();
        }
        return Done(saved.PublishedVersion is null ? "Draft saved" : "Draft saved. Publish to make it live.", $"/admin/automations/{saved.Id}");
    }

    public async Task<IActionResult> OnPostPublishAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        var (saved, def) = await TrySaveAsync(ct);
        if (saved is null)
        {
            await PrepareAsync(def, ct);
            return Page();
        }
        try
        {
            var version = await workflows.PublishAsync(saved.Id, ct);
            return Done($"Published version {version.Number}", $"/admin/automations/{saved.Id}");
        }
        catch (ValidationException ex)
        {
            Workflow = await workflows.GetAsync(saved.Id, ct);
            ErrorIntro = "Your draft is saved, but it can't go live yet. Fix these first:";
            Errors = ex.Errors.SelectMany(e => e.Value).ToList();
            await PrepareAsync(def, ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDescribeAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        var catalog = await catalogs.LoadAsync(Workspace.Id, ct);
        return Content(Describer.Sentence(ParseOrEmpty(DefinitionJson), catalog), "text/html", Encoding.UTF8);
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        TestReport? report = null;
        var number = TicketNumber().Match(TestTicket ?? "");
        if (number.Success && int.TryParse(number.Value, out var n))
        {
            var visible = await permissions.AgentVisibleTicketsAsync(ct);
            var ticketId = await visible.Where(t => t.Number == n && t.WorkspaceId == Workspace.Id && t.PurgedAt == null)
                .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
            if (ticketId is { } id)
            {
                var name = string.IsNullOrWhiteSpace(Name) ? "New workflow" : Name.Trim();
                report = await tester.RunAsync(ParseOrEmpty(DefinitionJson), Workspace.Id, id, name, ct);
            }
        }
        return Partial("~/Areas/Automation/Pages/Shared/_TestReport.cshtml", report);
    }

    public async Task<IActionResult> OnPostEnableAsync(bool enabled, CancellationToken ct)
    {
        if (!await LoadAsync(ct) || Workflow is null)
        {
            return NotFound();
        }
        await workflows.SetEnabledAsync(Workflow.Id, enabled, ct);
        return Done(enabled ? "Workflow turned on" : "Workflow turned off", PageUrl);
    }

    public async Task<IActionResult> OnPostRollbackAsync(int number, CancellationToken ct)
    {
        if (!await LoadAsync(ct) || Workflow is null)
        {
            return NotFound();
        }
        try
        {
            var version = await workflows.RollbackAsync(Workflow.Id, number, ct);
            return Done($"Rolled back. Version {version.Number} is live and matches version {number}.", PageUrl);
        }
        catch (ValidationException ex)
        {
            return Done($"Version {number} can't go live any more: {ex.Errors.SelectMany(e => e.Value).FirstOrDefault()}", PageUrl, UI.ToastKind.Error);
        }
    }

    public async Task<IActionResult> OnPostCloneAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct) || Workflow is null)
        {
            return NotFound();
        }
        var copy = await workflows.CloneAsync(Workflow.Id, ct);
        return Done("Workflow copied. The copy is a draft and turned off.", $"/admin/automations/{copy.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct) || Workflow is null)
        {
            return NotFound();
        }
        await workflows.DeleteAsync(Workflow.Id, ct);
        return Done("Workflow deleted", "/admin/automations" + WsQuery);
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct) || Workflow is null)
        {
            return NotFound();
        }
        var json = await workflows.ExportAsync(Workflow.Id, ct);
        var slug = Slug().Replace(Workflow.Name.ToLowerInvariant(), "-").Trim('-');
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"{(slug.Length == 0 ? "workflow" : slug)}.json");
    }

    /// <summary>Loads the workflow (or a new one) and the workspace it belongs to.</summary>
    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        if (Id == "new")
        {
            return await LoadWorkspaceAsync(ct: ct);
        }
        if (!Guid.TryParse(Id, out var id))
        {
            return false;
        }
        Workflow = await workflows.GetAsync(id, ct);
        return await LoadWorkspaceAsync(Workflow.WorkspaceId, ct) && Workspace.Id == Workflow.WorkspaceId;
    }

    private async Task<(AutWorkflow? Saved, WorkflowDefinition Definition)> TrySaveAsync(CancellationToken ct)
    {
        WorkflowDefinition def;
        try
        {
            def = WorkflowDefinition.Parse(DefinitionJson);
        }
        catch (JsonException)
        {
            Errors = ["The workflow couldn't be read. Reload the page and try again."];
            return (null, new WorkflowDefinition());
        }
        try
        {
            return (await workflows.SaveDraftAsync(Workflow?.Id, Workspace.Id, Name, Description, def, ct), def);
        }
        catch (ValidationException ex)
        {
            ErrorIntro = "The draft wasn't saved:";
            Errors = ex.Errors.SelectMany(e => e.Value).ToList();
            return (null, def);
        }
    }

    private async Task PrepareAsync(WorkflowDefinition def, CancellationToken ct)
    {
        var catalog = await catalogs.LoadAsync(Workspace.Id, ct);
        // Never send stored secrets back to the browser.
        var editor = SecretProtector.Redact(def, keepFlag: true);
        if (Workflow is not null)
        {
            var stored = WorkflowDefinition.Parse(Workflow.DraftJson);
            foreach (var action in editor.Actions.Where(a => a.Type == ActionTypes.WebRequest))
            {
                if (stored.Actions.Any(s => s.Id == action.Id && !string.IsNullOrEmpty(s.Get("authValue"))))
                {
                    action.Params[SecretProtector.SetFlag] = "true";
                }
            }
        }
        CatalogJson = Json(catalog);
        EditorJson = Json(editor);
        SentenceHtml = Describer.Sentence(def, catalog);
        if (Workflow is not null)
        {
            Versions = await workflows.VersionsAsync(Workflow.Id, ct);
            RecentRuns = await Db.Set<AutRun>().AsNoTracking().Where(r => r.WorkflowId == Workflow.Id)
                .OrderByDescending(r => r.StartedAt).Take(5).ToListAsync(ct);
        }
    }

    private static WorkflowDefinition ParseOrEmpty(string json)
    {
        try
        {
            return WorkflowDefinition.Parse(json);
        }
        catch (JsonException)
        {
            return new WorkflowDefinition();
        }
    }

    [GeneratedRegex(@"\d{1,9}")]
    private static partial Regex TicketNumber();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Slug();
}
