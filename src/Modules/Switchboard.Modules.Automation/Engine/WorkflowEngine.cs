using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Net;

namespace Switchboard.Modules.Automation.Engine;

public static class TriggerMatcher
{
    /// <summary>True when the event is the workflow's trigger and passes its trigger-level filters.</summary>
    public static bool Matches(TriggerDefinition trigger, TriggerEvent te, BuilderCatalog catalog)
    {
        if (!string.Equals(trigger.Event, te.TriggerKey, StringComparison.Ordinal))
        {
            return false;
        }
        switch (trigger.Event)
        {
            case TriggerKeys.TicketUpdated when trigger.Changes.Count > 0:
                return trigger.Changes.Any(filter =>
                {
                    var field = catalog.Field(filter.Field);
                    var change = field?.ChangeName is { } changeName
                        ? te.Changes.FirstOrDefault(c => string.Equals(c.Field, changeName, StringComparison.OrdinalIgnoreCase))
                        : null;
                    return change is not null && (string.IsNullOrWhiteSpace(filter.To) || ChangedTo(field!, filter.To, change, catalog));
                });
            case TriggerKeys.Conversation:
                return trigger.Conversation switch
                {
                    "requester" => te.ConversationKind == ConversationKind.RequesterMessage,
                    "agent_reply" => te.ConversationKind == ConversationKind.AgentReply,
                    "note" => te.ConversationKind == ConversationKind.Note,
                    _ => te.ConversationKind is not ConversationKind.System,
                };
            default:
                return true;
        }
    }

    /// <summary>
    /// "Changes to X", judged from the event's own change record (display values), so a change made
    /// later by another workflow can't make an earlier event look like it matched.
    /// </summary>
    public static bool ChangedTo(CatalogField field, string expected, FieldChange change, BuilderCatalog catalog)
    {
        static List<string> Split(string? value) =>
            (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (field.Key.StartsWith("ticket.cf.", StringComparison.Ordinal))
        {
            // Custom field changes carry JSON values.
            var values = new List<string>();
            try
            {
                var node = change.To is null ? null : System.Text.Json.Nodes.JsonNode.Parse(change.To);
                if (node is System.Text.Json.Nodes.JsonArray arr)
                {
                    values.AddRange(arr.Select(x => x?.ToString() ?? ""));
                }
                else if (node is not null)
                {
                    values.Add(node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var str) ? str : node.ToJsonString());
                }
            }
            catch (System.Text.Json.JsonException)
            {
                values.Add(change.To ?? "");
            }
            return values.Any(v => string.Equals(v, expected, StringComparison.OrdinalIgnoreCase));
        }

        switch (field.Kind)
        {
            case FieldKind.Tags:
                // A tag was added in this change.
                return Split(change.To).Contains(expected, StringComparer.OrdinalIgnoreCase)
                    && !Split(change.From).Contains(expected, StringComparer.OrdinalIgnoreCase);
            case FieldKind.Text:
                return string.Equals(change.To, expected, StringComparison.OrdinalIgnoreCase);
            default:
                var label = catalog.OptionLabel(field.Key, expected);
                var to = change.To ?? "";
                return string.Equals(to, label, StringComparison.OrdinalIgnoreCase)
                    // A category condition matches any level: "Network" covers "Network / VPN".
                    || (field.Key == "ticket.category" && to.StartsWith(label + " / ", StringComparison.OrdinalIgnoreCase));
        }
    }
}

public enum RunOutcome
{
    NotRun,
    Succeeded,
    Failed,
    Blocked,
    AlreadyDone,
}

public sealed record RunRequest(
    AutWorkflow Workflow,
    WorkflowDefinition Definition,
    TriggerEvent Event,
    Guid CascadeRootId,
    EvaluationResult Evaluation,
    AutomationLimits Limits,
    BuilderCatalog Catalog);

