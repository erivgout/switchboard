using Microsoft.AspNetCore.Authorization;
using Switchboard.Core.Security;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Server;

namespace Switchboard.Modules.Mcp.Areas.Mcp.Pages.AgentActions;

/// <summary>/agent-actions: what the user's AI agents asked to do, waiting for them first (MCP-51, MCP-72).</summary>
[Authorize]
public sealed class IndexModel(
    PendingActionService pending,
    McpAdminService admin,
    McpReauthentication reauth,
    IActorAccessor actors,
    IPermissionService permissions) : AgentActionsPage(pending, admin, reauth, actors, permissions)
{
    public IReadOnlyList<McpPendingAction> Waiting { get; private set; } = [];

    public IReadOnlyList<McpPendingAction> Recent { get; private set; } = [];

    public async Task OnGetAsync()
    {
        await LoadShellAsync();
        var all = await Pending.ListAsync(UserId, 60, HttpContext.RequestAborted);
        Waiting = all.Where(a => a.Status == McpPendingStatus.Pending).ToList();
        Recent = all.Where(a => a.Status != McpPendingStatus.Pending).Take(40).ToList();
    }
}
