using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.Manage)]
public sealed class FoldersModel(
    IKnowledgeVisibility visibility,
    IKbStructureService structure,
    ISettingsService settings,
    SwitchboardDbContext db) : PageModel
{
    public KbFolderIndex Index { get; private set; } = default!;
    public KbViewer Viewer { get; private set; } = default!;
    public IReadOnlyList<KbCategoryNode> Categories { get; private set; } = [];
    public IReadOnlyDictionary<Guid, int> Counts { get; private set; } = new Dictionary<Guid, int>();
    public IReadOnlyDictionary<Guid, string> Workspaces { get; private set; } = new Dictionary<Guid, string>();
    public bool PublicKb { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Viewer = await visibility.ViewerAsync(ct);
        Index = await visibility.FoldersAsync(ct);
        Categories = Index.Categories.Values
            .Where(c => !c.InTrash && Viewer.CanManage(c.WorkspaceId))
            .OrderBy(c => c.Order).ThenBy(c => c.Name)
            .ToList();
        Counts = await db.Set<KbArticle>().AsNoTracking()
            .Where(a => a.DeletedAt == null)
            .GroupBy(a => a.FolderId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        Workspaces = await db.Workspaces.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        PublicKb = await settings.GetAsync(KbSettingKeys.Public, false, ct: ct);
    }

    public async Task<IActionResult> OnPostPublicAsync(bool enabled, CancellationToken ct)
    {
        await structure.SetPublicAsync(enabled, ct);
        TempData.Toast(enabled ? "Signed-out visitors can now read public articles" : "Only signed-in people can read articles now");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMoveAsync(string kind, Guid id, int direction, CancellationToken ct)
    {
        await structure.MoveAsync(kind, id, direction, ct);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTrashAsync(string kind, Guid id, CancellationToken ct)
    {
        await structure.TrashAsync(kind, [id], ct);
        TempData.Toast(kind == KbStructureService.Category ? "Category moved to trash" : "Folder moved to trash");
        return RedirectToPage();
    }
}
