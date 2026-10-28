using Switchboard.Core.Domain;
using Switchboard.Modules.Knowledge.Domain;

namespace Switchboard.Modules.Knowledge.Services;

/// <summary>Everything the editor sends when it saves (explicitly or by autosave).</summary>
public sealed class KbDraftInput
{
    public Guid FolderId { get; set; }
    public string Title { get; set; } = "";
    public string? BodyHtml { get; set; }
    public KbArticleType Type { get; set; } = KbArticleType.Permanent;
    public IReadOnlyList<string>? Tags { get; set; }
    public string? Keywords { get; set; }
    public Guid? OwnerId { get; set; }
    public DateOnly? ReviewBy { get; set; }
    public string? ExternalUrl { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    /// <summary>Null leaves related articles unchanged.</summary>
    public IReadOnlyList<Guid>? RelatedArticleIds { get; set; }
    /// <summary>Pending uploads to attach as files (inline images are found in the body).</summary>
    public IReadOnlyList<Guid>? AttachmentIds { get; set; }
    /// <summary>Files to detach.</summary>
    public IReadOnlyList<Guid>? RemoveAttachmentIds { get; set; }
    public Guid? SourceTicketId { get; set; }
    /// <summary>Revision of the draft the editor loaded; a mismatch means someone else saved first.</summary>
    public int? ExpectedRevision { get; set; }
    public string? ChangeNote { get; set; }
}

public sealed record KbSaveResult(Guid ArticleId, int Number, int VersionNumber, int Revision, DateTimeOffset SavedAt);

public enum KbSearchMode
{
    /// <summary>Agent console: every status, in workspaces where the viewer holds kb.view.</summary>
    Agent,
    /// <summary>Portal, search slots, suggestions and requester tools: published versions only.</summary>
    Published,
}

public enum KbSort
{
    Relevance,
    Updated,
    Popular,
    Title,
}

public sealed class KbSearchQuery
{
    public string? Text { get; set; }
    public KbSearchMode Mode { get; set; } = KbSearchMode.Published;
    public Guid? CategoryId { get; set; }
    /// <summary>Includes sub-folders.</summary>
    public Guid? FolderId { get; set; }
    public KbArticleStatus? Status { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? AuthorId { get; set; }
    /// <summary>Review-by date has passed (agent mode).</summary>
    public bool NeedsReview { get; set; }
    /// <summary>Waiting for the viewer's approval (agent mode).</summary>
    public bool AwaitingMyApproval { get; set; }
    /// <summary>Drop weak matches. Title hits score 10, tag or keyword hits 6, body hits 2 per word.</summary>
    public int MinScore { get; set; } = 1;
    public KbSort Sort { get; set; } = KbSort.Relevance;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed record KbSearchHit(
    Guid Id,
    int Number,
    string Title,
    string Snippet,
    Guid FolderId,
    string FolderPath,
    string CategoryName,
    KbArticleStatus Status,
    /// <summary>For published articles: Draft or InReview when newer changes wait to be published.</summary>
    KbVersionState? PendingChanges,
    KbArticleType Type,
    Guid? OwnerId,
    string? OwnerName,
    DateTimeOffset UpdatedAt,
    DateOnly? ReviewBy,
    int Views,
    string? ExternalUrl)
{
    public string DisplayId => KbArticle.FormatDisplayId(Number);
}

public sealed record KbSearchPage(IReadOnlyList<KbSearchHit> Items, int Total, int Page, int PageSize, IReadOnlyList<string> Terms)
{
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed record KbFeedbackSummary(int Helpful, int NotHelpful, int Deflections, int Views)
{
    public int Votes => Helpful + NotHelpful;
    public int? HelpfulPercent => Votes == 0 ? null : (int)Math.Round(Helpful * 100.0 / Votes);
}

public sealed record KbFeedbackComment(Guid Id, string? AuthorName, bool Helpful, string Comment, DateTimeOffset At);

public sealed record KbArticleLink(Guid Id, int Number, string Title)
{
    public string DisplayId => KbArticle.FormatDisplayId(Number);
}

public sealed record KbTicketRef(Guid Id, int Number, string DisplayId, string Subject);

/// <summary>What the current viewer may do with an article (also enforced by the service).</summary>
public sealed record KbArticleAccess(
    bool CanEdit,
    bool CanSubmit,
    bool CanPublish,
    bool CanApprove,
    bool CanWithdraw,
    bool CanArchive,
    bool CanUnarchive,
    bool CanDiscardDraft,
    bool CanTrash,
    bool CanRestore,
    bool CanPurge);

/// <summary>An article as agents see it: the latest version plus everything around it.</summary>
public sealed class KbArticleDetail
{
    public required KbArticle Article { get; init; }
    public required KbArticleVersion Latest { get; init; }
    public KbArticleVersion? Published { get; init; }
    public required KbFolderNode Folder { get; init; }
    public required string FolderPath { get; init; }
    public required string CategoryName { get; init; }
    public string? AuthorName { get; init; }
    public string? OwnerName { get; init; }
    public KbArticleReview? PendingReview { get; init; }
    public string? SubmitterName { get; init; }
    public KbArticleReview? LastDecision { get; init; }
    public string? DeciderName { get; init; }
    public required IReadOnlyList<string> ApproverNames { get; init; }
    public required KbArticleAccess Access { get; init; }
    public required IReadOnlyList<KbArticleLink> Related { get; init; }
    public required IReadOnlyList<Attachment> Attachments { get; init; }
    public required IReadOnlyList<KbTicketRef> Tickets { get; init; }
    public required KbFeedbackSummary Feedback { get; init; }

    public bool HasPendingChanges => Article.Status == KbArticleStatus.Published && Latest.State is KbVersionState.Draft or KbVersionState.InReview;
}

/// <summary>A published article as requesters (and every published read path) see it.</summary>
public sealed class KbPublishedArticle
{
    public required Guid Id { get; init; }
    public required int Number { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string Title { get; init; }
    public required string BodyHtml { get; init; }
    public required string BodyText { get; init; }
    public required string Excerpt { get; init; }
    public required KbArticleType Type { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public string? ExternalUrl { get; init; }
    public string? SeoTitle { get; init; }
    public string? SeoDescription { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required int VersionNumber { get; init; }
    public required KbFolderNode Folder { get; init; }
    public required string FolderPath { get; init; }
    public required string CategoryName { get; init; }
    public string? AuthorName { get; init; }
    public required IReadOnlyList<KbArticleLink> Related { get; init; }
    public required IReadOnlyList<Attachment> Attachments { get; init; }

    public string DisplayId => KbArticle.FormatDisplayId(Number);
}

public sealed record KbVersionSummary(
    Guid Id,
    int Number,
    string Title,
    KbVersionState State,
    string? AuthorName,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    string? PublishedByName,
    string? ChangeNote,
    int? RestoredFromNumber);

public sealed record KbReviewItem(Guid ArticleId, int Number, string Title, string FolderPath, string? SubmitterName, DateTimeOffset SubmittedAt, string? Note)
{
    public string DisplayId => KbArticle.FormatDisplayId(Number);
}

public sealed record KbTrashItem(string Kind, Guid Id, string Title, string Location, DateTimeOffset DeletedAt, string? DeletedByName, bool CanRestore, bool CanPurge);
