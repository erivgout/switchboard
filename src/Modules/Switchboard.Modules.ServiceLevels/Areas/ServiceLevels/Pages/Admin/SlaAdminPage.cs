using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Services;

namespace Switchboard.Modules.ServiceLevels.Areas.ServiceLevels.Pages.Admin;

/// <summary>Admin pages: need <c>sla.manage</c> somewhere; each workspace is checked again by the service.</summary>
[Authorize(Policy = SwitchboardPolicies.PrivilegePrefix + SlaAdminService.Privilege)]
public abstract class SlaAdminPage(SwitchboardDbContext db, IPermissionService permissions) : PageModel
{
    public Workspace Workspace { get; private set; } = default!;
    public IReadOnlyList<Workspace> Workspaces { get; private set; } = [];

    protected SwitchboardDbContext Db => db;

    /// <summary>The workspace from <c>?ws=key</c>, or the first one the user manages.</summary>
    protected async Task<bool> UseWorkspaceAsync(string? key, CancellationToken ct)
    {
        await LoadWorkspacesAsync(ct);
        Workspace = Workspaces.FirstOrDefault(w => string.Equals(w.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Workspaces.FirstOrDefault()!;
        return Workspace is not null;
    }

    protected async Task<bool> UseWorkspaceAsync(Guid id, CancellationToken ct)
    {
        await LoadWorkspacesAsync(ct);
        Workspace = Workspaces.FirstOrDefault(w => w.Id == id)!;
        return Workspace is not null;
    }

    /// <summary>Adds the query string that keeps the chosen workspace, when there is more than one.</summary>
    public string WithWorkspace(string path) =>
        Workspaces.Count > 1 && Workspace is not null ? $"{path}{(path.Contains('?') ? '&' : '?')}ws={Uri.EscapeDataString(Workspace.Key)}" : path;

    protected void AddErrors(ValidationException ex)
    {
        foreach (var (key, messages) in ex.Errors)
        {
            foreach (var message in messages)
            {
                ModelState.AddModelError(key, message);
            }
        }
    }

    private async Task LoadWorkspacesAsync(CancellationToken ct)
    {
        var p = await permissions.CurrentAsync(ct);
        var all = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived)
            .OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).ToListAsync(ct);
        Workspaces = all.Where(w => p?.Has(SlaAdminService.Privilege, w.Id) == true).ToList();
    }
}
