using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Approvals.Services;

public sealed record DelegationView(Guid Id, Guid UserId, string UserName, Guid DelegateUserId, string DelegateName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsActive);

/// <summary>
/// Approval delegation (FR-IAM-4): a user hands their approvals to someone else for a date range.
/// Each user has at most one approval delegate at any instant, and delegation never forms a cycle (INV-APR-3).
/// Delegation is not transitive: only the direct delegate may decide.
/// </summary>
public interface IApprovalDelegationService
{
    /// <summary>Delegations the current user gave or received that haven't ended.</summary>
    Task<IReadOnlyList<DelegationView>> ListForCurrentUserAsync(CancellationToken ct = default);

    /// <summary>The current user delegates their approvals to <paramref name="delegateUserId"/> from <paramref name="startsAt"/> until <paramref name="endsAt"/>.</summary>
    Task<UserDelegation> CreateAsync(Guid delegateUserId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct = default);

    /// <summary>Ends one of the current user's delegations now (or removes it if it hasn't started).</summary>
    Task EndAsync(Guid delegationId, CancellationToken ct = default);

    /// <summary>People whose approvals <paramref name="delegateUserId"/> covers at <paramref name="at"/>.</summary>
    Task<IReadOnlyList<Guid>> DelegatorsOfAsync(Guid delegateUserId, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Active approval delegate of each of <paramref name="userIds"/> at <paramref name="at"/>.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> DelegatesOfAsync(IReadOnlyCollection<Guid> userIds, DateTimeOffset at, CancellationToken ct = default);
}

internal sealed class ApprovalDelegationService(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IAuditLog audit,
    TimeProvider time) : IApprovalDelegationService
{
    private static readonly TimeSpan MaxLength = TimeSpan.FromDays(366);

    public async Task<IReadOnlyList<DelegationView>> ListForCurrentUserAsync(CancellationToken ct = default)
    {
        var uid = RequireUser();
        var now = time.GetUtcNow();
        var rows = await db.UserDelegations.AsNoTracking()
            .Where(d => (d.UserId == uid || d.DelegateUserId == uid) && d.EndsAt > now && (d.Scope & DelegationScope.Approvals) != 0)
            .OrderBy(d => d.StartsAt)
            .ToListAsync(ct);
        var ids = rows.SelectMany(r => new[] { r.UserId, r.DelegateUserId }).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return rows.Select(r => new DelegationView(r.Id, r.UserId, names.GetValueOrDefault(r.UserId, "Someone"), r.DelegateUserId,
            names.GetValueOrDefault(r.DelegateUserId, "Someone"), r.StartsAt, r.EndsAt, r.StartsAt <= now)).ToList();
    }

    public async Task<UserDelegation> CreateAsync(Guid delegateUserId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct = default)
    {
        var uid = RequireUser();
        var now = time.GetUtcNow();
        if (delegateUserId == uid)
        {
            throw new ValidationException("delegate", "Choose someone other than yourself.");
        }
        var delegateUser = await db.Users.AsNoTracking()
            .Where(u => u.Id == delegateUserId && u.IsActive && !u.IsForgotten)
            .Select(u => new { u.Id, u.DisplayName })
            .FirstOrDefaultAsync(ct)
            ?? throw new ValidationException("delegate", "That person doesn't exist or is inactive.");
        if (endsAt <= startsAt)
        {
            throw new ValidationException("endsAt", "The end date must be after the start date.");
        }
        if (endsAt <= now)
        {
            throw new ValidationException("endsAt", "The end date has already passed.");
        }
        if (endsAt - startsAt > MaxLength)
        {
            throw new ValidationException("endsAt", "Delegate for a year at most.");
        }

        // At most one approval delegate at any instant (INV-APR-3).
        var overlapping = await db.UserDelegations.AsNoTracking()
            .Where(d => d.UserId == uid && (d.Scope & DelegationScope.Approvals) != 0 && d.StartsAt < endsAt && d.EndsAt > startsAt)
            .Select(d => d.DelegateUserId)
            .FirstOrDefaultAsync(ct);
        if (overlapping != Guid.Empty)
        {
            throw new ValidationException("startsAt", "You already have a delegate during those dates. End that delegation first.");
        }

        // No cycles: walk the delegate's own delegations that overlap this period.
        if (await LeadsBackAsync(delegateUserId, uid, startsAt, endsAt, ct))
        {
            throw new ValidationException("delegate", $"{delegateUser.DisplayName} already hands their approvals to you, directly or through others, during those dates.");
        }

        var delegation = new UserDelegation
        {
            UserId = uid,
            DelegateUserId = delegateUserId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Scope = DelegationScope.Approvals,
            CreatedAt = now,
        };
        db.UserDelegations.Add(delegation);
        audit.Record("approvals.delegation.created", "user", uid.ToString(), new { delegateUserId, startsAt, endsAt });
        await db.SaveChangesAsync(ct);
        return delegation;
    }

    public async Task EndAsync(Guid delegationId, CancellationToken ct = default)
    {
        var uid = RequireUser();
        var now = time.GetUtcNow();
        var delegation = await db.UserDelegations.FirstOrDefaultAsync(d => d.Id == delegationId && d.UserId == uid, ct)
            ?? throw new NotFoundException();
        if (delegation.EndsAt <= now)
        {
            return;
        }
        if (delegation.StartsAt > now)
        {
            db.UserDelegations.Remove(delegation);
        }
        else
        {
            delegation.EndsAt = now;
        }
        audit.Record("approvals.delegation.ended", "user", uid.ToString(), new { delegation.DelegateUserId });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> DelegatorsOfAsync(Guid delegateUserId, DateTimeOffset at, CancellationToken ct = default) =>
        await db.UserDelegations.AsNoTracking()
            .Where(d => d.DelegateUserId == delegateUserId && d.StartsAt <= at && d.EndsAt > at && (d.Scope & DelegationScope.Approvals) != 0)
            .Select(d => d.UserId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, Guid>> DelegatesOfAsync(IReadOnlyCollection<Guid> userIds, DateTimeOffset at, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }
        var ids = userIds.ToList();
        var rows = await db.UserDelegations.AsNoTracking()
            .Where(d => ids.Contains(d.UserId) && d.StartsAt <= at && d.EndsAt > at && (d.Scope & DelegationScope.Approvals) != 0)
            .Join(db.Users.Where(u => u.IsActive), d => d.DelegateUserId, u => u.Id, (d, u) => new { d.UserId, d.DelegateUserId, d.StartsAt })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartsAt).First().DelegateUserId);
    }

    private async Task<bool> LeadsBackAsync(Guid from, Guid target, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct)
    {
        var seen = new HashSet<Guid> { from };
        var frontier = new List<Guid> { from };
        for (var depth = 0; depth < 32 && frontier.Count > 0; depth++)
        {
            var current = frontier;
            var next = await db.UserDelegations.AsNoTracking()
                .Where(d => current.Contains(d.UserId) && (d.Scope & DelegationScope.Approvals) != 0 && d.StartsAt < endsAt && d.EndsAt > startsAt)
                .Select(d => d.DelegateUserId)
                .Distinct()
                .ToListAsync(ct);
            if (next.Contains(target))
            {
                return true;
            }
            frontier = next.Where(seen.Add).ToList();
        }
        return false;
    }

    private Guid RequireUser()
    {
        var actor = actors.Current;
        if (actor.IsInternal || actor.UserId is not { } uid)
        {
            throw new ForbiddenException("Sign in to manage delegation.");
        }
        return uid;
    }
}
