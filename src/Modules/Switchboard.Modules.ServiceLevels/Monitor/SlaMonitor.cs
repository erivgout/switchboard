using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Events;

namespace Switchboard.Modules.ServiceLevels.Monitor;

/// <summary>One ticket the monitor looks at.</summary>
internal sealed record SlaCandidate(
    Guid TicketId,
    Guid WorkspaceId,
    Guid? AgentId,
    Guid? GroupId,
    DateTimeOffset? FirstResponseDueBy,
    DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? DueBy,
    int Cycle,
    Guid? PolicyId,
    DateTimeOffset? NextResponseDueBy,
    int NextResponseSeq);

internal readonly record struct SlaFiringKey(int Cycle, SlaEventKind Kind, SlaTargetKind Target, int Level, int Sequence);

internal sealed record SlaFiring(SlaFiringKey Key, DateTimeOffset DueAt, int MinutesBefore, SlaNotifyTargets Notify);

/// <summary>
/// Pure: which reminders, breaches and escalations are due for a candidate now, given what already
/// fired, and when it next needs looking at.
/// </summary>
internal static class SlaFiringPlanner
{
    private static readonly SlaNotifyTargets AssignedAgent = new() { Assignee = true };

    public static (List<SlaFiring> Due, DateTimeOffset? NextCheckAt) Plan(SlaCandidate c, SlaPolicy? policy, IReadOnlySet<SlaFiringKey> fired, DateTimeOffset now)
    {
        var due = new List<SlaFiring>();
        DateTimeOffset? next = null;
        var reminders = policy?.Reminders ?? [];
        var plan = policy?.Escalations ?? new SlaEscalationPlan();

        void Consider(DateTimeOffset at, SlaFiring firing, bool reminder)
        {
            if (fired.Contains(firing.Key))
            {
                return;
            }
            if (at > now)
            {
                next = next is { } n && n <= at ? n : at;
                return;
            }
            // A reminder that is already past its target is pointless; the breach covers it.
            if (!reminder || now < firing.DueAt)
            {
                due.Add(firing);
            }
        }

        void Target(SlaTargetKind kind, DateTimeOffset dueAt, int cycle, int sequence, SlaNotifyTargets breachNotify)
        {
            for (var i = 0; i < reminders.Count; i++)
            {
                var r = reminders[i];
                if (r.Target == kind && r.MinutesBefore > 0)
                {
                    Consider(dueAt.AddMinutes(-r.MinutesBefore),
                        new SlaFiring(new SlaFiringKey(cycle, SlaEventKind.Reminder, kind, i + 1, sequence), dueAt, r.MinutesBefore, AssignedAgent), true);
                }
            }
            Consider(dueAt, new SlaFiring(new SlaFiringKey(cycle, SlaEventKind.Breach, kind, 0, sequence), dueAt, 0, breachNotify), false);
        }

        // The first response target belongs to the first cycle, so it fires at most once per ticket.
        if (c.FirstRespondedAt is null && c.FirstResponseDueBy is { } fr)
        {
            Target(SlaTargetKind.FirstResponse, fr, 1, 0, plan.FirstResponse);
        }
        if (c.DueBy is { } resolution)
        {
            Target(SlaTargetKind.Resolution, resolution, c.Cycle, 0, new SlaNotifyTargets());
            foreach (var level in plan.Resolution.Where(l => l.Level is >= 1 and <= 4).OrderBy(l => l.Level))
            {
                Consider(resolution.AddMinutes(Math.Max(0, level.DelayMinutes)),
                    new SlaFiring(new SlaFiringKey(c.Cycle, SlaEventKind.Escalation, SlaTargetKind.Resolution, level.Level, 0), resolution, 0, level.Notify), false);
            }
        }
        if (c.NextResponseDueBy is { } nr)
        {
            Target(SlaTargetKind.NextResponse, nr, c.Cycle, c.NextResponseSeq, plan.FirstResponse);
        }
        return (due, next);
    }
}

