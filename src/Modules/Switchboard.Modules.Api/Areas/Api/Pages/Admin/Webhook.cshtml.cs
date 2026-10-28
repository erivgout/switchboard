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

/// <summary>One webhook: settings, signing secret, test delivery and the delivery log.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.SettingsManage)]
public sealed class WebhookModel(WebhookService webhooks, SwitchboardDbContext db, WebhookSignal signal) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty(SupportsGet = true, Name = "show")]
    public string Show { get; set; } = "all";

    [BindProperty]
    public WebhookForm Form { get; set; } = new();

    public WebhookSummary Hook { get; private set; } = default!;
    public WebhookFormModel FormModel { get; private set; } = default!;
    public IReadOnlyList<DeliveryRow> Deliveries { get; private set; } = [];
    public string? NewSecret { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync(fromHook: true, errors: null);
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        try
        {
            await webhooks.UpdateAsync(Id, Form.ToInput(), HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            await LoadAsync(fromHook: false, ex.Errors);
            return Page();
        }
        TempData.Toast("Changes saved.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRotateAsync()
    {
        NewSecret = await webhooks.RotateSecretAsync(Id, HttpContext.RequestAborted);
        Response.Headers.CacheControl = "no-store";
        await LoadAsync(fromHook: true, errors: null);
        return Page();
    }

    public async Task<IActionResult> OnPostPingAsync()
    {
        await webhooks.PingAsync(Id, HttpContext.RequestAborted);
        signal.Notify();
        TempData.Toast("Test delivery queued. It shows in the log below within a few seconds.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostPauseAsync(bool active)
    {
        await webhooks.SetActiveAsync(Id, active, HttpContext.RequestAborted);
        TempData.Toast(active ? "Webhook resumed." : "Webhook paused.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await webhooks.DeleteAsync(Id, HttpContext.RequestAborted);
        TempData.Toast("Webhook deleted.");
        return Redirect("/admin/webhooks");
    }

    public async Task<IActionResult> OnPostReplayAsync(Guid deliveryId)
    {
        try
        {
            await webhooks.ReplayAsync(deliveryId, HttpContext.RequestAborted);
            signal.Notify();
            TempData.Toast("Delivery queued to send again.");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return RedirectToPage(new { id = Id, show = Show });
    }

    private async Task LoadAsync(bool fromHook, IReadOnlyDictionary<string, string[]>? errors)
    {
        Hook = await webhooks.GetAsync(Id, HttpContext.RequestAborted);
        if (fromHook)
        {
            Form = WebhookForm.From(Hook);
        }
        var workspaces = await WebhookUi.WorkspacesAsync(db, webhooks, HttpContext.RequestAborted);
        FormModel = new WebhookFormModel(Form, workspaces, errors, "Save changes");
        Show = Show == "failed" ? "failed" : "all";
        var rows = await webhooks.DeliveriesAsync(Id, null, 100, HttpContext.RequestAborted);
        Deliveries = Show == "failed"
            ? rows.Where(d => d.Status is WebhookDeliveryStatus.Dead or WebhookDeliveryStatus.Retrying).ToList()
            : rows;
    }
}
