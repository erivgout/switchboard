using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Admin.Pages.Categories;

[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + Privileges.FieldsManage)]
public sealed class EditModel(TicketConfigService config, SwitchboardDbContext db) : AdminPageModel
{
    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public string? ChildName { get; set; }

    /// <summary>Replacement category id, or <c>clear</c> to leave tickets without this level.</summary>
    [BindProperty]
    public string? Replacement { get; set; }

    public TicketCategory Category { get; private set; } = default!;
    public CategoryNode? Node { get; private set; }
    public IReadOnlyList<(TicketCategory Category, string Path)> Path { get; private set; } = [];
    public IReadOnlyList<(Guid Id, string Label)> SameLevel { get; private set; } = [];
    public string WorkspaceKey { get; private set; } = "";
    public string LevelName => Category.Level switch { 1 => "category", 2 => "sub-category", _ => "item" };

    public async Task OnGetAsync(Guid id)
    {
        await LoadAsync(id);
        Name = Category.Name;
    }

    public async Task<IActionResult> OnPostRenameAsync(Guid id)
    {
        await LoadAsync(id);
        if (await TryAsync(() => config.RenameCategoryAsync(id, Name ?? ""), ""))
        {
            TempData.Toast("Category renamed");
            return Redirect($"/admin/categories/{id}");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(Guid id)
    {
        await LoadAsync(id);
        if (await TryAsync(() => config.CreateCategoryAsync(Category.WorkspaceId, id, ChildName ?? ""), "Child"))
        {
            TempData.Toast("Added");
            return Redirect($"/admin/categories/{id}");
        }
        Name = Category.Name;
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        await LoadAsync(id);
        var clear = Replacement == "clear";
        Guid? replacement = Guid.TryParse(Replacement, out var r) ? r : null;
        var moved = 0;
        if (await TryAsync(async () => moved = await config.DeleteCategoryAsync(id, replacement, clear), ""))
        {
            TempData.Toast(moved > 0 ? $"Deleted. {moved} {(moved == 1 ? "ticket" : "tickets")} updated." : "Category deleted");
            return Redirect(Link("/admin/categories", ("ws", WorkspaceKey)));
        }
        Name = Category.Name;
        return Page();
    }

    private async Task LoadAsync(Guid id)
    {
        Category = await config.GetCategoryAsync(id);
        WorkspaceKey = await db.Workspaces.Where(w => w.Id == Category.WorkspaceId).Select(w => w.Key).FirstAsync();
        var tree = await config.CategoryTreeAsync(Category.WorkspaceId);
        var flat = new List<(CategoryNode Node, string Path)>();
        void Walk(IEnumerable<CategoryNode> nodes, string prefix)
        {
            foreach (var n in nodes)
            {
                var path = prefix.Length == 0 ? n.Category.Name : $"{prefix} › {n.Category.Name}";
                flat.Add((n, path));
                Walk(n.Children, path);
            }
        }
        Walk(tree, "");
        Node = flat.FirstOrDefault(f => f.Node.Category.Id == id).Node;
        SameLevel = flat.Where(f => f.Node.Category.Level == Category.Level && f.Node.Category.Id != id).Select(f => (f.Node.Category.Id, f.Path)).ToList();
        var ancestors = new List<(TicketCategory, string)>();
        var current = Category;
        while (current.ParentId is { } parent && flat.FirstOrDefault(f => f.Node.Category.Id == parent) is { Node: not null } p)
        {
            ancestors.Insert(0, (p.Node.Category, p.Path));
            current = p.Node.Category;
        }
        Path = ancestors;
    }
}
