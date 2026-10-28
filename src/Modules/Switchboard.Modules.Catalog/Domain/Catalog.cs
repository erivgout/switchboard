using System.Text.Json.Nodes;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.Catalog.Domain;

public enum CatItemState
{
    Draft = 1,
    Published = 2,
    Archived = 3,
}

/// <summary>FR-SRM-4. Hardware and software are labels for now; stock and license fulfillment come with the asset modules.</summary>
public enum CatItemKind
{
    Standard = 1,
    Hardware = 2,
    Software = 3,
    /// <summary>Contains other items; each becomes its own requested item.</summary>
    Bundle = 4,
}

public enum CatFieldType
{
    Text = 1,
    Paragraph = 2,
    Number = 3,
    Dropdown = 4,
    MultiSelect = 5,
    Checkbox = 6,
    Date = 7,
    UserLookup = 8,
}

/// <summary>FR-SRM-7.</summary>
public enum RequestedItemStage
{
    Requested = 1,
    InProgress = 2,
    Delivered = 3,
    Cancelled = 4,
}

/// <summary>An ordered service category (FR-SRM-1).</summary>
public class CatCategory : Entity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Icon { get; set; } = "bi-grid";
    public int Order { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Something people can request (FR-SRM-2).</summary>
public class CatItem : Entity, IHasTimestamps
{
    public string Name { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    /// <summary>Plain text; paragraphs separated by blank lines.</summary>
    public string? Description { get; set; }
    public string Icon { get; set; } = "bi-box-seam";
    public CatItemKind Kind { get; set; } = CatItemKind.Standard;
    public CatItemState State { get; set; } = CatItemState.Draft;

    /// <summary>Price per unit in minor units (cents), or null when there's no charge to show (INV-MON-1).</summary>
    public long? CostMinor { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>Estimated working days to deliver; 0 means same day.</summary>
    public int? DeliveryDays { get; set; }

    /// <summary>Largest quantity one request may ask for; 1 hides the quantity choice.</summary>
    public int MaxQuantity { get; set; } = 1;
    /// <summary>Requesters may ask on behalf of someone else.</summary>
    public bool AllowRequestedFor { get; set; }

    /// <summary>Requester groups that see the item. Empty, together with no departments, means everyone.</summary>
    public List<Guid> VisibleToGroupIds { get; set; } = [];
    public List<Guid> VisibleToDepartmentIds { get; set; } = [];

    /// <summary>Agent group that delivers it; assigned once the request is approved (or straight away without approvals).</summary>
    public Guid? FulfillmentGroupId { get; set; }

    /// <summary>Serialized <c>ApprovalConfig</c>, or null when no approval is needed.</summary>
    public string? ApprovalConfigJson { get; set; }

    /// <summary>Shown to the requester after they submit.</summary>
    public string? PostSubmitMessage { get; set; }

    /// <summary>Items included in a bundle.</summary>
    public List<Guid> BundleItemIds { get; set; } = [];

    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<CatItemCategory> Categories { get; set; } = [];
    public List<CatItemField> Fields { get; set; } = [];
}

public class CatItemCategory
{
    public Guid ItemId { get; set; }
    public Guid CategoryId { get; set; }
}

/// <summary>One question on an item's request form. With <see cref="SharedFieldId"/> set, label, type, help and options come from the shared field.</summary>
public class CatItemField : Entity
{
    public Guid ItemId { get; set; }
    public Guid? SharedFieldId { get; set; }
    public CatSharedField? SharedField { get; set; }
    /// <summary>Stable snake_case key; answers are stored under it.</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public CatFieldType FieldType { get; set; }
    public bool Required { get; set; }
    public string? HelpText { get; set; }
    public List<string> Options { get; set; } = [];
    public int Order { get; set; }
}

/// <summary>A question defined once and reused on many items (FR-SRM-3).</summary>
public class CatSharedField : Entity
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public CatFieldType FieldType { get; set; }
    public string? HelpText { get; set; }
    public List<string> Options { get; set; } = [];
    /// <summary>Archived fields can't be added to more items; items already using them keep working.</summary>
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One requested item on a service request ticket (FR-SRM-6, FR-SRM-7). Changes show on the ticket's timeline.</summary>
public class CatRequestedItem : Entity, IHasTimestamps, IActivitySubject
{
    public Guid TicketId { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>Item name when requested, so history reads correctly after renames.</summary>
    public string ItemName { get; set; } = "";
    public string Icon { get; set; } = "bi-box-seam";
    /// <summary>For items that came with a bundle: the bundle's requested item.</summary>
    public Guid? ParentId { get; set; }
    public int Quantity { get; set; } = 1;
    /// <summary>JSON array of <see cref="FieldAnswer"/>.</summary>
    public string AnswersJson { get; set; } = "[]";
    public RequestedItemStage Stage { get; set; } = RequestedItemStage.Requested;
    public long? UnitCostMinor { get; set; }
    public long? CostMinor { get; set; }
    public string Currency { get; set; } = "USD";
    public Guid? RequestedForId { get; set; }
    public DateTimeOffset? StageChangedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    string IActivitySubject.ActivitySubjectType => Ticket.Family;
    Guid IActivitySubject.ActivitySubjectId => TicketId;
    Guid? IActivitySubject.ActivityWorkspaceId => null;
}

/// <summary>A stored answer: the question as it was asked, the value, and readable text.</summary>
public sealed record FieldAnswer(string Key, string Label, CatFieldType Type, JsonNode? Value, string Display);
