using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Domain;

namespace Switchboard.Modules.Approvals.Services;

/// <summary>Values of user lookup fields on the record being approved, keyed by field key.</summary>
public sealed record ApprovalLookupValue(string Label, IReadOnlyList<Guid> UserIds);

internal sealed record ResolvedApprover(Guid UserId, string Source);

internal sealed record ResolvedLevel(int Order, string? Name, ApprovalRule Rule, IReadOnlyList<ResolvedApprover> Approvers);

/// <summary>Turns approver sources into people when approval is requested (FR-APR-2, FR-IAM-9).</summary>
internal sealed class ApproverResolver(SwitchboardDbContext db)
{
    public const int MaxManagerLevels = 10;

    /// <summary>
    /// Resolves every level. <paramref name="excluded"/> (people who may not decide) are left out; a level that
    /// ends up with nobody throws <see cref="ValidationException"/> explaining why.
    /// </summary>
    public async Task<IReadOnlyList<ResolvedLevel>> ResolveAsync(
        ApprovalConfig config,
        Guid requesterId,
        IReadOnlyDictionary<string, ApprovalLookupValue>? lookups,
        IReadOnlySet<Guid> excluded,
        CancellationToken ct)
    {
        var requester = await db.Users.AsNoTracking()
            .Where(u => u.Id == requesterId)
            .Select(u => new { u.Id, u.DisplayName, u.ManagerId, u.DepartmentId })
            .FirstOrDefaultAsync(ct)
            ?? throw new ValidationException("requester", "The requester doesn't exist.");

        var levels = new List<ResolvedLevel>();
        var order = 0;
        foreach (var level in config.Levels)
        {
            order++;
            var found = new List<ResolvedApprover>();
            var reasons = new List<string>();

            foreach (var source in level.Sources)
            {
                switch (source.Kind)
                {
                    case ApproverSourceKind.Users:
                        foreach (var id in source.UserIds ?? [])
                        {
                            found.Add(new ResolvedApprover(id, "Named approver"));
                        }
                        break;

                    case ApproverSourceKind.Manager:
                    {
                        var steps = Math.Clamp(source.ManagerLevel ?? 1, 1, MaxManagerLevels);
                        var manager = await ManagerAtAsync(requester.ManagerId, steps, requester.Id, ct);
                        if (manager is { } m)
                        {
                            found.Add(new ResolvedApprover(m, steps == 1 ? "Manager" : steps == 2 ? "Manager's manager" : $"Manager, {steps} levels up"));
                        }
                        else
                        {
                            reasons.Add(steps == 1 ? $"{requester.DisplayName} has no manager" : $"{requester.DisplayName} has no manager {steps} levels up");
                        }
                        break;
                    }

                    case ApproverSourceKind.DepartmentHead:
                    {
                        var department = requester.DepartmentId is { } d
                            ? await db.Departments.AsNoTracking().Where(x => x.Id == d).Select(x => new { x.Name, x.HeadUserId }).FirstOrDefaultAsync(ct)
                            : null;
                        if (department?.HeadUserId is { } head)
                        {
                            found.Add(new ResolvedApprover(head, $"Head of {department.Name}"));
                        }
                        else
                        {
                            reasons.Add(department is null ? $"{requester.DisplayName} isn't in a department" : $"{department.Name} has no department head");
                        }
                        break;
                    }

                    case ApproverSourceKind.AgentGroup when source.GroupId is { } groupId:
                    {
                        var group = await db.AgentGroups.AsNoTracking().Where(g => g.Id == groupId).Select(g => g.Name).FirstOrDefaultAsync(ct);
                        var members = await db.AgentGroupMembers.AsNoTracking()
                            .Where(m => m.GroupId == groupId && !m.IsObserver)
                            .Select(m => m.UserId)
                            .ToListAsync(ct);
                        found.AddRange(members.Select(id => new ResolvedApprover(id, $"{group ?? "Agent"} group")));
                        if (members.Count == 0)
                        {
                            reasons.Add(group is null ? "the approving group no longer exists" : $"the {group} group has no members");
                        }
                        break;
                    }

                    case ApproverSourceKind.LookupField when source.FieldKey is { } key:
                    {
                        var value = lookups?.GetValueOrDefault(key);
                        if (value is { UserIds.Count: > 0 })
                        {
                            found.AddRange(value.UserIds.Select(id => new ResolvedApprover(id, $"Picked in {value.Label}")));
                        }
                        else
                        {
                            reasons.Add($"nobody was picked in {value?.Label ?? key}");
                        }
                        break;
                    }
                }
            }

            var candidateIds = found.Select(f => f.UserId).Distinct().ToList();
            var active = await db.Users.AsNoTracking()
                .Where(u => candidateIds.Contains(u.Id) && u.IsActive && !u.IsForgotten)
                .Select(u => u.Id)
                .ToListAsync(ct);
            var activeSet = active.ToHashSet();
            if (candidateIds.Any(id => !activeSet.Contains(id)))
            {
                reasons.Add("some approvers are inactive");
            }
            if (candidateIds.Any(excluded.Contains))
            {
                reasons.Add("people can't approve their own requests");
            }

            var approvers = found
                .Where(f => activeSet.Contains(f.UserId) && !excluded.Contains(f.UserId))
                .DistinctBy(f => f.UserId)
                .ToList();
            if (approvers.Count == 0)
            {
                var why = reasons.Count > 0 ? string.Join(", ", reasons.Distinct()) : "no approvers are set up";
                throw new ValidationException("approvers",
                    $"No one can approve {(config.Levels.Count > 1 ? $"level {order}" : "this request")}: {why}. Ask your service desk to check the approval settings.");
            }
            levels.Add(new ResolvedLevel(order, string.IsNullOrWhiteSpace(level.Name) ? null : level.Name.Trim(), level.Rule, approvers));
        }
        return levels;
    }

    private async Task<Guid?> ManagerAtAsync(Guid? start, int steps, Guid requesterId, CancellationToken ct)
    {
        var current = start;
        var seen = new HashSet<Guid> { requesterId };
        for (var i = 1; i < steps && current is { } id; i++)
        {
            if (!seen.Add(id))
            {
                return null;
            }
            current = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.ManagerId).FirstOrDefaultAsync(ct);
        }
        return current is { } found && !seen.Contains(found) ? found : null;
    }
}
