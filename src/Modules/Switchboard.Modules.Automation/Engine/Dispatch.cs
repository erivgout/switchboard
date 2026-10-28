using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Automation.Assignment;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;

namespace Switchboard.Modules.Automation.Engine;

/// <summary>
/// Pre-commit: tags events raised by an automation run with the run's cascade, in the same
/// transaction, so the next hop knows which chain it belongs to (INV-AUT-2).
/// </summary>
internal sealed class LineageRecorder(SwitchboardDbContext db, TimeProvider time) :
    IPreCommitHandler<TicketCreated>,
    IPreCommitHandler<TicketUpdated>,
    IPreCommitHandler<TicketStatusChanged>,
    IPreCommitHandler<TicketAssigned>,
    IPreCommitHandler<TicketConversationAdded>
{
    public Task HandleAsync(TicketCreated e, CancellationToken ct) => Record(e);
    public Task HandleAsync(TicketUpdated e, CancellationToken ct) => Record(e);
    public Task HandleAsync(TicketStatusChanged e, CancellationToken ct) => Record(e);
    public Task HandleAsync(TicketAssigned e, CancellationToken ct) => Record(e);
    public Task HandleAsync(TicketConversationAdded e, CancellationToken ct) => Record(e);

    private Task Record(DomainEvent e)
    {
        if (AutomationRunContext.Current is { } frame && db.Set<AutEventLineage>().Local.All(l => l.EventId != e.EventId))
        {
            db.Set<AutEventLineage>().Add(new AutEventLineage
            {
                EventId = e.EventId,
                CascadeRootId = frame.CascadeRootId,
                RunId = frame.RunId,
                CreatedAt = time.GetUtcNow(),
            });
        }
        return Task.CompletedTask;
    }
}

/// <summary>Pre-commit: remembers when each ticket entered its status, for "hours in status" rules.</summary>
internal sealed class StatusClockRecorder(SwitchboardDbContext db) :
    IPreCommitHandler<TicketCreated>,
    IPreCommitHandler<TicketStatusChanged>
{
    public async Task HandleAsync(TicketCreated e, CancellationToken ct)
    {
        var ticket = db.Tickets.Local.FirstOrDefault(t => t.Id == e.TicketId);
        if (ticket is not null)
        {
            await SetAsync(e.TicketId, ticket.StatusId, e.OccurredAt, ct);
        }
    }

    public Task HandleAsync(TicketStatusChanged e, CancellationToken ct) => SetAsync(e.TicketId, e.ToStatusId, e.OccurredAt, ct);

    private async Task SetAsync(Guid ticketId, Guid statusId, DateTimeOffset since, CancellationToken ct)
    {
        var clocks = db.Set<AutTicketClock>();
        var clock = clocks.Local.FirstOrDefault(c => c.TicketId == ticketId) ?? await clocks.FirstOrDefaultAsync(c => c.TicketId == ticketId, ct);
        if (clock is null)
        {
            clocks.Add(new AutTicketClock { TicketId = ticketId, StatusId = statusId, StatusSince = since });
        }
        else if (clock.StatusId != statusId)
        {
            clock.StatusId = statusId;
            clock.StatusSince = since;
        }
    }
}

