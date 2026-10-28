using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;

namespace Switchboard.Modules.Messaging;

public static class MessagingPrivileges
{
    public const string Manage = "messaging.manage";

    public static PrivilegeDefinition Definition { get; } =
        new(Manage, "Manage email and notifications", "Channels", "Mailboxes, outgoing mail, sender rules and notification templates.");
}

/// <summary>
/// Who may change what. Mailboxes and templates belong to a workspace and need <c>messaging.manage</c>
/// there. Outgoing mail and sender rules affect the whole instance: org admins, or
/// <c>messaging.manage</c> in the primary workspace.
/// </summary>
public sealed class MessagingAccess(SwitchboardDbContext db, IPermissionService permissions, IActorAccessor actors)
{
    public async Task<IReadOnlyList<Guid>> ManageableWorkspacesAsync(CancellationToken ct = default)
    {
        if (actors.Current.IsInternal)
        {
            return await db.Workspaces.Select(w => w.Id).ToListAsync(ct);
        }
        var p = await permissions.CurrentAsync(ct);
        if (p is null)
        {
            return [];
        }
        if (p.IsOrgAdmin)
        {
            return await db.Workspaces.Where(w => !w.IsArchived).Select(w => w.Id).ToListAsync(ct);
        }
        return p.WorkspacesWith(MessagingPrivileges.Manage);
    }

    public Task DemandWorkspaceAsync(Guid workspaceId, CancellationToken ct = default) =>
        permissions.DemandAsync(MessagingPrivileges.Manage, workspaceId, ct);

    public async Task<bool> CanManageInstanceAsync(CancellationToken ct = default)
    {
        if (actors.Current.IsInternal)
        {
            return true;
        }
        var p = await permissions.CurrentAsync(ct);
        if (p is null)
        {
            return false;
        }
        if (p.IsOrgAdmin)
        {
            return true;
        }
        var primary = await db.Workspaces.Where(w => w.IsPrimary).Select(w => w.Id).FirstOrDefaultAsync(ct);
        return await permissions.HasAsync(MessagingPrivileges.Manage, primary, ct);
    }

    public async Task DemandInstanceAsync(CancellationToken ct = default)
    {
        if (!await CanManageInstanceAsync(ct))
        {
            throw new ForbiddenException("Only an organization admin can change outgoing mail and sender rules.");
        }
    }
}
