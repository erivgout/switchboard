using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.ServiceLevels.Domain;
using Switchboard.Modules.ServiceLevels.Time;

namespace Switchboard.Modules.ServiceLevels.Engine;

public enum SlaTrigger
{
    Created,
    Updated,
    StatusChanged,
    Trashed,
    Restored,
    RequesterMessage,
    AgentReply,
}

/// <summary>What happened to the ticket, taken from the domain event.</summary>
public sealed record SlaChange(
    SlaTrigger Trigger,
    DateTimeOffset At,
    ActorSnapshot? Actor,
    IReadOnlyList<FieldChange>? Changes = null,
    StatusCategory? FromCategory = null,
    StatusCategory? ToCategory = null);

public static class SlaSettingKeys
{
    /// <summary>Re-select the policy and targets when priority or a condition field changes (FR-SLA-5). Default on.</summary>
    public const string ReevaluateOnChange = "sla.reevaluate_on_change";
}

/// <summary>
/// Keeps a ticket's SLA state and due dates consistent with its policy, status and history. Runs inside
/// <c>SaveChanges</c> (pre-commit) and only touches tracked entities. Every run recomputes the due dates
/// from the stored inputs, so running it again changes nothing (INV-SLA-3).
/// </summary>
public sealed class SlaEngine(SwitchboardDbContext db, ISettingsService settings, ILogger<SlaEngine> logger)
{
    /// <summary>Ticket change fields that never affect which policy applies.</summary>
    private static readonly HashSet<string> NonConditionFields = new(StringComparer.Ordinal)
    {
        "Subject", "Description", "Requested for", "CC", "Urgency", "Impact", "Agent", "Tags", "Major incident",
        "Due by", "First response due", "Planned start", "Planned end", "Planned effort", "Status",
    };

    private const string DueByField = "Due by";
    private const string FirstResponseDueField = "First response due";

    private readonly Dictionary<Guid, List<SlaPolicy>> _policies = [];
    private readonly Dictionary<Guid, BusinessCalendar> _calendars = [];

    public async Task EvaluateAsync(Guid ticketId, SlaChange change, CancellationToken ct)
    {
        var ticket = await db.Tickets.FindAsync([ticketId], ct);
        if (ticket is null || ticket.PurgedAt is not null)
        {
            return;
        }
        var status = ticket.Status is { } s && s.Id == ticket.StatusId ? s : await db.TicketStatuses.FindAsync([ticket.StatusId], ct);
        if (status is null)
        {
            return;
        }

        var now = change.At;
        var excluded = ticket.DeletedAt is not null || ticket.IsSpam || ticket.MergedIntoId is not null;
        var stopped = status.Category is StatusCategory.Resolved or StatusCategory.Closed;
        // INV-SLA-2 and INV-TKT-9: no time accrues while the status stops the clock or the ticket is in trash or spam.
        var shouldPause = !stopped && (excluded || !status.SlaTimerOn);

        var notes = new List<string>();
        var state = await db.Set<SlaTicketState>().FindAsync([ticketId], ct);
        var isNew = state is null;
        if (state is null)
        {
            if (excluded)
            {
                return;
            }
            state = new SlaTicketState
            {
                TicketId = ticket.Id,
                WorkspaceId = ticket.WorkspaceId,
                CycleStartedAt = ticket.CreatedAt == default ? now : ticket.CreatedAt,
            };
            db.Set<SlaTicketState>().Add(state);
        }

        var reevaluate = await settings.GetAsync(SlaSettingKeys.ReevaluateOnChange, true, ticket.WorkspaceId, ct);

        // A reopen starts a new cycle with a fresh resolution clock (INV-SLA-4).
        var reopened = !isNew && change.Trigger == SlaTrigger.StatusChanged
            && change.FromCategory is StatusCategory.Resolved or StatusCategory.Closed
            && change.ToCategory is StatusCategory.Open or StatusCategory.Pending;
        if (reopened)
        {
            state.Cycle++;
            state.CycleStartedAt = now;
            state.ResolutionOverridden = false;
            state.NextResponseStartedAt = null;
            notes.Add("Reopened, so a new resolution clock started.");
        }

        TrackPause(state, now, shouldPause, excluded ? SlaPause.TrashReason : SlaPause.StatusReason, status.Name, stopped, notes);
        ApplyOverrides(ticket, state, change, now, notes);
        TrackNextResponse(ticket, state, change, now, notes);

        var select = isNew || state.SelectedAt is null
            || (reevaluate && (reopened || (change.Trigger == SlaTrigger.Updated && (change.Changes ?? []).Any(c => !NonConditionFields.Contains(c.Field)))));
        if (select)
        {
            await SelectPolicyAsync(ticket, state, isNew, notes, now, ct);
        }

        await RecomputeAsync(ticket, state, ct);

        db.Entry(state).DetectChanges();
        var modified = db.Entry(state).State == EntityState.Modified;
        if (isNew || modified)
        {
            var summary = notes.Count > 0 ? string.Join(" ", notes) : "Service level due dates recalculated.";
            db.Activity(state, isNew ? "sla_applied" : "sla_updated", Truncate(summary, 1000));
        }
    }

