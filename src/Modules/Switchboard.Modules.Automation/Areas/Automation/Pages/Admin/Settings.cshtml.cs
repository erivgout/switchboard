using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Automation.Engine;
using Switchboard.Modules.Automation.Web;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Admin;

/// <summary>Safety limits (SR-AUT-1, SR-AUT-4) and what happens to events while paused (SR-AUT-2), per workspace.</summary>
public sealed class SettingsModel(ISettingsService settings, AutomationSettings automation, IPermissionService permissions, IAuditLog audit) : AutomationAdminPageModel
{
    [BindProperty] public string PauseMode { get; set; } = "queue";
    [BindProperty] public int MaxCascadeDepth { get; set; }
    [BindProperty] public int FailureThreshold { get; set; }
    [BindProperty] public int WorkflowRateLimit { get; set; }
    [BindProperty] public int TicketRateLimit { get; set; }

    public Dictionary<string, string> Errors { get; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadWorkspaceAsync(ct: ct))
        {
            return Forbid();
        }
        var limits = await automation.LimitsAsync(Workspace.Id, ct);
        PauseMode = await automation.QueueWhilePausedAsync(Workspace.Id, ct) ? "queue" : "drop";
        MaxCascadeDepth = limits.MaxCascadeDepth;
        FailureThreshold = limits.FailureThreshold;
        WorkflowRateLimit = limits.WorkflowRateLimit;
        TicketRateLimit = limits.TicketRateLimit;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadWorkspaceAsync(ct: ct))
        {
            return Forbid();
        }
        await permissions.DemandAsync(AutomationModule.ManagePrivilege, Workspace.Id, ct);
        if (PauseMode is not ("queue" or "drop"))
        {
            Errors[nameof(PauseMode)] = "Choose what happens to events while paused.";
        }
        if (MaxCascadeDepth is < 1 or > 10)
        {
            Errors[nameof(MaxCascadeDepth)] = "Enter a chain length from 1 to 10.";
        }
        if (FailureThreshold is < 1 or > 100)
        {
            Errors[nameof(FailureThreshold)] = "Enter a number of failures from 1 to 100.";
        }
        if (WorkflowRateLimit is < 1 or > 100_000)
        {
            Errors[nameof(WorkflowRateLimit)] = "Enter a number of runs from 1 to 100,000.";
        }
        if (TicketRateLimit is < 1 or > 10_000)
        {
            Errors[nameof(TicketRateLimit)] = "Enter a number of runs from 1 to 10,000.";
        }
        if (Errors.Count > 0)
        {
            return Page();
        }

        await settings.SetAsync(AutomationSettingKeys.PauseMode, PauseMode, Workspace.Id, ct);
        await settings.SetAsync(AutomationSettingKeys.MaxCascadeDepth, MaxCascadeDepth, Workspace.Id, ct);
        await settings.SetAsync(AutomationSettingKeys.FailureThreshold, FailureThreshold, Workspace.Id, ct);
        await settings.SetAsync(AutomationSettingKeys.WorkflowRateLimit, WorkflowRateLimit, Workspace.Id, ct);
        await settings.SetAsync(AutomationSettingKeys.TicketRateLimit, TicketRateLimit, Workspace.Id, ct);
        audit.Record("automation.settings_changed", "workspace", Workspace.Id.ToString(),
            new { PauseMode, MaxCascadeDepth, FailureThreshold, WorkflowRateLimit, TicketRateLimit });
        await Db.SaveChangesAsync(ct);
        return Done("Automation settings saved", "/admin/automations/settings" + WsQuery);
    }
}