/// <summary>
/// Executes one workflow run. Ticket changes and the run record commit in one transaction keyed by a
/// unique run key, so a retried event never applies them twice (INV-AUT-1). Email and web requests are
/// stored as pending steps in that transaction and sent after commit, each claimed at most once (SR-AUT-6).
/// </summary>
public sealed class WorkflowExecutor(
    IServiceScopeFactory scopes,
    ExternalStepSender sender,
    AlertService alerts,
    TimeProvider time,
    ILogger<WorkflowExecutor> logger)
{
    public async Task<RunOutcome> ExecuteAsync(RunRequest r, CancellationToken ct)
    {
        var wf = r.Workflow;
        var te = r.Event;
        var now = time.GetUtcNow();

        await using (var gateScope = scopes.CreateAsyncScope())
        {
            var db = gateScope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            var runs = db.Set<AutRun>().AsNoTracking();

            // INV-AUT-2 (also enforced by a unique index): never twice for the same ticket within one cascade.
            if (await runs.AnyAsync(x => x.WorkflowId == wf.Id && x.TicketId == te.TicketId && x.CascadeRootId == r.CascadeRootId && x.Status != RunStatus.Blocked, ct))
            {
                return RunOutcome.AlreadyDone;
            }
            if (te.CascadeDepth >= r.Limits.MaxCascadeDepth)
            {
                await RecordBlockedAsync(r, $"Stopped: this chain of automations reached the limit of {r.Limits.MaxCascadeDepth} steps.", "depth", ct);
                return RunOutcome.Blocked;
            }
            var since = now - AutomationLimits.RateWindow;
            if (await runs.CountAsync(x => x.WorkflowId == wf.Id && x.StartedAt > since && x.Status != RunStatus.Blocked, ct) >= r.Limits.WorkflowRateLimit)
            {
                await RecordBlockedAsync(r, $"Stopped: this workflow ran {r.Limits.WorkflowRateLimit} times in 10 minutes, its limit.", "workflow-rate", ct);
                return RunOutcome.Blocked;
            }
            if (await runs.CountAsync(x => x.TicketId == te.TicketId && x.StartedAt > since && x.Status != RunStatus.Blocked, ct) >= r.Limits.TicketRateLimit)
            {
                await RecordBlockedAsync(r, $"Stopped: automations ran {r.Limits.TicketRateLimit} times on this ticket in 10 minutes, its limit.", "ticket-rate", ct);
                return RunOutcome.Blocked;
            }
        }

        var runId = Guid.CreateVersion7();
        var steps = new List<AutRunStep>();
        Exception? failure = null;
        var failedAt = -1;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var runner = sp.GetRequiredService<ActionRunner>();
            var loader = sp.GetRequiredService<TicketFactsLoader>();
            using var actor = sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.Automation(wf.Name, te.CascadeDepth + 1));
            using var lineage = AutomationRunContext.Begin(runId, r.CascadeRootId);

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var run = NewRun(r, runId, RunStatus.Running, now);
            db.Set<AutRun>().Add(run);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (Sql.IsUniqueViolation(ex))
            {
                return RunOutcome.AlreadyDone;
            }

            steps.Add(ConditionsStep(runId, r.Evaluation, now));
            try
            {
                var facts = await loader.LoadAsync(te.TicketId, te.ConversationId, ct)
                    ?? throw new AutomationActionException("The ticket no longer exists.");
                for (var i = 0; i < r.Definition.Actions.Count; i++)
                {
                    failedAt = i;
                    var plan = await runner.PlanAsync(r.Definition.Actions[i], facts, r.Catalog, wf.Name, ct);
                    if (plan.External)
                    {
                        steps.Add(Step(runId, steps.Count, plan, plan.NoChange is null ? StepStatus.Pending : StepStatus.Succeeded, now,
                            input: ExternalInput(plan), output: plan.NoChange is null ? null : new { skipped = plan.NoChange }, external: true));
                        continue;
                    }
                    var output = await runner.ApplyAsync(plan, facts, ct);
                    steps.Add(Step(runId, steps.Count, plan, StepStatus.Succeeded, time.GetUtcNow(), plan.Input, output));
                    if (plan.NoChange is null)
                    {
                        facts = await loader.LoadAsync(te.TicketId, te.ConversationId, ct) ?? facts;
                    }
                }
                failedAt = -1;

                var pending = steps.Any(s => s.Status == StepStatus.Pending);
                run.Status = pending ? RunStatus.Running : RunStatus.Succeeded;
                run.FinishedAt = pending ? null : time.GetUtcNow();
                db.Set<AutRunStep>().AddRange(steps);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (IsFinal(ex))
            {
                failure = ex;
                await tx.RollbackAsync(CancellationToken.None);
            }
        }

        if (failure is not null)
        {
            await RecordFailedAsync(r, runId, steps, failedAt, failure, now, ct);
            await RecordOutcomeAsync(wf.Id, wf.WorkspaceId, success: false, failure.Message, r.Limits, ct);
            return RunOutcome.Failed;
        }

        if (steps.Any(s => s.Status == StepStatus.Pending))
        {
            await sender.SendRunAsync(runId, ct);
            return RunOutcome.Succeeded;
        }
        await RecordOutcomeAsync(wf.Id, wf.WorkspaceId, success: true, null, r.Limits, ct);
        return RunOutcome.Succeeded;
    }

    /// <summary>Domain failures end the run; anything else (deadlocks, conflicts, outages) rethrows so the event is retried.</summary>
    public static bool IsFinal(Exception ex) =>
        ex is ValidationException or ForbiddenException or NotFoundException or AutomationActionException;

    private static AutRun NewRun(RunRequest r, Guid runId, RunStatus status, DateTimeOffset now) => new()
    {
        Id = runId,
        WorkflowId = r.Workflow.Id,
        WorkflowName = r.Workflow.Name,
        Version = r.Workflow.PublishedVersion,
        RunKey = r.Event.RunKey(r.Workflow.Id),
        TicketId = r.Event.TicketId,
        TriggerKey = r.Event.TriggerKey,
        TriggerSummary = Truncate(r.Event.Summary, 500),
        EventId = r.Event.EventId,
        Anchor = r.Event.Anchor,
        CascadeRootId = r.CascadeRootId,
        CascadeDepth = r.Event.CascadeDepth,
        Status = status,
        StartedAt = now,
    };

    private async Task RecordBlockedAsync(RunRequest r, string message, string reason, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            var run = NewRun(r, Guid.CreateVersion7(), RunStatus.Blocked, now);
            run.Message = message;
            run.FinishedAt = now;
            db.Set<AutRun>().Add(run);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (Sql.IsUniqueViolation(ex))
            {
                return;
            }
        }
        logger.LogWarning("Workflow {Workflow} blocked for ticket {Ticket}: {Message}", r.Workflow.Name, r.Event.TicketId, message);
        // SR-AUT-1: exceeding a limit alerts admins, at most once per workflow, reason and hour.
        await alerts.SendOnceAsync($"blocked:{reason}:{r.Workflow.Id:N}:{now:yyyyMMddHH}", r.Workflow.WorkspaceId,
            $"Automation stopped: {r.Workflow.Name}",
            $"{message}\n\nWorkflow: {r.Workflow.Name}\nTicket: {r.Event.TicketId}\n\nCheck the workflow's run history for details.", ct);
    }

    private async Task RecordFailedAsync(RunRequest r, Guid runId, List<AutRunStep> steps, int failedAt, Exception failure, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        var run = NewRun(r, runId, RunStatus.Failed, now);
        run.FinishedAt = time.GetUtcNow();
        run.Message = Truncate(failure.Message, 2000);
        foreach (var step in steps)
        {
            if (step.Kind != "conditions")
            {
                step.Status = step.Status == StepStatus.Pending ? StepStatus.Skipped : StepStatus.RolledBack;
                step.Error = step.Status == StepStatus.Skipped ? "Not sent because the run failed." : "Undone because a later step failed.";
            }
        }
        if (failedAt >= 0 && failedAt < r.Definition.Actions.Count)
        {
            var action = r.Definition.Actions[failedAt];
            steps.Add(new AutRunStep
            {
                RunId = runId,
                Order = steps.Count,
                Kind = action.Type,
                Title = r.Catalog.Action(action.Type)?.Label ?? action.Type,
                Status = StepStatus.Failed,
                Error = Truncate(failure.Message, 4000),
                CreatedAt = now,
                FinishedAt = time.GetUtcNow(),
            });
            for (var i = failedAt + 1; i < r.Definition.Actions.Count; i++)
            {
                var next = r.Definition.Actions[i];
                steps.Add(new AutRunStep
                {
                    RunId = runId,
                    Order = steps.Count,
                    Kind = next.Type,
                    Title = r.Catalog.Action(next.Type)?.Label ?? next.Type,
                    Status = StepStatus.Skipped,
                    Error = "Not run because an earlier step failed.",
                    CreatedAt = now,
                });
            }
        }
        run.Message = $"Nothing was changed. {run.Message}";
        db.Set<AutRun>().Add(run);
        db.Set<AutRunStep>().AddRange(steps);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Sql.IsUniqueViolation(ex))
        {
            // A concurrent attempt recorded this run already.
        }
    }

    /// <summary>Updates the workflow's health; disables it after too many failures in a row (SR-AUT-4).</summary>
    public async Task RecordOutcomeAsync(Guid workflowId, Guid workspaceId, bool success, string? error, AutomationLimits limits, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        var set = db.Set<AutWorkflow>().Where(w => w.Id == workflowId);
        if (success)
        {
            await set.ExecuteUpdateAsync(s => s
                .SetProperty(w => w.ConsecutiveFailures, 0)
                .SetProperty(w => w.LastRunAt, now)
                .SetProperty(w => w.LastRunStatus, RunStatus.Succeeded), ct);
            return;
        }

        await set.ExecuteUpdateAsync(s => s
            .SetProperty(w => w.ConsecutiveFailures, w => w.ConsecutiveFailures + 1)
            .SetProperty(w => w.LastRunAt, now)
            .SetProperty(w => w.LastRunStatus, RunStatus.Failed), ct);
        var current = await set.AsNoTracking().Select(w => new { w.Name, w.ConsecutiveFailures, w.IsEnabled }).FirstOrDefaultAsync(ct);
        if (current is not { IsEnabled: true } || current.ConsecutiveFailures < limits.FailureThreshold)
        {
            return;
        }

        var reason = $"Turned off after {current.ConsecutiveFailures} failed runs in a row. Last error: {Truncate(error ?? "unknown", 400)}";
        var disabled = await set.Where(w => w.IsEnabled).ExecuteUpdateAsync(s => s
            .SetProperty(w => w.IsEnabled, false)
            .SetProperty(w => w.AutoDisabledAt, now)
            .SetProperty(w => w.DisabledReason, reason), ct);
        if (disabled != 1)
        {
            return;
        }
        using (sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Automation")))
        {
            await sp.GetRequiredService<IAuditLog>().WriteAsync("automation.workflow_auto_disabled", "workflow", workflowId.ToString(),
                new { current.Name, current.ConsecutiveFailures, error }, ct);
        }
        await alerts.SendOnceAsync($"disabled:{workflowId:N}:{now.UtcTicks}", workspaceId, $"Workflow turned off: {current.Name}",
            $"The workflow \"{current.Name}\" failed {current.ConsecutiveFailures} times in a row, so it was turned off.\n\n{reason}\n\nFix the problem, then turn it back on from Admin, Automations.", ct);
    }

    private static AutRunStep ConditionsStep(Guid runId, EvaluationResult eval, DateTimeOffset now) => new()
    {
        RunId = runId,
        Order = 0,
        Kind = "conditions",
        Title = eval.Groups.Count == 0 ? "No conditions: runs every time" : "Conditions matched",
        Status = StepStatus.Succeeded,
        OutputJson = JsonSerializer.Serialize(eval.Groups.Select(g => new
        {
            match = g.Match,
            matched = g.Matched,
            rules = g.Rules.Select(x => new { condition = x.Description, matched = x.Matched, actual = x.Actual }),
        }), WorkflowDefinition.Json),
        CreatedAt = now,
        FinishedAt = now,
    };

    private static AutRunStep Step(Guid runId, int order, PlannedStep plan, StepStatus status, DateTimeOffset now, object? input, object? output = null, bool external = false) => new()
    {
        RunId = runId,
        Order = order,
        Kind = plan.Action.Type,
        Title = Truncate(plan.Title, 500),
        InputJson = input is null ? null : JsonSerializer.Serialize(input, WorkflowDefinition.Json),
        OutputJson = output is null ? null : JsonSerializer.Serialize(output, WorkflowDefinition.Json),
        Status = status,
        IsExternal = external,
        CreatedAt = now,
        FinishedAt = status == StepStatus.Pending ? null : now,
    };

    /// <summary>What an external step needs to send later. Secrets stay encrypted.</summary>
    private static object ExternalInput(PlannedStep plan) => plan.Email is not null
        ? new ExternalStepInput { Email = plan.Email }
        : new ExternalStepInput { Web = plan.Web };

    internal static string Truncate(string? value, int max) => value is null ? "" : value.Length <= max ? value : value[..max];
}