    // ------------------------------------------------------------------ pauses

    private static void TrackPause(SlaTicketState state, DateTimeOffset now, bool shouldPause, string reason, string statusName, bool stopped, List<string> notes)
    {
        var pauses = state.Pauses;
        var open = pauses.FindLastIndex(p => p.EndedAt is null);
        if (shouldPause && open < 0)
        {
            pauses.Add(new SlaPause(state.Cycle, now, null, reason));
            state.PausedSince = now;
            notes.Add(reason == SlaPause.TrashReason
                ? "SLA clock paused while the ticket is in the trash or marked as spam."
                : $"SLA clock paused: the {statusName} status stops it.");
        }
        else if (!shouldPause && open >= 0)
        {
            var pause = pauses[open];
            pauses[open] = pause with { EndedAt = now };
            state.PausedSince = null;
            notes.Add(stopped
                ? "SLA clock stopped: the ticket is resolved."
                : $"SLA clock resumed after {Describe(now - pause.StartedAt)} on hold; due dates moved later by the paused working time.");
        }
        else
        {
            return;
        }
        state.Pauses = pauses;
    }

    private static IReadOnlyList<TimeRange> ClosedPauses(SlaTicketState state, int cycle) =>
        state.Pauses.Where(p => p.Cycle == cycle && p.EndedAt is not null).Select(p => p.Range).ToList();

    // ------------------------------------------------------------------ overrides (FR-SLA-6)

