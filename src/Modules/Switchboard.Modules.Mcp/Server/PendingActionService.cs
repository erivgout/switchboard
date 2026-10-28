using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Data;

namespace Switchboard.Modules.Mcp.Server;

public enum ApprovalResult
{
    Done,
    NotFound,
    AlreadyDecided,
    Expired,
    NeedsReauthentication,
}

/// <summary>
/// Out-of-band confirmation (MCP-51/52): the delegating user approves or declines a pending action while
/// signed in to Switchboard as themselves. Approval is single-use and runs exactly the confirmed parameters.
/// </summary>
public sealed class PendingActionService(
    SwitchboardDbContext db,
    McpToolDispatcher dispatcher,
    IAuditLog audit,
    TimeProvider time)
{
    /// <summary>The user's own pending action, with an elapsed expiry applied.</summary>
    public async Task<McpPendingAction?> GetAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var action = await db.Set<McpPendingAction>().FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
        if (action is not null && await ExpireIfDueAsync(action, ct))
        {
            await db.SaveChangesAsync(ct);
        }
        return action;
    }

    public async Task<IReadOnlyList<McpPendingAction>> ListAsync(Guid userId, int take, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await db.Set<McpPendingAction>()
            .Where(p => p.UserId == userId && p.Status == McpPendingStatus.Pending && p.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, McpPendingStatus.Expired).SetProperty(p => p.ResultCode, "expired"), ct);
        return await db.Set<McpPendingAction>().AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<int> CountPendingAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.Set<McpPendingAction>().CountAsync(p => p.UserId == userId && p.Status == McpPendingStatus.Pending && p.ExpiresAt > now, ct);
    }

    /// <summary>
    /// Approves and runs the action. <paramref name="reauthenticated"/> must be true for class D (the caller
    /// checked the password or a recent sign-in).
    /// </summary>
    public async Task<(ApprovalResult Result, ToolOutcome? Outcome)> ApproveAsync(Guid id, Guid userId, bool reauthenticated, CancellationToken ct)
    {
        var action = await db.Set<McpPendingAction>().FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
        if (action is null)
        {
            return (ApprovalResult.NotFound, null);
        }
        if (await ExpireIfDueAsync(action, ct))
        {
            await db.SaveChangesAsync(ct);
            return (ApprovalResult.Expired, null);
        }
        if (action.Status != McpPendingStatus.Pending)
        {
            return (ApprovalResult.AlreadyDecided, null);
        }
        if (action.RequiresReauthentication && !reauthenticated)
        {
            return (ApprovalResult.NeedsReauthentication, null);
        }

        action.Status = McpPendingStatus.Approved;
        action.DecidedAt = time.GetUtcNow();
        audit.Record("mcp.action.approved", "mcp_pending_action", action.Id.ToString(), new
        {
            tool = action.Tool,
            clientId = action.ClientId,
            grantId = action.GrantId,
            parameterHash = action.ParameterHash,
            reauthenticated = action.RequiresReauthentication ? true : (bool?)null,
        });
        try
        {
            // RowVersion makes the approval single-use: a second approval of the same row fails here (MCP-52).
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return (ApprovalResult.AlreadyDecided, null);
        }

        var outcome = await dispatcher.ExecuteApprovedAsync(action, ct);
        return (ApprovalResult.Done, outcome);
    }

    public async Task<ApprovalResult> DeclineAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var action = await db.Set<McpPendingAction>().FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
        if (action is null)
        {
            return ApprovalResult.NotFound;
        }
        if (await ExpireIfDueAsync(action, ct))
        {
            await db.SaveChangesAsync(ct);
            return ApprovalResult.Expired;
        }
        if (action.Status != McpPendingStatus.Pending)
        {
            return ApprovalResult.AlreadyDecided;
        }
        action.Status = McpPendingStatus.Declined;
        action.DecidedAt = time.GetUtcNow();
        action.ResultCode = McpErrorCodes.ConfirmationDeclined;
        action.ResultText = "The user declined this action.";
        audit.Record("mcp.action.declined", "mcp_pending_action", action.Id.ToString(), new { tool = action.Tool, clientId = action.ClientId, grantId = action.GrantId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApprovalResult.AlreadyDecided;
        }
        return ApprovalResult.Done;
    }

    private Task<bool> ExpireIfDueAsync(McpPendingAction action, CancellationToken ct)
    {
        _ = ct;
        if (action.Status == McpPendingStatus.Pending && action.ExpiresAt <= time.GetUtcNow())
        {
            action.Status = McpPendingStatus.Expired;
            action.ResultCode = "expired";
            action.ResultText = "Nobody confirmed this action in time, so it didn't run.";
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }
}

/// <summary>Plain-language labels for the confirmation screens.</summary>
public static class PendingActionLabels
{
    public static string Status(McpPendingAction a) => a.Status switch
    {
        McpPendingStatus.Pending => "Waiting for you",
        McpPendingStatus.Declined => "Declined",
        McpPendingStatus.Expired => "Expired",
        _ when a.CompletedAt is null => "Running",
        _ when a.ResultCode == McpErrorCodes.Ok => "Done",
        _ => "Failed",
    };

    public static string Class(Core.Agents.AgentActionClass c) => c switch
    {
        Core.Agents.AgentActionClass.External => "Sends a message outside your team",
        Core.Agents.AgentActionClass.Authority => "Makes a decision in your name",
        Core.Agents.AgentActionClass.Destructive => "Hard to undo: deletes, merges or changes many records",
        Core.Agents.AgentActionClass.InternalWrite => "Changes records",
        _ => "Reads records",
    };
}
