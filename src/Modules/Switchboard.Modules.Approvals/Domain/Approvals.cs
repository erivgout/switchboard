using Switchboard.Core.Domain;

namespace Switchboard.Modules.Approvals.Domain;

/// <summary>Approval status of a record (FR-APR-7). "Not requested" is the absence of a request.</summary>
public enum ApprovalStatus
{
    Requested = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4,
}

/// <summary>How many approvers of one level must approve (FR-APR-3).</summary>
public enum ApprovalRule
{
    /// <summary>Any one approver approving satisfies the level.</summary>
    Any = 1,
    /// <summary>Every approver must approve.</summary>
    Everyone = 2,
    /// <summary>More than half of the approvers must approve.</summary>
    Majority = 3,
}

public enum ApprovalVerdict
{
    Approved = 1,
    Rejected = 2,
}

/// <summary>Where a level's approvers come from (FR-APR-2). Resolved to people when approval is requested.</summary>
public enum ApproverSourceKind
{
    /// <summary>Specific people.</summary>
    Users = 1,
    /// <summary>The requester's manager, <see cref="ApproverSource.ManagerLevel"/> levels up (1 = direct manager).</summary>
    Manager = 2,
    /// <summary>The head of the requester's department.</summary>
    DepartmentHead = 3,
    /// <summary>Working members of an agent group.</summary>
    AgentGroup = 4,
    /// <summary>People picked in a user lookup field of the record's form.</summary>
    LookupField = 5,
}

/// <summary>One approver source in a level's configuration.</summary>
public sealed record ApproverSource
{
    public ApproverSourceKind Kind { get; init; }
    public List<Guid>? UserIds { get; init; }
    public int? ManagerLevel { get; init; }
    public Guid? GroupId { get; init; }
    public string? FieldKey { get; init; }
}

/// <summary>Configuration of one approval level. Levels run in sequence.</summary>
public sealed record ApprovalLevelConfig
{
    public string? Name { get; init; }
    public ApprovalRule Rule { get; init; } = ApprovalRule.Any;
    public List<ApproverSource> Sources { get; init; } = [];
}

/// <summary>An approval chain as configured on a catalog item, change type, etc. Stored as JSON.</summary>
public sealed record ApprovalConfig
{
    public List<ApprovalLevelConfig> Levels { get; init; } = [];

    public bool IsEmpty => Levels.Count == 0;
}

/// <summary>
/// An approval request on any record (FR-APR-1). The status column caches
/// <see cref="Services.ApprovalEvaluator.Evaluate(AprRequest)"/> and is recomputed on every change (INV-APR-2).
/// Every change, including each decision, writes one entry to the subject's activity timeline.
/// </summary>
public class AprRequest : Entity, IHasTimestamps, IActivitySubject
{
    /// <summary>Owner record type, e.g. <c>ticket</c>, <c>change</c>, <c>kb_article</c>.</summary>
    public string SubjectType { get; set; } = "";
    public Guid SubjectId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string Title { get; set; } = "";

    /// <summary>The person the approval is for: approver sources (manager, department) resolve from them, and they can't approve.</summary>
    public Guid RequesterId { get; set; }
    /// <summary>The user who started the approval, when different from the requester (an agent, or null for automations).</summary>
    public Guid? RequestedById { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Requested;
    /// <summary>Order of the level waiting for decisions; null once the request is complete.</summary>
    public int? CurrentLevel { get; set; }
    /// <summary>When the current level started waiting (drives reminders and "waiting since").</summary>
    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>Snapshot of the configuration the levels were resolved from.</summary>
    public string ConfigJson { get; set; } = "{}";
    /// <summary>People who may not decide (the requester and whoever started it), unless self-approval was allowed.</summary>
    public List<Guid> ConflictUserIds { get; set; } = [];
    public bool AllowSelfApproval { get; set; }

    /// <summary>Incremented on every decision so concurrent decisions conflict on <see cref="RowVersion"/>.</summary>
    public int DecisionCount { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledById { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? LastRemindedAt { get; set; }
    public int ReminderCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<AprLevel> Levels { get; set; } = [];
    public List<AprDecision> Decisions { get; set; } = [];

    string IActivitySubject.ActivitySubjectType => SubjectType;
    Guid IActivitySubject.ActivitySubjectId => SubjectId;
    Guid? IActivitySubject.ActivityWorkspaceId => WorkspaceId;
}

public class AprLevel : Entity
{
    public Guid RequestId { get; set; }
    /// <summary>1-based position in the chain.</summary>
    public int Order { get; set; }
    public string? Name { get; set; }
    public ApprovalRule Rule { get; set; }
    public List<AprApprover> Approvers { get; set; } = [];
}

/// <summary>A person designated to decide one level, resolved when the approval was requested.</summary>
public class AprApprover : Entity
{
    public Guid LevelId { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    /// <summary>Why they were chosen, e.g. "Manager" or "Service Desk group".</summary>
    public string Source { get; set; } = "";
}

/// <summary>
/// A recorded decision. Immutable (INV-APR-1): properties are init-only, and a database trigger rejects
/// updates and deletes. To supersede a decision, cancel the request and start a new one.
/// </summary>
public class AprDecision : Entity
{
    public Guid RequestId { get; init; }
    public Guid LevelId { get; init; }
    /// <summary>The designated approver this decision counts for.</summary>
    public Guid ApproverId { get; init; }
    /// <summary>Who actually decided: the approver, or their delegate.</summary>
    public Guid DecidedById { get; init; }
    /// <summary>Set when a delegate decided on behalf of <see cref="ApproverId"/> (FR-IAM-4, FR-APR-6).</summary>
    public Guid? OnBehalfOfId { get; init; }
    public ApprovalVerdict Verdict { get; init; }
    public string? Comment { get; init; }
    public Channel Channel { get; init; }
    public string? ClientName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Single-use link for deciding from email when "require sign-in for approvals" is off (SR-APR-2).
/// Only the hash of the token is stored.
/// </summary>
public class AprLinkToken : Entity
{
    public Guid RequestId { get; set; }
    public Guid LevelId { get; set; }
    /// <summary>The person the link was emailed to; decisions through it are theirs.</summary>
    public Guid RecipientId { get; set; }
    /// <summary>Set when the recipient is a delegate of this approver.</summary>
    public Guid? OnBehalfOfId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class ApprovalSubjectTypes
{
    public const string Ticket = "ticket";
}
