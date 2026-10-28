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

public sealed record KbOption(Guid Id, string Name);

[Authorize(Policy = "priv:" + KbPrivileges.Manage)]
public sealed class FolderEditModel(IKnowledgeVisibility visibility, IKbStructureService structure, SwitchboardDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? Id { get; set; }
    [BindProperty(SupportsGet = true, Name = "category")] public Guid? CategoryQuery { get; set; }
    [BindProperty(SupportsGet = true, Name = "parent")] public Guid? ParentQuery { get; set; }

    [BindProperty] public KbFolderInput Input { get; set; } = new();
    [BindProperty] public List<Guid> RequesterGroupIds { get; set; } = [];
    [BindProperty] public List<Guid> DepartmentIds { get; set; } = [];
    [BindProperty] public List<Guid> AgentGroupIds { get; set; } = [];
    [BindProperty] public List<Guid> ApproverIds { get; set; } = [];

    public KbFolderIndex Index { get; private set; } = default!;
    public IReadOnlyList<KbCategoryNode> Categories { get; private set; } = [];
    public IReadOnlyList<KbOption> RequesterGroups { get; private set; } = [];
    public IReadOnlyList<KbOption> Departments { get; private set; } = [];
    public IReadOnlyList<KbOption> AgentGroups { get; private set; } = [];
    public IReadOnlyList<KbOption> Agents { get; private set; } = [];
    public bool IsNew => Id is null;
    public string? ParentName { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (Id is { } id)
        {
            var folder = await db.Set<KbFolder>().AsNoTracking().Include(f => f.Audiences).Include(f => f.Approvers)
                .FirstOrDefaultAsync(f => f.Id == id && f.DeletedAt == null, ct) ?? throw new NotFoundException();
            if (!(await visibility.ViewerAsync(ct)).CanManage(folder.WorkspaceId))
            {
                throw new ForbiddenException();
            }
            Input = new KbFolderInput
            {
                CategoryId = folder.CategoryId,
                ParentId = folder.ParentId,
                Name = folder.Name,
                Description = folder.Description,
                Visibility = folder.Visibility,
                RequiresApproval = folder.RequiresApproval,
            };
            RequesterGroupIds = folder.Audiences.Where(a => a.Kind == KbAudienceKind.RequesterGroup).Select(a => a.TargetId).ToList();
            DepartmentIds = folder.Audiences.Where(a => a.Kind == KbAudienceKind.Department).Select(a => a.TargetId).ToList();
            AgentGroupIds = folder.Audiences.Where(a => a.Kind == KbAudienceKind.AgentGroup).Select(a => a.TargetId).ToList();
            ApproverIds = folder.Approvers.Select(a => a.UserId).ToList();
        }
        else
        {
            Input.CategoryId = CategoryQuery ?? Categories.FirstOrDefault()?.Id ?? Guid.Empty;
            Input.ParentId = ParentQuery;
            Input.Visibility = KbFolderVisibility.Everyone;
        }
        SetParentName();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Input.RequesterGroupIds = RequesterGroupIds;
        Input.DepartmentIds = DepartmentIds;
        Input.AgentGroupIds = AgentGroupIds;
        Input.ApproverIds = ApproverIds;
        // A sub-folder lives in its parent's category.
        if (Input.ParentId is { } parentId && (await visibility.FoldersAsync(ct)).Get(parentId) is { } parent)
        {
            Input.CategoryId = parent.CategoryId;
        }
        try
        {
            if (Id is { } id)
            {
                await structure.UpdateFolderAsync(id, Input, ct);
                TempData.Toast("Folder saved");
            }
            else
            {
                await structure.CreateFolderAsync(Input, ct);
                TempData.Toast("Folder created");
            }
            return Redirect("/kb/folders");
        }
        catch (ValidationException ex)
        {
            this.AddErrors(ex, nameof(Input));
            await LoadAsync(ct);
            SetParentName();
            return Page();
        }
    }

    private void SetParentName() =>
        ParentName = Input.ParentId is { } p && Index.Get(p) is { } parent ? Index.PathOf(parent.Id) : null;

    private async Task LoadAsync(CancellationToken ct)
    {
        var viewer = await visibility.ViewerAsync(ct);
        Index = await visibility.FoldersAsync(ct);
        Categories = Index.Categories.Values.Where(c => !c.InTrash && viewer.CanManage(c.WorkspaceId)).OrderBy(c => c.Order).ThenBy(c => c.Name).ToList();
        RequesterGroups = await db.RequesterGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new KbOption(g.Id, g.Name)).ToListAsync(ct);
        Departments = await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => new KbOption(d.Id, d.Name)).ToListAsync(ct);
        AgentGroups = await db.AgentGroups.AsNoTracking().OrderBy(g => g.Name).Select(g => new KbOption(g.Id, g.Name)).ToListAsync(ct);
        Agents = await db.Users.AsNoTracking().Where(u => u.IsActive && (u.IsAgent || u.IsOrgAdmin)).OrderBy(u => u.DisplayName)
            .Select(u => new KbOption(u.Id, u.DisplayName)).ToListAsync(ct);
    }
}
