using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.View)]
public sealed class IndexModel(IKnowledgeService knowledge, IKnowledgeVisibility visibility, SwitchboardDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Folder { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Category { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Owner { get; set; }
    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public KbSearchPage Results { get; private set; } = default!;
    public KbFolderIndex Index { get; private set; } = default!;
    public KbViewer Viewer { get; private set; } = default!;
    public IReadOnlySet<Guid> AgentFolders { get; private set; } = new HashSet<Guid>();
    public IReadOnlyDictionary<Guid, int> FolderCounts { get; private set; } = new Dictionary<Guid, int>();
    public int AwaitingMe { get; private set; }
    public int NeedsReviewCount { get; private set; }
    public string Heading { get; private set; } = "All articles";
    public bool Filtered => !string.IsNullOrWhiteSpace(Q) || Folder is not null || Category is not null || !string.IsNullOrEmpty(Status) || !string.IsNullOrEmpty(Owner);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Viewer = await visibility.ViewerAsync(ct);
        Index = await visibility.FoldersAsync(ct);
        AgentFolders = await visibility.AgentFolderIdsAsync(ct);

        var query = new KbSearchQuery
        {
            Text = Q,
            Mode = KbSearchMode.Agent,
            FolderId = Folder,
            CategoryId = Category,
            Page = Math.Max(1, PageNumber),
            PageSize = 25,
            Sort = Sort switch
            {
                "updated" => KbSort.Updated,
                "popular" => KbSort.Popular,
                "title" => KbSort.Title,
                _ => KbSort.Relevance,
            },
        };
        if (Enum.TryParse<KbArticleStatus>(Status, true, out var status))
        {
            query.Status = status;
        }
        if (Owner == "me")
        {
            query.OwnerId = Viewer.UserId;
        }

        switch (View)
        {
            case "mine":
                query.OwnerId = Viewer.UserId;
                Heading = "Owned by me";
                break;
            case "drafts":
                query.AuthorId = Viewer.UserId;
                query.Status = KbArticleStatus.Draft;
                Heading = "My drafts";
                break;
            case "approval":
                query.AwaitingMyApproval = true;
                Heading = "Waiting for my approval";
                break;
            case "review":
                query.NeedsReview = true;
                Heading = "Due for review";
                break;
            case "archived":
                query.Status = KbArticleStatus.Archived;
                Heading = "Archived";
                break;
            default:
                View = null;
                break;
        }
        if (Folder is { } f && Index.Get(f) is { } folder)
        {
            Heading = Index.PathOf(folder.Id);
        }
        else if (Category is { } c && Index.Categories.GetValueOrDefault(c) is { } category)
        {
            Heading = category.Name;
        }
        if (!string.IsNullOrWhiteSpace(Q))
        {
            Heading = $"Results for “{Q.Trim()}”";
        }

        Results = await knowledge.SearchAsync(query, ct);

        var folders = AgentFolders.ToList();
        FolderCounts = await db.Set<KbArticle>().AsNoTracking()
            .Where(a => a.DeletedAt == null && folders.Contains(a.FolderId))
            .GroupBy(a => a.FolderId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        AwaitingMe = (await knowledge.ReviewsAwaitingMeAsync(ct)).Count;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        NeedsReviewCount = await db.Set<KbArticle>().AsNoTracking()
            .CountAsync(a => a.DeletedAt == null && folders.Contains(a.FolderId) && a.ReviewBy != null && a.ReviewBy <= today && a.Status != KbArticleStatus.Archived, ct);
    }

    public string PageLink(int? page = null, string? view = null, bool keepView = true) =>
        QueryUrl(new Dictionary<string, string?>
        {
            ["q"] = Q,
            ["folder"] = Folder?.ToString(),
            ["category"] = Category?.ToString(),
            ["status"] = Status,
            ["owner"] = Owner,
            ["view"] = keepView ? View : view,
            ["sort"] = Sort,
            ["p"] = page is > 1 ? page.ToString() : null,
        });

    private static string QueryUrl(Dictionary<string, string?> values)
    {
        var parts = values.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!)}").ToList();
        return parts.Count == 0 ? "/kb" : "/kb?" + string.Join("&", parts);
    }
}
