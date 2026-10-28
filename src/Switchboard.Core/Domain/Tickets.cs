namespace Switchboard.Core.Domain;

public enum TicketType
{
    Incident = 1,
    ServiceRequest = 2,
}

public enum Priority
{
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4,
}

/// <summary>Three-step scale used for urgency and impact.</summary>
public enum Level
{
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>System category every status maps to (FR-TKT-4).</summary>
public enum StatusCategory
{
    Open = 1,
    Pending = 2,
    Resolved = 3,
    Closed = 4,
}

public class TicketStatus : Entity
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? RequesterLabel { get; set; }
    public StatusCategory Category { get; set; }
    /// <summary>When false the SLA clock pauses while a ticket has this status.</summary>
    public bool SlaTimerOn { get; set; } = true;
    public int Order { get; set; }
    public bool IsSystem { get; set; }
    /// <summary>The status new tickets (and reopened tickets) get.</summary>
    public bool IsDefault { get; set; }
}

public class TicketSource : Entity
{
    /// <summary>Stable key, e.g. <c>email</c>, <c>portal</c>, <c>mcp</c>.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "bi-question-circle";
    public bool IsSystem { get; set; }
    public int Order { get; set; }
}

/// <summary>Three-level category tree: category → sub-category → item.</summary>
public class TicketCategory : Entity
{
    public Guid WorkspaceId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public int Level { get; set; } = 1;
    public int Order { get; set; }
}

public class Ticket : Entity, IHasTimestamps, IActivitySubject
{
    public const string Family = "ticket";

    /// <summary>Display number, unique and never reused across incidents and requests (INV-TKT-1).</summary>
    public int Number { get; set; }
    public TicketType Type { get; set; } = TicketType.Incident;
    public string DisplayId => FormatDisplayId(Type, Number);

    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public string Subject { get; set; } = "";
    /// <summary>Sanitized HTML.</summary>
    public string DescriptionHtml { get; set; } = "";
    public string DescriptionText { get; set; } = "";

    public Guid RequesterId { get; set; }
    public User? Requester { get; set; }
    public Guid? RequestedForId { get; set; }
    public User? RequestedFor { get; set; }
    public List<string> CcEmails { get; set; } = [];

