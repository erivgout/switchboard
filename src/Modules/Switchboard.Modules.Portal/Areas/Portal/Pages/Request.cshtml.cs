using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Portal.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Portal.Areas.Portal.Pages;

public sealed class RequestModel(PortalRequests portal, ITicketService tickets, IActorAccessor actors) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public int Number { get; set; }

    [BindProperty]
    public string? Body { get; set; }

    [BindProperty]
    public List<Guid> AttachmentIds { get; set; } = [];

    public PortalRequestView View { get; private set; } = default!;

    /// <summary>Model of the <c>portal.ticket</c> slot.</summary>
    public Ticket Ticket { get; private set; } = default!;

    public IReadOnlyList<PortalFile> Uploaded { get; private set; } = [];

    public Guid ViewerId { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    /// <summary>Status and conversation, refreshed in place when the service desk replies.</summary>
    public async Task<IActionResult> OnGetLiveAsync(CancellationToken ct)
    {
        View = await portal.GetAsync(Number, ct);
        Response.Headers.CacheControl = "no-store";
        return Partial("_RequestLive", View);
    }

    public async Task<IActionResult> OnPostReplyAsync(CancellationToken ct)
    {
        try
        {
            var before = await portal.GetAsync(Number, ct);
            var result = await portal.ReplyAsync(Number, Body, AttachmentIds, ct);
            if (result.CreatedFollowUp)
            {
                TempData.Toast($"Follow-up request {result.Ticket.DisplayId} sent");
                return Redirect($"/portal/requests/{result.Ticket.Number}");
            }
            TempData.Toast(before.ReplyReopens ? "Request reopened" : "Reply sent");
            return Redirect($"/portal/requests/{Number}");
        }
        catch (ValidationException ex)
        {
            PortalErrors.Add(this, ex);
            await LoadAsync(ct);
            return Page();
        }
        catch (ForbiddenException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return Redirect($"/portal/requests/{Number}");
        }
    }

    public async Task<IActionResult> OnPostCloseAsync(CancellationToken ct)
    {
        try
        {
            await portal.CloseAsync(Number, ct);
            TempData.Toast("Request closed");
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect($"/portal/requests/{Number}");
    }

    public async Task<IActionResult> OnPostShareAsync(string? email, CancellationToken ct)
    {
        try
        {
            var name = await portal.ShareAsync(Number, email, ct);
            TempData.Toast($"Shared with {name}");
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect($"/portal/requests/{Number}#people");
    }

    public async Task<IActionResult> OnPostUnshareAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await portal.UnshareAsync(Number, userId, ct);
            TempData.Toast("Stopped sharing");
        }
        catch (Exception ex) when (ex is ValidationException or ForbiddenException)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Redirect($"/portal/requests/{Number}#people");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        View = await portal.GetAsync(Number, ct);
        Ticket = await tickets.GetForRequesterAsync(View.Id, ct);
        Uploaded = await portal.PendingUploadsAsync(AttachmentIds, ct);
        ViewerId = actors.Current.UserId ?? Guid.Empty;
        ViewData["WatchTicket"] = View.Id;
    }
}
