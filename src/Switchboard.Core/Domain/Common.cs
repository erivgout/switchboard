namespace Switchboard.Core.Domain;

/// <summary>Base entity with a time-ordered (v7) GUID key.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Entities whose changes appear in an activity timeline. Child records (conversations, tasks,
/// links) report their parent as the subject so the parent's timeline shows them (INV-HIS-1).
/// </summary>
public interface IActivitySubject
{
    string ActivitySubjectType { get; }
    Guid ActivitySubjectId { get; }
    Guid? ActivityWorkspaceId { get; }
}

/// <summary>A file attached to any owner (ticket, conversation, article, ...).</summary>
public class Attachment : Entity
{
    public string OwnerType { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string StorageKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool IsInline { get; set; }
    public Guid? UploadedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class AttachmentOwners
{
    public const string Ticket = "ticket";
    public const string Conversation = "conversation";
    /// <summary>Uploaded but not yet attached; purged after 24 hours.</summary>
    public const string Pending = "pending";
}

/// <summary>One entry in an entity's activity timeline.</summary>
public class ActivityEntry : Entity
{
    public string SubjectType { get; set; } = "";
    public Guid SubjectId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public ActorKind ActorKind { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorName { get; set; } = "";
    public Channel Channel { get; set; }
    public string? ClientName { get; set; }
    /// <summary>Machine-readable verb, e.g. <c>created</c>, <c>updated</c>, <c>replied</c>.</summary>
    public string Action { get; set; } = "";
    /// <summary>Human-readable sentence for the timeline.</summary>
    public string Summary { get; set; } = "";
    /// <summary>JSON array of <see cref="FieldChange"/>.</summary>
    public string ChangesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record FieldChange(string Field, string? From, string? To);

/// <summary>Append-only, hash-chained audit record (SR-AUD).</summary>
public class AuditEntry
{
    public long Sequence { get; set; }
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; set; }
    public ActorKind ActorKind { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorName { get; set; } = "";
    public Guid? OnBehalfOfUserId { get; set; }
    public Channel Channel { get; set; }
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>Dotted verb, e.g. <c>auth.login.succeeded</c>, <c>roles.updated</c>.</summary>
    public string Action { get; set; } = "";
    public string? ObjectType { get; set; }
    public string? ObjectId { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public string? RequestId { get; set; }
    public string? PrevHash { get; set; }
    public string? Hash { get; set; }
}

public enum CustomFieldType
{
    Text = 1,
    Paragraph = 2,
    Number = 3,
    Decimal = 4,
    Checkbox = 5,
    Date = 6,
    DateTime = 7,
    Dropdown = 8,
    MultiSelect = 9,
    DependentDropdown = 10,
    Lookup = 11,
    Content = 12,
}

/// <summary>Admin-defined field. Values live in the owning entity's <c>CustomFieldsJson</c> keyed by <see cref="Key"/>.</summary>
public class CustomFieldDefinition : Entity
{
    /// <summary>Owning entity type, e.g. <c>ticket</c>, <c>user</c>, <c>department</c>.</summary>
    public string EntityType { get; set; } = "";
    public Guid? WorkspaceId { get; set; }
    /// <summary>Stable snake_case key, unique per entity type and workspace.</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? RequesterLabel { get; set; }
    public CustomFieldType FieldType { get; set; }
    /// <summary>Choices for dropdowns (JSON array of strings, or nested objects for dependent dropdowns); lookup target for lookups.</summary>
    public string OptionsJson { get; set; } = "[]";
    public bool RequiredForAgents { get; set; }
    public bool RequiredOnResolve { get; set; }
    public bool RequiredForRequesters { get; set; }
    public bool VisibleToRequesters { get; set; }
    public bool EditableByRequesters { get; set; }
    public string? HelpText { get; set; }
    public string? DefaultValue { get; set; }
    public string? Section { get; set; }
    public int Order { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Key/value configuration, optionally per workspace.</summary>
public class Setting : Entity
{
    public Guid? WorkspaceId { get; set; }
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "null";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Monotonic display-number sequences per entity family (INV-TKT-1).</summary>
public class DisplayIdCounter
{
    public string Family { get; set; } = "";
    public int NextValue { get; set; } = 1;
}

/// <summary>Personal API key (stored hashed, shown once).</summary>
public class ApiKey : Entity
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>First characters of the key, shown in lists to identify it.</summary>
    public string Prefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Domain event persisted for reliable after-commit handling.</summary>
public class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public int CascadeDepth { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? DeadAt { get; set; }
}

/// <summary>Records that a handler finished an outbox message, so retries skip it.</summary>
public class OutboxHandlerReceipt
{
    public Guid MessageId { get; set; }
    public string HandlerName { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; }
}

public enum ActorKind
{
    Anonymous = 0,
    User = 1,
    System = 2,
    Automation = 3,
    McpClient = 4,
    Plugin = 5,
}

public enum Channel
{
    System = 0,
    Web = 1,
    Portal = 2,
    Api = 3,
    Mcp = 4,
    Email = 5,
    Automation = 6,
    Chat = 7,
    Import = 8,
}
