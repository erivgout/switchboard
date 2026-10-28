using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>
/// Proof that the person approving a destructive action is the account owner, right now (MCP-51):
/// their password, or an explicit sign-in within the last few minutes.
/// </summary>
public sealed class McpReauthentication(SwitchboardDbContext db, IHttpContextAccessor http, IAuditLog audit, TimeProvider time)
{
    private static readonly string[] SignInActions = ["auth.login.succeeded", "auth.mfa.succeeded"];

    public async Task<bool> RecentlySignedInAsync(Guid userId, CancellationToken ct)
    {
        var since = time.GetUtcNow() - McpSettings.RecentSignIn;
        var id = userId.ToString();
        return await db.AuditEntries.AsNoTracking()
            .AnyAsync(a => a.ObjectType == "user" && a.ObjectId == id && SignInActions.Contains(a.Action) && a.OccurredAt >= since, ct);
    }

    public async Task<bool> CheckPasswordAsync(Guid userId, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password) || http.HttpContext?.RequestServices.GetService<SignInManager<User>>() is not { } signIn)
        {
            return false;
        }
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (user is null)
        {
            return false;
        }
        // Counts toward lockout like any other password attempt (SR-AUTHN-2).
        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            await audit.WriteAsync("mcp.action.reauth_failed", "user", userId.ToString(), new { lockedOut = result.IsLockedOut }, ct);
        }
        return result.Succeeded;
    }
}
