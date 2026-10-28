using System.Text.Json;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Modules.ServiceLevels.Time;

namespace Switchboard.Modules.ServiceLevels.Domain;

/// <summary>A business calendar: working hours per weekday in a timezone, plus holidays (FR-SLA-1).</summary>
public class SlaCalendar : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>IANA or Windows id, resolved with <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>.</summary>
    public string TimeZoneId { get; set; } = "UTC";
    /// <summary>Every hour of every day. Holidays still apply.</summary>
    public bool Is24x7 { get; set; }
    /// <summary>Used when neither the policy nor the ticket's group names a calendar.</summary>
    public bool IsDefault { get; set; }
    /// <summary>Kept so tickets that used it still resolve; hidden from lists (INV-REF-1).</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public List<SlaWorkingHours> Hours { get; set; } = [];
    public List<SlaHoliday> Holidays { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public BusinessCalendar ToBusinessCalendar()
    {
        var zone = BusinessCalendar.FindZone(TimeZoneId) ?? TimeZoneInfo.Utc;
        var holidays = Holidays.Select(h => h.Date);
        return Is24x7
            ? BusinessCalendar.TwentyFourSeven(zone, holidays)
            : new BusinessCalendar(zone, Hours.Select(h => new WorkingInterval(h.Day, h.StartMinute, h.EndMinute)), holidays);
    }
}

/// <summary>One working interval. <see cref="EndMinute"/> past 1440 runs into the next day.</summary>
public class SlaWorkingHours : Entity
{
    public Guid CalendarId { get; set; }
    public DayOfWeek Day { get; set; }
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
}

