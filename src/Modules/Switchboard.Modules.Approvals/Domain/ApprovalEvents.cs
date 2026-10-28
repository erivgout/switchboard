using Switchboard.Core.Events;

namespace Switchboard.Modules.Approvals.Domain;

/// <summary>
/// Approvers were asked to decide: raised when a request starts (level 1) and again each time the chain
/// moves on to the next level. <see cref="ApproverIds"/> are the designated approvers of <see cref="Level"/>.
/// </summary>
public sealed record ApprovalRequested(
    Guid ApprovalId,
    string SubjectType,
    Guid SubjectId,
    Guid? WorkspaceId,
    Guid RequesterId,
    int Level,
    IReadOnlyList<Guid> ApproverIds) : DomainEvent;

/// <summary>
/// One decision was recorded. <see cref="Status"/> is the request's status after it: subscribers act when it
/// is <see cref="ApprovalStatus.Approved"/> or <see cref="ApprovalStatus.Rejected"/>. <see cref="Comment"/> is
/// user-written text.
/// </summary>
public sealed record ApprovalDecided(
    Guid ApprovalId,
    string SubjectType,
    Guid SubjectId,
    Guid? WorkspaceId,
    Guid DecisionId,
    Guid ApproverId,
    Guid DecidedById,
    Guid? OnBehalfOfId,
    ApprovalVerdict Verdict,
    ApprovalStatus Status,
    string? Comment) : DomainEvent;

/// <summary>The request was withdrawn before it completed. Recorded decisions stay as they were.</summary>
public sealed record ApprovalCancelled(
    Guid ApprovalId,
    string SubjectType,
    Guid SubjectId,
    Guid? WorkspaceId,
    string? Reason) : DomainEvent;
