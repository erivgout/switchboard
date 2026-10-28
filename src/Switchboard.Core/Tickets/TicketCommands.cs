using System.Text.Json;
using Switchboard.Core.Domain;

namespace Switchboard.Core.Tickets;

/// <summary>A value that may be absent (unchanged) as opposed to explicitly null (cleared).</summary>
public readonly struct Optional<T>
{
    private readonly T _value;

    public Optional(T value)
    {
        _value = value;
        HasValue = true;
    }

    public bool HasValue { get; }

    public T Value => HasValue ? _value : throw new InvalidOperationException("Optional has no value.");

    public static implicit operator Optional<T>(T value) => new(value);

    public static Optional<T> None => default;

    public override string ToString() => HasValue ? $"{_value}" : "(unset)";
}

public static class TicketSourceKeys
{
    public const string Email = "email";
    public const string Portal = "portal";
    public const string Phone = "phone";
    public const string Chat = "chat";
    public const string Widget = "widget";
    public const string WalkUp = "walk_up";
    public const string Slack = "slack";
    public const string Teams = "teams";
    public const string Alert = "alert";
    public const string Api = "api";
    public const string Mcp = "mcp";
    public const string Onboarding = "onboarding";
    public const string Offboarding = "offboarding";
    public const string Journey = "journey";
    public const string Agent = "agent";
    public const string Scheduler = "scheduler";
}

public sealed record CreateTicketCommand
{
    public Guid? WorkspaceId { get; init; }
    public TicketType Type { get; init; } = TicketType.Incident;
    public string Subject { get; init; } = "";
    /// <summary>HTML; sanitized on save.</summary>
    public string? DescriptionHtml { get; init; }

    /// <summary>Existing requester. Otherwise <see cref="RequesterEmail"/>, otherwise the current user.</summary>
    public Guid? RequesterId { get; init; }
    public string? RequesterEmail { get; init; }
    public string? RequesterName { get; init; }
    public Guid? RequestedForId { get; init; }
    public IReadOnlyList<string>? CcEmails { get; init; }

    public Guid? StatusId { get; init; }
    public Priority? Priority { get; init; }
    public Level? Urgency { get; init; }
    public Level? Impact { get; init; }
    public string SourceKey { get; init; } = TicketSourceKeys.Agent;
    public Guid? CategoryId { get; init; }
    public Guid? SubCategoryId { get; init; }
    public Guid? ItemCategoryId { get; init; }
    public Guid? GroupId { get; init; }
    public Guid? AgentId { get; init; }
    public Guid? DepartmentId { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyDictionary<string, JsonElement?>? CustomFields { get; init; }
    /// <summary>Pending uploads to attach.</summary>
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
    public Guid? ParentId { get; init; }
    public bool IsMajorIncident { get; init; }
    public DateTimeOffset? PlannedStartAt { get; init; }
    public DateTimeOffset? PlannedEndAt { get; init; }
    public int? PlannedEffortMinutes { get; init; }

    /// <summary>
    /// Submitted by the requester themselves (portal, email, chat). Uses requester permissions and
    /// requester field rules; group/agent/status are ignored.
    /// </summary>
    public bool AsRequester { get; init; }

    /// <summary>Message-ID of the email that created the ticket, for threading.</summary>
    public string? EmailMessageId { get; init; }
}

/// <summary>Partial update. Unset properties are left unchanged.</summary>
public sealed record TicketPatch
{
    public Optional<string> Subject { get; init; }
    public Optional<string> DescriptionHtml { get; init; }
    public Optional<TicketType> Type { get; init; }
    public Optional<Guid> RequesterId { get; init; }
    public Optional<Guid?> RequestedForId { get; init; }
    public Optional<string[]> CcEmails { get; init; }
    public Optional<Guid> StatusId { get; init; }
    public Optional<Priority> Priority { get; init; }
    public Optional<Level> Urgency { get; init; }
    public Optional<Level> Impact { get; init; }
    public Optional<Guid> SourceId { get; init; }
    public Optional<Guid?> CategoryId { get; init; }
    public Optional<Guid?> SubCategoryId { get; init; }
    public Optional<Guid?> ItemCategoryId { get; init; }
    public Optional<Guid?> GroupId { get; init; }
    public Optional<Guid?> AgentId { get; init; }
    public Optional<Guid?> DepartmentId { get; init; }
    public Optional<string[]> Tags { get; init; }
    public Optional<Dictionary<string, JsonElement?>> CustomFields { get; init; }
    public Optional<bool> IsMajorIncident { get; init; }
    public Optional<DateTimeOffset?> DueBy { get; init; }
    public Optional<DateTimeOffset?> FirstResponseDueBy { get; init; }
    public Optional<DateTimeOffset?> PlannedStartAt { get; init; }
    public Optional<DateTimeOffset?> PlannedEndAt { get; init; }
    public Optional<int?> PlannedEffortMinutes { get; init; }