/// <summary>Turns domain events into <see cref="TriggerEvent"/>s.</summary>
public static class TriggerEventMapper
{
    public static async Task<TriggerEvent?> MapAsync(DomainEvent evt, SwitchboardDbContext db, IEnumerable<ExternalTrigger> external, CancellationToken ct)
    {
        TriggerEvent Base(string key, Guid ticketId, Guid workspaceId, string summary) => new()
        {
            TriggerKey = key,
            TicketId = ticketId,
            WorkspaceId = workspaceId,
            EventId = evt.EventId,
            OccurredAt = evt.OccurredAt,
            CascadeDepth = evt.CascadeDepth,
            Actor = evt.Actor,
            Summary = summary,
        };

        switch (evt)
        {
            case TicketCreated e:
                return Base(TriggerKeys.TicketCreated, e.TicketId, e.WorkspaceId, "Ticket created");
            case TicketUpdated e:
                return Base(TriggerKeys.TicketUpdated, e.TicketId, e.WorkspaceId,
                    "Updated " + string.Join(", ", e.Changes.Select(c => c.Field.ToLowerInvariant()).Distinct())) with { Changes = e.Changes };
            case TicketStatusChanged e:
                return Base(TriggerKeys.StatusChanged, e.TicketId, e.WorkspaceId, $"Status changed from {e.FromCategory} to {e.ToCategory}");
            case TicketAssigned e:
                return Base(TriggerKeys.Assigned, e.TicketId, e.WorkspaceId, "Assignment changed") with
                {
                    LandedInGroupWithoutAgent = e.ToGroupId is not null && e.FromGroupId != e.ToGroupId && e.ToAgentId is null,
                };
            case TicketConversationAdded e:
                return Base(TriggerKeys.Conversation, e.TicketId, e.WorkspaceId, e.Kind switch
                {
                    ConversationKind.RequesterMessage => "The requester replied",
                    ConversationKind.AgentReply => "An agent replied",
                    ConversationKind.Forward => "Forwarded",
                    ConversationKind.Note => e.IsPrivate ? "Private note added" : "Public note added",
                    _ => "Message added",
                }) with { ConversationId = e.ConversationId, ConversationKind = e.Kind };
        }

        var registration = external.FirstOrDefault(x => x.EventType == evt.GetType());
        if (registration is null || ReadGuid(evt, "TicketId") is not { } ticketId)
        {
            return null;
        }
        var workspaceId = ReadGuid(evt, "WorkspaceId")
            ?? await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => (Guid?)t.WorkspaceId).FirstOrDefaultAsync(ct);
        return workspaceId is { } ws ? Base(registration.Key, ticketId, ws, registration.Label) : null;
    }

    private static Guid? ReadGuid(object evt, string property) =>
        evt.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(evt) switch
        {
            Guid g => g,
            _ => null,
        };
}

/// <summary>
/// Entry point for every triggering event: honours the pause switch (queue or drop, SR-AUT-2),
/// runs matching workflows, then auto-assignment.
/// </summary>
public sealed class AutomationDispatcher(
    IServiceScopeFactory scopes,
    WorkflowEngine engine,
    AutoAssigner assigner,
    IEnumerable<ExternalTrigger> external,
    TimeProvider time)
{
    /// <returns>False when automations are paused and the event was queued or dropped.</returns>
    public async Task<bool> DispatchAsync(DomainEvent evt, CancellationToken ct, bool fromQueue = false)
    {
        TriggerEvent? te;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            te = await TriggerEventMapper.MapAsync(evt, db, external, ct);
            if (te is null)
            {
                return true;
            }
            var settings = sp.GetRequiredService<AutomationSettings>();
            if (await settings.IsPausedAsync(te.WorkspaceId, ct))
            {
                if (!fromQueue && await settings.QueueWhilePausedAsync(te.WorkspaceId, ct))
                {
                    db.Set<AutQueuedEvent>().Add(new AutQueuedEvent
                    {
                        EventId = evt.EventId,
                        WorkspaceId = te.WorkspaceId,
                        EventType = $"{evt.GetType().FullName}, {evt.GetType().Assembly.GetName().Name}",
                        PayloadJson = JsonSerializer.Serialize(evt, evt.GetType(), JsonDefaults.Options),
                        OccurredAt = evt.OccurredAt,
                        CreatedAt = time.GetUtcNow(),
                    });
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException ex) when (Sql.IsUniqueViolation(ex))
                    {
                        // Already queued by an earlier attempt.
                    }
                }
                return false;
            }
        }

        await engine.RunAsync(te, ct);
        await assigner.HandleAsync(te, ct);
        return true;
    }

    /// <summary>Replays events queued while paused, oldest first, for workspaces that are running again.</summary>
    public async Task<int> DrainQueueAsync(int max, CancellationToken ct)
    {
        List<AutQueuedEvent> batch;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var settings = sp.GetRequiredService<AutomationSettings>();
            if (await settings.IsPausedAsync(null, ct))
            {
                return 0;
            }
            var queued = sp.GetRequiredService<SwitchboardDbContext>().Set<AutQueuedEvent>().AsNoTracking();
            // Skip still-paused workspaces before taking a batch, so their backlog can't starve the others.
            var running = new List<Guid?>();
            foreach (var workspaceId in await queued.Select(q => q.WorkspaceId).Distinct().ToListAsync(ct))
            {
                if (!await settings.IsPausedAsync(workspaceId, ct))
                {
                    running.Add(workspaceId);
                }
            }
            batch = await queued.Where(q => running.Contains(q.WorkspaceId))
                .OrderBy(q => q.OccurredAt).Take(max).ToListAsync(ct);
        }

        var done = 0;
        foreach (var queued in batch)
        {
            var type = Type.GetType(queued.EventType);
            var evt = type is null ? null : JsonSerializer.Deserialize(queued.PayloadJson, type, JsonDefaults.Options) as DomainEvent;
            if (evt is not null && !await DispatchAsync(evt, ct, fromQueue: true))
            {
                // Its workspace is still paused; leave it queued.
                continue;
            }
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>().Set<AutQueuedEvent>()
                .Where(q => q.Id == queued.Id).ExecuteDeleteAsync(ct);
            done++;
        }
        return done;
    }
}

