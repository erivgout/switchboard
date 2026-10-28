using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Statuses;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.FieldsManage)]
public sealed class IndexModel(TicketConfigService config, SwitchboardDbContext db) : AdminPageModel
{
    public sealed class StatusForm
    {
        public string Name { get; set; } = "";
        public string? RequesterLabel { get; set; }
        public StatusCategory Category { get; set; } = StatusCategory.Pending;
        public bool SlaTimerOn { get; set; }
        public bool IsDefault { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public string? Ws { get; set; }

    [BindProperty]
    public StatusForm Input { get; set; } = new();

    public Workspace Workspace { get; private set; } = default!;
    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
    public IReadOnlyList<StatusRow> Statuses { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (await TryAsync(() => config.CreateStatusAsync(Workspace.Id, new StatusInput(Input.Name, Input.RequesterLabel, Input.Category, Input.SlaTimerOn, Input.IsDefault))))
        {
            TempData.Toast("Status added");
            return Redirect(Link("/admin/statuses", ("ws", Ws)));
        }
        return Page();
    }

    public async Task<IActionResult> OnPostMoveAsync(Guid statusId, int delta)
    {
        await config.MoveStatusAsync(statusId, delta);
        return Redirect(Link("/admin/statuses", ("ws", Ws)) + "#status-" + statusId);
    }

    private async Task LoadAsync()
    {
        (Workspace, Workspaces) = await ResolveWorkspaceAsync(db, Ws);
        Statuses = await config.ListStatusesAsync(Workspace.Id);
    }

    public static string CategoryHint(StatusCategory c) => c switch
    {
        StatusCategory.Open => "Someone is working on it.",
        StatusCategory.Pending => "Waiting on the requester or a third party.",
        StatusCategory.Resolved => "Fixed; the requester can still reopen it.",
        _ => "Finished for good.",
    };
}
