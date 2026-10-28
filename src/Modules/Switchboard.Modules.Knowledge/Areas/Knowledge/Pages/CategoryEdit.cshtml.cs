using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.UI;

namespace Switchboard.Modules.Knowledge.Areas.Knowledge.Pages;

[Authorize(Policy = "priv:" + KbPrivileges.Manage)]
public sealed class CategoryEditModel(IKnowledgeVisibility visibility, IKbStructureService structure, SwitchboardDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public Guid WorkspaceId { get; set; }

    public IReadOnlyList<KbOption> Workspaces { get; private set; } = [];
    public bool IsNew => Id is null;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (Id is { } id)
        {
            var category = await db.Set<KbCategory>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct)
                ?? throw new NotFoundException();
            if (!(await visibility.ViewerAsync(ct)).CanManage(category.WorkspaceId))
            {
                throw new ForbiddenException();
            }
            Name = category.Name;
            Description = category.Description;
            WorkspaceId = category.WorkspaceId;
        }
        else
        {
            WorkspaceId = Workspaces.FirstOrDefault()?.Id ?? Guid.Empty;
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            if (Id is { } id)
            {
                await structure.UpdateCategoryAsync(id, Name, Description, ct);
                TempData.Toast("Category saved");
            }
            else
            {
                await structure.CreateCategoryAsync(WorkspaceId, Name, Description, ct);
                TempData.Toast("Category created");
            }
            return Redirect("/kb/folders");
        }
        catch (ValidationException ex)
        {
            this.AddErrors(ex);
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var all = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name)
            .Select(w => new KbOption(w.Id, w.Name)).ToListAsync(ct);
        Workspaces = all.Where(w => viewer.CanManage(w.Id)).ToList();
    }
}
