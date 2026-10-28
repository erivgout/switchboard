using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Services;
using Switchboard.Modules.Knowledge.Domain;

namespace Switchboard.Modules.Knowledge.Services;

/// <summary>
/// Who may download article files: agents who can work on the article, and readers of the published
/// version. Inline images must appear in the published body, so draft-only images stay private.
/// </summary>
public sealed class KbArticleAttachmentPolicy(SwitchboardDbContext db, IKnowledgeVisibility visibility) : IAttachmentAccessPolicy
{
    public string OwnerType => KbAttachmentOwners.Article;

    public async Task<bool> CanReadAsync(Attachment attachment, CancellationToken ct)
    {
        if (attachment.OwnerId is not { } articleId)
        {
            return false;
        }
        var article = await db.Set<KbArticle>().AsNoTracking()
            .Where(a => a.Id == articleId)
            .Select(a => new { a.FolderId, a.Status, a.DeletedAt, a.PublishedVersionId })
            .FirstOrDefaultAsync(ct);
        if (article is null)
        {
            return false;
        }
        if ((await visibility.AgentFolderIdsAsync(ct)).Contains(article.FolderId))
        {
            return true;
        }
        if (article.DeletedAt is not null || article.Status != KbArticleStatus.Published
            || !(await visibility.VisibleFolderIdsAsync(ct)).Contains(article.FolderId))
        {
            return false;
        }
        if (!attachment.IsInline)
        {
            return true;
        }
        var id = attachment.Id.ToString();
        return await db.Set<KbArticleVersion>().AsNoTracking()
            .AnyAsync(v => v.Id == article.PublishedVersionId && v.State == KbVersionState.Published && v.BodyHtml.Contains(id), ct);
    }
}

/// <summary>Announcement images: anyone signed in who could be in an audience.</summary>
public sealed class KbAnnouncementAttachmentPolicy(SwitchboardDbContext db, IKnowledgeVisibility visibility, TimeProvider time) : IAttachmentAccessPolicy
{
    public string OwnerType => KbAttachmentOwners.Announcement;

    public async Task<bool> CanReadAsync(Attachment attachment, CancellationToken ct)
    {
        if (attachment.OwnerId is not { } id)
        {
            return false;
        }
        var viewer = await visibility.ViewerAsync(ct);
        var item = await db.Set<KbAnnouncement>().AsNoTracking().Include(a => a.Targets).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (item is null)
        {
            return false;
        }
        if (viewer.CanManage(item.WorkspaceId))
        {
            return true;
        }
        var now = time.GetUtcNow();
        var live = item.IsPublished && item.ActiveFrom <= now && (item.ActiveTo == null || item.ActiveTo > now);
        return live && (KbAnnouncementService.Shows(item, viewer, asAgent: true) || KbAnnouncementService.Shows(item, viewer, asAgent: false));
    }
}

/// <summary>
/// A requester who files a ticket soon after reading a suggested article was not deflected
/// (FR-POR-7). Idempotent: only unmatched candidates are updated.
/// </summary>
public sealed class KbDeflectionTicketHandler(SwitchboardDbContext db) : IEventHandler<TicketCreated>
{
    public async Task HandleAsync(TicketCreated domainEvent, CancellationToken cancellationToken)
    {
        var ticket = await db.Tickets.AsNoTracking()
            .Where(t => t.Id == domainEvent.TicketId)
            .Select(t => new { t.Id, t.RequesterId, t.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (ticket is null)
        {
            return;
        }
        var since = ticket.CreatedAt - KbDeflection.Window;
        await db.Set<KbDeflection>()
            .Where(d => d.UserId == ticket.RequesterId && d.TicketId == null && d.ViewedAt >= since && d.ViewedAt <= ticket.CreatedAt)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.TicketId, ticket.Id), cancellationToken);
    }
}