    /// <summary>Required when changing due dates by hand (FR-SLA-6).</summary>
    public string? DueOverrideReason { get; init; }

    public bool IsEmpty =>
        !(Subject.HasValue || DescriptionHtml.HasValue || Type.HasValue || RequesterId.HasValue || RequestedForId.HasValue
          || CcEmails.HasValue || StatusId.HasValue || Priority.HasValue || Urgency.HasValue || Impact.HasValue || SourceId.HasValue
          || CategoryId.HasValue || SubCategoryId.HasValue || ItemCategoryId.HasValue || GroupId.HasValue || AgentId.HasValue
          || DepartmentId.HasValue || Tags.HasValue || CustomFields.HasValue || IsMajorIncident.HasValue || DueBy.HasValue
          || FirstResponseDueBy.HasValue || PlannedStartAt.HasValue || PlannedEndAt.HasValue || PlannedEffortMinutes.HasValue);
}

public sealed record ReplyCommand
{
    public string BodyHtml { get; init; } = "";
    /// <summary>Extra CC addresses for this reply (the ticket's CCs are always included).</summary>
    public IReadOnlyList<string>? Cc { get; init; }
    public IReadOnlyList<string>? Bcc { get; init; }
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
    /// <summary>Optionally set a status in the same step ("Send and set to Pending").</summary>
    public Guid? StatusId { get; init; }
}

public sealed record NoteCommand
{
    public string BodyHtml { get; init; } = "";
    public bool IsPrivate { get; init; } = true;
    /// <summary>Agents to notify (@mentions).</summary>
    public IReadOnlyList<Guid>? NotifyUserIds { get; init; }
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
}

public sealed record ForwardCommand
{
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string>? Cc { get; init; }
    public string BodyHtml { get; init; } = "";
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
}

public sealed record RequesterMessageCommand
{
    public string BodyHtml { get; init; } = "";
    /// <summary>Sender address for email; defaults to the current user's.</summary>
    public string? FromEmail { get; init; }
    public IReadOnlyList<Guid>? AttachmentIds { get; init; }
    public string? EmailMessageId { get; init; }
}

public sealed record RequesterMessageResult(TicketConversation Conversation, Ticket Ticket, bool CreatedFollowUp);

public sealed record TaskCommand
{
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public Guid? AssigneeId { get; init; }
    public Guid? GroupId { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int? NotifyBeforeMinutes { get; init; }
    public DateTimeOffset? PlannedStartAt { get; init; }
    public DateTimeOffset? PlannedEndAt { get; init; }
    public int? PlannedEffortMinutes { get; init; }
}

public sealed record TaskPatch
{
    public Optional<string> Title { get; init; }
    public Optional<string?> Description { get; init; }
    public Optional<Guid?> AssigneeId { get; init; }
    public Optional<Guid?> GroupId { get; init; }
    public Optional<TicketTaskStatus> Status { get; init; }
    public Optional<DateTimeOffset?> DueAt { get; init; }
}
