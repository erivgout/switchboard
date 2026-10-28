using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Scenarios;
using Switchboard.UI;

namespace Switchboard.Modules.Automation.Areas.Automation.Pages.Scenarios;

/// <summary>Runs a scenario on a ticket from the ticket page's More menu, then returns there with a toast.</summary>
[Authorize(Policy = SwitchboardPolicies.Agent)]
public sealed class RunModel(ScenarioService scenarios) : PageModel
{
    public IActionResult OnGet() => Redirect("/automations/scenarios");

    public async Task<IActionResult> OnPostAsync(Guid id, Guid ticketId, string? returnUrl, CancellationToken ct)
    {
        var back = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/tickets";
        string message;
        var kind = ToastKind.Success;
        try
        {
            var result = await scenarios.RunAsync(id, ticketId, ct);
            message = result.Done.Count == 0
                ? $"Ran “{result.Name}”. Nothing needed to change."
                : result.Skipped.Count == 0
                    ? $"Ran “{result.Name}”."
                    : $"Ran “{result.Name}”. {result.Skipped.Count} of its steps had nothing to change.";
        }
        catch (ValidationException ex)
        {
            message = ex.Message;
            kind = ToastKind.Error;
        }
        catch (ConflictException ex)
        {
            message = ex.Message;
            kind = ToastKind.Error;
        }

        if (Request.IsHtmx())
        {
            Response.HxToast(message, kind);
            Response.Headers["HX-Refresh"] = "true";
            return new NoContentResult();
        }
        TempData.Toast(message, kind);
        return Redirect(back);
    }
}
