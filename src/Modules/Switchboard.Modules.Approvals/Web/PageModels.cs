using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Approvals.Web;

public sealed record PersonChoice(Guid Id, string Name);

/// <summary>Model for the shared people picker partial (<c>/Areas/Approvals/Pages/Shared/_PeoplePicker.cshtml</c>).</summary>
public sealed record PeoplePickerModel(
    string Name,
    string Id,
    string Label,
    bool Multiple = false,
    IReadOnlyList<PersonChoice>? Selected = null,
    string? Help = null,
    bool Required = false,
    bool AgentsOnly = false,
    string? Placeholder = null,
    string? Error = null);

public sealed record ApprovalListModel(IReadOnlyList<ApprovalListItem> Items, string BasePath, string View);

public static class ApprovalText
{
    public static string Rule(ApprovalRule rule, int approvers, int needed) => rule switch
    {
        _ when approvers == 1 => "One approver",
        ApprovalRule.Any => "Any one approves",
        ApprovalRule.Everyone => "Everyone approves",
        _ => $"Majority approves ({needed} of {approvers})",
    };

    public static string Level(LevelOutcome outcome, ApprovalStatus status) => outcome switch
    {
        LevelOutcome.Pending => "Waiting",
        LevelOutcome.Approved => "Approved",
        LevelOutcome.Rejected => "Rejected",
        _ => status is ApprovalStatus.Requested ? "Up next" : "Not reached",
    };
}

/// <summary>Shared behavior of <c>/approvals</c> and <c>/portal/approvals</c>.</summary>
public abstract class ApprovalIndexPageBase(IApprovalService approvals, IApprovalDelegationService delegation, IActorAccessor actors) : PageModel
{
    public abstract string BasePath { get; }

    [BindProperty(SupportsGet = true)]
    public string View { get; set; } = "pending";

    public IReadOnlyList<ApprovalListItem> Items { get; private set; } = [];
    public int PendingCount { get; private set; }
    public IReadOnlyList<DelegationView> Covering { get; private set; } = [];
    public IReadOnlyList<DelegationView> Delegated { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        View = View is "decided" or "requested" ? View : "pending";
        PendingCount = await approvals.CountPendingAsync(ct);
        Items = View switch
        {
            "decided" => await approvals.ListDecidedAsync(100, ct),
            "requested" => await approvals.ListRequestedAsync(100, ct),
            _ => await approvals.ListPendingAsync(100, ct),
        };
        var uid = actors.Current.UserId;
        var delegations = await delegation.ListForCurrentUserAsync(ct);
        Covering = delegations.Where(d => d.DelegateUserId == uid && d.IsActive).ToList();
        Delegated = delegations.Where(d => d.UserId == uid && d.IsActive).ToList();
    }
}

/// <summary>Shared behavior of <c>/approvals/{id}</c> and <c>/portal/approvals/{id}</c>: view, decide, remind, cancel.</summary>
public abstract class ApprovalDetailPageBase(IApprovalService approvals) : PageModel
{
    public abstract string BasePath { get; }

    public ApprovalDetails Details { get; private set; } = default!;

    [BindProperty]
    public DecisionInput Input { get; set; } = new();

    public sealed class DecisionInput
    {
        public ApprovalVerdict? Verdict { get; set; }
        public string? Comment { get; set; }
        public Guid? OnBehalfOfId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Details = await approvals.GetAsync(id, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostDecideAsync(Guid id)
    {
        var ct = HttpContext.RequestAborted;
        if (Input.Verdict is not { } verdict)
        {
            ModelState.AddModelError("Input.Verdict", "Choose approve or reject.");
            Details = await approvals.GetAsync(id, ct);
            return Page();
        }
        try
        {
            await approvals.DecideAsync(id, new DecideApprovalCommand { Verdict = verdict, Comment = Input.Comment, OnBehalfOfId = Input.OnBehalfOfId }, ct);
        }
        catch (ValidationException ex)
        {
            foreach (var (key, messages) in ex.Errors)
            {
                foreach (var message in messages)
                {
                    ModelState.AddModelError(key switch { "comment" => "Input.Comment", "onBehalfOf" => "Input.OnBehalfOfId", _ => "" }, message);
                }
            }
            Details = await approvals.GetAsync(id, ct);
            return Page();
        }
        catch (ConflictException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
            return RedirectToPage(new { id });
        }
        TempData.Toast(verdict == ApprovalVerdict.Approved ? "Approved" : "Rejected");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemindAsync(Guid id, string? returnUrl)
    {
        try
        {
            var count = await approvals.RemindAsync(id, HttpContext.RequestAborted);
            TempData.Toast(count == 1 ? "Reminder sent" : $"Reminders sent to {count} approvers");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Back(id, returnUrl);
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id, string? reason, string? returnUrl)
    {
        try
        {
            await approvals.CancelAsync(id, reason, HttpContext.RequestAborted);
            TempData.Toast("Approval cancelled");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        return Back(id, returnUrl);
    }

    private IActionResult Back(Guid id, string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage(new { id });
}