public class SlaHoliday : Entity
{
    public Guid CalendarId { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>An SLA policy. Active policies form an ordered list; the first that matches applies (FR-SLA-2).</summary>
public class SlaPolicy : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Order { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Null: use the ticket group's calendar, then the workspace default.</summary>
    public Guid? CalendarId { get; set; }
    public string ConditionsJson { get; set; } = "[]";
    public string TargetsJson { get; set; } = "[]";
    public string RemindersJson { get; set; } = "[]";
    public string EscalationsJson { get; set; } = "{}";
    /// <summary>Soft delete: tickets keep a valid reference to the policy that applied (INV-REF-1).</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<SlaCondition> Conditions
    {
        get => SlaJson.Read<List<SlaCondition>>(ConditionsJson) ?? [];
        set => ConditionsJson = SlaJson.Write(value);
    }

    public List<SlaTarget> Targets
    {
        get => SlaJson.Read<List<SlaTarget>>(TargetsJson) ?? [];
        set => TargetsJson = SlaJson.Write(value);
    }

    public List<SlaReminderRule> Reminders
    {
        get => SlaJson.Read<List<SlaReminderRule>>(RemindersJson) ?? [];
        set => RemindersJson = SlaJson.Write(value);
    }

    public SlaEscalationPlan Escalations
    {
        get => SlaJson.Read<SlaEscalationPlan>(EscalationsJson) ?? new SlaEscalationPlan();
        set => EscalationsJson = SlaJson.Write(value);
    }

    public SlaTarget? TargetFor(Priority priority) => Targets.FirstOrDefault(t => t.Priority == priority);
}

/// <summary>Condition field keys. Custom fields use <c>custom:&lt;key&gt;</c>.</summary>
public static class SlaConditionFields
{
    public const string Type = "type";
    public const string Source = "source";
    public const string RequesterGroup = "requester_group";
    public const string Department = "department";
    public const string Category = "category";
    public const string Group = "group";
    public const string CustomPrefix = "custom:";
}

/// <summary>Matches when the ticket's value for <see cref="Field"/> is any of <see cref="Values"/>.</summary>
public sealed record SlaCondition(string Field, List<string> Values);

/// <summary>Targets for one priority, in minutes. Null means no target of that kind.</summary>
public sealed record SlaTarget(Priority Priority, int? FirstResponseMinutes, int? NextResponseMinutes, int? ResolutionMinutes, bool BusinessHours = true);

public enum SlaTargetKind
{
    FirstResponse = 1,
    Resolution = 2,
    NextResponse = 3,
}

/// <summary>Remind the assigned agent (or the group, when unassigned) N minutes before a target.</summary>
public sealed record SlaReminderRule(SlaTargetKind Target, int MinutesBefore);

/// <summary>Who to tell. Resolved to concrete agents when the notification fires.</summary>
public sealed record SlaNotifyTargets
{
    /// <summary>The assigned agent; while nobody is assigned, the members of the ticket's group.</summary>
    public bool Assignee { get; init; }
    public bool AssigneeManager { get; init; }
    public List<Guid> GroupIds { get; init; } = [];
    public List<Guid> UserIds { get; init; } = [];

    public bool IsEmpty => !Assignee && !AssigneeManager && GroupIds.Count == 0 && UserIds.Count == 0;
}

/// <summary>A resolution escalation level (1-4): fires <see cref="DelayMinutes"/> after the breach.</summary>
public sealed record SlaEscalationLevel(int Level, int DelayMinutes, SlaNotifyTargets Notify);

public sealed record SlaEscalationPlan
{
    /// <summary>Told when a first (or next) response target is missed.</summary>
    public SlaNotifyTargets FirstResponse { get; init; } = new();
    public List<SlaEscalationLevel> Resolution { get; init; } = [];
}

/// <summary>
/// The SLA state of one ticket: which policy applies and why (INV-SLA-1), the targets it was given,
/// pause intervals, the cycle, and manual overrides. Due dates on the ticket are recomputed from this
/// row as a pure function (INV-SLA-3).
/// </summary>
public class SlaTicketState : IActivitySubject, IHasTimestamps
{
    public Guid TicketId { get; set; }
    public Guid WorkspaceId { get; set; }

    public Guid? PolicyId { get; set; }
    public string? PolicyName { get; set; }
    public string SelectionReason { get; set; } = "";
    public DateTimeOffset? SelectedAt { get; set; }

    /// <summary>Targets copied from the policy when it was selected (or last re-evaluated).</summary>
    public Priority? TargetPriority { get; set; }
    public int? FirstResponseMinutes { get; set; }
    public int? NextResponseMinutes { get; set; }
    public int? ResolutionMinutes { get; set; }
    public bool BusinessHours { get; set; } = true;
    public Guid? CalendarId { get; set; }

    /// <summary>Starts at 1; a reopen starts a new cycle (INV-SLA-4).</summary>
    public int Cycle { get; set; } = 1;
    public DateTimeOffset CycleStartedAt { get; set; }

    /// <summary>Set while the clock is paused (status with the SLA timer off, or trash).</summary>
    public DateTimeOffset? PausedSince { get; set; }
    public string PausesJson { get; set; } = "[]";

    /// <summary>The computed targets, kept even when a person overrides the due date (FR-SLA-6).</summary>
    public DateTimeOffset? FirstResponseTargetAt { get; set; }
    public DateTimeOffset? ResolutionTargetAt { get; set; }
    public bool FirstResponseOverridden { get; set; }
    public bool ResolutionOverridden { get; set; }
    public string? OverrideReason { get; set; }
    public Guid? OverriddenById { get; set; }
    public DateTimeOffset? OverriddenAt { get; set; }

    /// <summary>Open "next response" window: started by a requester message, closed by an agent reply.</summary>
    public DateTimeOffset? NextResponseStartedAt { get; set; }
    public DateTimeOffset? NextResponseDueBy { get; set; }
    public int NextResponseSeq { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    string IActivitySubject.ActivitySubjectType => Ticket.Family;
    Guid IActivitySubject.ActivitySubjectId => TicketId;
    Guid? IActivitySubject.ActivityWorkspaceId => WorkspaceId;

    public List<SlaPause> Pauses
    {
        get => SlaJson.Read<List<SlaPause>>(PausesJson) ?? [];
        set => PausesJson = SlaJson.Write(value);
    }
}

/// <summary>A pause of the SLA clock. <see cref="EndedAt"/> is null while it lasts.</summary>
public sealed record SlaPause(int Cycle, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Reason)
{
    public const string StatusReason = "status";
    public const string TrashReason = "trash";

    public TimeRange Range => new(StartedAt, EndedAt ?? DateTimeOffset.MaxValue);
}

public enum SlaEventKind
{
    Reminder = 1,
    Breach = 2,
    Escalation = 3,
}

/// <summary>
/// A reminder, breach or escalation that fired. Unique per ticket, cycle, kind, target, level and
/// next-response window, so each fires at most once (INV-SLA-4), even across instances.
/// </summary>
public class SlaEvent : Entity, IActivitySubject
{
    public Guid TicketId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int Cycle { get; set; }
    public SlaEventKind Kind { get; set; }
    public SlaTargetKind Target { get; set; }
    /// <summary>Escalation level 1-4; reminder rule index; 0 for a breach.</summary>
    public int Level { get; set; }
    /// <summary>Next-response window number; 0 for other targets.</summary>
    public int Sequence { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset FiredAt { get; set; }
    public string Summary { get; set; } = "";
    public string NotifiedUserIdsJson { get; set; } = "[]";

    string IActivitySubject.ActivitySubjectType => Ticket.Family;
    Guid IActivitySubject.ActivitySubjectId => TicketId;
    Guid? IActivitySubject.ActivityWorkspaceId => WorkspaceId;
}

/// <summary>
/// Monitor bookkeeping: the due dates it last looked at and when it next needs to look. A ticket whose
/// due dates change (or whose <see cref="NextCheckAt"/> arrives) is checked again; others are skipped.
/// </summary>
public class SlaMonitorCursor
{
    public Guid TicketId { get; set; }
    public int Cycle { get; set; }
    public Guid? PolicyId { get; set; }
    public DateTimeOffset? FirstResponseDueBy { get; set; }
    public DateTimeOffset? DueBy { get; set; }
    public DateTimeOffset? NextResponseDueBy { get; set; }
    public int NextResponseSeq { get; set; }
    /// <summary>Null when nothing else can fire for these due dates.</summary>
    public DateTimeOffset? NextCheckAt { get; set; }
}

internal static class SlaJson
{
    public static T? Read<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
}
