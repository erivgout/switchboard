using Switchboard.Core.Events;

namespace Switchboard.Modules.Knowledge.Domain;

/// <summary>A version went live (directly or after approval).</summary>
public sealed record KbArticlePublished(Guid ArticleId, Guid WorkspaceId, Guid VersionId, int VersionNumber) : DomainEvent;

/// <summary>An article was submitted for approval; approvers should be told (FR-KB-3).</summary>
public sealed record KbArticleSubmitted(Guid ArticleId, Guid WorkspaceId, Guid ReviewId, IReadOnlyList<Guid> ApproverIds) : DomainEvent;

/// <summary>An approver approved or rejected a submission.</summary>
public sealed record KbArticleReviewed(Guid ArticleId, Guid WorkspaceId, Guid ReviewId, bool Approved, Guid? SubmittedById) : DomainEvent;
