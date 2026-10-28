using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages.Portal;

public sealed record KbPortalArticle(Guid Id, string Title, int Views);

public sealed record KbPortalFolder(KbFolderNode Folder, IReadOnlyList<KbPortalArticle> Top, int Total);

public sealed record KbPortalCategory(KbCategoryNode Category, IReadOnlyList<KbPortalFolder> Folders);

/// <summary>Portal knowledge home and search (FR-POR-1). Open to signed-out visitors when the public KB is on.</summary>
[AllowAnonymous]
public sealed class PortalIndexModel(IKnowledgeService knowledge, IKnowledgeVisibility visibility, SwitchboardDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public KbSearchPage? Results { get; private set; }
    public IReadOnlyList<KbPortalCategory> Categories { get; private set; } = [];
    public IReadOnlyList<KbSearchHit> Popular { get; private set; } = [];
    public KbViewer Viewer { get; private set; } = default!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Viewer = await visibility.ViewerAsync(ct);
        if (!string.IsNullOrWhiteSpace(Q))
        {
            Results = await knowledge.SearchAsync(new KbSearchQuery
            {
                Text = Q,
                Mode = KbSearchMode.Published,
                Page = Math.Max(1, PageNumber),
                PageSize = 15,
            }, ct);
            return;
        }

        var index = await visibility.FoldersAsync(ct);
        var visible = await visibility.VisibleFolderIdsAsync(ct);
        var folderIds = visible.ToList();
        var articles = await (
                from a in db.Set<KbArticle>().AsNoTracking()
                join v in db.Set<KbArticleVersion>().AsNoTracking() on a.PublishedVersionId equals v.Id
                where a.DeletedAt == null && a.Status == KbArticleStatus.Published && v.State == KbVersionState.Published && folderIds.Contains(a.FolderId)
                select new { a.Id, a.FolderId, v.Title, a.ViewCount })
            .ToListAsync(ct);

        // A top-level folder lists its own articles and those of its sub-folders.
        var byRoot = articles.GroupBy(a => index.Folders[a.FolderId].RootId).ToDictionary(g => g.Key, g => g.ToList());
        Categories = index.Categories.Values
            .Where(c => !c.InTrash)
            .OrderBy(c => c.Order).ThenBy(c => c.Name)
            .Select(c => new KbPortalCategory(c, index.Folders.Values
                .Where(f => f.CategoryId == c.Id && f.ParentId is null && visible.Contains(f.Id) && byRoot.ContainsKey(f.Id))
                .OrderBy(f => f.Order).ThenBy(f => f.Name)
                .Select(f => new KbPortalFolder(f,
                    byRoot[f.Id].OrderByDescending(a => a.ViewCount).ThenBy(a => a.Title).Take(5).Select(a => new KbPortalArticle(a.Id, a.Title, a.ViewCount)).ToList(),
                    byRoot[f.Id].Count))
                .ToList()))
            .Where(c => c.Folders.Count > 0)
            .ToList();
        Popular = await knowledge.PopularAsync(5, ct);
    }

    public string PageUrl(int page) => $"/portal/kb?q={Uri.EscapeDataString(Q ?? "")}{(page > 1 ? $"&p={page}" : "")}";
}
