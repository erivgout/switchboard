using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;

namespace Switchboard.Modules.Approvals.Areas.Approvals.Pages.Email;

/// <summary>
/// Decide from an emailed link without signing in (SR-APR-2, only when "require sign-in" is off). The link opens
/// this confirmation page; the decision is only recorded on POST, so link scanners can't approve anything.
/// The token is single-use, bound to one approver and expires.
/// </summary>
[AllowAnonymous]
public sealed class DecideModel(
    SwitchboardDbContext db,
    ApprovalLinkTokens tokens,
    IApprovalService approvals,
    IActorAccessor actors,
    ISettingsService settings,
    TimeProvider time) : PageModel
{
    public ApprovalDetails? Details { get; private set; }
    public string? RecipientName { get; private set; }
    public Guid? OnBehalfOfId { get; private set; }
    public string? Problem { get; private set; }
    public ApprovalVerdict? Recorded { get; private set; }

    [BindProperty]
    public string? Comment { get; set; }

    public async Task<IActionResult> OnGetAsync(string token)
    {
        await LoadAsync(token);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, ApprovalVerdict? verdict)
    {
        var link = await LoadAsync(token);
        if (link is null || Details is null || Problem is not null)
        {
            return Page();
        }
        if (verdict is not { } v)
        {
            ModelState.AddModelError(nameof(Comment), "Choose approve or reject.");
            return Page();
        }

        var recipient = await db.Users.AsNoTracking().FirstAsync(u => u.Id == link.RecipientId);
        using (actors.BeginScope(Actor.ForUser(recipient, Channel.Email)))
        {
            // Marked used in the same save as the decision, so a link can never record two decisions.
            link.UsedAt = time.GetUtcNow();
            try
            {
                await approvals.DecideAsync(link.RequestId, new DecideApprovalCommand { Verdict = v, Comment = Comment, OnBehalfOfId = link.OnBehalfOfId });
            }
            catch (ValidationException ex)
            {
                link.UsedAt = null;
                foreach (var message in ex.Errors.SelectMany(e => e.Value))
                {
                    ModelState.AddModelError(nameof(Comment), message);
                }
                return Page();
            }
            catch (Exception ex) when (ex is ForbiddenException or NotFoundException or ConflictException)
            {
                Problem = ex.Message;
                return Page();
            }
            Details = await approvals.GetAsync(link.RequestId);
        }
        Recorded = v;
        return Page();
    }

    private async Task<AprLinkToken?> LoadAsync(string token)
    {
        // Turning "require sign-in" back on retires every outstanding link.
        var link = await settings.GetAsync(ApprovalSettingKeys.RequireLogin, ApprovalSettingKeys.RequireLoginDefault)
            ? null
            : await tokens.FindValidAsync(token, HttpContext.RequestAborted);
        if (link is null)
        {
            Problem = "This link has expired or was already used. Sign in to see the approvals waiting on you.";
            return null;
        }
        var recipient = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == link.RecipientId && u.IsActive);
        if (recipient is null)
        {
            Problem = "This link is no longer valid.";
            return null;
        }
        RecipientName = recipient.DisplayName;
        OnBehalfOfId = link.OnBehalfOfId;
        using (actors.BeginScope(Actor.ForUser(recipient, Channel.Email)))
        {
            try
            {
                Details = await approvals.GetAsync(link.RequestId);
            }
            catch (NotFoundException)
            {
                Problem = "This link is no longer valid.";
                return null;
            }
        }
        if (Details.Request.Status != ApprovalStatus.Requested)
        {
            Problem = $"This request is already {ApprovalService.StatusWord(Details.Request.Status)}.";
        }
        else if (Details.Request.Levels.FirstOrDefault(l => l.Id == link.LevelId)?.Order != Details.Request.CurrentLevel || Details.CanDecideFor.Count == 0)
        {
            Problem = "This request no longer needs your decision.";
        }
        return link;
    }
}
