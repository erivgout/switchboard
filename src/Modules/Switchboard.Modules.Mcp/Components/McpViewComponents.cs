using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Server;

namespace Switchboard.Modules.Mcp.Components;

internal static class McpViews
{
    public const string Root = "/Areas/Mcp/Pages/Components/";
}

public sealed record ConnectedAgentRow(McpGrant Grant, string Domain, IReadOnlyList<McpCallRecord> Recent);

public sealed record ConnectedAgentsModel(IReadOnlyList<ConnectedAgentRow> Grants, int PendingCount, bool McpEnabled, string ReturnUrl);

/// <summary>The "Connected agents" section on /profile (MCP-25): grants, scopes, last use, recent actions, revoke.</summary>
public sealed class ConnectedAgentsViewComponent(SwitchboardDbContext db, IActorAccessor actors, PendingActionService pending, McpAdminService admin, TimeProvider time) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        // Only ever the signed-in user's own connections.
        if (model is not Guid userId || actors.Current.UserId != userId)
        {
            return Content("");
        }
        var now = time.GetUtcNow();
        var grants = await db.Set<McpGrant>().AsNoTracking()
            .Where(g => g.UserId == userId && g.RevokedAt == null && g.ExpiresAt > now)
            .OrderByDescending(g => g.LastUsedAt ?? g.CreatedAt).Take(20).ToListAsync();
        var clientIds = grants.Select(g => g.ClientId).Distinct().ToList();
        var clients = await db.Set<McpClient>().AsNoTracking().Where(c => clientIds.Contains(c.ClientId)).ToDictionaryAsync(c => c.ClientId);
        var grantIds = grants.Select(g => (Guid?)g.Id).ToList();
        var recent = await db.Set<McpCallRecord>().AsNoTracking()
            .Where(r => r.UserId == userId && grantIds.Contains(r.GrantId) && r.ActionClass != Core.Agents.AgentActionClass.Read)
            .OrderByDescending(r => r.At).Take(60).ToListAsync();
        var rows = grants.Select(g => new ConnectedAgentRow(g,
            clients.TryGetValue(g.ClientId, out var c) ? ClientDisplay.Domain(c) : "",
            recent.Where(r => r.GrantId == g.Id).Take(5).ToList())).ToList();
        var settings = await admin.GetSettingsAsync(HttpContext.RequestAborted);
        var model2 = new ConnectedAgentsModel(rows, await pending.CountPendingAsync(userId, HttpContext.RequestAborted), settings.Enabled,
            Request.Path + Request.QueryString);
        return View(McpViews.Root + "ConnectedAgents.cshtml", model2);
    }
}

/// <summary>Top-bar link that appears while agent actions wait for the user's confirmation.</summary>
public sealed class AgentActionsIndicatorViewComponent(IActorAccessor actors, PendingActionService pending) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        _ = model;
        var count = actors.Current.UserId is { } id ? await pending.CountPendingAsync(id, HttpContext.RequestAborted) : 0;
        return View(McpViews.Root + "Indicator.cshtml", count);
    }
}

/// <summary>"Agent actions" in the user menu.</summary>
public sealed class AgentActionsMenuViewComponent(IActorAccessor actors, PendingActionService pending) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(object? model)
    {
        _ = model;
        var count = actors.Current.UserId is { } id ? await pending.CountPendingAsync(id, HttpContext.RequestAborted) : 0;
        return View(McpViews.Root + "Menu.cshtml", count);
    }
}

public static class ClientDisplay
{
    /// <summary>Where an agent app comes from, as a person can check it (MCP-13).</summary>
    public static string Domain(McpClient client) => client.Source switch
    {
        McpClientSource.MetadataDocument when Uri.TryCreate(client.MetadataUrl, UriKind.Absolute, out var u) => u.Host,
        _ when Uri.TryCreate(client.ClientUri, UriKind.Absolute, out var u) => u.Host,
        _ => "",
    };

    /// <summary>One line for people: where the app comes from, and how far that is verified.</summary>
    public static string Describe(McpClient client)
    {
        var domain = Domain(client);
        return client.Source switch
        {
            McpClientSource.Dynamic => domain.Length > 0 ? $"Registered itself and says it's from {domain}. Not verified." : "Registered itself. No website given.",
            McpClientSource.MetadataDocument => $"Identified by details published at {domain}",
            _ => domain.Length > 0 ? $"Registered by an administrator, {domain}" : "Registered by an administrator",
        };
    }

    public static string Source(McpClient client) => client.Source switch
    {
        McpClientSource.MetadataDocument => "Identified by its published details",
        McpClientSource.Admin => "Registered by an administrator",
        _ => "Registered itself",
    };

    public static string RedirectHost(string? redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            return "";
        }
        return uri.Scheme is "http" or "https" ? uri.Authority : $"the {uri.Scheme} app on this device";
    }
}
