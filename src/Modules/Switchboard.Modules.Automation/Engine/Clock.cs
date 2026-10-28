using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Net;
using Switchboard.Modules.Automation.Scheduling;

namespace Switchboard.Modules.Automation.Engine;

/// <summary>
/// Evaluates time-based workflows (supervisor rules). Each ticket runs a workflow at most once per
/// clock value ("anchor"): once per creation, per update, per status entry, or per due time.
/// </summary>
public sealed class TimeRuleRunner(IServiceScopeFactory scopes, WorkflowExecutor executor, TimeProvider time, ILogger<TimeRuleRunner> logger)
{
    private const int BatchSize = 300;

    // Per-workflow cursor so tickets that keep failing conditions can't starve the rest.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _cursors = new();

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        List<AutWorkflow> workflows;
        await using (var scope = scopes.CreateAsyncScope())
        {
            workflows = await scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>().Set<AutWorkflow>().AsNoTracking()
                .Where(w => w.IsEnabled && w.PublishedTrigger == TriggerKeys.Time && w.PublishedJson != null)
                .OrderBy(w => w.Order).ThenBy(w => w.CreatedAt).ToListAsync(ct);
        }

        var runs = 0;
        foreach (var wf in workflows)
        {
            try
            {
                runs += await RunWorkflowAsync(wf, now, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Time-based workflow {Workflow} failed to evaluate", wf.Name);
            }
        }
        return runs;
    }

    private async Task<int> RunWorkflowAsync(AutWorkflow wf, DateTimeOffset now, CancellationToken ct)
    {
        var def = WorkflowDefinition.Parse(wf.PublishedJson);
        if (def.Trigger.Hours is not (> 0 and var hours) || def.Trigger.Clock is not { } clock)
        {
            return 0;
        }

        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        using var _ = sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Automation clock"));
        var settings = sp.GetRequiredService<AutomationSettings>();
        if (await settings.IsPausedAsync(wf.WorkspaceId, ct))
        {
            return 0;
        }

        var span = TimeSpan.FromHours(hours);
        var cutoff = now - span;
        var tickets = db.Tickets.AsNoTracking().Where(t => t.WorkspaceId == wf.WorkspaceId && t.DeletedAt == null && !t.IsSpam
            && t.MergedIntoId == null && t.PurgedAt == null && t.Status!.Category != StatusCategory.Closed);
        IQueryable<Candidate> candidates = clock switch
        {
            TimeClocks.Created => tickets.Where(t => t.CreatedAt <= cutoff).Select(t => new Candidate { TicketId = t.Id, Anchor = t.CreatedAt }),
            TimeClocks.Updated => tickets.Where(t => t.UpdatedAt <= cutoff).Select(t => new Candidate { TicketId = t.Id, Anchor = t.UpdatedAt }),
            TimeClocks.InStatus =>
                from t in tickets
                join c in db.Set<AutTicketClock>() on t.Id equals c.TicketId into clocks
                from c in clocks.DefaultIfEmpty()
                let since = c != null && c.StatusId == t.StatusId ? c.StatusSince : t.CreatedAt
                where since <= cutoff
                select new Candidate { TicketId = t.Id, Anchor = since },
            TimeClocks.Due => tickets
                .Where(t => t.DueBy != null && t.DueBy <= now + span
                    && (t.Status!.Category == StatusCategory.Open || t.Status.Category == StatusCategory.Pending))
                .Select(t => new Candidate { TicketId = t.Id, Anchor = t.DueBy!.Value }),
            _ => Enumerable.Empty<Candidate>().AsQueryable(),
        };

        var runs = db.Set<AutRun>();
        var cursor = _cursors.GetValueOrDefault(wf.Id, DateTimeOffset.MinValue);
        var batch = await candidates
            .Where(x => !runs.Any(r => r.WorkflowId == wf.Id && r.TicketId == x.TicketId && r.Anchor == x.Anchor))
            .Where(x => x.Anchor > cursor)
            .OrderBy(x => x.Anchor)
            .Take(BatchSize)
            .ToListAsync(ct);
        _cursors[wf.Id] = batch.Count < BatchSize ? DateTimeOffset.MinValue : batch[^1].Anchor;
        if (batch.Count == 0)
        {
            return 0;
        }

        var catalog = await sp.GetRequiredService<CatalogLoader>().LoadAsync(wf.WorkspaceId, ct);
        var limits = await settings.LimitsAsync(wf.WorkspaceId, ct);
        var loader = sp.GetRequiredService<TicketFactsLoader>();
        var clockLabel = catalog.Clocks.FirstOrDefault(c => c.Value == clock)?.Label ?? clock;
        var count = 0;
        foreach (var candidate in batch)
        {
            var facts = await loader.LoadAsync(candidate.TicketId, null, ct);
            if (facts is null || facts.IsClosedOrDeleted)
            {
                continue;
            }
            var evaluation = ConditionEvaluator.Evaluate(def.Conditions, facts, catalog);
            if (!evaluation.Matched)
            {
                continue;
            }
            var eventId = Sql.DeterministicGuid("time", wf.Id, candidate.TicketId, candidate.Anchor.UtcTicks);
            var te = new TriggerEvent
            {
                TriggerKey = TriggerKeys.Time,
                TicketId = candidate.TicketId,
                WorkspaceId = wf.WorkspaceId,
                EventId = eventId,
                OccurredAt = now,
                Anchor = candidate.Anchor,
                Summary = $"{Describer.Hours(hours)} {clockLabel}",
            };
            var outcome = await executor.ExecuteAsync(new RunRequest(wf, def, te, Sql.DeterministicGuid("time-root", eventId), evaluation, limits, catalog), ct);
            if (outcome is RunOutcome.Succeeded or RunOutcome.Failed)
            {
                count++;
            }
        }
        return count;
    }

