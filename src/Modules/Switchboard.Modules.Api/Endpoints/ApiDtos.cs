using System.Text.Json;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.Api.Endpoints;

public sealed record RefDto(Guid Id, string Name);

public sealed record PersonRefDto(Guid Id, string Name, string? Email = null);

public sealed record StatusDto(Guid Id, string Name, StatusCategory Category);

public sealed record SourceDto(Guid Id, string Key, string Name);

public sealed record PageMeta(int Total, int Page, int PerPage, string? NextCursor);

/// <summary>Every list response: <c>{ "data": [...], "meta": {...} }</c>. Follow <c>meta.nextCursor</c> to page.</summary>
public sealed record ListResponse<T>(IReadOnlyList<T> Data, PageMeta Meta);

/// <summary>A list without paging (small reference lists).</summary>
public sealed record ItemsResponse<T>(IReadOnlyList<T> Data);

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, long Size, bool IsInline, DateTimeOffset CreatedAt, string Url);

/// <summary>A ticket as an agent sees it.</summary>
public sealed record TicketDto
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public string DisplayId { get; init; } = "";
    public TicketType Type { get; init; }
    public Guid WorkspaceId { get; init; }
    public string Subject { get; init; } = "";
    public string DescriptionHtml { get; init; } = "";
    public StatusDto Status { get; init; } = default!;
    public Priority Priority { get; init; }
    public Level Urgency { get; init; }
    public Level Impact { get; init; }
    public SourceDto Source { get; init; } = default!;
    public PersonRefDto Requester { get; init; } = default!;
    public PersonRefDto? RequestedFor { get; init; }
    public IReadOnlyList<string> CcEmails { get; init; } = [];
    public RefDto? Group { get; init; }
    public PersonRefDto? Agent { get; init; }
    public RefDto? Department { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? SubCategoryId { get; init; }
    public Guid? ItemCategoryId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public JsonElement CustomFields { get; init; }
    /// <summary>True when the ticket waits on an agent (the queue lamp).</summary>
    public bool IsWaiting { get; init; }
    public bool IsMajorIncident { get; init; }
    public bool IsSpam { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public Guid? MergedIntoId { get; init; }
    public Guid? ParentId { get; init; }
    public DateTimeOffset? DueBy { get; init; }
    public DateTimeOffset? FirstResponseDueBy { get; init; }
    public DateTimeOffset? FirstRespondedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public DateTimeOffset? PlannedStartAt { get; init; }
    public DateTimeOffset? PlannedEndAt { get; init; }
    public int? PlannedEffortMinutes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];
    public IReadOnlyList<ConversationDto>? Conversations { get; init; }
    public IReadOnlyList<TaskDto>? Tasks { get; init; }
    public IReadOnlyList<LinkDto>? Links { get; init; }
}

/// <summary>A status as requesters see it: the requester label, never the internal name.</summary>
public sealed record RequesterStatusDto(string Label, StatusCategory Category);

/// <summary>A ticket as its requester sees it: no internal fields, no private content (SR-AUTHZ-7).</summary>
public sealed record RequesterTicketDto
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public string DisplayId { get; init; } = "";
    public TicketType Type { get; init; }
    public string Subject { get; init; } = "";
    public string DescriptionHtml { get; init; } = "";
    public RequesterStatusDto Status { get; init; } = default!;
    public PersonRefDto Requester { get; init; } = default!;
    public PersonRefDto? RequestedFor { get; init; }
    public IReadOnlyList<string> CcEmails { get; init; } = [];
    public JsonElement CustomFields { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];
    public IReadOnlyList<RequesterConversationDto>? Conversations { get; init; }
}

