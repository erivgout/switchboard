using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Categories;

public sealed record CategoryNodesModel(IReadOnlyList<CategoryNode> Nodes, Guid? ParentId, string Ws);

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.FieldsManage)]
public sealed class IndexModel(TicketConfigService config, SwitchboardDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Ws { get; set; }

    [BindProperty]
    public string? Name { get; set; }

    public Workspace Workspace { get; private set; } = default!;
    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];
    public IReadOnlyList<CategoryNode> Tree { get; private set; } = [];

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (await TryAsync(() => config.CreateCategoryAsync(Workspace.Id, null, Name ?? ""), ""))
        {
            TempData.Toast("Category added");
            return Redirect(Link("/admin/categories", ("ws", Ws)));
        }
        return Page();
    }

    /// <summary>Drag-and-drop order from admin.js (sends the antiforgery header).</summary>
    public async Task<IActionResult> OnPostReorderAsync(Guid[] ids, Guid? parentId)
    {
        await LoadAsync();
        await config.ReorderCategoriesAsync(Workspace.Id, parentId, ids);
        return new NoContentResult();
    }

    public async Task<IActionResult> OnPostMoveAsync(Guid categoryId, int delta)
    {
        await config.MoveCategoryAsync(categoryId, delta);
        return Redirect(Link("/admin/categories", ("ws", Ws)) + "#cat-" + categoryId);
    }

    private async Task LoadAsync()
    {
        (Workspace, Workspaces) = await ResolveWorkspaceAsync(db, Ws);
        Ws = Workspace.Key;
        Tree = await config.CategoryTreeAsync(Workspace.Id);
    }
}
