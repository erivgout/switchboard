using Switchboard.Core.Domain;

namespace Switchboard.Modules.Knowledge.Domain;

/// <summary>Who can read a folder's published articles (FR-KB-1). Sub-folders follow their top-level folder.</summary>
public enum KbFolderVisibility
{
    /// <summary>Every signed-in user, and signed-out visitors when the public knowledge base is on.</summary>
    Everyone = 1,
    /// <summary>Every signed-in user.</summary>
    SignedIn = 2,
    /// <summary>Agents of the folder's workspace.</summary>
    AgentsOnly = 3,
    /// <summary>Only the listed requester groups, departments and agent groups.</summary>
    Restricted = 4,
}

public enum KbAudienceKind
{
    RequesterGroup = 1,
    Department = 2,
    AgentGroup = 3,
}

/// <summary>Headline status of an article (FR-KB-2).</summary>
public enum KbArticleStatus
{
    Draft = 1,
    InReview = 2,
    Published = 3,
    Archived = 4,
}

public enum KbArticleType
{
    Permanent = 1,
    Workaround = 2,
}

/// <summary>State of one saved version. At most one version per article is Published (INV-KB-1).</summary>
public enum KbVersionState
{
    /// <summary>The working copy; autosave updates it in place.</summary>
    Draft = 1,
    /// <summary>Submitted for approval; read-only until approved, rejected or withdrawn.</summary>
    InReview = 2,
    /// <summary>The live version requesters read.</summary>
    Published = 3,
    /// <summary>History: a version that was replaced.</summary>
    Previous = 4,
}

public enum KbReviewDecision
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Withdrawn = 3,
}

public enum KbAnnouncementAudience
{
    Everyone = 1,
    Agents = 2,
    Requesters = 3,
    /// <summary>Members of the listed agent groups, requester groups and departments.</summary>
    Specific = 4,
}

/// <summary>Top level of the knowledge base, per workspace.</summary>
public class KbCategory : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Order { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A folder in a category, or a sub-folder of another folder.</summary>
public class KbFolder : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    public Guid CategoryId { get; set; }
    /// <summary>Set for sub-folders. Visibility and approval come from the top-level folder.</summary>
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Order { get; set; }
    public KbFolderVisibility Visibility { get; set; } = KbFolderVisibility.Everyone;
    /// <summary>Articles need an approver's sign-off before they are published (FR-KB-3).</summary>
    public bool RequiresApproval { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<KbFolderAudience> Audiences { get; set; } = [];
    public List<KbFolderApprover> Approvers { get; set; } = [];
}

public class KbFolderAudience
{
    public Guid FolderId { get; set; }
    public KbAudienceKind Kind { get; set; }
    public Guid TargetId { get; set; }
}

public class KbFolderApprover
{
    public Guid FolderId { get; set; }
    public Guid UserId { get; set; }
}

public class KbArticle : Entity, IHasTimestamps, IActivitySubject
{
    public const string Family = "kb_article";
    public const string DisplayFamily = "kb";

