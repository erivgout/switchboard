using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;
using Switchboard.Modules.Automation.Web;
using Switchboard.Modules.Automation.Workflows;
using Switchboard.UI;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Admin;

public sealed class IndexModel(
    WorkflowService workflows,
    CatalogLoader catalogs,
    AutomationSettings automation,
    ISettingsService settings,
    IPermissionService permissions,
    IAuditLog audit,
    AutomationClockSignal clock) : AutomationAdminPageModel
{
    public sealed record Row(AutWorkflow Workflow, string SentenceHtml);

    public List<Row> Rows { get; private set; } = [];
    public bool InstancePaused { get; private set; }
    public bool WorkspacePaused { get; private set; }
    public bool CanPauseInstance { get; private set; }
    public int Queued { get; private set; }
    public bool QueueMode { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadWorkspaceAsync(ct: ct))
        {
            return Forbid();
        }
        var catalog = await catalogs.LoadAsync(Workspace.Id, ct);
        foreach (var wf in await workflows.ListAsync(Workspace.Id, ct))
        {
            Rows.Add(new Row(wf, Describer.Sentence(WorkflowDefinition.Parse(wf.PublishedJson ?? wf.DraftJson), catalog)));
        }
        InstancePaused = await settings.GetAsync(AutomationSettingKeys.Paused, false, null, ct);
        WorkspacePaused = !InstancePaused && await automation.IsPausedAsync(Workspace.Id, ct);
        CanPauseInstance = (await permissions.CurrentAsync(ct))?.IsOrgAdmin == true;
        QueueMode = await automation.QueueWhilePausedAsync(Workspace.Id, ct);
        Queued = await Db.Set<AutQueuedEvent>().CountAsync(q => q.WorkspaceId == Workspace.Id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var wf = await workflows.GetAsync(id, ct);
        await workflows.SetEnabledAsync(id, enabled, ct);
        await LoadWorkspaceAsync(wf.WorkspaceId, ct);
        return Done(enabled ? "Workflow turned on" : "Workflow turned off", "/admin/automations" + WsQuery);
    }

    public async Task<IActionResult> OnPostMoveAsync(Guid id, int direction, CancellationToken ct)
    {
        var wf = await workflows.GetAsync(id, ct);
        await workflows.MoveAsync(id, direction, ct);
        await LoadWorkspaceAsync(wf.WorkspaceId, ct);
        return Done(direction < 0 ? "Moved earlier" : "Moved later", "/admin/automations" + WsQuery);
    }

    public Task<IActionResult> OnPostPauseAsync(bool everywhere, CancellationToken ct) => SetPausedAsync(true, everywhere, ct);

    public Task<IActionResult> OnPostResumeAsync(bool everywhere, CancellationToken ct) => SetPausedAsync(false, everywhere, ct);

    private async Task<IActionResult> SetPausedAsync(bool paused, bool everywhere, CancellationToken ct)
    {
        if (!await LoadWorkspaceAsync(ct: ct))
        {
            return Forbid();
        }
        if (everywhere)
        {
            if ((await permissions.CurrentAsync(ct))?.IsOrgAdmin != true)
            {
                throw new ForbiddenException("Only organization admins can pause automations everywhere.");
            }
            await settings.SetAsync(AutomationSettingKeys.Paused, paused, null, ct);
        }
        else
        {
            await permissions.DemandAsync(AutomationModule.ManagePrivilege, Workspace.Id, ct);
            await settings.SetAsync(AutomationSettingKeys.Paused, paused, Workspace.Id, ct);
        }
        audit.Record(paused ? "automation.paused" : "automation.resumed", "workspace", everywhere ? "instance" : Workspace.Id.ToString(), new { everywhere });
        await Db.SaveChangesAsync(ct);
        if (!paused)
        {
            clock.Notify();
        }
        return Done(paused ? "Automations paused" : "Automations resumed", "/admin/automations" + WsQuery);
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file, CancellationToken ct)
    {
        if (!await LoadWorkspaceAsync(ct: ct))
        {
            return Forbid();
        }
        if (file is null || file.Length == 0)
        {
            return Done("Choose a workflow file to import.", "/admin/automations" + WsQuery, ToastKind.Error);
        }
        if (file.Length > WorkflowService.MaxImportBytes)
        {
            return Done("That file is too large to be a workflow export.", "/admin/automations" + WsQuery, ToastKind.Error);
        }
        using var reader = new StreamReader(file.OpenReadStream());
        try
        {
            var wf = await workflows.ImportAsync(Workspace.Id, await reader.ReadToEndAsync(ct), ct);
            return Done("Workflow imported as a draft. Review it, then publish.", $"/admin/automations/{wf.Id}");
        }
        catch (ValidationException ex)
        {
            return Done(ex.Message, "/admin/automations" + WsQuery, ToastKind.Error);
        }
    }
}
