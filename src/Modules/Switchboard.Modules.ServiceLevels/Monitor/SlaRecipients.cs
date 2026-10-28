using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Modules.ServiceLevels.Domain;

namespace Switchboard.Modules.ServiceLevels.Monitor;

/// <summary>Turns notify targets into the active agents to tell, at the moment a notification fires.</summary>
internal static class SlaRecipients
{
    /// <summary>"The assigned agent" means the group's members while nobody is assigned.</summary>
    public static async Task<List<Guid>> ResolveAsync(SwitchboardDbContext db, SlaNotifyTargets targets, Guid? agentId, Guid? groupId, CancellationToken ct)
    {
        var ids = new HashSet<Guid>(targets.UserIds);
        var groups = new HashSet<Guid>(targets.GroupIds);

        if (targets.Assignee)
        {
            if (agentId is { } a)
            {
                ids.Add(a);
            }
            else if (groupId is { } g)
            {
                groups.Add(g);
            }
        }
        if (targets.AssigneeManager && agentId is { } assignee)
        {
            var manager = await db.Users.AsNoTracking().Where(u => u.Id == assignee).Select(u => u.ManagerId).FirstOrDefaultAsync(ct);
            if (manager is { } m)
            {
                ids.Add(m);
            }
        }
        if (groups.Count > 0)
        {
            var members = await db.AgentGroupMembers.AsNoTracking()
                .Where(m => groups.Contains(m.GroupId) && !m.IsObserver)
                .Select(m => m.UserId)
                .ToListAsync(ct);
            ids.UnionWith(members);
        }
        if (ids.Count == 0)
        {
            return [];
        }
        // Only active agents get SLA notifications.
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.IsActive && (u.IsAgent || u.IsOrgAdmin))
            .OrderBy(u => u.DisplayName)
            .Select(u => u.Id)
            .ToListAsync(ct);
    }
}
