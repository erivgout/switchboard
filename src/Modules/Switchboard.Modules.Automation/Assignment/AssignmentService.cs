using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.Automation.Assignment;

public sealed record AssignmentChoice(Guid? AgentId, string? AgentName, string Reason, Guid? PreviousPointer);

/// <summary>
/// Picks an agent in a group (FR-RTE-1/2). Only active, non-observer members who are available and
/// hold a role in the workspace are eligible. Agents at the group's open-ticket cap are skipped.
/// </summary>
public sealed class AssignmentService(SwitchboardDbContext db)
{
    /// <summary>Chooses without changing anything (safe for test runs).</summary>
    public async Task<AssignmentChoice> ChooseAsync(Guid groupId, AssignmentMode mode, CancellationToken ct = default)
    {
        var group = await db.AgentGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
        {
            return new AssignmentChoice(null, null, "That group no longer exists.", null);
        }

        var members = await db.AgentGroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && !m.IsObserver)
            .Select(m => new
            {
                m.UserId,
                m.User!.DisplayName,
                m.User.IsActive,
                IsAgent = m.User.IsAgent || m.User.IsOrgAdmin,
                m.User.Availability,
                InWorkspace = m.User.IsOrgAdmin || m.User.Memberships.Any(w => w.WorkspaceId == group.WorkspaceId),
            })
            .ToListAsync(ct);

        // Stable rotation order, independent of how SQL Server sorts GUIDs.
        var eligible = members
            .Where(m => m.IsActive && m.IsAgent && m.InWorkspace && m.Availability == AgentAvailability.Available)
            .OrderBy(m => m.UserId)
            .ToList();
        if (eligible.Count == 0)
        {
            var reason = members.Count == 0
                ? $"{group.Name} has no members who can take tickets."
                : $"Nobody in {group.Name} is available right now.";
            return new AssignmentChoice(null, null, reason, group.LastAssignedUserId);
        }

        var ids = eligible.Select(m => m.UserId).ToList();
        var open = await db.Tickets.AsNoTracking()
            .Where(t => t.AgentId != null && ids.Contains(t.AgentId.Value)
                && t.DeletedAt == null && !t.IsSpam && t.MergedIntoId == null && t.PurgedAt == null
                && (t.Status!.Category == StatusCategory.Open || t.Status.Category == StatusCategory.Pending))
            .GroupBy(t => t.AgentId!.Value)
            .Select(g => new { AgentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AgentId, x => x.Count, ct);
        int Load(Guid id) => open.GetValueOrDefault(id);

        // Rotation starts after the last agent who got a ticket, even if that agent has since gone away.
        var start = group.LastAssignedUserId is { } last ? Math.Max(0, ids.FindIndex(id => id.CompareTo(last) > 0)) : 0;
        var rotation = Enumerable.Range(0, ids.Count).Select(i => eligible[(start + i) % ids.Count]).ToList();
        var underCap = rotation.Where(m => group.MaxOpenPerAgent is not { } cap || Load(m.UserId) < cap).ToList();
        if (underCap.Count == 0)
        {
            return new AssignmentChoice(null, null, $"Everyone available in {group.Name} already has {group.MaxOpenPerAgent} open tickets.", group.LastAssignedUserId);
        }

        var pick = mode == AssignmentMode.LoadBased
            ? underCap.OrderBy(m => Load(m.UserId)).First()
            : underCap.First();
        var why = mode == AssignmentMode.LoadBased
            ? $"{pick.DisplayName} has the fewest unresolved tickets ({Load(pick.UserId)})."
            : $"{pick.DisplayName} is next in the rotation.";
        return new AssignmentChoice(pick.UserId, pick.DisplayName, why, group.LastAssignedUserId);
    }

    /// <summary>
    /// Moves the round-robin pointer from <paramref name="expected"/> to <paramref name="next"/>.
    /// Returns false when another assignment moved it first, so the caller can choose again.
    /// </summary>
    public async Task<bool> AdvanceAsync(Guid groupId, Guid? expected, Guid next, CancellationToken ct = default)
    {
        var updated = expected is { } e
            ? await db.AgentGroups.Where(g => g.Id == groupId && g.LastAssignedUserId == e)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastAssignedUserId, next), ct)
            : await db.AgentGroups.Where(g => g.Id == groupId && g.LastAssignedUserId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastAssignedUserId, next), ct);
        return updated == 1;
    }

    /// <summary>Chooses and claims the next agent, retrying if a concurrent assignment raced ahead.</summary>
    public async Task<AssignmentChoice> ClaimAsync(Guid groupId, AssignmentMode mode, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var choice = await ChooseAsync(groupId, mode, ct);
            if (choice.AgentId is not { } agent || await AdvanceAsync(groupId, choice.PreviousPointer, agent, ct) || attempt >= 4)
            {
                return choice;
            }
        }
    }
}
