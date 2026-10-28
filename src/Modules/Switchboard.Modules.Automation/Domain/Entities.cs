using Switchboard.Core.Domain;

namespace Switchboard.Modules.Automation.Domain;

/// <summary>An event-driven or time-based rule: When (trigger), If (conditions), Then (actions).</summary>
public class AutWorkflow : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    /// <summary>Record type the workflow acts on. Only <c>tickets</c> today; execution order is per module.</summary>
    public string Module { get; set; } = WorkflowModules.Tickets;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Lower runs first.</summary>
    public int Order { get; set; }
    public bool IsEnabled { get; set; }

    /// <summary>The editable definition (JSON <see cref="Definitions.WorkflowDefinition"/>).</summary>
    public string DraftJson { get; set; } = "{}";
    /// <summary>Number of the live version, or null while the workflow has never been published.</summary>
    public int? PublishedVersion { get; set; }
    /// <summary>Copy of the live version's definition, so dispatch needs one query.</summary>
    public string? PublishedJson { get; set; }
    /// <summary>Trigger key of the live version (e.g. <c>ticket.created</c>), for filtering at dispatch.</summary>
    public string? PublishedTrigger { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? AutoDisabledAt { get; set; }
    public string? DisabledReason { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public RunStatus? LastRunStatus { get; set; }

    public Guid? CreatedById { get; set; }
    public Guid? UpdatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool HasUnpublishedChanges => PublishedJson is null || !string.Equals(PublishedJson, DraftJson, StringComparison.Ordinal);
}

public static class WorkflowModules
{
    public const string Tickets = "tickets";
}

/// <summary>An immutable published definition. Rolling back publishes a copy of an older version.</summary>
public class AutWorkflowVersion : Entity
{
    public Guid WorkflowId { get; set; }
    public int Number { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public string Note { get; set; } = "";
    public Guid? CreatedById { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public enum RunStatus
{
    /// <summary>Ticket changes are saved; email or web request steps are still being sent.</summary>
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    /// <summary>Matched but stopped by a safety limit (cascade depth, rate limit).</summary>
    Blocked = 4,
}

/// <summary>One execution of a workflow (or of auto-assignment) for one record.</summary>
public class AutRun : Entity
{
    public Guid? WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";
    public int? Version { get; set; }
    /// <summary>Idempotency key: one run per triggering event and workflow (INV-AUT-1).</summary>
    public string RunKey { get; set; } = "";
    public Guid? TicketId { get; set; }
    public string TriggerKey { get; set; } = "";
    public string TriggerSummary { get; set; } = "";
    public Guid? EventId { get; set; }
    /// <summary>For time-based runs: the clock value that crossed the threshold (created, updated, status or due time).</summary>
    public DateTimeOffset? Anchor { get; set; }
    /// <summary>The first event of the chain this run belongs to (INV-AUT-2).</summary>
    public Guid CascadeRootId { get; set; }
    public int CascadeDepth { get; set; }
    public RunStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<AutRunStep> Steps { get; set; } = [];
}

public enum StepStatus
{
    Succeeded = 1,
    Failed = 2,
    /// <summary>External step (email, web request) waiting to be sent after the ticket changes commit.</summary>
    Pending = 3,
    /// <summary>External step claimed by a sender.</summary>
    Sending = 4,
    /// <summary>Not run because an earlier step failed.</summary>
    Skipped = 5,
    /// <summary>Ran, but the run failed later so its changes were undone.</summary>
    RolledBack = 6,
}

public class AutRunStep : Entity
{
    public Guid RunId { get; set; }
    public int Order { get; set; }
    /// <summary><c>conditions</c> or an action type such as <c>set_status</c>.</summary>
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? InputJson { get; set; }
    public string? OutputJson { get; set; }
    public string? Error { get; set; }
    public StepStatus Status { get; set; }
    public bool IsExternal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public enum ScenarioVisibility
{
    Personal = 1,
    Group = 2,
    Global = 3,
}

/// <summary>A named bundle of actions an agent runs on a ticket (FR-AUT-6).</summary>
public class AutScenario : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ScenarioVisibility Visibility { get; set; } = ScenarioVisibility.Personal;
    /// <summary>Owner of a personal scenario; creator otherwise.</summary>
    public Guid? OwnerUserId { get; set; }
    /// <summary>Agent group for group scenarios.</summary>
    public Guid? GroupId { get; set; }
    /// <summary>JSON array of <see cref="Definitions.ActionDefinition"/>.</summary>
    public string ActionsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ClosureRuleKind
{
    AllTasksCompleted = 1,
    PublicReplySent = 2,
    CategorySet = 3,
    CustomFieldSet = 4,
}

/// <summary>A condition that must hold before a ticket can be resolved or closed (FR-TKT-25).</summary>
public class AutClosureRule : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    public ClosureRuleKind Kind { get; set; }
    /// <summary>Applies to this ticket type only; null for every type.</summary>
    public TicketType? TicketType { get; set; }
    /// <summary>Custom field key for <see cref="ClosureRuleKind.CustomFieldSet"/>.</summary>
    public string? FieldKey { get; set; }
    /// <summary>Optional message shown instead of the default.</summary>
    public string? Message { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ScheduleFrequency
{
    Daily = 1,
    Weekdays = 2,
    Weekly = 3,
    Monthly = 4,
}

/// <summary>Creates a ticket from a template on a recurring schedule (FR-AUT-10).</summary>
public class AutSchedule : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    /// <summary>JSON <see cref="Scheduling.TicketTemplate"/>.</summary>
    public string TemplateJson { get; set; } = "{}";
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;
    /// <summary>Bit per <see cref="DayOfWeek"/> (Sunday = 1) for weekly schedules.</summary>
    public int DaysOfWeek { get; set; }
    /// <summary>1–31 for monthly schedules; short months use their last day.</summary>
    public int? DayOfMonth { get; set; }
    public TimeOnly TimeOfDay { get; set; } = new(9, 0);
    /// <summary>IANA or Windows time zone id.</summary>
    public string TimeZone { get; set; } = "UTC";
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastTicketId { get; set; }
    public string? LastError { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One occurrence of a schedule. Unique per schedule and time, so each occurrence creates one ticket (SR-AUT-6).</summary>
public class AutScheduleOccurrence : Entity
{
    public Guid ScheduleId { get; set; }
    public DateTimeOffset OccurrenceAt { get; set; }
    public Guid? TicketId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>When a ticket entered its current status, for "hours in status" supervisor rules.</summary>
public class AutTicketClock
{
    public Guid TicketId { get; set; }
    public Guid StatusId { get; set; }
    public DateTimeOffset StatusSince { get; set; }
}

/// <summary>Which automation cascade an event belongs to. Written in the same transaction as the event.</summary>
public class AutEventLineage
{
    public Guid EventId { get; set; }
    public Guid CascadeRootId { get; set; }
    public Guid? RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An event that arrived while automations were paused in "queue" mode (SR-AUT-2).</summary>
public class AutQueuedEvent : Entity
{
    public Guid EventId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An admin alert that was sent. The unique key deduplicates alerts (SR-AUT-6).</summary>
public class AutAlert : Entity
{
    public string Key { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
