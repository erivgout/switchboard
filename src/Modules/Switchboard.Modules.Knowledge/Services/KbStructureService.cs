using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Knowledge.Domain;

namespace Switchboard.Modules.Knowledge.Services;

public sealed class KbFolderInput
{
    public Guid CategoryId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public KbFolderVisibility Visibility { get; set; } = KbFolderVisibility.Everyone;
    public IReadOnlyList<Guid> RequesterGroupIds { get; set; } = [];
    public IReadOnlyList<Guid> DepartmentIds { get; set; } = [];
    public IReadOnlyList<Guid> AgentGroupIds { get; set; } = [];
    public bool RequiresApproval { get; set; }
    public IReadOnlyList<Guid> ApproverIds { get; set; } = [];
}

/// <summary>Categories, folders, the public-KB setting and their trash (FR-KB-1, FR-KB-9).</summary>
public interface IKbStructureService
{
    Task<KbCategory> CreateCategoryAsync(Guid workspaceId, string name, string? description, CancellationToken ct = default);
    Task UpdateCategoryAsync(Guid id, string name, string? description, CancellationToken ct = default);
    Task<KbFolder> CreateFolderAsync(KbFolderInput input, CancellationToken ct = default);
    Task UpdateFolderAsync(Guid id, KbFolderInput input, CancellationToken ct = default);
    /// <summary>Moves a category or folder one place up (-1) or down (+1) among its siblings.</summary>
    Task MoveAsync(string kind, Guid id, int direction, CancellationToken ct = default);
    Task<int> TrashAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<int> RestoreAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<int> PurgeAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<IReadOnlyList<KbTrashItem>> TrashedAsync(CancellationToken ct = default);
    Task SetPublicAsync(bool enabled, CancellationToken ct = default);
}