public sealed class ExternalStepInput
{
    public EmailPayload? Email { get; set; }
    public WebRequestPayload? Web { get; set; }
}

/// <summary>Sends pending email and web request steps. Each step is claimed with a conditional update, so it is sent at most once.</summary>
public sealed class ExternalStepSender(
    IServiceScopeFactory scopes,
    SafeHttpClient http,
    SecretProtector secrets,
    IOptions<SwitchboardOptions> options,
    TimeProvider time,
    ILogger<ExternalStepSender> logger)
{
    public static readonly TimeSpan StaleSending = TimeSpan.FromMinutes(15);

    public async Task SendRunAsync(Guid runId, CancellationToken ct)
    {
        List<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            ids = await scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>().Set<AutRunStep>().AsNoTracking()
                .Where(s => s.RunId == runId && s.Status == StepStatus.Pending)
                .OrderBy(s => s.Order).Select(s => s.Id).ToListAsync(ct);
        }
        foreach (var id in ids)
        {
            await SendStepAsync(id, ct);
        }
        await FinishRunAsync(runId, ct);
    }

    public async Task SendStepAsync(Guid stepId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        var claimed = await db.Set<AutRunStep>().Where(s => s.Id == stepId && s.Status == StepStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.Sending), ct);
        if (claimed != 1)
        {
            return;
        }

        var step = await db.Set<AutRunStep>().FirstAsync(s => s.Id == stepId, ct);
        object? output = null;
        string? error = null;
        try
        {
            var input = JsonSerializer.Deserialize<ExternalStepInput>(step.InputJson ?? "{}", WorkflowDefinition.Json) ?? new ExternalStepInput();
            if (input.Email is { } email)
            {
                var messageId = await scope.ServiceProvider.GetRequiredService<ISystemEmailSender>().SendAsync(new OutboundEmail(
                    email.To, email.Subject, email.Html, email.Text,
                    Headers: new Dictionary<string, string>
                    {
                        ["Auto-Submitted"] = "auto-generated",
                        ["X-Switchboard-Workflow"] = email.Workflow.Replace("\r", " ").Replace("\n", " "),
                    }), ct);
                output = new { sent = email.To.Count, messageId };
            }
            else if (input.Web is { } web)
            {
                output = await SendWebAsync(web, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            error = ex is OutboundRequestBlockedException or TimeoutException or HttpRequestException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning(ex, "Automation step {Step} failed", stepId);
        }

        step.Status = error is null ? StepStatus.Succeeded : StepStatus.Failed;
        step.Error = error is null ? null : WorkflowExecutor.Truncate(error, 4000);
        step.OutputJson = output is null ? null : JsonSerializer.Serialize(output, WorkflowDefinition.Json);
        step.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<object> SendWebAsync(WebRequestPayload web, CancellationToken ct)
    {
        if (options.Value.NonProduction)
        {
            return new { skipped = "Not sent: this is a non-production instance." };
        }
        var headers = new Dictionary<string, string>();
        if (web.AuthHeader is { Length: > 0 } name && secrets.Unprotect(web.AuthValue) is { } value)
        {
            headers[name] = value;
        }
        var response = await http.SendAsync(new OutboundRequest(web.Method, web.Url, headers, web.Body, TimeSpan.FromSeconds(web.TimeoutSeconds)), ct);
        var body = response.Body.Length > 4000 ? response.Body[..4000] : response.Body;
        if (response.StatusCode >= 400)
        {
            throw new HttpRequestException($"The server answered {response.StatusCode}. {body}".Trim());
        }
        return new { status = response.StatusCode, contentType = response.ContentType, body, truncated = response.Truncated || response.Body.Length > 4000 };
    }

    /// <summary>Marks a run finished once no external steps are waiting, and records its outcome.</summary>
    public async Task FinishRunAsync(Guid runId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        var run = await db.Set<AutRun>().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status != RunStatus.Running)
        {
            return;
        }
        var statuses = await db.Set<AutRunStep>().AsNoTracking().Where(s => s.RunId == runId).Select(s => new { s.Status, s.Error, s.Title }).ToListAsync(ct);
        if (statuses.Any(s => s.Status is StepStatus.Pending or StepStatus.Sending))
        {
            return;
        }
        var failed = statuses.FirstOrDefault(s => s.Status == StepStatus.Failed);
        run.Status = failed is null ? RunStatus.Succeeded : RunStatus.Failed;
        run.Message = failed is null ? null : WorkflowExecutor.Truncate($"{failed.Title} failed: {failed.Error}. Ticket changes from this run were kept.", 2000);
        run.FinishedAt = time.GetUtcNow();
        var claimed = await db.Set<AutRun>().Where(r => r.Id == runId && r.Status == RunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, run.Status)
                .SetProperty(r => r.Message, run.Message)
                .SetProperty(r => r.FinishedAt, run.FinishedAt), ct);
        if (claimed == 1 && run.WorkflowId is { } workflowId)
        {
            var workspaceId = await db.Set<AutWorkflow>().Where(w => w.Id == workflowId).Select(w => w.WorkspaceId).FirstOrDefaultAsync(ct);
            var limits = await sp.GetRequiredService<AutomationSettings>().LimitsAsync(workspaceId, ct);
            await sp.GetRequiredService<WorkflowExecutor>().RecordOutcomeAsync(workflowId, workspaceId, failed is null, run.Message, limits, ct);
        }
    }

    /// <summary>Recovers after a crash: sends steps left pending, and fails steps stuck while sending (never resent).</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        List<Guid> pending;
        List<Guid> touchedRuns;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            var steps = db.Set<AutRunStep>();
            var stuckBefore = now - StaleSending;
            var stuck = await steps.Where(s => s.IsExternal && s.Status == StepStatus.Sending && s.CreatedAt < stuckBefore)
                .OrderBy(s => s.CreatedAt).Select(s => new { s.Id, s.RunId }).Take(200).ToListAsync(ct);
            foreach (var s in stuck)
            {
                await steps.Where(x => x.Id == s.Id && x.Status == StepStatus.Sending).ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.Status, StepStatus.Failed)
                    .SetProperty(x => x.Error, "Interrupted while sending. Not retried, so it is never sent twice.")
                    .SetProperty(x => x.FinishedAt, now), ct);
            }
            var pendingBefore = now.AddMinutes(-2);
            var waiting = await steps.Where(s => s.IsExternal && s.Status == StepStatus.Pending && s.CreatedAt < pendingBefore)
                .OrderBy(s => s.CreatedAt).Select(s => new { s.Id, s.RunId }).Take(100).ToListAsync(ct);
            pending = waiting.Select(s => s.Id).ToList();
            touchedRuns = stuck.Select(s => s.RunId).Concat(waiting.Select(s => s.RunId)).Distinct().ToList();
        }
        foreach (var id in pending)
        {
            await SendStepAsync(id, ct);
        }
        foreach (var runId in touchedRuns)
        {
            await FinishRunAsync(runId, ct);
        }
    }
}

