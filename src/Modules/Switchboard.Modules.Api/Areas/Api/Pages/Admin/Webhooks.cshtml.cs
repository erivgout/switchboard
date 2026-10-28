using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Data;
using Switchboard.Modules.Api.Pages;
using Switchboard.Modules.Api.Webhooks;
using Switchboard.UI;

namespace Switchboard.Modules.Api.Areas.Api.Pages.Admin;

/// <summary>Webhook list and the dead-letter list with replay (FR-INT-3).</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.SettingsManage)]
public sealed class WebhooksModel(WebhookService webhooks) : PageModel
{
    public IReadOnlyList<WebhookSummary> Hooks { get; private set; } = [];
    public IReadOnlyList<DeliveryRow> Failed { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Hooks = await webhooks.ListAsync(HttpContext.RequestAborted);
        Failed = await webhooks.DeliveriesAsync(null, WebhookDeliveryStatus.Dead, 50, HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostReplayAsync(Guid id)
    {
        try
        {
            await webhooks.ReplayAsync(id, HttpContext.RequestAborted);
            TempData.Toast("Delivery queued to send again.");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return RedirectToPage();
    }
}

/// <summary>New webhook. The signing secret is shown in the response to the create, and never again.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.SettingsManage)]
public sealed class WebhookNewModel(WebhookService webhooks, SwitchboardDbContext db) : PageModel
{
    [BindProperty]
    public WebhookForm Form { get; set; } = new() { Events = ["ticket.created", "ticket.updated"] };

    public WebhookFormModel FormModel { get; private set; } = default!;
    public WebhookSummary? Created { get; private set; }
    public string? Secret { get; private set; }

    public async Task OnGetAsync() => await LoadAsync(null);

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            (Created, Secret) = await webhooks.CreateAsync(Form.ToInput(), HttpContext.RequestAborted);
            Response.Headers.CacheControl = "no-store";
            return Page();
        }
        catch (ValidationException ex)
        {
            await LoadAsync(ex.Errors);
            return Page();
        }
    }

    private async Task LoadAsync(IReadOnlyDictionary<string, string[]>? errors)
    {
        var workspaces = await WebhookUi.WorkspacesAsync(db, webhooks, HttpContext.RequestAborted);
        Form.Workspace ??= workspaces.FirstOrDefault(w => w.Value != "all")?.Value ?? workspaces.FirstOrDefault()?.Value;
        FormModel = new WebhookFormModel(Form, workspaces, errors, "Create webhook");
    }
}
