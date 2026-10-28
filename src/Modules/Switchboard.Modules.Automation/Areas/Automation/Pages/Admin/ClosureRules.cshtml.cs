using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Automation.Closure;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Web;
using Switchboard.UI;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Admin;

public sealed class ClosureRulesModel(ClosureRuleService rules, ICustomFieldService fields) : AutomationAdminPageModel
{
    public List<AutClosureRule> Rules { get; private set; } = [];
    public IReadOnlyList<CustomFieldDefinition> Fields { get; private set; } = [];

    [BindProperty]
    public ClosureRuleKind Kind { get; set; } = ClosureRuleKind.AllTasksCompleted;

    [BindProperty]
    public string? AppliesTo { get; set; }

    [BindProperty]
    public string? FieldKey { get; set; }

    [BindProperty]
    public string? Message { get; set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadWorkspaceAsync(ct: ct))
        {
            return Forbid();
        }
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        if (!await LoadWorkspaceAsync(ct: ct))
        {
            return Forbid();
        }
        TicketType? type = Enum.TryParse<TicketType>(AppliesTo, out var t) ? t : null;
        try
        {
            await rules.AddAsync(Workspace.Id, new ClosureRuleInput(Kind, type, FieldKey, Message), ct);
            return Done("Closure rule added", "/admin/automations/closure-rules" + WsQuery);
        }
        catch (ValidationException ex)
        {
            Error = ex.Message;
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostEnableAsync(Guid id, bool enabled, CancellationToken ct)
    {
        await LoadWorkspaceAsync(ct: ct);
        await rules.SetEnabledAsync(id, enabled, ct);
        return Done(enabled ? "Rule turned on" : "Rule turned off", "/admin/automations/closure-rules" + WsQuery);
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        await LoadWorkspaceAsync(ct: ct);
        await rules.DeleteAsync(id, ct);
        return Done("Rule deleted", "/admin/automations/closure-rules" + WsQuery);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rules = await rules.ListAsync(Workspace.Id, ct);
        Fields = (await fields.GetDefinitionsAsync("ticket", Workspace.Id, ct)).Where(f => f.FieldType != CustomFieldType.Content).ToList();
    }

    public string FieldLabel(string? key) => Fields.FirstOrDefault(f => f.Key == key)?.Label ?? key ?? "";

    public static string TypeLabel(TicketType? type) => type switch
    {
        TicketType.Incident => "Incidents",
        TicketType.ServiceRequest => "Service requests",
        _ => "Every ticket",
    };
}