/// <summary>
/// Detects reminders, breaches and escalations every 30 seconds (NFR-PERF-4: breaches within 60s).
/// One instance works at a time (<c>sp_getapplock</c>); the unique index on <see cref="SlaEvent"/>
/// backs that up, so each fires at most once (INV-SLA-4).
/// </summary>
public sealed class SlaMonitor(IServiceScopeFactory scopes, TimeProvider time, ILogger<SlaMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 200;
    private const int MaxBatchesPerPass = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "SLA monitor pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Runs one pass. Returns how many reminders, breaches and escalations fired, or -1 when another instance holds the lease.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerPass; batch++)
        {
            var (fired, examined) = await RunBatchAsync(ct);
            if (fired < 0)
            {
                return batch == 0 ? -1 : total;
            }
            total += fired;
            if (examined < BatchSize)
            {
                break;
            }
        }
        return total;
    }

    private async Task<(int Fired, int Examined)> RunBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        using var _ = sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Service levels"));

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await TryLeaseAsync(db, tx, ct))
        {
            return (-1, 0);
        }
        var result = await new SlaMonitorPass(db).RunAsync(time.GetUtcNow(), BatchSize, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Transaction-scoped application lock: released on commit or rollback, so a crash can't strand it.</summary>
    private static async Task<bool> TryLeaseAsync(SwitchboardDbContext db, IDbContextTransaction tx, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = N'switchboard:sla-monitor', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
            SELECT @result;
            """;
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        return result >= 0;
    }
}

internal sealed class SlaMonitorPass(SwitchboardDbContext db)
{
    private static readonly StatusCategory[] Running = [StatusCategory.Open, StatusCategory.Pending];

    public async Task<(int Fired, int Examined)> RunAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        var policies = await db.Set<SlaPolicy>().AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var lead = policies.Values.SelectMany(p => p.Reminders).Select(r => r.MinutesBefore).DefaultIfEmpty(0).Max();
        var horizon = now.AddMinutes(Math.Max(0, lead));

        var cursors = db.Set<SlaMonitorCursor>();
        var picked = await (
            from x in Eligible()
            join cursorRow in cursors on x.TicketId equals cursorRow.TicketId into cursorRows
            from c in cursorRows.DefaultIfEmpty()
            where (c == null
                    && ((x.FirstRespondedAt == null && x.FirstResponseDueBy != null && x.FirstResponseDueBy <= horizon)
                        || (x.DueBy != null && x.DueBy <= horizon)
                        || (x.NextResponseDueBy != null && x.NextResponseDueBy <= horizon)))
                || (c != null
                    && ((c.NextCheckAt != null && c.NextCheckAt <= now)
                        || c.Cycle != x.Cycle || c.PolicyId != x.PolicyId
                        || c.FirstResponseDueBy != x.FirstResponseDueBy || c.DueBy != x.DueBy
                        || c.NextResponseDueBy != x.NextResponseDueBy || c.NextResponseSeq != x.NextResponseSeq))
            orderby x.DueBy
            select x.TicketId)
            .Take(batchSize)
            .ToListAsync(ct);
        if (picked.Count == 0)
        {
            return (0, 0);
        }

        // Lock the picked tickets until commit and read them again: a ticket paused, resolved or
        // trashed meanwhile drops out, and one being changed now is read after that change commits.
        var ids = await LockStillEligibleAsync(picked, ct);
        var candidates = (await Eligible().Where(x => ids.Contains(x.TicketId)).ToListAsync(ct))
            .Select(x => new SlaCandidate(x.TicketId, x.WorkspaceId, x.AgentId, x.GroupId, x.FirstResponseDueBy, x.FirstRespondedAt, x.DueBy,
                x.Cycle, x.PolicyId, x.NextResponseDueBy, x.NextResponseSeq))
            .ToList();
        var firedRows = await db.Set<SlaEvent>().AsNoTracking()
            .Where(e => ids.Contains(e.TicketId))
            .Select(e => new { e.TicketId, e.Cycle, e.Kind, e.Target, e.Level, e.Sequence })
            .ToListAsync(ct);
        var firedByTicket = firedRows
            .GroupBy(e => e.TicketId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<SlaFiringKey>)g.Select(e => new SlaFiringKey(e.Cycle, e.Kind, e.Target, e.Level, e.Sequence)).ToHashSet());
        var existingCursors = await cursors.Where(c => ids.Contains(c.TicketId)).ToDictionaryAsync(c => c.TicketId, ct);

        var count = 0;
        foreach (var candidate in candidates)
        {
            var policy = candidate.PolicyId is { } pid ? policies.GetValueOrDefault(pid) : null;
            var fired = firedByTicket.GetValueOrDefault(candidate.TicketId) ?? new HashSet<SlaFiringKey>();
            var (due, nextCheck) = SlaFiringPlanner.Plan(candidate, policy, fired, now);
            foreach (var firing in due)
            {
                await FireAsync(candidate, policy, firing, now, ct);
                count++;
            }

            if (!existingCursors.TryGetValue(candidate.TicketId, out var cursor))
            {
                cursor = new SlaMonitorCursor { TicketId = candidate.TicketId };
                cursors.Add(cursor);
            }
            cursor.Cycle = candidate.Cycle;
            cursor.PolicyId = candidate.PolicyId;
            cursor.FirstResponseDueBy = candidate.FirstResponseDueBy;
            cursor.DueBy = candidate.DueBy;
            cursor.NextResponseDueBy = candidate.NextResponseDueBy;
            cursor.NextResponseSeq = candidate.NextResponseSeq;
            cursor.NextCheckAt = nextCheck;
        }

        await db.SaveChangesAsync(ct);
        return (count, picked.Count);
    }

    private sealed class EligibleRow
    {
        public Guid TicketId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid? AgentId { get; init; }
        public Guid? GroupId { get; init; }
        public DateTimeOffset? FirstResponseDueBy { get; init; }
        public DateTimeOffset? FirstRespondedAt { get; init; }
        public DateTimeOffset? DueBy { get; init; }
        public int Cycle { get; init; }
        public Guid? PolicyId { get; init; }
        public DateTimeOffset? NextResponseDueBy { get; init; }
        public int NextResponseSeq { get; init; }
    }

    /// <summary>INV-SLA-5: never paused, resolved, closed, trashed, spam or merged tickets.</summary>
    private IQueryable<EligibleRow> Eligible() =>
        from s in db.Set<SlaTicketState>()
        join t in db.Tickets on s.TicketId equals t.Id
        where t.DeletedAt == null && !t.IsSpam && t.MergedIntoId == null && t.PurgedAt == null
            && s.PausedSince == null
            && Running.Contains(t.Status!.Category) && t.Status.SlaTimerOn
        select new EligibleRow
        {
            TicketId = t.Id,
            WorkspaceId = t.WorkspaceId,
            AgentId = t.AgentId,
            GroupId = t.GroupId,
            FirstResponseDueBy = t.FirstResponseDueBy,
            FirstRespondedAt = t.FirstRespondedAt,
            DueBy = t.DueBy,
            Cycle = s.Cycle,
            PolicyId = s.PolicyId,
            NextResponseDueBy = s.NextResponseDueBy,
            NextResponseSeq = s.NextResponseSeq,
        };

    /// <summary>
    /// Update-locks the tickets and their SLA state rows until the monitor's transaction ends, so a
    /// concurrent pause, resolve or trash either commits first (and the ticket drops out) or waits for
    /// the firing. The monitor yields if this ever deadlocks; the next pass retries.
    /// </summary>
    private async Task<List<Guid>> LockStillEligibleAsync(List<Guid> ids, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(ids);
        return await db.Database.SqlQueryRaw<Guid>("""
            SET DEADLOCK_PRIORITY LOW;
            SELECT t.[Id] AS [Value]
            FROM [Tickets] t WITH (UPDLOCK, ROWLOCK)
            JOIN [SlaTicketStates] s WITH (UPDLOCK, ROWLOCK) ON s.[TicketId] = t.[Id]
            JOIN [TicketStatuses] st ON st.[Id] = t.[StatusId]
            WHERE t.[Id] IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@ids))
              AND t.[DeletedAt] IS NULL AND t.[IsSpam] = 0 AND t.[MergedIntoId] IS NULL AND t.[PurgedAt] IS NULL
              AND s.[PausedSince] IS NULL AND st.[SlaTimerOn] = 1 AND st.[Category] IN (1, 2);
            """, new Microsoft.Data.SqlClient.SqlParameter("@ids", json)).ToListAsync(ct);
    }

    private async Task FireAsync(SlaCandidate c, SlaPolicy? policy, SlaFiring firing, DateTimeOffset now, CancellationToken ct)
    {
        var notify = await SlaRecipients.ResolveAsync(db, firing.Notify, c.AgentId, c.GroupId, ct);
        var names = notify.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => notify.Contains(u.Id)).OrderBy(u => u.DisplayName).Select(u => u.DisplayName).ToListAsync(ct);
        var key = firing.Key;
        var target = SlaText.Target(key.Target);
        var told = names.Count == 0 ? "" : $" Notified {SlaText.JoinAnd(names.Count > 5 ? [.. names.Take(5), $"{names.Count - 5} others"] : names)}.";
        var summary = key.Kind switch
        {
            SlaEventKind.Reminder => $"Reminder: the {target} target is due in {SlaText.Duration(firing.DueAt - now)}.{told}",
            SlaEventKind.Breach => $"Missed the {target} target.{told}",
            _ => $"Escalated to level {key.Level} after missing the {target} target.{(told.Length > 0 ? told : " No one was set up to be notified.")}",
        };

        var row = new SlaEvent
        {
            TicketId = c.TicketId,
            WorkspaceId = c.WorkspaceId,
            Cycle = key.Cycle,
            Kind = key.Kind,
            Target = key.Target,
            Level = key.Level,
            Sequence = key.Sequence,
            DueAt = firing.DueAt,
            FiredAt = now,
            Summary = summary.Length > 1000 ? summary[..1000] : summary,
            NotifiedUserIdsJson = SlaJson.Write(notify),
        };
        db.Set<SlaEvent>().Add(row);
        db.Activity(row, key.Kind switch
        {
            SlaEventKind.Reminder => "sla_reminder",
            SlaEventKind.Breach => "sla_breached",
            _ => "sla_escalated",
        }, row.Summary);

        db.Raise(key.Kind switch
        {
            SlaEventKind.Reminder => new SlaReminderDue(c.TicketId, c.WorkspaceId, key.Cycle, key.Target, firing.DueAt, firing.MinutesBefore, notify),
            SlaEventKind.Breach => new SlaBreached(c.TicketId, c.WorkspaceId, key.Cycle, key.Target, firing.DueAt, policy?.Id, notify),
            _ => (Core.Events.DomainEvent)new SlaEscalated(c.TicketId, c.WorkspaceId, key.Cycle, key.Target, key.Level, firing.DueAt, policy?.Id, notify),
        });
    }
}
