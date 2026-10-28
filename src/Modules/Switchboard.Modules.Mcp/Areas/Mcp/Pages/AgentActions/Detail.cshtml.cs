using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Security;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Server;

namespace Switchboard.Modules.Mcp.Areas.Mcp.Pages.AgentActions;

/// <summary>/agent-actions/{id}: one request, exactly as it will run, with its decision and result.</summary>
[Authorize]
public sealed class DetailModel(
    PendingActionService pending,
    McpAdminService admin,
    McpReauthentication reauth,
    IActorAccessor actors,
    IPermissionService permissions) : AgentActionsPage(pending, admin, reauth, actors, permissions)
{
    public McpPendingAction Action { get; private set; } = default!;

    public string? ResultJson { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        await LoadShellAsync();
        var action = await Pending.GetAsync(id, UserId, HttpContext.RequestAborted);
        if (action is null)
        {
            return NotFound();
        }
        Action = action;
        if (action.ResultJson is { Length: > 0 } json && JsonNode.Parse(json) is { } node)
        {
            ResultJson = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        return Page();
    }
}
