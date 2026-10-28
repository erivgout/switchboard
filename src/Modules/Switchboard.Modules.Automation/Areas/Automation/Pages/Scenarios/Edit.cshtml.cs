using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Scenarios;
using Switchboard.Modules.Automation.Web;
using Switchboard.UI;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Scenarios;

[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class EditModel(ScenarioService scenarios, CatalogLoader catalogs, IPermissionService permissions, SwitchboardDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = "";

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public ScenarioVisibility Visibility { get; set; } = ScenarioVisibility.Personal;
    [BindProperty] public Guid? GroupId { get; set; }
    [BindProperty] public Guid? WorkspaceId { get; set; }
    [BindProperty] public string ActionsJson { get; set; } = "[]";

    public AutScenario? Scenario { get; private set; }
    public List<Workspace> Workspaces { get; private set; } = [];
    public bool CanShare { get; private set; }
    public string CatalogJson { get; private set; } = "{}";
    public string EditorJson { get; private set; } = "{}";
    public IReadOnlyList<CatalogOption> Groups { get; private set; } = [];
    public List<string> Errors { get; private set; } = [];
    public static string AllowedActions => string.Join(",", ActionTypes.ScenarioAllowed);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        if (Scenario is not null)
        {
            Name = Scenario.Name;
            Description = Scenario.Description;
            Visibility = Scenario.Visibility;
            GroupId = Scenario.GroupId;
            ActionsJson = JsonSerializer.Serialize(ScenarioService.Actions(Scenario), WorkflowDefinition.Json);
        }
        await PrepareAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct))
        {
            return NotFound();
        }
        List<ActionDefinition> actions;
        try
        {
            actions = JsonSerializer.Deserialize<WorkflowDefinition>(ActionsJson, WorkflowDefinition.Json)?.Actions ?? [];
        }
        catch (JsonException)
        {
            actions = [];
        }
        try
        {
            var saved = await scenarios.SaveAsync(Scenario?.Id, new ScenarioInput(WorkspaceId!.Value, Name, Description, Visibility, GroupId, actions), ct);
            TempData.Toast("Scenario saved");
            return Redirect($"/automations/scenarios/{saved.Id}");
        }
        catch (ValidationException ex)
        {
            Errors = ex.Errors.SelectMany(e => e.Value).ToList();
        }
        catch (ForbiddenException ex)
        {
            Errors = [ex.Message];
        }
        ActionsJson = JsonSerializer.Serialize(actions, WorkflowDefinition.Json);
        await PrepareAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct) || Scenario is null)
        {
            return NotFound();
        }
        await scenarios.DeleteAsync(Scenario.Id, ct);
        TempData.Toast("Scenario deleted");
        return Redirect("/automations/scenarios");
    }

    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var p = await permissions.CurrentAsync(ct) ?? throw new ForbiddenException();
        var allowed = p.IsOrgAdmin
            ? await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).Select(w => w.Id).ToListAsync(ct)
            : p.WorkspacesWith(Privileges.TicketsView).ToList();
        Workspaces = await db.Workspaces.AsNoTracking().Where(w => allowed.Contains(w.Id) && !w.IsArchived)
            .OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).ToListAsync(ct);
        if (Id != "new")
        {
            if (!Guid.TryParse(Id, out var id))
            {
                return false;
            }
            Scenario = await scenarios.GetForEditAsync(id, ct);
            WorkspaceId = Scenario.WorkspaceId;
        }
        WorkspaceId = Workspaces.FirstOrDefault(w => w.Id == WorkspaceId)?.Id ?? Workspaces.FirstOrDefault()?.Id;
        return WorkspaceId is not null;
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        var catalog = await catalogs.LoadAsync(WorkspaceId!.Value, ct);
        CanShare = await scenarios.CanManageSharedAsync(WorkspaceId.Value, ct);
        Groups = catalog.Field("ticket.group")?.Options ?? [];
        CatalogJson = AutomationAdminPageModel.Json(catalog);
        List<ActionDefinition> actions;
        try
        {
            actions = JsonSerializer.Deserialize<List<ActionDefinition>>(ActionsJson, WorkflowDefinition.Json) ?? [];
        }
        catch (JsonException)
        {
            actions = [];
        }
        EditorJson = AutomationAdminPageModel.Json(new { actions });
    }
}