/// <summary>
/// Assigns an agent when a ticket lands in a group without one (FR-RTE-1/2), using the group's
/// assignment mode. Runs as an automation actor, inside the cascade of the event that caused it.
/// </summary>
public sealed class AutoAssigner(IServiceScopeFactory scopes, ILogger<AutoAssigner> logger)
{
    public const string ActorName = "Auto-assignment";

    public async Task HandleAsync(TriggerEvent te, CancellationToken ct)
    {
        if (te.TriggerKey != TriggerKeys.TicketCreated && !(te.TriggerKey == TriggerKeys.Assigned && te.LandedInGroupWithoutAgent))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var limits = await sp.GetRequiredService<AutomationSettings>().LimitsAsync(te.WorkspaceId, ct);
            if (te.CascadeDepth >= limits.MaxCascadeDepth)
            {
                return;
            }

            var ticket = await db.Tickets.AsNoTracking()
                .Where(t => t.Id == te.TicketId)
                .Select(t => new
                {
                    t.GroupId,
                    t.AgentId,
                    t.RowVersion,
                    Closed = t.DeletedAt != null || t.IsSpam || t.MergedIntoId != null || t.PurgedAt != null
                        || t.Status!.Category == StatusCategory.Resolved || t.Status.Category == StatusCategory.Closed,
                })
                .FirstOrDefaultAsync(ct);
            if (ticket is not { GroupId: { } groupId, AgentId: null, Closed: false })
            {
                return;
            }
            var mode = await db.AgentGroups.AsNoTracking().Where(g => g.Id == groupId).Select(g => g.AssignmentMode).FirstOrDefaultAsync(ct);
            if (mode is not (AssignmentMode.RoundRobin or AssignmentMode.LoadBased))
            {
                return;
            }

            var root = await WorkflowEngine.ResolveCascadeRootAsync(db, te, ct);
            using var actor = sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.Automation(ActorName, te.CascadeDepth + 1));
            using var lineage = AutomationRunContext.Begin(null, root);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var choice = await sp.GetRequiredService<AssignmentService>().ClaimAsync(groupId, mode, ct);
            if (choice.AgentId is not { } agentId)
            {
                logger.LogInformation("No agent assigned to ticket {Ticket}: {Reason}", te.TicketId, choice.Reason);
                return;
            }
            try
            {
                // Expected version: never overwrite an agent someone picked in the meantime.
                await sp.GetRequiredService<ITicketService>().UpdateAsync(te.TicketId, new TicketPatch { AgentId = agentId }, ticket.RowVersion, ct);
                await tx.CommitAsync(ct);
                return;
            }
            catch (ConflictException)
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is ValidationException or ForbiddenException or NotFoundException)
            {
                logger.LogWarning("Auto-assignment skipped ticket {Ticket}: {Message}", te.TicketId, ex.Message);
                return;
            }
        }
    }
}

