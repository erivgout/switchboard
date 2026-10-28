using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;
using Switchboard.UI;

namespace Switchboard.Modules.Automation.Web;

/// <summary>Base for the admin automation pages: privilege gate and workspace choice (<c>?ws=</c>).</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + AutomationModule.ManagePrivilege)]
public abstract class AutomationAdminPageModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? Ws { get; set; }

    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];

    public Workspace Workspace { get; private set; } = default!;

    public bool ManyWorkspaces => Workspaces.Count > 1;

    /// <summary>Query string that keeps the chosen workspace when there is more than one.</summary>
    public string WsQuery => ManyWorkspaces ? $"?ws={Workspace.Id}" : "";

    protected SwitchboardDbContext Db => HttpContext.RequestServices.GetRequiredService<SwitchboardDbContext>();

    protected async Task<bool> LoadWorkspaceAsync(Guid? preferred = null, CancellationToken ct = default)
    {
        var permissions = HttpContext.RequestServices.GetRequiredService<IPermissionService>();
        var p = await permissions.CurrentAsync(ct);
        var all = await Db.Workspaces.AsNoTracking().Where(w => !w.IsArchived)
            .OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).ToListAsync(ct);
        Workspaces = all.Where(w => p?.Has(AutomationModule.ManagePrivilege, w.Id) == true).ToList();
        var wanted = preferred ?? Ws;
        Workspace = Workspaces.FirstOrDefault(w => w.Id == wanted) ?? Workspaces.FirstOrDefault()!;
        return Workspace is not null;
    }

    protected IActionResult Done(string message, string url, ToastKind kind = ToastKind.Success)
    {
        TempData.Toast(message, kind);
        return Redirect(url);
    }

    /// <summary>Link to a ticket page from its display id ("INC-1042" -> "/tickets/1042").</summary>
    public static string TicketHref(string displayId) => "/tickets/" + displayId[(displayId.LastIndexOf('-') + 1)..];

    public static string Json(object value) => JsonSerializer.Serialize(value, WorkflowDefinition.Json);

    /// <summary>Indented JSON for run logs, with secrets masked.</summary>
    public static string Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                .Replace("\"authValue\": \"enc:", "\"authValue\": \"•••• enc:");
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public static (string Css, string Icon, string Text) RunState(RunStatus? status) => status switch
    {
        RunStatus.Succeeded => ("aut-state--ok", "bi-check2-circle", "Succeeded"),
        RunStatus.Failed => ("aut-state--bad", "bi-x-octagon", "Failed"),
        RunStatus.Blocked => ("aut-state--warn", "bi-sign-stop", "Stopped by a limit"),
        RunStatus.Running => ("aut-state--live", "bi-hourglass-split", "Sending"),
        _ => ("aut-state--off", "bi-dash-circle", "Never run"),
    };

    public static (string Css, string Icon, string Text) StepState(StepStatus status) => status switch
    {
        StepStatus.Succeeded => ("aut-state--ok", "bi-check2", "Done"),
        StepStatus.Failed => ("aut-state--bad", "bi-x-octagon", "Failed"),
        StepStatus.Pending => ("aut-state--live", "bi-hourglass", "Waiting to send"),
        StepStatus.Sending => ("aut-state--live", "bi-send", "Sending"),
        StepStatus.Skipped => ("aut-state--off", "bi-skip-forward", "Not run"),
        StepStatus.RolledBack => ("aut-state--warn", "bi-arrow-counterclockwise", "Undone"),
        _ => ("aut-state--off", "bi-dash", status.ToString()),
    };
}
