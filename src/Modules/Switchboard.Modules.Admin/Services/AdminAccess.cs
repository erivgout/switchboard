using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Admin.Services;

/// <summary>
/// Admin authorization rules shared by every admin service (SR-AUTHZ-3): nobody grants what they
/// don't hold, nobody changes their own access, and nobody manages a person with more access than they have.
/// </summary>
public sealed class AdminAccess(SwitchboardDbContext db, IPermissionService permissions, IActorAccessor actors)
{
    public Actor Actor => actors.Current;

    /// <summary>The acting user's id; null for system actors.</summary>
    public Guid? ActorUserId => actors.Current.IsInternal ? null : actors.Current.UserId;

    public Task DemandAsync(string privilege, Guid? workspaceId = null, CancellationToken ct = default) =>
        permissions.DemandAsync(privilege, workspaceId, ct);

    public Task<bool> HasAsync(string privilege, Guid? workspaceId = null, CancellationToken ct = default) =>
        permissions.HasAsync(privilege, workspaceId, ct);

    /// <summary>Permissions of the acting user, or null for internal actors (who pass every check).</summary>
    public async Task<UserPermissions?> MeAsync(CancellationToken ct = default)
    {
        if (actors.Current.IsInternal)
        {
            return null;
        }
        return await permissions.CurrentAsync(ct) ?? throw new ForbiddenException();
    }

    public async Task<bool> IsOrgAdminAsync(CancellationToken ct = default) =>
        actors.Current.IsInternal || (await permissions.CurrentAsync(ct))?.IsOrgAdmin == true;

    public async Task DemandOrgAdminAsync(CancellationToken ct = default)
    {
        if (!await IsOrgAdminAsync(ct))
        {
            throw new ForbiddenException("Only organization admins can do that.");
        }
    }

    /// <summary>
    /// The actor may hold (and so grant or take away) <paramref name="privilege"/> in every workspace listed;
    /// with no workspaces, in at least one.
    /// </summary>
    public async Task<bool> HoldsAsync(string privilege, IReadOnlyCollection<Guid> workspaceIds, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        if (me is null || me.IsOrgAdmin)
        {
            return true;
        }
        return workspaceIds.Count == 0 ? me.Has(privilege) : workspaceIds.All(w => me.Has(privilege, w));
    }

    /// <summary>Assigning a role in a workspace needs every privilege of that role in that workspace.</summary>
    public async Task EnsureCanAssignRoleAsync(Role role, Guid workspaceId, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        if (me is null || me.IsOrgAdmin)
        {
            return;
        }
        var missing = role.Privileges.Where(p => !me.Has(p, workspaceId)).ToList();
        if (missing.Count > 0)
        {
            throw new ForbiddenException($"The {role.Name} role includes access you don't have, so you can't give it to anyone or take it away.");
        }
    }

    /// <summary>
    /// The actor may change <paramref name="targetUserId"/>'s account only when the target has no access the
    /// actor lacks. Stops someone with people management from taking over a more powerful account.
    /// </summary>
    public async Task EnsureCanManageUserAsync(Guid targetUserId, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        if (me is null || me.IsOrgAdmin)
        {
            return;
        }
        var target = await db.Users.AsNoTracking().Where(u => u.Id == targetUserId)
            .Select(u => new { u.IsOrgAdmin, u.DisplayName })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException();
        if (target.IsOrgAdmin)
        {
            throw new ForbiddenException("Only organization admins can change an organization admin's account.");
        }
        var memberships = await db.WorkspaceMemberships.AsNoTracking()
            .Where(m => m.UserId == targetUserId)
            .Select(m => new { m.WorkspaceId, m.Role!.Privileges })
            .ToListAsync(ct);
        if (memberships.Any(m => m.Privileges.Any(p => !me.Has(p, m.WorkspaceId))))
        {
            throw new ForbiddenException($"{target.DisplayName} has access you don't have, so only someone with that access can change their account.");
        }
    }

    /// <summary>Non-org-admins can't change their own access (SR-AUTHZ-3).</summary>
    public async Task EnsureNotSelfAsync(Guid targetUserId, string message, CancellationToken ct = default)
    {
        if (ActorUserId == targetUserId && !await IsOrgAdminAsync(ct))
        {
            throw new ForbiddenException(message);
        }
    }
}

/// <summary>Transactions and the org-admin invariant (INV-ID-1).</summary>
internal static class AdminData
{
    /// <summary>Runs <paramref name="work"/> in a transaction, joining one that is already open.</summary>
    public static async Task<T> InTransactionAsync<T>(this SwitchboardDbContext db, Func<Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await work();
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work();
        await tx.CommitAsync(ct);
        return result;
    }

    public static Task InTransactionAsync(this SwitchboardDbContext db, Func<Task> work, CancellationToken ct) =>
        db.InTransactionAsync(async () =>
        {
            await work();
            return true;
        }, ct);

    /// <summary>
    /// Serializes changes that could leave no active organization admin, so two admins demoting each
    /// other at the same moment can't both succeed. Call inside a transaction.
    /// </summary>
    public static Task LockOrgAdminsAsync(this SwitchboardDbContext db, CancellationToken ct) => AppLockAsync(db, "adm-org-admins", ct);

    /// <summary>Serializes email address changes across users (INV-ID-2). Call inside a transaction.</summary>
    public static Task LockEmailsAsync(this SwitchboardDbContext db, CancellationToken ct) => AppLockAsync(db, "adm-user-emails", ct);

    private static Task AppLockAsync(SwitchboardDbContext db, string resource, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; IF @r < 0 THROW 51000, 'Another admin change is in progress. Try again.', 1;",
            [resource], ct);

    /// <summary>Throws unless another active organization admin remains after <paramref name="userId"/> loses the role.</summary>
    public static async Task EnsureAnotherOrgAdminAsync(this SwitchboardDbContext db, Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.IsOrgAdmin && u.IsActive && u.Id != userId, ct))
        {
            throw new ValidationException("OrgAdmin",
                "This is the last active organization admin. Make someone else an organization admin first.");
        }
    }

    /// <summary>Escapes LIKE wildcards so a literal string can be searched for.</summary>
    public static string LikeLiteral(string value) =>
        value.Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal);
}

/// <summary>The organization logo is public branding: it also appears on signed-out pages such as the public knowledge base.</summary>
internal sealed class LogoAttachmentPolicy : IAttachmentAccessPolicy
{
    public const string Owner = "adm.logo";

    public string OwnerType => Owner;

    public Task<bool> CanReadAsync(Attachment attachment, CancellationToken ct) =>
        Task.FromResult(true);
}
