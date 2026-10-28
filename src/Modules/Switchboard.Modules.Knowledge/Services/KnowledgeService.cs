using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Knowledge.Domain;

namespace Switchboard.Modules.Knowledge.Services;

/// <summary>
/// Articles: drafts, review and approval, publishing, versions, trash, feedback and search.
/// Every method checks permissions and folder visibility itself; pages and tools only call it.
/// </summary>
public interface IKnowledgeService
{
    Task<KbSaveResult> CreateDraftAsync(KbDraftInput input, CancellationToken ct = default);
    Task<KbSaveResult> SaveDraftAsync(Guid articleId, KbDraftInput input, CancellationToken ct = default);
    Task SubmitForReviewAsync(Guid articleId, string? note, CancellationToken ct = default);
    Task WithdrawReviewAsync(Guid articleId, CancellationToken ct = default);
    Task ApproveAsync(Guid articleId, string? comment, CancellationToken ct = default);
    Task RejectAsync(Guid articleId, string comment, CancellationToken ct = default);
    Task PublishAsync(Guid articleId, string? note = null, CancellationToken ct = default);
    Task ArchiveAsync(Guid articleId, CancellationToken ct = default);
    Task UnarchiveAsync(Guid articleId, CancellationToken ct = default);
    Task DiscardDraftAsync(Guid articleId, CancellationToken ct = default);
    Task<KbSaveResult> RestoreVersionAsync(Guid articleId, int versionNumber, CancellationToken ct = default);
    Task<int> TrashAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default);
    Task<int> RestoreAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default);
    Task<int> PurgeAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default);

    Task<KbArticleDetail> GetForAgentAsync(Guid articleId, CancellationToken ct = default);
    Task<KbPublishedArticle> GetPublishedAsync(Guid articleId, CancellationToken ct = default);
    /// <summary>Resolves "KB-1042", "1042" or a GUID to an article id the viewer can see; null otherwise.</summary>
    Task<Guid?> ResolveAsync(string reference, CancellationToken ct = default);
    Task<IReadOnlyList<KbVersionSummary>> VersionsAsync(Guid articleId, CancellationToken ct = default);
    Task<KbArticleVersion> VersionAsync(Guid articleId, int number, CancellationToken ct = default);

    Task<KbSearchPage> SearchAsync(KbSearchQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<KbSearchHit>> SuggestAsync(string text, int limit = 5, CancellationToken ct = default);
    Task<IReadOnlyList<KbSearchHit>> PopularAsync(int limit = 6, CancellationToken ct = default);
    Task<IReadOnlyList<KbReviewItem>> ReviewsAwaitingMeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<KbArticleLink>> PickAsync(string text, Guid? exclude, CancellationToken ct = default);

    Task RecordViewAsync(Guid articleId, CancellationToken ct = default);
    Task RecordDeflectionCandidateAsync(Guid articleId, CancellationToken ct = default);
    Task<KbArticleFeedback> VoteAsync(Guid articleId, bool helpful, string? comment, string? visitorKey, CancellationToken ct = default);
    Task<KbArticleFeedback?> MyVoteAsync(Guid articleId, string? visitorKey, CancellationToken ct = default);
    Task<IReadOnlyList<KbFeedbackComment>> FeedbackCommentsAsync(Guid articleId, CancellationToken ct = default);

    Task<IReadOnlyList<KbTrashItem>> TrashedArticlesAsync(CancellationToken ct = default);
}

