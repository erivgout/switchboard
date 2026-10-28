using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Knowledge.Domain;

namespace Switchboard.Modules.Knowledge.Services;

/// <summary>Who is reading, with everything folder visibility depends on. Loaded once per request.</summary>
public sealed class KbViewer
{
    public Guid? UserId { get; init; }
    public string DisplayName { get; init; } = "";
    public bool SignedIn { get; init; }
    public bool IsInternal { get; init; }
    public bool IsOrgAdmin { get; init; }
    /// <summary>Signed-out visitors may read folders visible to everyone.</summary>
    public bool PublicKb { get; init; }
    public Guid? DepartmentId { get; init; }
    public IReadOnlySet<Guid> AgentGroupIds { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> RequesterGroupIds { get; init; } = new HashSet<Guid>();
    /// <summary>Workspaces where the viewer holds each knowledge privilege.</summary>
    public IReadOnlySet<Guid> ViewWorkspaces { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> WriteWorkspaces { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> PublishWorkspaces { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> ManageWorkspaces { get; init; } = new HashSet<Guid>();

    /// <summary>Agent of the workspace's knowledge base: sees drafts, reviews and archived articles there.</summary>
    public bool IsKbAgent(Guid workspaceId) => IsInternal || ViewWorkspaces.Contains(workspaceId);
    public bool CanWrite(Guid workspaceId) => IsInternal || WriteWorkspaces.Contains(workspaceId);
    public bool CanPublish(Guid workspaceId) => IsInternal || PublishWorkspaces.Contains(workspaceId);
    public bool CanManage(Guid workspaceId) => IsInternal || ManageWorkspaces.Contains(workspaceId);
    public bool IsAnyKbAgent => IsInternal || ViewWorkspaces.Count > 0;
    public bool CanManageAny => IsInternal || ManageWorkspaces.Count > 0;
}

/// <summary>One folder with the settings it inherits from its top-level folder.</summary>
public sealed record KbFolderNode(
    Guid Id,
    Guid WorkspaceId,
    Guid CategoryId,
    Guid? ParentId,
    Guid RootId,
    string Name,
    string? Description,
    int Order,
    int Depth,
    KbFolderVisibility Visibility,
    bool RequiresApproval,
    IReadOnlyList<KbFolderAudience> Audiences,
    IReadOnlyList<Guid> ApproverIds,
    /// <summary>The folder, an ancestor or its category is in the trash.</summary>
    bool InTrash,
    bool IsTrashedItself)
{
    /// <summary>Some requesters can read it (so a portal link is useful to them).</summary>
    public bool RequestersCanRead =>
        Visibility is KbFolderVisibility.Everyone or KbFolderVisibility.SignedIn
        || (Visibility == KbFolderVisibility.Restricted && Audiences.Any(a => a.Kind != KbAudienceKind.AgentGroup));

    public string VisibilityLabel => Visibility switch
    {
        KbFolderVisibility.Everyone => "Everyone",
        KbFolderVisibility.SignedIn => "Signed-in people",
        KbFolderVisibility.AgentsOnly => "Agents only",
        _ => "Selected groups",
    };
}

public sealed record KbCategoryNode(Guid Id, Guid WorkspaceId, string Name, string? Description, int Order, bool InTrash);

/// <summary>Folder tree of every workspace, with effective (inherited) settings.</summary>
public sealed class KbFolderIndex
{
    public required IReadOnlyDictionary<Guid, KbFolderNode> Folders { get; init; }
    public required IReadOnlyDictionary<Guid, KbCategoryNode> Categories { get; init; }

    public KbFolderNode? Get(Guid id) => Folders.GetValueOrDefault(id);

    /// <summary>"Network / VPN" style path of a folder.</summary>
    public string PathOf(Guid folderId)
    {
        var names = new List<string>();
        var current = Get(folderId);
        while (current is not null)
        {
            names.Insert(0, current.Name);
            current = current.ParentId is { } p ? Get(p) : null;
        }
        return string.Join(" / ", names);
    }

    public IEnumerable<Guid> DescendantsOf(Guid folderId)
    {
        foreach (var child in Folders.Values.Where(f => f.ParentId == folderId))
        {
            yield return child.Id;
            foreach (var grandchild in DescendantsOf(child.Id))
            {
                yield return grandchild;
            }
        }
    }
}

/// <summary>
/// Server-side visibility for every knowledge read path (INV-KB-2): lists, search, article pages,
/// slots, suggestions, agent tools and the REST API all start from <see cref="VisibleFolderIdsAsync"/>.
/// </summary>
public interface IKnowledgeVisibility
{
    Task<KbViewer> ViewerAsync(CancellationToken ct = default);
    Task<KbFolderIndex> FoldersAsync(CancellationToken ct = default);

    /// <summary>Folders whose published articles the viewer may read (not in trash).</summary>
    Task<IReadOnlySet<Guid>> VisibleFolderIdsAsync(CancellationToken ct = default);

    /// <summary>Folders the viewer may work in as an agent: visible and in a workspace where they hold kb.view.</summary>
    Task<IReadOnlySet<Guid>> AgentFolderIdsAsync(CancellationToken ct = default);

    bool CanSee(KbFolderNode folder, KbViewer viewer);

    /// <summary>Forget cached folders after the structure changed in this request.</summary>
    void Reset();
}

public sealed class KnowledgeVisibility(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IPermissionService permissions,
    ISettingsService settings) : IKnowledgeVisibility
{
    private KbViewer? _viewer;
    private KbFolderIndex? _folders;
    private IReadOnlySet<Guid>? _visible;
    private IReadOnlySet<Guid>? _agentFolders;

    public async Task<KbViewer> ViewerAsync(CancellationToken ct = default)
    {
        if (_viewer is not null)
        {
            return _viewer;
        }

        var actor = actors.Current;
        var publicKb = await settings.GetAsync(KbSettingKeys.Public, false, ct: ct);
        if (actor.IsInternal)
        {
            return _viewer = new KbViewer { IsInternal = true, SignedIn = true, PublicKb = publicKb, DisplayName = actor.DisplayName };
        }

        var perms = await permissions.CurrentAsync(ct);
        if (perms is null)
        {
            return _viewer = new KbViewer { PublicKb = publicKb, DisplayName = "Visitor" };
        }

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == perms.UserId)
            .Select(u => new { u.DepartmentId, u.DisplayName })
            .FirstAsync(ct);
        var requesterGroups = await db.RequesterGroupMembers.AsNoTracking()
            .Where(m => m.UserId == perms.UserId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);
        var workspaces = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).Select(w => w.Id).ToListAsync(ct);

        // HasAsync applies token scope guards (MCP, scoped API keys) on top of role privileges.
        async Task<HashSet<Guid>> With(string privilege)
        {
            var set = new HashSet<Guid>();
            foreach (var ws in workspaces)
            {
                if (await permissions.HasAsync(privilege, ws, ct))
                {
                    set.Add(ws);
                }
            }
            return set;
        }

        var view = await With(KbPrivileges.View);
        var write = (await With(KbPrivileges.Write)).Where(view.Contains).ToHashSet();
        var publish = (await With(KbPrivileges.Publish)).Where(view.Contains).ToHashSet();
        var manage = (await With(KbPrivileges.Manage)).Where(view.Contains).ToHashSet();

        return _viewer = new KbViewer
        {
            UserId = perms.UserId,
            DisplayName = user.DisplayName,
            SignedIn = true,
            IsOrgAdmin = perms.IsOrgAdmin,
            PublicKb = publicKb,
            DepartmentId = user.DepartmentId,
            AgentGroupIds = perms.GroupIds.Concat(perms.ObserverGroupIds).ToHashSet(),
            RequesterGroupIds = requesterGroups.ToHashSet(),
            ViewWorkspaces = view,
            WriteWorkspaces = write,
            PublishWorkspaces = publish,
            ManageWorkspaces = manage,
        };
    }

    public async Task<KbFolderIndex> FoldersAsync(CancellationToken ct = default)
    {
        if (_folders is not null)
        {
            return _folders;
        }

        var categories = await db.Set<KbCategory>().AsNoTracking()
            .Select(c => new { c.Id, c.WorkspaceId, c.Name, c.Description, c.Order, c.DeletedAt })
            .ToListAsync(ct);
        var folders = await db.Set<KbFolder>().AsNoTracking()
            .Include(f => f.Audiences)
            .Include(f => f.Approvers)
            .ToListAsync(ct);

        var categoryNodes = categories.ToDictionary(
            c => c.Id,
            c => new KbCategoryNode(c.Id, c.WorkspaceId, c.Name, c.Description, c.Order, c.DeletedAt is not null));
        var byId = folders.ToDictionary(f => f.Id);
        var nodes = new Dictionary<Guid, KbFolderNode>();

        foreach (var folder in folders)
        {
            var root = folder;
            var depth = 0;
            var trashed = folder.DeletedAt is not null;
            var seen = new HashSet<Guid> { folder.Id };
            while (root.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent) && seen.Add(parentId))
            {
                root = parent;
                depth++;
                trashed |= parent.DeletedAt is not null;
            }
            trashed |= !categoryNodes.TryGetValue(folder.CategoryId, out var category) || category.InTrash;

            nodes[folder.Id] = new KbFolderNode(
                folder.Id,
                folder.WorkspaceId,
                folder.CategoryId,
                folder.ParentId,
                root.Id,
                folder.Name,
                folder.Description,
                folder.Order,
                depth,
                root.Visibility,
                root.RequiresApproval,
                root.Audiences,
                root.Approvers.Select(a => a.UserId).ToList(),
                trashed,
                folder.DeletedAt is not null);
        }

        return _folders = new KbFolderIndex { Folders = nodes, Categories = categoryNodes };
    }

    public async Task<IReadOnlySet<Guid>> VisibleFolderIdsAsync(CancellationToken ct = default)
    {
        if (_visible is not null)
        {
            return _visible;
        }
        var viewer = await ViewerAsync(ct);
        var index = await FoldersAsync(ct);
        return _visible = index.Folders.Values.Where(f => !f.InTrash && CanSee(f, viewer)).Select(f => f.Id).ToHashSet();
    }

    public async Task<IReadOnlySet<Guid>> AgentFolderIdsAsync(CancellationToken ct = default)
    {
        if (_agentFolders is not null)
        {
            return _agentFolders;
        }
        var viewer = await ViewerAsync(ct);
        var index = await FoldersAsync(ct);
        var visible = await VisibleFolderIdsAsync(ct);
        return _agentFolders = visible.Where(id => viewer.IsKbAgent(index.Folders[id].WorkspaceId)).ToHashSet();
    }

    public void Reset()
    {
        _folders = null;
        _visible = null;
        _agentFolders = null;
    }

    public bool CanSee(KbFolderNode folder, KbViewer viewer)
    {
        if (viewer.IsInternal || viewer.CanManage(folder.WorkspaceId))
        {
            return true;
        }

        var matchesAudience = viewer.SignedIn && folder.Audiences.Any(a => a.Kind switch
        {
            KbAudienceKind.RequesterGroup => viewer.RequesterGroupIds.Contains(a.TargetId),
            KbAudienceKind.Department => viewer.DepartmentId == a.TargetId,
            KbAudienceKind.AgentGroup => viewer.AgentGroupIds.Contains(a.TargetId),
            _ => false,
        });

        if (viewer.IsKbAgent(folder.WorkspaceId))
        {
            // Agents of the workspace maintain every folder, except ones restricted to other agent groups.
            if (folder.Visibility != KbFolderVisibility.Restricted)
            {
                return true;
            }
            var agentGroups = folder.Audiences.Where(a => a.Kind == KbAudienceKind.AgentGroup).ToList();
            return agentGroups.Count == 0 || matchesAudience;
        }

        return folder.Visibility switch
        {
            KbFolderVisibility.Everyone => viewer.SignedIn || viewer.PublicKb,
            KbFolderVisibility.SignedIn => viewer.SignedIn,
            KbFolderVisibility.Restricted => matchesAudience,
            _ => false,
        };
    }
}