    private sealed class Candidate
    {
        public Guid TicketId { get; init; }
        public DateTimeOffset Anchor { get; init; }
    }
}

/// <summary>Wakes the automation clock early (e.g. after automations resume).</summary>
public sealed class AutomationClockSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Background loop: replays queued events, recovers unsent steps, creates scheduled tickets, evaluates
/// time-based workflows and prunes old logs. The interval is <c>Switchboard:Automation:ClockSeconds</c>
/// (default 300, or 20 in Development).
/// </summary>
internal sealed class AutomationClock(
    IServiceScopeFactory scopes,
    AutomationDispatcher dispatcher,
    ExternalStepSender sender,
    ScheduleRunner schedules,
    TimeRuleRunner timeRules,
    AutomationClockSignal signal,
    IOptions<AutomationOptions> options,
    IServiceProvider services,
    TimeProvider time,
    ILogger<AutomationClock> logger) : BackgroundService
{
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var isDevelopment = services.GetService<IHostEnvironment>()?.IsDevelopment() == true;
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.ClockSeconds ?? (isDevelopment ? 20 : 300)));
        await Task.Delay(TimeSpan.FromSeconds(isDevelopment ? 5 : 30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await signal.WaitAsync(interval, stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await Safely("queued events", () => dispatcher.DrainQueueAsync(200, ct));
        await Safely("unsent steps", async () => { await sender.SweepAsync(ct); return 0; });
        await Safely("schedules", () => schedules.RunDueAsync(ct));
        await Safely("time-based workflows", () => timeRules.RunAsync(ct));
        if (time.GetUtcNow() - _lastPrune > TimeSpan.FromHours(6))
        {
            _lastPrune = time.GetUtcNow();
            await Safely("pruning", () => PruneAsync(ct));
        }

        async Task Safely(string what, Func<Task<int>> work)
        {
            try
            {
                await work();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Automation clock: {What} failed", what);
            }
        }
    }

    /// <summary>Keeps 90 days of run logs and 14 days of cascade lineage.</summary>
    private async Task<int> PruneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        var now = time.GetUtcNow();
        var runsBefore = now.AddDays(-90);
        var lineageBefore = now.AddDays(-14);
        var n = await db.Set<AutRun>().Where(r => r.StartedAt < runsBefore && r.Status != RunStatus.Running).OrderBy(r => r.StartedAt).Take(5000).ExecuteDeleteAsync(ct);
        n += await db.Set<AutEventLineage>().Where(l => l.CreatedAt < lineageBefore).OrderBy(l => l.CreatedAt).Take(5000).ExecuteDeleteAsync(ct);
        n += await db.Set<AutAlert>().Where(a => a.CreatedAt < runsBefore).ExecuteDeleteAsync(ct);
        return n;
    }
}
