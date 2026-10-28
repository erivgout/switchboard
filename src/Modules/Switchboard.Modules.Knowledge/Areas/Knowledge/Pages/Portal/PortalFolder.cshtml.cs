using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Services;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages.Portal;

/// <summary>Every published article in a folder and its sub-folders.</summary>
[AllowAnonymous]
public sealed class PortalFolderModel(IKnowledgeService knowledge, IKnowledgeVisibility visibility) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public KbFolderNode Folder { get; private set; } = default!;
    public string CategoryName { get; private set; } = "";
    public IReadOnlyList<KbSearchHit> Articles { get; private set; } = [];
    public bool SignedIn { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var viewer = await visibility.ViewerAsync(ct);
        SignedIn = viewer.SignedIn;
        var index = await visibility.FoldersAsync(ct);
        var visible = await visibility.VisibleFolderIdsAsync(ct);
        if (!visible.Contains(Id) || index.Get(Id) is not { } folder)
        {
            if (!SignedIn)
            {
                return Challenge();
            }
            throw new NotFoundException("That folder doesn't exist or you can't see it.");
        }
        Folder = folder;
        CategoryName = index.Categories.GetValueOrDefault(folder.CategoryId)?.Name ?? "";
        Articles = (await knowledge.SearchAsync(new KbSearchQuery { Mode = KbSearchMode.Published, FolderId = Id, Sort = KbSort.Title, PageSize = 100 }, ct)).Items;
        return Page();
    }
}