    private void ApplyOverrides(Ticket ticket, SlaTicketState state, SlaChange change, DateTimeOffset now, List<string> notes)
    {
        if (change.Trigger != SlaTrigger.Updated || change.Actor is null || change.Actor.Kind is ActorKind.System or ActorKind.Automation)
        {
            return;
        }
        foreach (var field in change.Changes ?? [])
        {
            var isResolution = field.Field == DueByField;
            if (!isResolution && field.Field != FirstResponseDueField)
            {
                continue;
            }
            var label = isResolution ? "resolution" : "first response";
            if (field.To is null)
            {
                // Clearing a hand-set date hands it back to the SLA target.
                SetOverridden(state, isResolution, false);
                notes.Add($"The {label} due date was cleared, so it follows the SLA target again.");
                continue;
            }
            if (!DateTimeOffset.TryParse(field.To, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            {
                continue;
            }
            SetOverridden(state, isResolution, true);
            // Another handler in this save may already have recomputed the field; the person's value wins.
            if (isResolution)
            {
                ticket.DueBy = value;
            }
            else
            {
                ticket.FirstResponseDueBy = value;
            }
            state.OverrideReason = OverrideReasonFor(ticket.Id) ?? state.OverrideReason;
            state.OverriddenById = change.Actor.UserId;
            state.OverriddenAt = now;
            notes.Add(state.OverrideReason is { Length: > 0 } reason
                ? $"The {label} due date was set by hand: {reason}"
                : $"The {label} due date was set by hand.");
        }
    }

    private static void SetOverridden(SlaTicketState state, bool resolution, bool value)
    {
        if (resolution)
        {
            state.ResolutionOverridden = value;
        }
        else
        {
            state.FirstResponseOverridden = value;
        }
    }

    /// <summary>The reason the ticket service audited in this unit of work, if any.</summary>
    private string? OverrideReasonFor(Guid ticketId)
    {
        var id = ticketId.ToString();
        var entry = db.ChangeTracker.Entries<AuditEntry>()
            .Where(e => e.State == EntityState.Added && e.Entity.Action == "tickets.due_overridden" && e.Entity.ObjectId == id)
            .Select(e => e.Entity)
            .LastOrDefault();
        if (entry is null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(entry.DetailsJson);
            return doc.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? Truncate(reason.GetString()!.Trim(), 500)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ next response

    private static void TrackNextResponse(Ticket ticket, SlaTicketState state, SlaChange change, DateTimeOffset now, List<string> notes)
    {
        if (change.Trigger == SlaTrigger.RequesterMessage
            && ticket.FirstRespondedAt is not null && state.NextResponseMinutes is not null && state.NextResponseStartedAt is null)
        {
            state.NextResponseStartedAt = now;
            state.NextResponseSeq++;
            notes.Add("The requester replied, so the next response clock started.");
        }
        else if (change.Trigger == SlaTrigger.AgentReply && state.NextResponseStartedAt is not null)
        {
            state.NextResponseStartedAt = null;
            notes.Add("An agent replied, so the next response clock stopped.");
        }
    }

    // ------------------------------------------------------------------ policy selection (INV-SLA-1)

    private async Task SelectPolicyAsync(Ticket ticket, SlaTicketState state, bool isNew, List<string> notes, DateTimeOffset now, CancellationToken ct)
    {
        var policies = await PoliciesAsync(ticket.WorkspaceId, ct);
        var facts = await FactsAsync(ticket, ct);
        var policy = SlaPolicyMatcher.Select(policies, facts);
        var target = policy?.TargetFor(ticket.Priority);
        var calendarId = policy is null ? null : await ResolveCalendarIdAsync(policy, ticket, ct);

        var policyChanged = isNew || state.PolicyId != policy?.Id;
        var targetsChanged = state.TargetPriority != (policy is null ? null : ticket.Priority)
            || state.FirstResponseMinutes != target?.FirstResponseMinutes
            || state.NextResponseMinutes != target?.NextResponseMinutes
            || state.ResolutionMinutes != target?.ResolutionMinutes
            || state.BusinessHours != (target?.BusinessHours ?? true)
            || state.CalendarId != calendarId;

        if (policyChanged)
        {
            state.PolicyId = policy?.Id;
            state.PolicyName = policy?.Name;
            state.SelectedAt = now;
            state.SelectionReason = policy is null
                ? "No active SLA policy matches this ticket."
                : await DescribeSelectionAsync(policy, policies, ct);
            notes.Insert(0, policy is null ? "No SLA policy applies." : $"SLA policy “{policy.Name}” applies. {state.SelectionReason}");
        }
        state.SelectedAt ??= now;
        ticket.SlaPolicyId = state.PolicyId;

        if (targetsChanged)
        {
            state.TargetPriority = policy is null ? null : ticket.Priority;
            state.FirstResponseMinutes = target?.FirstResponseMinutes;
            state.NextResponseMinutes = target?.NextResponseMinutes;
            state.ResolutionMinutes = target?.ResolutionMinutes;
            state.BusinessHours = target?.BusinessHours ?? true;
            state.CalendarId = calendarId;
            if (state.NextResponseMinutes is null)
            {
                state.NextResponseStartedAt = null;
            }
            if (!policyChanged && policy is not null)
            {
                notes.Add($"Targets updated for {ticket.Priority} priority.");
            }
        }
    }

    private async Task<List<SlaPolicy>> PoliciesAsync(Guid workspaceId, CancellationToken ct)
    {
        if (!_policies.TryGetValue(workspaceId, out var list))
        {
            list = await db.Set<SlaPolicy>().AsNoTracking()
                .Where(p => p.WorkspaceId == workspaceId && p.DeletedAt == null && p.IsActive)
                .OrderBy(p => p.Order).ThenBy(p => p.CreatedAt)
                .ToListAsync(ct);
            _policies[workspaceId] = list;
        }
        return list;
    }

    private async Task<SlaTicketFacts> FactsAsync(Ticket ticket, CancellationToken ct)
    {
        var requesterGroups = await db.RequesterGroupMembers.AsNoTracking()
            .Where(m => m.UserId == ticket.RequesterId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);
        var categories = new[] { ticket.CategoryId, ticket.SubCategoryId, ticket.ItemCategoryId }
            .Where(c => c is not null).Select(c => c!.Value).ToList();
        return new SlaTicketFacts(
            ticket.Type,
            ticket.SourceId,
            requesterGroups.ToHashSet(),
            ticket.DepartmentId,
            categories,
            ticket.GroupId,
            CustomFieldService.Parse(ticket.CustomFieldsJson));
    }

    /// <summary>The policy's calendar, else the ticket group's, else the workspace default.</summary>
    private async Task<Guid?> ResolveCalendarIdAsync(SlaPolicy policy, Ticket ticket, CancellationToken ct)
    {
        var calendars = db.Set<SlaCalendar>().AsNoTracking().Where(c => c.WorkspaceId == ticket.WorkspaceId && c.DeletedAt == null);
        if (policy.CalendarId is { } own && await calendars.AnyAsync(c => c.Id == own, ct))
        {
            return own;
        }
        if (ticket.GroupId is { } groupId)
        {
            var groupCalendar = await db.AgentGroups.AsNoTracking().Where(g => g.Id == groupId).Select(g => g.BusinessCalendarId).FirstOrDefaultAsync(ct);
            if (groupCalendar is { } gc && await calendars.AnyAsync(c => c.Id == gc, ct))
            {
                return gc;
            }
        }
        return await calendars.Where(c => c.IsDefault).OrderBy(c => c.CreatedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<string> DescribeSelectionAsync(SlaPolicy policy, List<SlaPolicy> ordered, CancellationToken ct)
    {
        var position = ordered.FindIndex(p => p.Id == policy.Id);
        var skipped = position <= 0 ? "" : position == 1 ? "1 earlier policy didn't match. " : $"{position} earlier policies didn't match. ";
        var conditions = policy.Conditions.Where(c => c.Values.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
        if (conditions.Count == 0)
        {
            return $"{skipped}It has no conditions, so it covers every ticket that reaches it.";
        }
        var parts = new List<string>();
        foreach (var condition in conditions)
        {
            parts.Add(await DescribeConditionAsync(condition, ct));
        }
        return $"{skipped}Conditions met: {string.Join("; ", parts)}.";
    }

    internal async Task<string> DescribeConditionAsync(SlaCondition condition, CancellationToken ct)
    {
        var ids = condition.Values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
        IEnumerable<string> names = condition.Field switch
        {
            SlaConditionFields.Type => condition.Values.Select(v => v == "incident" ? "Incident" : "Service request"),
            SlaConditionFields.Source => await db.TicketSources.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Name).ToListAsync(ct),
            SlaConditionFields.RequesterGroup => await db.RequesterGroups.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Name).ToListAsync(ct),
            SlaConditionFields.Department => await db.Departments.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Name).ToListAsync(ct),
            SlaConditionFields.Category => await db.TicketCategories.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Name).ToListAsync(ct),
            SlaConditionFields.Group => await db.AgentGroups.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => x.Name).ToListAsync(ct),
            _ => condition.Values,
        };
        return $"{SlaText.ConditionLabel(condition.Field)} is {SlaText.JoinOr(names.ToList())}";
    }

    // ------------------------------------------------------------------ due dates (INV-SLA-3)

    private async Task RecomputeAsync(Ticket ticket, SlaTicketState state, CancellationToken ct)
    {
        // The first response target belongs to the first cycle; a reopen doesn't ask for it again.
        // Once answered it is settled: later pauses, priority or policy changes don't move it.
        var settled = state.Cycle > 1 || (ticket.FirstRespondedAt is not null && state.FirstResponseTargetAt is not null);

        if (state.PolicyId is null)
        {
            if (!settled)
            {
                state.FirstResponseTargetAt = null;
            }
            state.ResolutionTargetAt = null;
            state.NextResponseDueBy = null;
            if (!state.FirstResponseOverridden)
            {
                ticket.FirstResponseDueBy = state.FirstResponseTargetAt;
            }
            if (!state.ResolutionOverridden)
            {
                ticket.DueBy = null;
            }
            return;
        }

        var calendar = state.BusinessHours ? await CalendarAsync(state.CalendarId, ct) : BusinessCalendar.AlwaysOpen;

        if (!settled)
        {
            var created = ticket.CreatedAt == default ? state.CycleStartedAt : ticket.CreatedAt;
            state.FirstResponseTargetAt = Due(calendar, created, state.FirstResponseMinutes, ClosedPauses(state, 1));
        }
        if (!state.FirstResponseOverridden)
        {
            ticket.FirstResponseDueBy = state.FirstResponseTargetAt;
        }

        var cyclePauses = ClosedPauses(state, state.Cycle);
        state.ResolutionTargetAt = Due(calendar, state.CycleStartedAt, state.ResolutionMinutes, cyclePauses);
        if (!state.ResolutionOverridden)
        {
            ticket.DueBy = state.ResolutionTargetAt;
        }

        state.NextResponseDueBy = state.NextResponseStartedAt is { } started
            ? Due(calendar, started, state.NextResponseMinutes, cyclePauses)
            : null;
    }

    private DateTimeOffset? Due(BusinessCalendar calendar, DateTimeOffset start, int? minutes, IReadOnlyList<TimeRange> pauses)
    {
        if (minutes is not { } m)
        {
            return null;
        }
        try
        {
            return calendar.Add(start, TimeSpan.FromMinutes(m), pauses);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Business calendar has no working time; using calendar hours");
            return BusinessCalendar.AlwaysOpen.Add(start, TimeSpan.FromMinutes(m), pauses);
        }
    }

    private async Task<BusinessCalendar> CalendarAsync(Guid? calendarId, CancellationToken ct)
    {
        if (calendarId is not { } id)
        {
            return BusinessCalendar.AlwaysOpen;
        }
        if (!_calendars.TryGetValue(id, out var calendar))
        {
            var row = await db.Set<SlaCalendar>().AsNoTracking()
                .Include(c => c.Hours)
                .Include(c => c.Holidays)
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            calendar = row?.ToBusinessCalendar() ?? BusinessCalendar.AlwaysOpen;
            _calendars[id] = calendar;
        }
        return calendar;
    }

    private static string Describe(TimeSpan span) => SlaText.Duration(span);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