    public Guid StatusId { get; set; }
    public TicketStatus? Status { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;
    public Level Urgency { get; set; } = Level.Low;
    public Level Impact { get; set; } = Level.Low;
    public Guid SourceId { get; set; }
    public TicketSource? Source { get; set; }

    public Guid? CategoryId { get; set; }
    public Guid? SubCategoryId { get; set; }
    public Guid? ItemCategoryId { get; set; }

    public Guid? GroupId { get; set; }
    public AgentGroup? Group { get; set; }
    public Guid? AgentId { get; set; }
    public User? Agent { get; set; }
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public DateTimeOffset? FirstResponseDueBy { get; set; }
    public DateTimeOffset? DueBy { get; set; }
    public DateTimeOffset? FirstRespondedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? PlannedStartAt { get; set; }
    public DateTimeOffset? PlannedEndAt { get; set; }
    public int? PlannedEffortMinutes { get; set; }

    /// <summary>Owned by the service-levels module; no FK from Core.</summary>
    public Guid? SlaPolicyId { get; set; }

    /// <summary>
    /// Set when the ticket needs an agent (new, or the requester replied); cleared by an agent reply.
    /// Drives the queue "lamp".
    /// </summary>
    public DateTimeOffset? AwaitingAgentSince { get; set; }
    public DateTimeOffset? LastRequesterActivityAt { get; set; }
    public DateTimeOffset? LastAgentActivityAt { get; set; }

    public bool IsMajorIncident { get; set; }
    public bool IsSpam { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedById { get; set; }
    /// <summary>Content was permanently erased; the row is a tombstone.</summary>
    public DateTimeOffset? PurgedAt { get; set; }

    public Guid? MergedIntoId { get; set; }
    public Guid? ParentId { get; set; }

    public string CustomFieldsJson { get; set; } = "{}";

    public List<TicketTag> Tags { get; set; } = [];
    public List<TicketConversation> Conversations { get; set; } = [];
    public List<TicketTask> Tasks { get; set; } = [];
    public List<TicketShare> Shares { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    string IActivitySubject.ActivitySubjectType => Family;
    Guid IActivitySubject.ActivitySubjectId => Id;
    Guid? IActivitySubject.ActivityWorkspaceId => WorkspaceId;

    /// <summary>True when the lamp is lit: the ticket is waiting on an agent.</summary>
    public bool IsLampLit(StatusCategory category) =>
        AwaitingAgentSince is not null && category is StatusCategory.Open or StatusCategory.Pending
        && DeletedAt is null && !IsSpam && MergedIntoId is null;

    public static string FormatDisplayId(TicketType type, int number) =>
        $"{(type == TicketType.Incident ? "INC" : "SR")}-{number}";
}

public class TicketTag
{
    public Guid TicketId { get; set; }
    /// <summary>Normalized to lower case (INV-TKT-10).</summary>
    public string Tag { get; set; } = "";
}

/// <summary>A requester given portal visibility of someone else's ticket (FR-TKT-14).</summary>
public class TicketShare
{
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public Guid? SharedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum ConversationKind
{
    /// <summary>Message from the requester or a CC'd person (portal, email).</summary>
    RequesterMessage = 1,
    /// <summary>Agent reply sent to the requester and CCs.</summary>
    AgentReply = 2,
    /// <summary>Agent forward to a third party.</summary>
    Forward = 3,
    /// <summary>Note; private unless <see cref="TicketConversation.IsPrivate"/> is false.</summary>
    Note = 4,
    /// <summary>System-generated entry (merge notice, follow-up link).</summary>
    System = 5,
}

public class TicketConversation : Entity, IActivitySubject
{
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public ConversationKind Kind { get; set; }
    /// <summary>Private notes are never shown to requesters (INV-TKT-7).</summary>
    public bool IsPrivate { get; set; }
    public Guid? AuthorId { get; set; }
    public User? Author { get; set; }
    public string? FromEmail { get; set; }
    public List<string> To { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public List<string> Bcc { get; set; } = [];
    /// <summary>Sanitized HTML.</summary>
    public string BodyHtml { get; set; } = "";
    public string BodyText { get; set; } = "";
    public Channel Channel { get; set; }
    public string? EmailMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    /// <summary>Hidden from view (audited). Sent messages are never edited (INV-TKT-8).</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>True for content a requester may see.</summary>
    public bool IsPublic => !IsPrivate && Kind != ConversationKind.Forward && DeletedAt is null;

    string IActivitySubject.ActivitySubjectType => Ticket.Family;
    Guid IActivitySubject.ActivitySubjectId => TicketId;
    Guid? IActivitySubject.ActivityWorkspaceId => null;
}

public enum TicketTaskStatus
{
    Open = 1,
    InProgress = 2,
    Completed = 3,
}

public class TicketTask : Entity, IActivitySubject
{
    public Guid TicketId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public Guid? AssigneeId { get; set; }
    public User? Assignee { get; set; }
    public Guid? GroupId { get; set; }
    public TicketTaskStatus Status { get; set; } = TicketTaskStatus.Open;
    public DateTimeOffset? DueAt { get; set; }
    public int? NotifyBeforeMinutes { get; set; }
    public DateTimeOffset? PlannedStartAt { get; set; }
    public DateTimeOffset? PlannedEndAt { get; set; }
    public int? PlannedEffortMinutes { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Order { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    string IActivitySubject.ActivitySubjectType => Ticket.Family;
    Guid IActivitySubject.ActivitySubjectId => TicketId;
    Guid? IActivitySubject.ActivityWorkspaceId => null;
}

/// <summary>Association from a ticket to another record (FR-TKT-13).</summary>
public class TicketLink : Entity, IActivitySubject
{
    public Guid TicketId { get; set; }
    /// <summary>Target record type, e.g. <c>ticket</c>, <c>problem</c>, <c>change</c>, <c>asset</c>.</summary>
    public string TargetType { get; set; } = "";
    public Guid TargetId { get; set; }
    /// <summary>See <see cref="TicketLinkTypes"/>.</summary>
    public string LinkType { get; set; } = TicketLinkTypes.Related;
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    string IActivitySubject.ActivitySubjectType => Ticket.Family;
    Guid IActivitySubject.ActivitySubjectId => TicketId;
    Guid? IActivitySubject.ActivityWorkspaceId => null;
}

public static class TicketLinkTypes
{
    public const string Related = "related";
    public const string Problem = "problem";
    public const string ChangeCausing = "change_causing";
    public const string ChangeRequested = "change_requested";
    public const string Asset = "asset";
    public const string FollowUpOf = "follow_up_of";
}