/// <summary>After-commit handler for the Core ticket events.</summary>
internal sealed class AutomationEventHandler(AutomationDispatcher dispatcher) :
    IEventHandler<TicketCreated>,
    IEventHandler<TicketUpdated>,
    IEventHandler<TicketStatusChanged>,
    IEventHandler<TicketAssigned>,
    IEventHandler<TicketConversationAdded>
{
    public Task HandleAsync(TicketCreated e, CancellationToken ct) => dispatcher.DispatchAsync(e, ct);
    public Task HandleAsync(TicketUpdated e, CancellationToken ct) => dispatcher.DispatchAsync(e, ct);
    public Task HandleAsync(TicketStatusChanged e, CancellationToken ct) => dispatcher.DispatchAsync(e, ct);
    public Task HandleAsync(TicketAssigned e, CancellationToken ct) => dispatcher.DispatchAsync(e, ct);
    public Task HandleAsync(TicketConversationAdded e, CancellationToken ct) => dispatcher.DispatchAsync(e, ct);
}

/// <summary>After-commit handler for another module's ticket event (e.g. SLA breached), bound by convention.</summary>
internal sealed class ExternalTicketEventHandler<TEvent>(AutomationDispatcher dispatcher) : IEventHandler<TEvent> where TEvent : DomainEvent
{
    public Task HandleAsync(TEvent e, CancellationToken ct) => dispatcher.DispatchAsync(e, ct);
}

public sealed record TestStep(string Title, bool External, string? NoChange, string? Error, IReadOnlyDictionary<string, object?> Input);

public sealed record TestReport(
    string TicketDisplayId,
    string TicketSubject,
    string TriggerNote,
    EvaluationResult Evaluation,
    IReadOnlyList<TestStep> Steps);

/// <summary>
/// Reports what a definition would do to a ticket. Reads only: no writes, no web requests, no email
/// (SR-AUT-3). Changes are simulated in memory so later steps see earlier ones.
/// </summary>
public sealed class WorkflowTestRunner(CatalogLoader catalogs, TicketFactsLoader loader, ActionRunner runner, Net.OutboundNetworkPolicy network)
{
    public async Task<TestReport?> RunAsync(WorkflowDefinition def, Guid workspaceId, Guid ticketId, string workflowName, CancellationToken ct)
    {
        var catalog = await catalogs.LoadAsync(workspaceId, ct);
        var facts = await loader.LoadAsync(ticketId, null, ct);
        if (facts is null || facts.WorkspaceId != workspaceId)
        {
            return null;
        }
        var subject = facts.Placeholders.GetValueOrDefault("ticket.subject") ?? "";
        var evaluation = ConditionEvaluator.Evaluate(def.Conditions, facts, catalog);
        var triggerNote = def.Trigger.Event switch
        {
            TriggerKeys.Time => "A test assumes the time has passed. When live, it runs once each time the clock crosses the threshold.",
            TriggerKeys.TicketUpdated when def.Trigger.Changes.Count > 0 => "A test assumes the field change happened.",
            TriggerKeys.Conversation => "A test assumes the message was added. Message conditions are empty in a test.",
            _ => "A test assumes the trigger just happened.",
        };

        var steps = new List<TestStep>();
        foreach (var action in def.Actions)
        {
            try
            {
                var plan = await runner.PlanAsync(action, facts, catalog, workflowName, ct);
                var noChange = plan.NoChange;
                if (plan.Web is { } web && Net.OutboundUrlPolicy.CheckStatic(web.Url, network) is { } problem)
                {
                    noChange = $"Would be blocked: {problem}";
                }
                steps.Add(new TestStep(plan.Title, plan.External, noChange, null, plan.Input));
                ActionRunner.Simulate(plan, facts, catalog);
            }
            catch (Exception ex) when (WorkflowExecutor.IsFinal(ex))
            {
                steps.Add(new TestStep(catalog.Action(action.Type)?.Label ?? action.Type, ActionTypes.IsExternal(action.Type), null, ex.Message,
                    new Dictionary<string, object?>()));
                break;
            }
        }
        return new TestReport(facts.DisplayId, subject, triggerNote, evaluation, steps);
    }
}
