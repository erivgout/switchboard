using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Catalog.Services;
using Switchboard.Modules.Catalog.Web;
using Switchboard.UI;

namespace Switchboard.Modules.Catalog.Areas.Catalog.Pages.Portal;

/// <summary>Change a rejected request and send it for approval again (FR-SRM-10).</summary>
public sealed class EditModel(ICatalogService catalog, ICatalogRequestService requests, IPermissionService permissions, SwitchboardDbContext db) : PageModel
{
    public TicketCatalogView View { get; private set; } = default!;
    public CatalogItemDetails Details { get; private set; } = default!;
    public FormRenderModel Form { get; private set; } = default!;
    public int Quantity { get; private set; } = 1;
    public Guid? RequestedForId { get; private set; }
    public string? RequestedForName { get; private set; }

    public async Task<IActionResult> OnGetAsync(int number)
    {
        if (await LoadViewAsync(number) is { } problem)
        {
            return problem;
        }
        var row = View.Items[0].Row;
        Quantity = row.Quantity;
        RequestedForId = row.RequestedForId;
        await LoadFormAsync(CatalogForm.ToInput(View.Items[0].Answers), new Dictionary<string, string>());
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int number, int? quantity, Guid? requestedFor)
    {
        if (await LoadViewAsync(number) is { } problem)
        {
            return problem;
        }
        var answers = CatalogFormBinding.Read(Request.Form);
        Quantity = quantity ?? 1;
        RequestedForId = requestedFor;
        try
        {
            await requests.ResubmitAsync(View.TicketId, new ResubmitRequestCommand { Quantity = Quantity, Answers = answers, RequestedForId = requestedFor }, HttpContext.RequestAborted);
        }
        catch (ValidationException ex)
        {
            var errors = CatalogFormBinding.Errors(ex, ModelState);
            await LoadFormAsync(answers, errors);
            return Page();
        }
        TempData.Toast("Request sent for approval again");
        return Redirect($"/portal/catalog/requests/{number}");
    }

    private async Task<IActionResult?> LoadViewAsync(int number)
    {
        var ct = HttpContext.RequestAborted;
        var ticketId = await (await permissions.RequesterVisibleTicketsAsync(ct)).Where(t => t.Number == number).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        if (ticketId is null || await requests.GetForRequesterAsync(ticketId.Value, ct) is not { } view)
        {
            return NotFound();
        }
        View = view;
        if (!view.CanResubmit)
        {
            TempData.Toast("Only a request that wasn't approved can be changed and sent again.", ToastKind.Error);
            return Redirect($"/portal/catalog/requests/{number}");
        }
        return null;
    }

    private async Task LoadFormAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> answers, IReadOnlyDictionary<string, string> errors)
    {
        var ct = HttpContext.RequestAborted;
        Details = await catalog.GetAsync(View.Items[0].Row.ItemId, forAgent: false, ct);
        var ids = CatalogForm.LookupIds(Details.Fields, answers).Concat(RequestedForId is { } rf ? [rf] : Array.Empty<Guid>()).ToList();
        var people = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        RequestedForName = RequestedForId is { } f ? people.GetValueOrDefault(f) : null;
        Form = new FormRenderModel(Details.Fields, answers, errors, people);
    }
}