public sealed partial class KnowledgeService(
    SwitchboardDbContext db,
    IKnowledgeVisibility visibility,
    IActorAccessor actors,
    IHtmlContent html,
    IAttachmentService attachments,
    IFileStore files,
    IDisplayIdGenerator displayIds,
    IAuditLog audit,
    IServiceProvider services,
    TimeProvider time) : IKnowledgeService
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "cant", "do", "does", "doesnt", "dont", "for", "from",
        "get", "has", "have", "how", "i", "if", "im", "in", "into", "is", "isnt", "it", "its", "keeps", "me", "my", "no",
        "not", "of", "on", "or", "our", "so", "that", "the", "their", "there", "this", "to", "too", "up", "was", "we",
        "what", "when", "where", "which", "who", "why", "will", "with", "wont", "you", "your", "please", "help", "need",
        "anymore", "again", "since", "after", "all", "any", "just", "still", "every", "some",
    };

    private DbSet<KbArticle> Articles => db.Set<KbArticle>();
    private DbSet<KbArticleVersion> Versions => db.Set<KbArticleVersion>();
    private DbSet<KbArticleReview> Reviews => db.Set<KbArticleReview>();

    // ================================================================ writing

    public async Task<KbSaveResult> CreateDraftAsync(KbDraftInput input, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        Demand(viewer.IsInternal || viewer.WriteWorkspaces.Count > 0);
        var folder = await WritableFolderAsync(input.FolderId, viewer, ct);
        var title = ValidTitle(input.Title);
        var now = time.GetUtcNow();
        var userId = actors.Current.UserId;

        var article = new KbArticle
        {
            WorkspaceId = folder.WorkspaceId,
            Number = await displayIds.NextAsync(KbArticle.DisplayFamily, ct),
            FolderId = folder.Id,
            Title = title,
            Status = KbArticleStatus.Draft,
            AuthorId = userId,
            OwnerId = userId,
        };
        await ApplyMetadataAsync(article, input, viewer, ct);
        var body = html.Sanitize(input.BodyHtml);
        var version = new KbArticleVersion
        {
            ArticleId = article.Id,
            Number = 1,
            State = KbVersionState.Draft,
            CreatedById = userId,
            CreatedAt = now,
        };
        SetContent(version, title, body, userId, now);
        version.ChangeNote = Trim(input.ChangeNote, 500);
        article.LatestVersionId = version.Id;

        if (input.SourceTicketId is { } ticketId)
        {
            // Only link tickets the author can actually see.
            var ticket = await services.GetRequiredService<ITicketService>().GetForAgentAsync(ticketId, ct);
            article.SourceTicketId = ticket.Id;
        }

        Articles.Add(article);
        Versions.Add(version);
        await ClaimFilesAsync(article.Id, body, input.AttachmentIds, ct);
        db.Activity(article, "created", $"Created draft {article.DisplayId}");
        await SaveAsync(ct);

        if (article.SourceTicketId is { } source)
        {
            await LinkTicketAsync(source, article.Id, ct);
        }
        return new KbSaveResult(article.Id, article.Number, version.Number, version.Revision, now);
    }

    public async Task<KbSaveResult> SaveDraftAsync(Guid articleId, KbDraftInput input, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        Demand(viewer.CanWrite(article.WorkspaceId));
        if (article.Status == KbArticleStatus.Archived)
        {
            throw new ValidationException("article", $"{article.DisplayId} is archived. Restore it from the archive before editing.");
        }
        var latest = await Versions.FirstAsync(v => v.Id == article.LatestVersionId, ct);
        if (latest.State == KbVersionState.InReview)
        {
            throw new ValidationException("article", $"{article.DisplayId} is waiting for review. Withdraw it from review to make changes.");
        }
        if (latest.State == KbVersionState.Draft && input.ExpectedRevision is { } expected && expected != latest.Revision)
        {
            var who = await UserNameAsync(latest.UpdatedById, ct) ?? "Someone";
            throw new ConflictException($"{who} saved a newer draft of this article. Reload to see their changes.");
        }

        if (input.FolderId != article.FolderId)
        {
            var folder = await WritableFolderAsync(input.FolderId, viewer, ct);
            if (folder.WorkspaceId != article.WorkspaceId)
            {
                throw new ValidationException("folderId", "Move the article to a folder in the same workspace.");
            }
            article.FolderId = folder.Id;
        }

        var title = ValidTitle(input.Title);
        var body = html.Sanitize(input.BodyHtml);
        var now = time.GetUtcNow();
        var userId = actors.Current.UserId;
        var draft = latest;
        if (latest.State != KbVersionState.Draft)
        {
            draft = new KbArticleVersion
            {
                ArticleId = article.Id,
                Number = await NextVersionNumberAsync(article.Id, ct),
                State = KbVersionState.Draft,
                CreatedById = userId,
                CreatedAt = now,
                Revision = 0,
            };
            Versions.Add(draft);
            article.LatestVersionId = draft.Id;
        }
        SetContent(draft, title, body, userId, now);
        draft.Revision++;
        if (input.ChangeNote is not null)
        {
            draft.ChangeNote = Trim(input.ChangeNote, 500);
        }
        article.Title = title;
        await ApplyMetadataAsync(article, input, viewer, ct);
        await ClaimFilesAsync(article.Id, body, input.AttachmentIds, ct);
        var removed = input.RemoveAttachmentIds is { Count: > 0 } remove ? await DetachFilesAsync(article.Id, remove, ct) : [];
        await SaveAsync(ct);
        await DeleteBlobsAsync(files, removed);
        return new KbSaveResult(article.Id, article.Number, draft.Number, draft.Revision, now);
    }

    public async Task SubmitForReviewAsync(Guid articleId, string? note, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        var (latest, folder) = await LatestAndFolderAsync(article, ct);
        var access = await AccessAsync(article, latest, folder, viewer, null, ct);
        Demand(access.CanSubmit, $"{article.DisplayId} has no draft to submit.");
        ValidateForPublishing(article, latest);

        latest.State = KbVersionState.InReview;
        latest.Revision++;
        var review = new KbArticleReview
        {
            ArticleId = article.Id,
            VersionId = latest.Id,
            SubmittedById = actors.Current.UserId,
            SubmittedAt = time.GetUtcNow(),
            SubmitNote = Trim(note, 2000),
            Decision = KbReviewDecision.Pending,
        };
        Reviews.Add(review);
        if (article.Status != KbArticleStatus.Published)
        {
            article.Status = KbArticleStatus.InReview;
        }
        db.Activity(article, "submitted", $"Submitted version {latest.Number} for review");
        db.Raise(new KbArticleSubmitted(article.Id, article.WorkspaceId, review.Id, folder.ApproverIds));
        await SaveAsync(ct);
    }

    public async Task WithdrawReviewAsync(Guid articleId, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        var (latest, folder) = await LatestAndFolderAsync(article, ct);
        var review = await PendingReviewAsync(article.Id, tracked: true, ct);
        var access = await AccessAsync(article, latest, folder, viewer, review, ct);
        Demand(access.CanWithdraw, $"{article.DisplayId} isn't waiting for review.");

        review!.Decision = KbReviewDecision.Withdrawn;
        review.DecidedById = actors.Current.UserId;
        review.DecidedAt = time.GetUtcNow();
        ReturnToDraft(article, latest);
        db.Activity(article, "withdrawn", $"Withdrew version {latest.Number} from review");
        await SaveAsync(ct);
    }

    public async Task ApproveAsync(Guid articleId, string? comment, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        var (latest, folder) = await LatestAndFolderAsync(article, ct);
        var review = await PendingReviewAsync(article.Id, tracked: true, ct);
        var access = await AccessAsync(article, latest, folder, viewer, review, ct);
        Demand(access.CanApprove, review is null ? $"{article.DisplayId} isn't waiting for review." : null);

        var now = time.GetUtcNow();
        review!.Decision = KbReviewDecision.Approved;
        review.DecidedById = actors.Current.UserId;
        review.DecidedAt = now;
        review.DecisionComment = Trim(comment, 2000);
        db.Raise(new KbArticleReviewed(article.Id, article.WorkspaceId, review.Id, true, review.SubmittedById));
        await PublishVersionAsync(article, latest, $"Approved and published version {latest.Number}", ct);
    }

    public async Task RejectAsync(Guid articleId, string comment, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new ValidationException("comment", "Say what needs to change so the author can fix it.");
        }
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        var (latest, folder) = await LatestAndFolderAsync(article, ct);
        var review = await PendingReviewAsync(article.Id, tracked: true, ct);
        var access = await AccessAsync(article, latest, folder, viewer, review, ct);
        Demand(access.CanApprove, review is null ? $"{article.DisplayId} isn't waiting for review." : null);

        review!.Decision = KbReviewDecision.Rejected;
        review.DecidedById = actors.Current.UserId;
        review.DecidedAt = time.GetUtcNow();
        review.DecisionComment = Trim(comment, 2000);
        ReturnToDraft(article, latest);
        db.Activity(article, "rejected", $"Sent version {latest.Number} back for changes");
        db.Raise(new KbArticleReviewed(article.Id, article.WorkspaceId, review.Id, false, review.SubmittedById));
        await SaveAsync(ct);
    }

    public async Task PublishAsync(Guid articleId, string? note = null, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        var (latest, folder) = await LatestAndFolderAsync(article, ct);
        if (folder.RequiresApproval && viewer.CanPublish(article.WorkspaceId) && !viewer.IsInternal)
        {
            throw new ValidationException("article", $"Articles in {folder.Name} need approval. Submit it for review instead.");
        }
        var access = await AccessAsync(article, latest, folder, viewer, null, ct);
        Demand(access.CanPublish, latest.State == KbVersionState.Published ? $"{article.DisplayId} has no unpublished changes." : null);
        if (note is not null)
        {
            latest.ChangeNote = Trim(note, 500);
        }
        await PublishVersionAsync(article, latest, $"Published version {latest.Number}", ct);
    }

    public async Task ArchiveAsync(Guid articleId, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        var (latest, folder) = await LatestAndFolderAsync(article, ct);
        var access = await AccessAsync(article, latest, folder, viewer, null, ct);
        Demand(access.CanArchive);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pending = await PendingReviewAsync(article.Id, tracked: true, ct);
        if (pending is not null)
        {
            pending.Decision = KbReviewDecision.Withdrawn;
            pending.DecidedById = actors.Current.UserId;
            pending.DecidedAt = time.GetUtcNow();
            var inReview = await Versions.FirstAsync(v => v.Id == pending.VersionId, ct);
            inReview.State = KbVersionState.Draft;
            inReview.Revision++;
        }
        await Versions.Where(v => v.ArticleId == article.Id && v.State == KbVersionState.Published)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.State, KbVersionState.Previous), ct);
        article.Status = KbArticleStatus.Archived;
        article.PublishedVersionId = null;
        db.Activity(article, "archived", "Archived the article; requesters can no longer read it");
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task UnarchiveAsync(Guid articleId, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        var (latest, folder) = await LatestAndFolderAsync(article, ct);
        var access = await AccessAsync(article, latest, folder, viewer, null, ct);
        Demand(access.CanUnarchive);

        if (latest.State != KbVersionState.Draft)
        {
            var now = time.GetUtcNow();
            var draft = new KbArticleVersion
            {
                ArticleId = article.Id,
                Number = await NextVersionNumberAsync(article.Id, ct),
                State = KbVersionState.Draft,
                CreatedById = actors.Current.UserId,
                CreatedAt = now,
                RestoredFromNumber = latest.Number,
            };
            SetContent(draft, latest.Title, latest.BodyHtml, actors.Current.UserId, now);
            Versions.Add(draft);
            article.LatestVersionId = draft.Id;
        }
        article.Status = KbArticleStatus.Draft;
        db.Activity(article, "unarchived", "Restored the article from the archive as a draft");
        await SaveAsync(ct);
    }

    public async Task DiscardDraftAsync(Guid articleId, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        var (latest, folder) = await LatestAndFolderAsync(article, ct);
        var access = await AccessAsync(article, latest, folder, viewer, null, ct);
        Demand(access.CanDiscardDraft, $"{article.DisplayId} has no draft changes to discard.");

        Versions.Remove(latest);
        article.LatestVersionId = article.PublishedVersionId;
        var published = await Versions.AsNoTracking().FirstAsync(v => v.Id == article.PublishedVersionId, ct);
        article.Title = published.Title;
        db.Activity(article, "draft_discarded", $"Discarded the unpublished changes in version {latest.Number}");
        await SaveAsync(ct);
    }

    public async Task<KbSaveResult> RestoreVersionAsync(Guid articleId, int versionNumber, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: true, ct: ct);
        Demand(viewer.CanWrite(article.WorkspaceId));
        if (article.Status == KbArticleStatus.Archived)
        {
            throw new ValidationException("article", $"{article.DisplayId} is archived. Restore it from the archive first.");
        }
        var latest = await Versions.FirstAsync(v => v.Id == article.LatestVersionId, ct);
        if (latest.State == KbVersionState.InReview)
        {
            throw new ValidationException("article", $"{article.DisplayId} is waiting for review. Withdraw it from review first.");
        }
        var source = await Versions.AsNoTracking().FirstOrDefaultAsync(v => v.ArticleId == article.Id && v.Number == versionNumber, ct)
            ?? throw new NotFoundException("That version doesn't exist.");
        if (source.Id == latest.Id)
        {
            throw new ValidationException("version", $"Version {versionNumber} is already the latest version.");
        }

        // Keep the current draft in history rather than overwriting it.
        if (latest.State == KbVersionState.Draft)
        {
            latest.State = KbVersionState.Previous;
            latest.Revision++;
        }
        var now = time.GetUtcNow();
        var draft = new KbArticleVersion
        {
            ArticleId = article.Id,
            Number = await NextVersionNumberAsync(article.Id, ct),
            State = KbVersionState.Draft,
            CreatedById = actors.Current.UserId,
            CreatedAt = now,
            RestoredFromNumber = source.Number,
            ChangeNote = $"Restored from version {source.Number}",
        };
        SetContent(draft, source.Title, source.BodyHtml, actors.Current.UserId, now);
        Versions.Add(draft);
        article.LatestVersionId = draft.Id;
        article.Title = draft.Title;
        if (article.Status == KbArticleStatus.InReview)
        {
            article.Status = KbArticleStatus.Draft;
        }
        db.Activity(article, "version_restored", $"Restored version {source.Number} as a new draft (version {draft.Number})");
        await SaveAsync(ct);
        return new KbSaveResult(article.Id, article.Number, draft.Number, draft.Revision, now);
    }

    public async Task<int> TrashAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var now = time.GetUtcNow();
        var count = 0;
        foreach (var id in articleIds.Distinct())
        {
            var article = await LoadAgentArticleAsync(id, viewer, tracked: true, ct: ct);
            Demand(CanTrash(article, viewer));
            article.DeletedAt = now;
            article.DeletedById = actors.Current.UserId;
            db.Activity(article, "trashed", $"Moved {article.DisplayId} to the trash");
            count++;
        }
        await SaveAsync(ct);
        return count;
    }

    public async Task<int> RestoreAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var count = 0;
        foreach (var id in articleIds.Distinct())
        {
            var article = await LoadAgentArticleAsync(id, viewer, tracked: true, includeTrashed: true, ct: ct);
            if (article.DeletedAt is null)
            {
                continue;
            }
            Demand(CanTrash(article, viewer));
            article.DeletedAt = null;
            article.DeletedById = null;
            db.Activity(article, "restored", $"Restored {article.DisplayId} from the trash");
            count++;
        }
        await SaveAsync(ct);
        return count;
    }

    public async Task<int> PurgeAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var targets = new List<KbArticle>();
        foreach (var id in articleIds.Distinct())
        {
            var article = await LoadAgentArticleAsync(id, viewer, tracked: false, includeTrashed: true, ct: ct);
            Demand(viewer.CanManage(article.WorkspaceId));
            if (article.DeletedAt is null)
            {
                throw new ValidationException("article", $"Move {article.DisplayId} to the trash before deleting it permanently.");
            }
            targets.Add(article);
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var article in targets)
        {
            audit.Record("kb.article.purged", KbArticle.Family, article.Id.ToString(), new { article.Number, article.Title });
        }
        var blobs = await PurgeArticlesAsync(db, targets.Select(a => a.Id).ToList(), ct);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        await DeleteBlobsAsync(files, blobs);
        return targets.Count;
    }

    /// <summary>
    /// Deletes articles with their versions, reviews, feedback, links and file records, inside the caller's
    /// transaction. Returns storage keys to delete once the transaction commits. Callers check permissions.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> PurgeArticlesAsync(SwitchboardDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var stored = await db.Attachments
            .Where(a => a.OwnerType == KbAttachmentOwners.Article && a.OwnerId != null && ids.Contains(a.OwnerId.Value))
            .ToListAsync(ct);
        await db.Set<KbArticleRelation>().Where(r => ids.Contains(r.RelatedArticleId)).ExecuteDeleteAsync(ct);
        // Links from tickets to a deleted article would point nowhere.
        await db.TicketLinks.Where(l => l.TargetType == KbArticle.Family && ids.Contains(l.TargetId)).ExecuteDeleteAsync(ct);
        await db.Set<KbArticle>().Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);
        db.Attachments.RemoveRange(stored);
        return stored.Select(a => a.StorageKey).ToList();
    }

    internal static async Task DeleteBlobsAsync(IFileStore files, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await files.DeleteAsync(key);
            }
            catch (IOException)
            {
                // An orphaned blob is harmless; its record is gone.
            }
        }
    }

    // ================================================================ reading

    public async Task<KbArticleDetail> GetForAgentAsync(Guid articleId, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: false, includeTrashed: true, ct: ct);
        var index = await visibility.FoldersAsync(ct);
        var folder = index.Folders[article.FolderId];
        var versions = await Versions.AsNoTracking()
            .Where(v => v.Id == article.LatestVersionId || v.Id == article.PublishedVersionId)
            .ToListAsync(ct);
        var latest = versions.First(v => v.Id == article.LatestVersionId);
        var published = versions.FirstOrDefault(v => v.Id == article.PublishedVersionId);
        var pending = await PendingReviewAsync(article.Id, tracked: false, ct);
        var lastDecision = await Reviews.AsNoTracking()
            .Where(r => r.ArticleId == article.Id && (r.Decision == KbReviewDecision.Rejected || r.Decision == KbReviewDecision.Approved))
            .OrderByDescending(r => r.DecidedAt)
            .FirstOrDefaultAsync(ct);

        var userIds = new[] { article.AuthorId, article.OwnerId, pending?.SubmittedById, lastDecision?.DecidedById }
            .Concat(folder.ApproverIds.Select(id => (Guid?)id))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? Name(Guid? id) => id is { } x && names.TryGetValue(x, out var n) ? n : null;

        var ticketIds = await db.TicketLinks.AsNoTracking()
            .Where(l => l.TargetType == KbArticle.Family && l.TargetId == article.Id)
            .Select(l => l.TicketId)
            .ToListAsync(ct);
        if (article.SourceTicketId is { } source && !ticketIds.Contains(source))
        {
            ticketIds.Add(source);
        }
        var tickets = new List<KbTicketRef>();
        if (ticketIds.Count > 0)
        {
            var visibleTickets = await services.GetRequiredService<IPermissionService>().AgentVisibleTicketsAsync(ct);
            tickets = (await visibleTickets.AsNoTracking()
                    .Where(t => ticketIds.Contains(t.Id) && t.DeletedAt == null && t.PurgedAt == null)
                    .Select(t => new { t.Id, t.Type, t.Number, t.Subject })
                    .ToListAsync(ct))
                .Select(t => new KbTicketRef(t.Id, t.Number, Ticket.FormatDisplayId(t.Type, t.Number), t.Subject))
                .ToList();
        }

        return new KbArticleDetail
        {
            Article = article,
            Latest = latest,
            Published = published,
            Folder = folder,
            FolderPath = index.PathOf(folder.Id),
            CategoryName = index.Categories.GetValueOrDefault(folder.CategoryId)?.Name ?? "",
            AuthorName = Name(article.AuthorId),
            OwnerName = Name(article.OwnerId),
            PendingReview = pending,
            SubmitterName = Name(pending?.SubmittedById),
            LastDecision = lastDecision,
            DeciderName = Name(lastDecision?.DecidedById),
            ApproverNames = folder.ApproverIds.Select(id => Name(id)).OfType<string>().ToList(),
            Access = await AccessAsync(article, latest, folder, viewer, pending, ct),
            Related = await RelatedAsync(article.Id, KbSearchMode.Agent, ct),
            Attachments = (await attachments.ListAsync(KbAttachmentOwners.Article, article.Id, ct)).Where(a => !a.IsInline).ToList(),
            Tickets = tickets,
            Feedback = await FeedbackSummaryAsync(article.Id, article.ViewCount, ct),
        };
    }

    public async Task<KbPublishedArticle> GetPublishedAsync(Guid articleId, CancellationToken ct = default)
    {
        var visible = (await visibility.VisibleFolderIdsAsync(ct)).ToList();
        var row = await (
                from a in Articles.AsNoTracking()
                join v in Versions.AsNoTracking() on a.PublishedVersionId equals v.Id
                where a.Id == articleId && a.DeletedAt == null && a.Status == KbArticleStatus.Published
                    && v.State == KbVersionState.Published && visible.Contains(a.FolderId)
                select new { Article = a, Version = v })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("That article doesn't exist or you can't see it.");

        var index = await visibility.FoldersAsync(ct);
        var folder = index.Folders[row.Article.FolderId];
        var author = row.Article.AuthorId is { } authorId
            ? await db.Users.AsNoTracking().Where(u => u.Id == authorId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        return new KbPublishedArticle
        {
            Id = row.Article.Id,
            Number = row.Article.Number,
            WorkspaceId = row.Article.WorkspaceId,
            Title = row.Version.Title,
            BodyHtml = row.Version.BodyHtml,
            BodyText = row.Version.BodyText,
            Excerpt = row.Version.Excerpt,
            Type = row.Article.Type,
            Tags = row.Article.Tags,
            ExternalUrl = row.Article.ExternalUrl,
            SeoTitle = row.Article.SeoTitle,
            SeoDescription = row.Article.SeoDescription,
            UpdatedAt = row.Version.PublishedAt ?? row.Version.UpdatedAt,
            VersionNumber = row.Version.Number,
            Folder = folder,
            FolderPath = index.PathOf(folder.Id),
            CategoryName = index.Categories.GetValueOrDefault(folder.CategoryId)?.Name ?? "",
            AuthorName = author,
            Related = await RelatedAsync(row.Article.Id, KbSearchMode.Published, ct),
            // Inline images are part of the body; the file list shows only attached files.
            Attachments = (await attachments.ListAsync(KbAttachmentOwners.Article, row.Article.Id, ct)).Where(a => !a.IsInline).ToList(),
        };
    }

    public async Task<Guid?> ResolveAsync(string reference, CancellationToken ct = default)
    {
        reference = (reference ?? "").Trim();
        Guid? id = null;
        if (Guid.TryParse(reference, out var guid))
        {
            id = guid;
        }
        else if (NumberReference().Match(reference) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var number))
        {
            id = await Articles.AsNoTracking().Where(a => a.Number == number).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        }
        if (id is not { } articleId)
        {
            return null;
        }
        var article = await Articles.AsNoTracking().Where(a => a.Id == articleId && a.DeletedAt == null)
            .Select(a => new { a.Id, a.FolderId, a.Status }).FirstOrDefaultAsync(ct);
        if (article is null)
        {
            return null;
        }
        var agentFolders = await visibility.AgentFolderIdsAsync(ct);
        var visible = await visibility.VisibleFolderIdsAsync(ct);
        return agentFolders.Contains(article.FolderId) || (visible.Contains(article.FolderId) && article.Status == KbArticleStatus.Published)
            ? article.Id
            : null;
    }

    public async Task<IReadOnlyList<KbVersionSummary>> VersionsAsync(Guid articleId, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: false, includeTrashed: true, ct: ct);
        var versions = await Versions.AsNoTracking()
            .Where(v => v.ArticleId == article.Id)
            .OrderByDescending(v => v.Number)
            .Select(v => new { v.Id, v.Number, v.Title, v.State, v.CreatedById, v.UpdatedById, v.UpdatedAt, v.PublishedAt, v.PublishedById, v.ChangeNote, v.RestoredFromNumber })
            .ToListAsync(ct);
        var ids = versions.SelectMany(v => new[] { v.UpdatedById ?? v.CreatedById, v.PublishedById }).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return versions.Select(v => new KbVersionSummary(
                v.Id, v.Number, v.Title, v.State,
                (v.UpdatedById ?? v.CreatedById) is { } a && names.TryGetValue(a, out var an) ? an : null,
                v.UpdatedAt, v.PublishedAt,
                v.PublishedById is { } p && names.TryGetValue(p, out var pn) ? pn : null,
                v.ChangeNote, v.RestoredFromNumber))
            .ToList();
    }

    public async Task<KbArticleVersion> VersionAsync(Guid articleId, int number, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: false, includeTrashed: true, ct: ct);
        return await Versions.AsNoTracking().FirstOrDefaultAsync(v => v.ArticleId == article.Id && v.Number == number, ct)
            ?? throw new NotFoundException("That version doesn't exist.");
    }

    // ================================================================ search

    public async Task<KbSearchPage> SearchAsync(KbSearchQuery query, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var index = await visibility.FoldersAsync(ct);
        IEnumerable<Guid> folderIds = query.Mode == KbSearchMode.Agent
            ? await visibility.AgentFolderIdsAsync(ct)
            : await visibility.VisibleFolderIdsAsync(ct);
        if (query.FolderId is { } folderId)
        {
            var scope = index.DescendantsOf(folderId).Append(folderId).ToHashSet();
            folderIds = folderIds.Where(scope.Contains);
        }
        if (query.CategoryId is { } categoryId)
        {
            folderIds = folderIds.Where(id => index.Folders[id].CategoryId == categoryId);
        }
        var folders = folderIds.ToList();

        IQueryable<KbSearchRow> rows = query.Mode == KbSearchMode.Agent
            ? from a in Articles.AsNoTracking()
              join v in Versions.AsNoTracking() on a.LatestVersionId equals v.Id
              where a.DeletedAt == null && folders.Contains(a.FolderId)
              select new KbSearchRow
              {
                  Id = a.Id, Number = a.Number, Title = v.Title, Body = v.BodyText, Excerpt = v.Excerpt, Terms = a.SearchTerms,
                  FolderId = a.FolderId, Status = a.Status, LatestState = v.State, Type = a.Type, OwnerId = a.OwnerId, AuthorId = a.AuthorId,
                  UpdatedAt = v.UpdatedAt, ReviewBy = a.ReviewBy, Views = a.ViewCount, ExternalUrl = a.ExternalUrl,
              }
            : from a in Articles.AsNoTracking()
              join v in Versions.AsNoTracking() on a.PublishedVersionId equals v.Id
              where a.DeletedAt == null && a.Status == KbArticleStatus.Published && v.State == KbVersionState.Published && folders.Contains(a.FolderId)
              select new KbSearchRow
              {
                  Id = a.Id, Number = a.Number, Title = v.Title, Body = v.BodyText, Excerpt = v.Excerpt, Terms = a.SearchTerms,
                  FolderId = a.FolderId, Status = a.Status, LatestState = v.State, Type = a.Type, OwnerId = a.OwnerId, AuthorId = a.AuthorId,
                  UpdatedAt = v.PublishedAt ?? v.UpdatedAt, ReviewBy = a.ReviewBy, Views = a.ViewCount, ExternalUrl = a.ExternalUrl,
              };

        if (query.Mode == KbSearchMode.Agent)
        {
            rows = query.Status switch
            {
                KbArticleStatus.InReview => rows.Where(r => r.Status == KbArticleStatus.InReview || r.LatestState == KbVersionState.InReview),
                KbArticleStatus.Draft => rows.Where(r => r.Status == KbArticleStatus.Draft || (r.Status == KbArticleStatus.Published && r.LatestState == KbVersionState.Draft)),
                { } s => rows.Where(r => r.Status == s),
                null => rows,
            };
            if (query.OwnerId is { } owner)
            {
                rows = rows.Where(r => r.OwnerId == owner);
            }
            if (query.AuthorId is { } author)
            {
                rows = rows.Where(r => r.AuthorId == author);
            }
            if (query.NeedsReview)
            {
                var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
                rows = rows.Where(r => r.ReviewBy != null && r.ReviewBy <= today && r.Status != KbArticleStatus.Archived);
            }
            if (query.AwaitingMyApproval)
            {
                var awaiting = (await ReviewsAwaitingMeAsync(ct)).Select(r => r.ArticleId).ToList();
                rows = rows.Where(r => awaiting.Contains(r.Id));
            }
        }

        var text = (query.Text ?? "").Trim();
        var terms = Terms(text);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var page = Math.Max(1, query.Page);

        IQueryable<KbScoredRow> scored;
        if (NumberReference().Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var number) && text.Contains('-'))
        {
            scored = rows.Where(r => r.Number == number).Select(r => new KbScoredRow { Row = r, Score = 100 });
        }
        else if (terms.Count > 0)
        {
            var minScore = Math.Max(1, query.MinScore);
            scored = rows.Select(ScoreExpression(terms, text)).Where(s => s.Score >= minScore);
        }
        else if (text.Length > 0)
        {
            // Only stop words: nothing meaningful to match.
            scored = rows.Where(_ => false).Select(r => new KbScoredRow { Row = r, Score = 0 });
        }
        else
        {
            scored = rows.Select(r => new KbScoredRow { Row = r, Score = 0 });
        }

        var sort = query.Sort == KbSort.Relevance && terms.Count == 0
            ? (query.Mode == KbSearchMode.Agent ? KbSort.Updated : KbSort.Popular)
            : query.Sort;
        scored = sort switch
        {
            KbSort.Updated => scored.OrderByDescending(s => s.Row.UpdatedAt),
            KbSort.Popular => scored.OrderByDescending(s => s.Row.Views).ThenByDescending(s => s.Row.UpdatedAt),
            KbSort.Title => scored.OrderBy(s => s.Row.Title),
            _ => scored.OrderByDescending(s => s.Score).ThenByDescending(s => s.Row.Views).ThenByDescending(s => s.Row.UpdatedAt),
        };

        var total = await scored.CountAsync(ct);
        var items = await scored.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new
            {
                s.Row.Id, s.Row.Number, s.Row.Title, s.Row.Excerpt, s.Row.FolderId, s.Row.Status, s.Row.LatestState, s.Row.Type,
                s.Row.OwnerId, s.Row.UpdatedAt, s.Row.ReviewBy, s.Row.Views, s.Row.ExternalUrl,
                Body = terms.Count > 0 ? s.Row.Body : null,
            })
            .ToListAsync(ct);

        var ownerIds = items.Select(i => i.OwnerId).OfType<Guid>().Distinct().ToList();
        var owners = query.Mode == KbSearchMode.Agent
            ? await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct)
            : [];

        var hits = items.Select(i => new KbSearchHit(
                i.Id,
                i.Number,
                i.Title,
                i.Body is null ? i.Excerpt : KbHtml.Snippet(i.Body, terms),
                i.FolderId,
                index.PathOf(i.FolderId),
                index.Categories.GetValueOrDefault(index.Folders[i.FolderId].CategoryId)?.Name ?? "",
                i.Status,
                i.Status == KbArticleStatus.Published && i.LatestState is KbVersionState.Draft or KbVersionState.InReview ? i.LatestState : null,
                i.Type,
                query.Mode == KbSearchMode.Agent ? i.OwnerId : null,
                i.OwnerId is { } o && owners.TryGetValue(o, out var on) ? on : null,
                i.UpdatedAt,
                i.ReviewBy,
                i.Views,
                i.ExternalUrl))
            .ToList();
        _ = viewer;
        return new KbSearchPage(hits, total, page, pageSize, terms);
    }

    public async Task<IReadOnlyList<KbSearchHit>> SuggestAsync(string text, int limit = 5, CancellationToken ct = default)
    {
        if (Terms(text).Count == 0)
        {
            return [];
        }
        // A suggestion needs a title or tag hit, or several words in the body.
        var page = await SearchAsync(new KbSearchQuery { Text = text, Mode = KbSearchMode.Published, PageSize = Math.Clamp(limit, 1, 10), MinScore = 4 }, ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<KbSearchHit>> PopularAsync(int limit = 6, CancellationToken ct = default) =>
        (await SearchAsync(new KbSearchQuery { Mode = KbSearchMode.Published, Sort = KbSort.Popular, PageSize = limit }, ct)).Items;

    public async Task<IReadOnlyList<KbReviewItem>> ReviewsAwaitingMeAsync(CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        if (viewer.UserId is not { } me && !viewer.IsInternal)
        {
            return [];
        }
        var folders = (await visibility.AgentFolderIdsAsync(ct)).ToList();
        var index = await visibility.FoldersAsync(ct);
        var pending = await (
                from r in Reviews.AsNoTracking()
                join a in Articles.AsNoTracking() on r.ArticleId equals a.Id
                join v in Versions.AsNoTracking() on r.VersionId equals v.Id
                where r.Decision == KbReviewDecision.Pending && a.DeletedAt == null && folders.Contains(a.FolderId)
                orderby r.SubmittedAt
                select new { r.ArticleId, a.Number, v.Title, a.FolderId, a.WorkspaceId, r.SubmittedById, r.SubmittedAt, r.SubmitNote })
            .ToListAsync(ct);
        var mine = pending.Where(p => CanDecide(index.Folders[p.FolderId], p.WorkspaceId, p.SubmittedById, viewer)).ToList();
        var ids = mine.Select(p => p.SubmittedById).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return mine.Select(p => new KbReviewItem(p.ArticleId, p.Number, p.Title, index.PathOf(p.FolderId),
                p.SubmittedById is { } s && names.TryGetValue(s, out var n) ? n : null, p.SubmittedAt, p.SubmitNote))
            .ToList();
    }

    public async Task<IReadOnlyList<KbArticleLink>> PickAsync(string text, Guid? exclude, CancellationToken ct = default)
    {
        var page = await SearchAsync(new KbSearchQuery { Text = text, Mode = KbSearchMode.Agent, PageSize = 8 }, ct);
        return page.Items.Where(i => i.Id != exclude && i.Status != KbArticleStatus.Archived)
            .Select(i => new KbArticleLink(i.Id, i.Number, i.Title))
            .ToList();
    }

    // ================================================================ feedback, views, deflection

    public async Task RecordViewAsync(Guid articleId, CancellationToken ct = default)
    {
        var visible = (await visibility.VisibleFolderIdsAsync(ct)).ToList();
        var userId = actors.Current.UserId;
        await Articles
            .Where(a => a.Id == articleId && a.Status == KbArticleStatus.Published && a.DeletedAt == null
                && visible.Contains(a.FolderId) && (userId == null || a.AuthorId != userId))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ViewCount, a => a.ViewCount + 1), ct);
    }

    public async Task RecordDeflectionCandidateAsync(Guid articleId, CancellationToken ct = default)
    {
        if (actors.Current.UserId is not { } userId)
        {
            return;
        }
        _ = await GetPublishedAsync(articleId, ct);
        var since = time.GetUtcNow() - KbDeflection.Window;
        if (await db.Set<KbDeflection>().AnyAsync(d => d.UserId == userId && d.ArticleId == articleId && d.ViewedAt >= since && d.TicketId == null, ct))
        {
            return;
        }
        db.Set<KbDeflection>().Add(new KbDeflection { ArticleId = articleId, UserId = userId, ViewedAt = time.GetUtcNow() });
        await SaveAsync(ct);
    }

    public async Task<KbArticleFeedback> VoteAsync(Guid articleId, bool helpful, string? comment, string? visitorKey, CancellationToken ct = default)
    {
        var article = await GetPublishedAsync(articleId, ct);
        var userId = actors.Current.UserId;
        if (userId is null && string.IsNullOrWhiteSpace(visitorKey))
        {
            throw new ValidationException("vote", "Sign in to rate this article.");
        }
        if (userId is null && !string.IsNullOrWhiteSpace(comment))
        {
            throw new ValidationException("comment", "Sign in to leave a comment.");
        }
        var key = userId is null ? Trim(visitorKey, 64) : null;
        var set = db.Set<KbArticleFeedback>();
        var feedback = userId is { } uid
            ? await set.FirstOrDefaultAsync(f => f.ArticleId == article.Id && f.UserId == uid, ct)
            : await set.FirstOrDefaultAsync(f => f.ArticleId == article.Id && f.VisitorKey == key, ct);
        var now = time.GetUtcNow();
        if (feedback is null)
        {
            feedback = new KbArticleFeedback { ArticleId = article.Id, UserId = userId, VisitorKey = key, CreatedAt = now };
            set.Add(feedback);
        }
        feedback.Helpful = helpful;
        if (comment is not null)
        {
            feedback.Comment = string.IsNullOrWhiteSpace(comment) ? null : Trim(comment.Trim(), 2000);
        }
        feedback.UpdatedAt = now;
        await SaveAsync(ct);
        return feedback;
    }

    public async Task<KbArticleFeedback?> MyVoteAsync(Guid articleId, string? visitorKey, CancellationToken ct = default)
    {
        var userId = actors.Current.UserId;
        var set = db.Set<KbArticleFeedback>().AsNoTracking();
        if (userId is { } uid)
        {
            return await set.FirstOrDefaultAsync(f => f.ArticleId == articleId && f.UserId == uid, ct);
        }
        return string.IsNullOrWhiteSpace(visitorKey) ? null : await set.FirstOrDefaultAsync(f => f.ArticleId == articleId && f.VisitorKey == visitorKey, ct);
    }

    public async Task<IReadOnlyList<KbFeedbackComment>> FeedbackCommentsAsync(Guid articleId, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var article = await LoadAgentArticleAsync(articleId, viewer, tracked: false, includeTrashed: true, ct: ct);
        return await (
                from f in db.Set<KbArticleFeedback>().AsNoTracking()
                join u in db.Users.AsNoTracking() on f.UserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                where f.ArticleId == article.Id && f.Comment != null
                orderby f.UpdatedAt descending
                select new KbFeedbackComment(f.Id, u != null ? u.DisplayName : null, f.Helpful, f.Comment!, f.UpdatedAt))
            .Take(50)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<KbTrashItem>> TrashedArticlesAsync(CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var index = await visibility.FoldersAsync(ct);
        var folders = index.Folders.Values.Where(f => viewer.IsKbAgent(f.WorkspaceId) && visibility.CanSee(f, viewer)).Select(f => f.Id).ToList();
        var rows = await Articles.AsNoTracking()
            .Where(a => a.DeletedAt != null && folders.Contains(a.FolderId))
            .OrderByDescending(a => a.DeletedAt)
            .ToListAsync(ct);
        var ids = rows.Select(r => r.DeletedById).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return rows.Select(a => new KbTrashItem(
                "article", a.Id, $"{a.DisplayId} {a.Title}", index.PathOf(a.FolderId), a.DeletedAt!.Value,
                a.DeletedById is { } d && names.TryGetValue(d, out var n) ? n : null,
                CanTrash(a, viewer), viewer.CanManage(a.WorkspaceId)))
            .ToList();
    }

    // ================================================================ rules

    private async Task<KbArticleAccess> AccessAsync(KbArticle article, KbArticleVersion latest, KbFolderNode folder, KbViewer viewer, KbArticleReview? pending, CancellationToken ct)
    {
        pending ??= latest.State == KbVersionState.InReview ? await PendingReviewAsync(article.Id, tracked: false, ct) : null;
        var ws = article.WorkspaceId;
        var me = actors.Current.UserId;
        var live = article.DeletedAt is null;
        var write = live && viewer.CanWrite(ws);
        var publish = live && viewer.CanPublish(ws);
        var hasDraft = latest.State == KbVersionState.Draft;
        var canDecide = live && pending is not null && CanDecide(folder, ws, pending.SubmittedById, viewer);
        return new KbArticleAccess(
            CanEdit: write && article.Status != KbArticleStatus.Archived && latest.State != KbVersionState.InReview,
            CanSubmit: write && hasDraft && article.Status != KbArticleStatus.Archived,
            CanPublish: publish && hasDraft && article.Status != KbArticleStatus.Archived && (!folder.RequiresApproval || viewer.IsInternal),
            CanApprove: canDecide,
            CanWithdraw: live && pending is not null && (pending.SubmittedById == me || canDecide || publish),
            CanArchive: publish && article.Status == KbArticleStatus.Published,
            CanUnarchive: publish && article.Status == KbArticleStatus.Archived,
            CanDiscardDraft: write && hasDraft && article.PublishedVersionId is not null && latest.Id != article.PublishedVersionId,
            CanTrash: live && CanTrash(article, viewer),
            CanRestore: !live && CanTrash(article, viewer),
            CanPurge: !live && viewer.CanManage(ws));
    }

    /// <summary>Folder approvers decide; without approvers, anyone who can publish. Never your own submission.</summary>
    private bool CanDecide(KbFolderNode folder, Guid workspaceId, Guid? submittedById, KbViewer viewer)
    {
        if (viewer.IsInternal)
        {
            return true;
        }
        if (viewer.UserId is not { } me || !viewer.IsKbAgent(workspaceId))
        {
            return false;
        }
        if (submittedById == me)
        {
            return false;
        }
        return folder.ApproverIds.Count > 0 ? folder.ApproverIds.Contains(me) || viewer.IsOrgAdmin : viewer.CanPublish(workspaceId);
    }

    private bool CanTrash(KbArticle article, KbViewer viewer) =>
        viewer.CanPublish(article.WorkspaceId)
        || (viewer.CanWrite(article.WorkspaceId) && article.AuthorId == actors.Current.UserId && article.PublishedAt is null);

    private static void ValidateForPublishing(KbArticle article, KbArticleVersion version)
    {
        if (string.IsNullOrWhiteSpace(version.BodyText) && string.IsNullOrWhiteSpace(article.ExternalUrl)
            && !version.BodyHtml.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("bodyHtml", "Write the article, or point it to an external page, before publishing.");
        }
    }

    private async Task PublishVersionAsync(KbArticle article, KbArticleVersion version, string summary, CancellationToken ct)
    {
        ValidateForPublishing(article, version);
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Demote the live version first: the unique index allows one published version per article (INV-KB-1).
        await Versions.Where(v => v.ArticleId == article.Id && v.State == KbVersionState.Published && v.Id != version.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.State, KbVersionState.Previous), ct);
        version.State = KbVersionState.Published;
        version.Revision++;
        version.PublishedAt = now;
        version.PublishedById = actors.Current.UserId;
        article.PublishedVersionId = version.Id;
        article.LatestVersionId = version.Id;
        article.Status = KbArticleStatus.Published;
        article.PublishedAt = now;
        article.Title = version.Title;
        db.Activity(article, "published", summary);
        db.Raise(new KbArticlePublished(article.Id, article.WorkspaceId, version.Id, version.Number));
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static void ReturnToDraft(KbArticle article, KbArticleVersion version)
    {
        version.State = KbVersionState.Draft;
        version.Revision++;
        if (article.Status == KbArticleStatus.InReview)
        {
            article.Status = KbArticleStatus.Draft;
        }
    }

    // ================================================================ helpers

    private async Task<KbArticle> LoadAgentArticleAsync(Guid id, KbViewer viewer, bool tracked, bool includeTrashed = false, CancellationToken ct = default)
    {
        var folders = await visibility.AgentFolderIdsAsync(ct);
        IQueryable<KbArticle> query = tracked ? Articles : Articles.AsNoTracking();
        var article = await query.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (article is null || (!includeTrashed && article.DeletedAt is not null))
        {
            throw new NotFoundException("That article doesn't exist or you can't see it.");
        }
        if (!folders.Contains(article.FolderId))
        {
            // Trashed folders drop out of the visible set; trash management still needs the article.
            var index = await visibility.FoldersAsync(ct);
            var folder = index.Get(article.FolderId);
            var allowed = includeTrashed && folder is not null && viewer.IsKbAgent(folder.WorkspaceId) && visibility.CanSee(folder, viewer);
            if (!allowed)
            {
                throw new NotFoundException("That article doesn't exist or you can't see it.");
            }
        }
        return article;
    }

    private async Task<(KbArticleVersion Latest, KbFolderNode Folder)> LatestAndFolderAsync(KbArticle article, CancellationToken ct)
    {
        var latest = await Versions.FirstAsync(v => v.Id == article.LatestVersionId, ct);
        var folder = (await visibility.FoldersAsync(ct)).Folders[article.FolderId];
        return (latest, folder);
    }

    private async Task<KbFolderNode> WritableFolderAsync(Guid folderId, KbViewer viewer, CancellationToken ct)
    {
        var folders = await visibility.AgentFolderIdsAsync(ct);
        var index = await visibility.FoldersAsync(ct);
        if (!folders.Contains(folderId) || index.Get(folderId) is not { } folder)
        {
            throw new ValidationException("folderId", "Choose a folder for the article.");
        }
        Demand(viewer.CanWrite(folder.WorkspaceId));
        return folder;
    }

    private Task<KbArticleReview?> PendingReviewAsync(Guid articleId, bool tracked, CancellationToken ct)
    {
        IQueryable<KbArticleReview> query = tracked ? Reviews : Reviews.AsNoTracking();
        return query.Where(r => r.ArticleId == articleId && r.Decision == KbReviewDecision.Pending)
            .OrderByDescending(r => r.SubmittedAt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<int> NextVersionNumberAsync(Guid articleId, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<KbArticleVersion>()
            .Where(e => e.Entity.ArticleId == articleId && e.State == EntityState.Added)
            .Select(e => e.Entity.Number)
            .DefaultIfEmpty(0)
            .Max();
        var stored = await Versions.Where(v => v.ArticleId == articleId).MaxAsync(v => (int?)v.Number, ct) ?? 0;
        return Math.Max(tracked, stored) + 1;
    }

    private void SetContent(KbArticleVersion version, string title, string sanitizedBody, Guid? userId, DateTimeOffset now)
    {
        version.Title = title;
        version.BodyHtml = sanitizedBody;
        version.BodyText = html.ToPlainText(sanitizedBody);
        version.Excerpt = KbHtml.Excerpt(version.BodyText, 280);
        version.UpdatedById = userId;
        version.UpdatedAt = now;
    }

    private async Task ApplyMetadataAsync(KbArticle article, KbDraftInput input, KbViewer viewer, CancellationToken ct)
    {
        article.Type = Enum.IsDefined(input.Type) ? input.Type : KbArticleType.Permanent;
        article.Tags = (input.Tags ?? [])
            .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length is > 0 and <= 40)
            .Distinct()
            .Take(20)
            .ToList();
        article.Keywords = Trim(input.Keywords, 500);
        article.SearchTerms = Trim(string.Join(' ', article.Tags) + " " + (article.Keywords ?? ""), 1200) ?? "";
        article.ReviewBy = input.ReviewBy;
        article.SeoTitle = Trim(input.SeoTitle, 200);
        article.SeoDescription = Trim(input.SeoDescription, 500);

        var url = input.ExternalUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            article.ExternalUrl = null;
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || url.Length > 1000)
        {
            throw new ValidationException("externalUrl", "Enter a full web address that starts with https://.");
        }
        else
        {
            article.ExternalUrl = uri.ToString();
        }

        if (input.OwnerId is { } owner && owner != article.OwnerId)
        {
            var ok = await db.Users.AnyAsync(u => u.Id == owner && u.IsActive && (u.IsAgent || u.IsOrgAdmin), ct);
            if (!ok)
            {
                throw new ValidationException("ownerId", "Choose an active agent as the owner.");
            }
            article.OwnerId = owner;
        }

        if (input.RelatedArticleIds is { } related)
        {
            var agentFolders = (await visibility.AgentFolderIdsAsync(ct)).ToList();
            var wanted = related.Where(id => id != article.Id).Distinct().Take(20).ToList();
            var valid = await Articles.Where(a => wanted.Contains(a.Id) && a.WorkspaceId == article.WorkspaceId && a.DeletedAt == null && agentFolders.Contains(a.FolderId))
                .Select(a => a.Id).ToListAsync(ct);
            var set = db.Set<KbArticleRelation>();
            var existing = await set.Where(r => r.ArticleId == article.Id).ToListAsync(ct);
            set.RemoveRange(existing.Where(r => !valid.Contains(r.RelatedArticleId)));
            foreach (var id in valid.Where(id => existing.All(r => r.RelatedArticleId != id)))
            {
                set.Add(new KbArticleRelation { ArticleId = article.Id, RelatedArticleId = id });
            }
        }
        _ = viewer;
    }

    private async Task ClaimFilesAsync(Guid articleId, string body, IReadOnlyList<Guid>? attachmentIds, CancellationToken ct)
    {
        var inline = KbHtml.ReferencedFileIds(body);
        var claimed = await attachments.ClaimPendingAsync(inline, KbAttachmentOwners.Article, articleId, ct);
        foreach (var file in claimed)
        {
            file.IsInline = true;
        }
        if (attachmentIds is { Count: > 0 })
        {
            var files = await attachments.ClaimPendingAsync(attachmentIds.Except(inline), KbAttachmentOwners.Article, articleId, ct);
            foreach (var file in files)
            {
                file.IsInline = false;
            }
        }
    }

    private async Task<IReadOnlyList<string>> DetachFilesAsync(Guid articleId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var rows = await db.Attachments
            .Where(a => a.OwnerType == KbAttachmentOwners.Article && a.OwnerId == articleId && !a.IsInline && ids.Contains(a.Id))
            .ToListAsync(ct);
        db.Attachments.RemoveRange(rows);
        return rows.Select(r => r.StorageKey).ToList();
    }

    private async Task LinkTicketAsync(Guid ticketId, Guid articleId, CancellationToken ct)
    {
        try
        {
            await services.GetRequiredService<ITicketService>().LinkAsync(ticketId, KbArticle.Family, articleId, TicketLinkTypes.Related, ct);
        }
        catch (Exception ex) when (ex is ForbiddenException or ValidationException or NotFoundException)
        {
            // The article keeps SourceTicketId; the ticket-side link needs tickets.edit.
        }
    }

    private async Task<IReadOnlyList<KbArticleLink>> RelatedAsync(Guid articleId, KbSearchMode mode, CancellationToken ct)
    {
        var folders = (mode == KbSearchMode.Agent ? await visibility.AgentFolderIdsAsync(ct) : await visibility.VisibleFolderIdsAsync(ct)).ToList();
        var relations = db.Set<KbArticleRelation>().AsNoTracking();
        var ids = await relations.Where(r => r.ArticleId == articleId).Select(r => r.RelatedArticleId)
            .Union(relations.Where(r => r.RelatedArticleId == articleId).Select(r => r.ArticleId))
            .ToListAsync(ct);
        if (ids.Count == 0)
        {
            return [];
        }
        if (mode == KbSearchMode.Agent)
        {
            return await Articles.AsNoTracking()
                .Where(a => ids.Contains(a.Id) && a.DeletedAt == null && folders.Contains(a.FolderId))
                .OrderBy(a => a.Title)
                .Select(a => new KbArticleLink(a.Id, a.Number, a.Title))
                .ToListAsync(ct);
        }
        return await (
                from a in Articles.AsNoTracking()
                join v in Versions.AsNoTracking() on a.PublishedVersionId equals v.Id
                where ids.Contains(a.Id) && a.DeletedAt == null && a.Status == KbArticleStatus.Published
                    && v.State == KbVersionState.Published && folders.Contains(a.FolderId)
                orderby v.Title
                select new KbArticleLink(a.Id, a.Number, v.Title))
            .ToListAsync(ct);
    }

    private async Task<KbFeedbackSummary> FeedbackSummaryAsync(Guid articleId, int views, CancellationToken ct)
    {
        var votes = await db.Set<KbArticleFeedback>().AsNoTracking()
            .Where(f => f.ArticleId == articleId)
            .GroupBy(f => f.Helpful)
            .Select(g => new { Helpful = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var settled = time.GetUtcNow() - KbDeflection.Window;
        var deflections = await db.Set<KbDeflection>().AsNoTracking()
            .CountAsync(d => d.ArticleId == articleId && d.TicketId == null && d.ViewedAt <= settled, ct);
        return new KbFeedbackSummary(
            votes.FirstOrDefault(v => v.Helpful)?.Count ?? 0,
            votes.FirstOrDefault(v => !v.Helpful)?.Count ?? 0,
            deflections,
            views);
    }

    private async Task<string?> UserNameAsync(Guid? id, CancellationToken ct) =>
        id is { } uid ? await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new ConflictException("Someone else changed this article at the same time. Reload to see the latest version.");
        }
    }

    private static void Demand(bool allowed, string? reason = null)
    {
        if (!allowed)
        {
            throw reason is null ? new ForbiddenException() : new ValidationException("article", reason);
        }
    }

    private static string ValidTitle(string? title)
    {
        var t = Regex.Replace(title ?? "", @"\s+", " ").Trim();
        if (t.Length == 0)
        {
            throw new ValidationException("title", "Give the article a title.");
        }
        if (t.Length > 300)
        {
            throw new ValidationException("title", "Keep the title under 300 characters.");
        }
        return t;
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var t = value.Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>Search terms: lower-case words without stop words, at most eight.</summary>
    internal static IReadOnlyList<string> Terms(string? text) =>
        WordPattern().Matches((text ?? "").ToLowerInvariant())
            .Select(m => m.Value.Replace("'", ""))
            .Where(w => w.Length >= 2 && !StopWords.Contains(w))
            .Distinct()
            .Take(8)
            .ToList();

    /// <summary>
    /// Ranking: a title containing the whole phrase first, then title words, then tags and keywords,
    /// then body text. LIKE-based; good enough until full-text search arrives.
    /// </summary>
    private static Expression<Func<KbSearchRow, KbScoredRow>> ScoreExpression(IReadOnlyList<string> terms, string phrase)
    {
        var row = Expression.Parameter(typeof(KbSearchRow), "r");
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        Expression Has(string member, string value)
        {
            // A captured holder makes EF send the term as a parameter instead of inlining it.
            var holder = Expression.Property(Expression.Constant(new TermHolder(value)), nameof(TermHolder.Value));
            return Expression.Call(Expression.Property(row, member), contains, holder);
        }
        Expression Weight(Expression test, int weight) => Expression.Condition(test, Expression.Constant(weight), Expression.Constant(0));

        Expression score = Expression.Constant(0);
        var cleanPhrase = Regex.Replace(phrase.Trim().ToLowerInvariant(), @"\s+", " ");
        if (terms.Count > 1 && cleanPhrase.Length is > 3 and <= 120)
        {
            score = Expression.Add(score, Weight(Has(nameof(KbSearchRow.Title), cleanPhrase), 40));
        }
        foreach (var term in terms)
        {
            score = Expression.Add(score, Weight(Has(nameof(KbSearchRow.Title), term), 10));
            score = Expression.Add(score, Weight(Has(nameof(KbSearchRow.Terms), term), 6));
            score = Expression.Add(score, Weight(Has(nameof(KbSearchRow.Body), term), 2));
        }
        var init = Expression.MemberInit(
            Expression.New(typeof(KbScoredRow)),
            Expression.Bind(typeof(KbScoredRow).GetProperty(nameof(KbScoredRow.Row))!, row),
            Expression.Bind(typeof(KbScoredRow).GetProperty(nameof(KbScoredRow.Score))!, score));
        return Expression.Lambda<Func<KbSearchRow, KbScoredRow>>(init, row);
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'+#.-]*[\p{L}\p{N}+#]|[\p{L}\p{N}]")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"^(?:KB-?)?(\d{1,9})$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberReference();

    private sealed record TermHolder(string Value);
}

/// <summary>Flattened article plus one version, the shape search runs on.</summary>
public sealed class KbSearchRow
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string Terms { get; set; } = "";
    public Guid FolderId { get; set; }
    public KbArticleStatus Status { get; set; }
    public KbVersionState LatestState { get; set; }
    public KbArticleType Type { get; set; }
    public Guid? OwnerId { get; set; }
    public Guid? AuthorId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateOnly? ReviewBy { get; set; }
    public int Views { get; set; }
    public string? ExternalUrl { get; set; }
}

public sealed class KbScoredRow
{
    public KbSearchRow Row { get; set; } = default!;
    public int Score { get; set; }
}
