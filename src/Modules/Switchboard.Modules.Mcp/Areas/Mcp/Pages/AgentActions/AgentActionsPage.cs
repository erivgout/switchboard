using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Server;
using Switchboard.UI;

namespace Switchboard.Modules.Mcp.Areas.Mcp.Pages.AgentActions;

/// <summary>Shared approve, decline and revoke handlers of the agent action pages.</summary>
public abstract class AgentActionsPage(
    PendingActionService pending,
    McpAdminService admin,
    McpReauthentication reauth,
    IActorAccessor actors,
    IPermissionService permissions) : PageModel
{
    protected PendingActionService Pending => pending;

    public bool IsAgent { get; private set; }

    public bool RecentlySignedIn { get; private set; }

    protected Guid UserId => actors.Current.UserId ?? throw new ForbiddenException();

    protected async Task LoadShellAsync()
    {
        IsAgent = (await permissions.CurrentAsync())?.IsAgent == true;
        RecentlySignedIn = await reauth.RecentlySignedInAsync(UserId, HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, string? password)
    {
        var action = await pending.GetAsync(id, UserId, HttpContext.RequestAborted);
        if (action is null)
        {
            return NotFound();
        }
        var proven = !action.RequiresReauthentication
            || await reauth.RecentlySignedInAsync(UserId, HttpContext.RequestAborted)
            || await reauth.CheckPasswordAsync(UserId, password, HttpContext.RequestAborted);

        var (result, outcome) = await pending.ApproveAsync(id, UserId, proven, HttpContext.RequestAborted);
        switch (result)
        {
            case ApprovalResult.NeedsReauthentication:
                TempData.Toast("That password isn't right. Enter your Switchboard password to approve this.", ToastKind.Error);
                break;
            case ApprovalResult.Expired:
                TempData.Toast("This request expired, so it didn't run. Ask the agent to try again.", ToastKind.Error);
                break;
            case ApprovalResult.AlreadyDecided:
                TempData.Toast("This request was already decided.", ToastKind.Error);
                break;
            case ApprovalResult.NotFound:
                return NotFound();
            default:
                if (outcome is { IsError: false })
                {
                    TempData.Toast("Approved and done");
                }
                else
                {
                    TempData.Toast($"Approved, but it didn't work: {outcome?.Text}", ToastKind.Error);
                }
                break;
        }
        return Redirect($"{McpPaths.AgentActions}/{id}");
    }

    public async Task<IActionResult> OnPostDeclineAsync(Guid id)
    {
        var result = await pending.DeclineAsync(id, UserId, HttpContext.RequestAborted);
        switch (result)
        {
            case ApprovalResult.NotFound:
                return NotFound();
            case ApprovalResult.Done:
                TempData.Toast("Declined. The agent has been told.");
                break;
            case ApprovalResult.Expired:
                TempData.Toast("This request had already expired, so it didn't run.");
                break;
            default:
                TempData.Toast("This request was already decided.", ToastKind.Error);
                break;
        }
        return Redirect($"{McpPaths.AgentActions}/{id}");
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid grantId, string? returnUrl)
    {
        var revoked = await admin.RevokeGrantAsync(grantId, HttpContext.RequestAborted);
        TempData.Toast(revoked ? "Agent disconnected" : "That connection was already disconnected.", revoked ? ToastKind.Success : ToastKind.Error);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/profile");
    }

    public static string ClassKey(Core.Agents.AgentActionClass c) => c switch
    {
        Core.Agents.AgentActionClass.External => "external",
        Core.Agents.AgentActionClass.Authority => "authority",
        Core.Agents.AgentActionClass.Destructive => "destructive",
        Core.Agents.AgentActionClass.InternalWrite => "write",
        _ => "read",
    };

    public static string ClassIcon(Core.Agents.AgentActionClass c) => c switch
    {
        Core.Agents.AgentActionClass.External => "bi-send",
        Core.Agents.AgentActionClass.Authority => "bi-patch-check",
        Core.Agents.AgentActionClass.Destructive => "bi-exclamation-octagon",
        _ => "bi-pencil",
    };

    public static (string Css, string Icon) OutcomeStyle(McpPendingAction a) => PendingActionLabels.Status(a) switch
    {
        "Done" => ("ok", "bi-check-circle"),
        "Failed" => ("bad", "bi-x-circle"),
        "Waiting for you" => ("waiting", "bi-hourglass-split"),
        "Running" => ("waiting", "bi-arrow-repeat"),
        _ => ("muted", "bi-dash-circle"),
    };
}
