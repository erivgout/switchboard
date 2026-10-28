using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.View)]
public sealed class TrashModel(IKnowledgeService knowledge, IKbStructureService structure) : PageModel
{
    public IReadOnlyList<KbTrashItem> Items { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var articles = await knowledge.TrashedArticlesAsync(ct);
        var structureItems = await structure.TrashedAsync(ct);
        Items = structureItems.Concat(articles).OrderByDescending(i => i.DeletedAt).ToList();
    }

    public Task<IActionResult> OnPostRestoreAsync(List<string> items, CancellationToken ct) =>
        RunAsync(items, restore: true, ct);

    public Task<IActionResult> OnPostPurgeAsync(List<string> items, CancellationToken ct) =>
        RunAsync(items, restore: false, ct);

    private async Task<IActionResult> RunAsync(List<string> items, bool restore, CancellationToken ct)
    {
        var parsed = items
            .Select(i => i.Split(':', 2))
            .Where(p => p.Length == 2 && Guid.TryParse(p[1], out _))
            .GroupBy(p => p[0], p => Guid.Parse(p[1]))
            .ToDictionary(g => g.Key, g => g.ToList());
        if (parsed.Count == 0)
        {
            TempData.Toast("Select something first.", ToastKind.Error);
            return RedirectToPage();
        }
        try
        {
            var count = 0;
            foreach (var kind in new[] { KbStructureService.Category, KbStructureService.Folder })
            {
                if (parsed.TryGetValue(kind, out var ids))
                {
                    count += restore ? await structure.RestoreAsync(kind, ids, ct) : await structure.PurgeAsync(kind, ids, ct);
                }
            }
            if (parsed.TryGetValue("article", out var articleIds))
            {
                count += restore ? await knowledge.RestoreAsync(articleIds, ct) : await knowledge.PurgeAsync(articleIds, ct);
            }
            TempData.Toast(restore
                ? $"{KbUi.Plural(count, "item", "items")} restored"
                : $"{KbUi.Plural(count, "item", "items")} deleted permanently");
        }
        catch (ValidationException ex)
        {
            TempData.Toast(ex.Message, ToastKind.Error);
        }
        catch (NotFoundException)
        {
            TempData.Toast("Some of those items were already restored or deleted. Reload to see the trash now.", ToastKind.Error);
        }
        return RedirectToPage();
    }
}