public sealed class KbStructureService(
    SwitchboardDbContext db,
    IKnowledgeVisibility visibility,
    IActorAccessor actors,
    ISettingsService settings,
    IAuditLog audit,
    IFileStore files,
    TimeProvider time) : IKbStructureService
{
    public const string Category = "category";
    public const string Folder = "folder";

    private DbSet<KbCategory> Categories => db.Set<KbCategory>();
    private DbSet<KbFolder> Folders => db.Set<KbFolder>();

    public async Task<KbCategory> CreateCategoryAsync(Guid workspaceId, string name, string? description, CancellationToken ct = default)
    {
        await DemandManageAsync(workspaceId, ct);
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId && !w.IsArchived, ct))
        {
            throw new ValidationException("workspaceId", "Choose a workspace.");
        }
        var order = await Categories.Where(c => c.WorkspaceId == workspaceId).MaxAsync(c => (int?)c.Order, ct) ?? 0;
        var category = new KbCategory
        {
            WorkspaceId = workspaceId,
            Name = ValidName(name),
            Description = Clip(description, 1000),
            Order = order + 1,
        };
        Categories.Add(category);
        audit.Record("kb.category.created", "kb_category", category.Id.ToString(), new { category.Name });
        await db.SaveChangesAsync(ct);
        visibility.Reset();
        return category;
    }

    public async Task UpdateCategoryAsync(Guid id, string name, string? description, CancellationToken ct = default)
    {
        var category = await Categories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        await DemandManageAsync(category.WorkspaceId, ct);
        category.Name = ValidName(name);
        category.Description = Clip(description, 1000);
        await db.SaveChangesAsync(ct);
        visibility.Reset();
    }

    public async Task<KbFolder> CreateFolderAsync(KbFolderInput input, CancellationToken ct = default)
    {
        var category = await Categories.FirstOrDefaultAsync(c => c.Id == input.CategoryId && c.DeletedAt == null, ct)
            ?? throw new ValidationException("categoryId", "Choose a category.");
        await DemandManageAsync(category.WorkspaceId, ct);
        var folder = new KbFolder { WorkspaceId = category.WorkspaceId, CategoryId = category.Id };
        await ApplyAsync(folder, input, ct);
        var siblings = Folders.Where(f => f.CategoryId == folder.CategoryId && f.ParentId == folder.ParentId);
        folder.Order = (await siblings.MaxAsync(f => (int?)f.Order, ct) ?? 0) + 1;
        Folders.Add(folder);
        audit.Record("kb.folder.created", "kb_folder", folder.Id.ToString(), Describe(folder));
        await db.SaveChangesAsync(ct);
        visibility.Reset();
        return folder;
    }

    public async Task UpdateFolderAsync(Guid id, KbFolderInput input, CancellationToken ct = default)
    {
        var folder = await Folders.Include(f => f.Audiences).Include(f => f.Approvers).FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new NotFoundException();
        await DemandManageAsync(folder.WorkspaceId, ct);
        var category = await Categories.FirstOrDefaultAsync(c => c.Id == input.CategoryId && c.DeletedAt == null, ct)
            ?? throw new ValidationException("categoryId", "Choose a category.");
        if (category.WorkspaceId != folder.WorkspaceId)
        {
            throw new ValidationException("categoryId", "Folders stay in their workspace. Choose a category in the same workspace.");
        }
        var before = Describe(folder);
        var moved = folder.CategoryId != category.Id || folder.ParentId != input.ParentId;
        folder.CategoryId = category.Id;
        await ApplyAsync(folder, input, ct);
        if (moved)
        {
            // Sub-folders follow their parent into the new category.
            foreach (var childId in (await visibility.FoldersAsync(ct)).DescendantsOf(folder.Id))
            {
                var child = await Folders.FirstAsync(f => f.Id == childId, ct);
                child.CategoryId = category.Id;
            }
        }
        audit.Record("kb.folder.updated", "kb_folder", folder.Id.ToString(), new { before, after = Describe(folder) });
        await db.SaveChangesAsync(ct);
        visibility.Reset();
    }

    public async Task MoveAsync(string kind, Guid id, int direction, CancellationToken ct = default)
    {
        direction = Math.Sign(direction);
        if (direction == 0)
        {
            return;
        }
        if (kind == Category)
        {
            var category = await Categories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
            await DemandManageAsync(category.WorkspaceId, ct);
            var siblings = await Categories.Where(c => c.WorkspaceId == category.WorkspaceId && c.DeletedAt == null)
                .OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct);
            Swap(siblings, category, direction, c => c.Order, (c, o) => c.Order = o);
        }
        else
        {
            var folder = await Folders.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
            await DemandManageAsync(folder.WorkspaceId, ct);
            var siblings = await Folders.Where(f => f.CategoryId == folder.CategoryId && f.ParentId == folder.ParentId && f.DeletedAt == null)
                .OrderBy(f => f.Order).ThenBy(f => f.Name).ToListAsync(ct);
            Swap(siblings, folder, direction, f => f.Order, (f, o) => f.Order = o);
        }
        await db.SaveChangesAsync(ct);
        visibility.Reset();
    }

    public async Task<int> TrashAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var me = actors.Current.UserId;
        var count = 0;
        foreach (var id in ids.Distinct())
        {
            if (kind == Category)
            {
                var c = await Categories.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct) ?? throw new NotFoundException();
                await DemandManageAsync(c.WorkspaceId, ct);
                c.DeletedAt = now;
                c.DeletedById = me;
                audit.Record("kb.category.trashed", "kb_category", c.Id.ToString(), new { c.Name });
            }
            else
            {
                var f = await Folders.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct) ?? throw new NotFoundException();
                await DemandManageAsync(f.WorkspaceId, ct);
                f.DeletedAt = now;
                f.DeletedById = me;
                audit.Record("kb.folder.trashed", "kb_folder", f.Id.ToString(), new { f.Name });
            }
            count++;
        }
        await db.SaveChangesAsync(ct);
        visibility.Reset();
        return count;
    }

    public async Task<int> RestoreAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var id in ids.Distinct())
        {
            if (kind == Category)
            {
                var c = await Categories.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt != null, ct) ?? throw new NotFoundException();
                await DemandManageAsync(c.WorkspaceId, ct);
                c.DeletedAt = null;
                c.DeletedById = null;
                audit.Record("kb.category.restored", "kb_category", c.Id.ToString(), new { c.Name });
            }
            else
            {
                var f = await Folders.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt != null, ct) ?? throw new NotFoundException();
                await DemandManageAsync(f.WorkspaceId, ct);
                f.DeletedAt = null;
                f.DeletedById = null;
                audit.Record("kb.folder.restored", "kb_folder", f.Id.ToString(), new { f.Name });
            }
            count++;
        }
        await db.SaveChangesAsync(ct);
        visibility.Reset();
        return count;
    }

    public async Task<int> PurgeAsync(string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var index = await visibility.FoldersAsync(ct);
        var folderIds = new List<Guid>();
        var categoryIds = new List<Guid>();
        foreach (var id in ids.Distinct())
        {
            if (kind == Category)
            {
                var c = await Categories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                await DemandManageAsync(c.WorkspaceId, ct);
                if (c.DeletedAt is null)
                {
                    throw new ValidationException("category", $"Move {c.Name} to the trash before deleting it permanently.");
                }
                categoryIds.Add(c.Id);
                folderIds.AddRange(index.Folders.Values.Where(f => f.CategoryId == c.Id).Select(f => f.Id));
                audit.Record("kb.category.purged", "kb_category", c.Id.ToString(), new { c.Name });
            }
            else
            {
                var f = await Folders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
                await DemandManageAsync(f.WorkspaceId, ct);
                if (f.DeletedAt is null)
                {
                    throw new ValidationException("folder", $"Move {f.Name} to the trash before deleting it permanently.");
                }
                folderIds.Add(f.Id);
                folderIds.AddRange(index.DescendantsOf(f.Id));
                audit.Record("kb.folder.purged", "kb_folder", f.Id.ToString(), new { f.Name });
            }
        }
        folderIds = folderIds.Distinct().ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var articleIds = await db.Set<KbArticle>().Where(a => folderIds.Contains(a.FolderId)).Select(a => a.Id).ToListAsync(ct);
        var blobs = await KnowledgeService.PurgeArticlesAsync(db, articleIds, ct);
        // Deepest folders first: sub-folders reference their parents.
        foreach (var level in folderIds.GroupBy(id => index.Get(id)?.Depth ?? 0).OrderByDescending(g => g.Key))
        {
            var batch = level.ToList();
            await Folders.Where(f => batch.Contains(f.Id)).ExecuteDeleteAsync(ct);
        }
        await Categories.Where(c => categoryIds.Contains(c.Id)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await KnowledgeService.DeleteBlobsAsync(files, blobs);
        visibility.Reset();
        return kind == Category ? categoryIds.Count : ids.Distinct().Count();
    }

    public async Task<IReadOnlyList<KbTrashItem>> TrashedAsync(CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        var index = await visibility.FoldersAsync(ct);
        var categories = index.Categories.Values.Where(c => c.InTrash && viewer.CanManage(c.WorkspaceId)).Select(c => c.Id).ToList();
        var folders = index.Folders.Values.Where(f => f.IsTrashedItself && viewer.CanManage(f.WorkspaceId)).Select(f => f.Id).ToList();
        var cRows = await Categories.AsNoTracking().Where(c => categories.Contains(c.Id)).ToListAsync(ct);
        var fRows = await Folders.AsNoTracking().Where(f => folders.Contains(f.Id)).ToListAsync(ct);
        var userIds = cRows.Select(c => c.DeletedById).Concat(fRows.Select(f => f.DeletedById)).OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? Name(Guid? id) => id is { } x && names.TryGetValue(x, out var n) ? n : null;
        return cRows.Select(c => new KbTrashItem(Category, c.Id, c.Name, "Category", c.DeletedAt!.Value, Name(c.DeletedById), true, true))
            .Concat(fRows.Select(f => new KbTrashItem(Folder, f.Id, f.Name,
                index.Categories.GetValueOrDefault(f.CategoryId)?.Name is { } cat ? $"{cat} / {index.PathOf(f.Id)}" : index.PathOf(f.Id),
                f.DeletedAt!.Value, Name(f.DeletedById), true, true)))
            .OrderByDescending(i => i.DeletedAt)
            .ToList();
    }

    public async Task SetPublicAsync(bool enabled, CancellationToken ct = default)
    {
        var viewer = await visibility.ViewerAsync(ct);
        if (!viewer.IsInternal && !viewer.IsOrgAdmin && !viewer.CanManageAny)
        {
            throw new ForbiddenException();
        }
        await settings.SetAsync(KbSettingKeys.Public, enabled, ct: ct);
        audit.Record("kb.settings.public_changed", "setting", KbSettingKeys.Public, new { enabled });
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyAsync(KbFolder folder, KbFolderInput input, CancellationToken ct)
    {
        folder.Name = ValidName(input.Name);
        folder.Description = Clip(input.Description, 1000);
        if (input.ParentId is { } parentId)
        {
            var index = await visibility.FoldersAsync(ct);
            var parent = index.Get(parentId);
            if (parent is null || parent.IsTrashedItself || parent.CategoryId != folder.CategoryId)
            {
                throw new ValidationException("parentId", "Choose a parent folder in the same category.");
            }
            if (parentId == folder.Id || index.DescendantsOf(folder.Id).Contains(parentId))
            {
                throw new ValidationException("parentId", "A folder can't go inside itself.");
            }
            if (parent.Depth >= 2)
            {
                throw new ValidationException("parentId", "Folders can be nested three levels deep at most.");
            }
            folder.ParentId = parentId;
        }
        else
        {
            folder.ParentId = null;
        }

        // Sub-folders follow their top-level folder, so only top-level folders keep these settings.
        var topLevel = folder.ParentId is null;
        folder.Visibility = topLevel && Enum.IsDefined(input.Visibility) ? input.Visibility : KbFolderVisibility.Everyone;
        folder.RequiresApproval = topLevel && input.RequiresApproval;

        var audiences = new List<KbFolderAudience>();
        if (topLevel && folder.Visibility == KbFolderVisibility.Restricted)
        {
            var requesterGroups = await db.RequesterGroups.Where(g => input.RequesterGroupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            var departments = await db.Departments.Where(d => input.DepartmentIds.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct);
            var agentGroups = await db.AgentGroups.Where(g => input.AgentGroupIds.Contains(g.Id) && g.WorkspaceId == folder.WorkspaceId).Select(g => g.Id).ToListAsync(ct);
            audiences.AddRange(requesterGroups.Select(id => new KbFolderAudience { FolderId = folder.Id, Kind = KbAudienceKind.RequesterGroup, TargetId = id }));
            audiences.AddRange(departments.Select(id => new KbFolderAudience { FolderId = folder.Id, Kind = KbAudienceKind.Department, TargetId = id }));
            audiences.AddRange(agentGroups.Select(id => new KbFolderAudience { FolderId = folder.Id, Kind = KbAudienceKind.AgentGroup, TargetId = id }));
            if (audiences.Count == 0)
            {
                throw new ValidationException("visibility", "Choose at least one group or department, or pick a different visibility.");
            }
        }
        folder.Audiences.Clear();
        folder.Audiences.AddRange(audiences);

        var approvers = new List<KbFolderApprover>();
        if (folder.RequiresApproval)
        {
            var ids = await db.Users.Where(u => input.ApproverIds.Contains(u.Id) && u.IsActive && (u.IsAgent || u.IsOrgAdmin)).Select(u => u.Id).ToListAsync(ct);
            approvers.AddRange(ids.Select(id => new KbFolderApprover { FolderId = folder.Id, UserId = id }));
        }
        folder.Approvers.Clear();
        folder.Approvers.AddRange(approvers);
    }

    private async Task DemandManageAsync(Guid workspaceId, CancellationToken ct)
    {
        var viewer = await visibility.ViewerAsync(ct);
        if (!viewer.CanManage(workspaceId))
        {
            throw new ForbiddenException();
        }
    }

    private static void Swap<T>(List<T> siblings, T item, int direction, Func<T, int> order, Action<T, int> setOrder)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            setOrder(siblings[i], i + 1);
        }
        var at = siblings.IndexOf(item);
        var to = at + direction;
        if (at < 0 || to < 0 || to >= siblings.Count)
        {
            return;
        }
        var other = siblings[to];
        var a = order(item);
        setOrder(item, order(other));
        setOrder(other, a);
    }

    private static object Describe(KbFolder f) => new
    {
        f.Name,
        f.CategoryId,
        f.ParentId,
        Visibility = f.Visibility.ToString(),
        f.RequiresApproval,
        Audiences = f.Audiences.Select(a => $"{a.Kind}:{a.TargetId}").ToList(),
        Approvers = f.Approvers.Select(a => a.UserId).ToList(),
    };

    private static string ValidName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0)
        {
            throw new ValidationException("name", "Enter a name.");
        }
        return n.Length > 200 ? throw new ValidationException("name", "Keep the name under 200 characters.") : n;
    }

    private static string? Clip(string? value, int max)
    {
        var v = value?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length <= max ? v : v[..max];
    }
}