    public Guid WorkspaceId { get; set; }
    public int Number { get; set; }
    public Guid FolderId { get; set; }
    /// <summary>Title of the latest version (agent lists). Requesters read the published version's title.</summary>
    public string Title { get; set; } = "";
    public KbArticleStatus Status { get; set; } = KbArticleStatus.Draft;
    public KbArticleType Type { get; set; } = KbArticleType.Permanent;
    public Guid? AuthorId { get; set; }
    public Guid? OwnerId { get; set; }
    public DateOnly? ReviewBy { get; set; }
    /// <summary>Lower-case tags.</summary>
    public List<string> Tags { get; set; } = [];
    public string? Keywords { get; set; }
    /// <summary>Tags and keywords joined, for search.</summary>
    public string SearchTerms { get; set; } = "";
    /// <summary>When set, the article points to this URL instead of holding content (FR-KB-7).</summary>
    public string? ExternalUrl { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public Guid? PublishedVersionId { get; set; }
    public Guid? LatestVersionId { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int ViewCount { get; set; }
    /// <summary>Ticket the article was written from (FR-KB-6).</summary>
    public Guid? SourceTicketId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string DisplayId => FormatDisplayId(Number);

    public static string FormatDisplayId(int number) => $"KB-{number}";

    string IActivitySubject.ActivitySubjectType => Family;
    Guid IActivitySubject.ActivitySubjectId => Id;
    Guid? IActivitySubject.ActivityWorkspaceId => WorkspaceId;
}

public class KbArticleVersion : Entity
{
    public Guid ArticleId { get; set; }
    /// <summary>1, 2, 3… per article.</summary>
    public int Number { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Sanitized HTML.</summary>
    public string BodyHtml { get; set; } = "";
    public string BodyText { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public KbVersionState State { get; set; } = KbVersionState.Draft;
    /// <summary>Bumped on every save of a draft; the editor sends it back to detect concurrent edits.</summary>
    public int Revision { get; set; } = 1;
    public string? ChangeNote { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? PublishedById { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>Set when this version was created by restoring an older one.</summary>
    public int? RestoredFromNumber { get; set; }
}

/// <summary>One submit → approve/reject round (FR-KB-3). Moves to the shared approvals engine later (FR-APR-1).</summary>
public class KbArticleReview : Entity
{
    public Guid ArticleId { get; set; }
    public Guid VersionId { get; set; }
    public Guid? SubmittedById { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public string? SubmitNote { get; set; }
    public KbReviewDecision Decision { get; set; }
    public Guid? DecidedById { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionComment { get; set; }
}

public class KbArticleRelation
{
    public Guid ArticleId { get; set; }
    public Guid RelatedArticleId { get; set; }
}

/// <summary>A helpful / not helpful vote with an optional comment (FR-KB-5). One per reader per article.</summary>
public class KbArticleFeedback : Entity
{
    public Guid ArticleId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>Random cookie value identifying a signed-out reader.</summary>
    public string? VisitorKey { get; set; }
    public bool Helpful { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A requester opened a suggested article from the new-ticket form (FR-POR-7). It counts as a
/// deflection unless the same requester files a ticket within <see cref="Window"/>.
/// </summary>
public class KbDeflection : Entity
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    public Guid ArticleId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ViewedAt { get; set; }
    /// <summary>Set when the requester filed a ticket after reading: not a deflection.</summary>
    public Guid? TicketId { get; set; }
}

public class KbAnnouncement : Entity, IHasTimestamps
{
    public Guid WorkspaceId { get; set; }
    public string Title { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public string BodyText { get; set; } = "";
    public KbAnnouncementAudience Audience { get; set; } = KbAnnouncementAudience.Everyone;
    public DateTimeOffset ActiveFrom { get; set; }
    public DateTimeOffset? ActiveTo { get; set; }
    /// <summary>Drafts are never shown.</summary>
    public bool IsPublished { get; set; }
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<KbAnnouncementTarget> Targets { get; set; } = [];
}

public class KbAnnouncementTarget
{
    public Guid AnnouncementId { get; set; }
    public KbAudienceKind Kind { get; set; }
    public Guid TargetId { get; set; }
}

public static class KbAttachmentOwners
{
    public const string Article = "kb_article";
    public const string Announcement = "kb_announcement";
}

public static class KbPrivileges
{
    public const string View = "kb.view";
    public const string Write = "kb.write";
    public const string Publish = "kb.publish";
    public const string Manage = "kb.manage";
}

public static class KbSettingKeys
{
    /// <summary>Signed-out visitors may read folders visible to everyone (FR-POR-6).</summary>
    public const string Public = "kb.public";
    /// <summary>Set once the module granted its default privileges to the built-in roles.</summary>
    public const string RolesSeeded = "kb.roles_seeded";
}