public sealed record TicketListItemDto
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public string DisplayId { get; init; } = "";
    public TicketType Type { get; init; }
    public Guid WorkspaceId { get; init; }
    public string Subject { get; init; } = "";
    public StatusDto Status { get; init; } = default!;
    public Priority Priority { get; init; }
    public RefDto? Group { get; init; }
    public PersonRefDto? Agent { get; init; }
    public PersonRefDto Requester { get; init; } = default!;
    public string Source { get; init; } = "";
    public bool IsWaiting { get; init; }
    public bool IsMajorIncident { get; init; }
    public bool IsSpam { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public DateTimeOffset? DueBy { get; init; }
    public DateTimeOffset? FirstResponseDueBy { get; init; }
    public DateTimeOffset? FirstRespondedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record RequesterTicketListItemDto(
    Guid Id,
    int Number,
    string DisplayId,
    TicketType Type,
    string Subject,
    RequesterStatusDto Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record ConversationDto
{
    public Guid Id { get; init; }
    public Guid TicketId { get; init; }
    public ConversationKind Kind { get; init; }
    public bool IsPrivate { get; init; }
    public PersonRefDto? Author { get; init; }
    public string? FromEmail { get; init; }
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];
    public string BodyHtml { get; init; } = "";
    public string BodyText { get; init; } = "";
    public Channel Channel { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? EditedAt { get; init; }
    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];
}

/// <summary>A public message as requesters see it. No recipients, no private notes, no forwards.</summary>
public sealed record RequesterConversationDto
{
    public Guid Id { get; init; }
    public ConversationKind Kind { get; init; }
    public string? AuthorName { get; init; }
    public bool FromYou { get; init; }
    public string BodyHtml { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];
}

public sealed record RequesterMessageResultDto(Guid TicketId, bool CreatedFollowUp, RequesterConversationDto Conversation);

public sealed record TaskDto
{
    public Guid Id { get; init; }
    public Guid TicketId { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public Guid? AssigneeId { get; init; }
    public Guid? GroupId { get; init; }
    public TicketTaskStatus Status { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public int? NotifyBeforeMinutes { get; init; }
    public DateTimeOffset? PlannedStartAt { get; init; }
    public DateTimeOffset? PlannedEndAt { get; init; }
    public int? PlannedEffortMinutes { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int Order { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record LinkDto(Guid Id, Guid TicketId, string TargetType, Guid TargetId, string LinkType, DateTimeOffset CreatedAt);

public sealed record PersonDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? Email { get; init; }
    public string? JobTitle { get; init; }
    public string? Phone { get; init; }
    public string? Mobile { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? LocationId { get; init; }
    public Guid? ManagerId { get; init; }
    public string? TimeZone { get; init; }
    public string Language { get; init; } = "en";
    public bool IsAgent { get; init; }
    public bool IsActive { get; init; }
    public bool IsVip { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Agents only: working (non-observer) group ids.</summary>
    public IReadOnlyList<Guid>? GroupIds { get; init; }
    public AgentAvailability? Availability { get; init; }
}

public sealed record MeWorkspaceDto(Guid Id, string Key, string Name, string Role, TicketScope TicketScope);

public sealed record MeDto(
    Guid Id,
    string Name,
    string? Email,
    bool IsAgent,
    bool IsOrgAdmin,
    string? TimeZone,
    string Language,
    IReadOnlyList<MeWorkspaceDto> Workspaces,
    IReadOnlyList<string>? Scopes);

public sealed record WorkspaceDto(Guid Id, string Key, string Name, bool IsPrimary);

public sealed record GroupDto(Guid Id, Guid WorkspaceId, string Name, string? Description, AssignmentMode AssignmentMode, IReadOnlyList<Guid> MemberIds, IReadOnlyList<Guid> ObserverIds);

public sealed record DepartmentDto(Guid Id, string Name, string? Description, Guid? HeadUserId, Guid? PrimeUserId, IReadOnlyList<string> Domains);

public sealed record LocationDto(Guid Id, string Name, Guid? ParentId, string? AddressLine1, string? AddressLine2, string? City, string? State, string? PostalCode, string? Country);

public sealed record TicketStatusDto(Guid Id, Guid WorkspaceId, string Name, string? RequesterLabel, StatusCategory Category, bool SlaTimerOn, bool IsDefault, int Order);

public sealed record TicketCategoryDto(Guid Id, Guid WorkspaceId, Guid? ParentId, string Name, int Level, int Order);

public sealed record TicketSourceDto(Guid Id, string Key, string Name, int Order);

public sealed record TicketFieldDto(
    Guid Id,
    Guid? WorkspaceId,
    string Key,
    string Label,
    string? RequesterLabel,
    CustomFieldType FieldType,
    JsonElement Options,
    bool RequiredForAgents,
    bool RequiredOnResolve,
    bool RequiredForRequesters,
    bool VisibleToRequesters,
    bool EditableByRequesters,
    string? HelpText,
    string? DefaultValue,
    string? Section,
    int Order);
