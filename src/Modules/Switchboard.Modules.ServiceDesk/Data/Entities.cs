using Switchboard.Core.Domain;

namespace Switchboard.Modules.ServiceDesk.Data;

/// <summary>A named queue filter. Private to its owner unless shared (sharing needs settings.manage).</summary>
public class SdSavedView : Entity, IHasTimestamps
{
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Filter language query (TicketQueryLanguage).</summary>
    public string Query { get; set; } = "";
    public string? Sort { get; set; }
    /// <summary>Optional queue columns; see <see cref="Services.QueueColumns"/>.</summary>
    public List<string> Columns { get; set; } = [];
    public bool IsShared { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum CannedVisibility
{
    /// <summary>Only the owner sees the folder.</summary>
    Personal = 1,
    /// <summary>Every agent sees the folder.</summary>
    Shared = 2,
    /// <summary>Members (and observers) of one agent group see the folder.</summary>
    Group = 3,
}

/// <summary>Folder of canned responses; its visibility applies to every response in it (FR-AUT-7).</summary>
public class SdCannedResponseFolder : Entity, IHasTimestamps
{
    public string Name { get; set; } = "";
    public CannedVisibility Visibility { get; set; } = CannedVisibility.Personal;
    /// <summary>Owner of a personal folder.</summary>
    public Guid? OwnerId { get; set; }
    /// <summary>Agent group of a group folder.</summary>
    public Guid? GroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class SdCannedResponse : Entity, IHasTimestamps
{
    public Guid FolderId { get; set; }
    public SdCannedResponseFolder? Folder { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Sanitized HTML with <c>{{placeholders}}</c>.</summary>
    public string BodyHtml { get; set; } = "";
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Pre-filled new-ticket form for recurring issues (FR-TKT-23).</summary>
public class SdTicketTemplate : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public TicketType Type { get; set; } = TicketType.Incident;
    public string Subject { get; set; } = "";
    /// <summary>Sanitized HTML.</summary>
    public string DescriptionHtml { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public Guid? SubCategoryId { get; set; }
    public Guid? ItemCategoryId { get; set; }
    public Priority? Priority { get; set; }
    public Level? Urgency { get; set; }
    public Level? Impact { get; set; }
    public Guid? GroupId { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum BulkJobStatus
{
    Queued = 1,
    Running = 2,
    Completed = 3,
}

public enum BulkItemStatus
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>A bulk action with per-record results (FR-TKT-21, SR-AUT-5).</summary>
public class SdBulkJob : Entity
{
    public Guid CreatedById { get; set; }
    public string CreatedByName { get; set; } = "";
    /// <summary>See <see cref="Services.BulkActions"/>.</summary>
    public string Action { get; set; } = "";
    /// <summary>Serialized <see cref="Services.BulkRequest"/>.</summary>
    public string ParametersJson { get; set; } = "{}";
    /// <summary>Plain-language description, e.g. "Set status to Resolved".</summary>
    public string Summary { get; set; } = "";
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public BulkJobStatus Status { get; set; } = BulkJobStatus.Queued;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<SdBulkJobItem> Items { get; set; } = [];
}

public class SdBulkJobItem : Entity
{
    public Guid JobId { get; set; }
    public Guid TicketId { get; set; }
    public string DisplayId { get; set; } = "";
    public int Number { get; set; }
    public int Order { get; set; }
    public BulkItemStatus Status { get; set; } = BulkItemStatus.Pending;
    public string? Error { get; set; }
}