/// <summary>Finds and runs the published workflows a trigger event matches, in execution order.</summary>
public sealed class WorkflowEngine(IServiceScopeFactory scopes, WorkflowExecutor executor, ILogger<WorkflowEngine> logger)
{
    public async Task RunAsync(TriggerEvent te, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        using var _ = sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Automation"));

        var workflows = await db.Set<AutWorkflow>().AsNoTracking()
            .Where(w => w.WorkspaceId == te.WorkspaceId && w.Module == WorkflowModules.Tickets && w.IsEnabled
                && w.PublishedTrigger == te.TriggerKey && w.PublishedJson != null)
            .OrderBy(w => w.Order).ThenBy(w => w.CreatedAt)
            .ToListAsync(ct);
        if (workflows.Count == 0)
        {
            return;
        }

        var loader = sp.GetRequiredService<TicketFactsLoader>();
        var facts = await loader.LoadAsync(te.TicketId, te.ConversationId, ct);
        if (facts is null || facts.IsClosedOrDeleted)
        {
            return;
        }
        var catalog = await sp.GetRequiredService<CatalogLoader>().LoadAsync(te.WorkspaceId, ct);
        var limits = await sp.GetRequiredService<AutomationSettings>().LimitsAsync(te.WorkspaceId, ct);
        var root = await ResolveCascadeRootAsync(db, te, ct);

        foreach (var wf in workflows)
        {
            WorkflowDefinition def;
            try
            {
                def = WorkflowDefinition.Parse(wf.PublishedJson);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Workflow {Workflow} has an unreadable definition", wf.Id);
                continue;
            }
            if (!TriggerMatcher.Matches(def.Trigger, te, catalog))
            {
                continue;
            }
            var evaluation = ConditionEvaluator.Evaluate(def.Conditions, facts, catalog);
            if (!evaluation.Matched)
            {
                continue;
            }
            var outcome = await executor.ExecuteAsync(new RunRequest(wf, def, te, root, evaluation, limits, catalog), ct);
            if (outcome == RunOutcome.Succeeded)
            {
                // Later workflows see this one's changes.
                facts = await loader.LoadAsync(te.TicketId, te.ConversationId, ct);
                if (facts is null || facts.IsClosedOrDeleted)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// The cascade an event belongs to: the cascade of the run that raised it, or a new one. Events
    /// from the same save share a root, so a workflow listening to several of them runs once.
    /// </summary>
    public static async Task<Guid> ResolveCascadeRootAsync(SwitchboardDbContext db, TriggerEvent te, CancellationToken ct)
    {
        var lineage = await db.Set<AutEventLineage>().AsNoTracking()
            .Where(l => l.EventId == te.EventId).Select(l => (Guid?)l.CascadeRootId).FirstOrDefaultAsync(ct);
        return lineage ?? Sql.DeterministicGuid("root", te.TicketId, te.OccurredAt.UtcTicks, te.Actor?.Kind, te.Actor?.UserId, te.Actor?.DisplayName);
    }
}
