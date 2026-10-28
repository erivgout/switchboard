using Switchboard.Core.Events;
using Switchboard.Modules.ServiceLevels.Domain;

namespace Switchboard.Modules.ServiceLevels.Events;

/// <summary>
/// A target is <see cref="MinutesBefore"/> minutes away. Raised once per reminder rule, target and
/// SLA cycle. <see cref="NotifyUserIds"/> are the assigned agent, or the group's members when the
/// ticket is unassigned.
/// </summary>
public sealed record SlaReminderDue(
    Guid TicketId,
    Guid WorkspaceId,
    int Cycle,
    SlaTargetKind Target,
    DateTimeOffset DueAt,
    int MinutesBefore,
    IReadOnlyList<Guid> NotifyUserIds) : DomainEvent;

/// <summary>
/// A target was missed. Raised once per target and SLA cycle (for next response, once per response
/// window). For first and next response, <see cref="NotifyUserIds"/> are the policy's first-response
/// escalation targets; for resolution it is empty and <see cref="SlaEscalated"/> follows.
/// </summary>
public sealed record SlaBreached(
    Guid TicketId,
    Guid WorkspaceId,
    int Cycle,
    SlaTargetKind Target,
    DateTimeOffset DueAt,
    Guid? PolicyId,
    IReadOnlyList<Guid> NotifyUserIds) : DomainEvent;

/// <summary>
/// A missed resolution target reached escalation <see cref="Level"/> (1-4), its delay after the
/// breach having passed. Raised once per level and SLA cycle.
/// </summary>
public sealed record SlaEscalated(
    Guid TicketId,
    Guid WorkspaceId,
    int Cycle,
    SlaTargetKind Target,
    int Level,
    DateTimeOffset DueAt,
    Guid? PolicyId,
    IReadOnlyList<Guid> NotifyUserIds) : DomainEvent;
