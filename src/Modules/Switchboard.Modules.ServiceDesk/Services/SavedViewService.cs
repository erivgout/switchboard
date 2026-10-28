using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Data;

namespace Switchboard.Modules.ServiceDesk.Services;

public sealed record SavedViewInput(string Name, string Query, string? Sort, IReadOnlyList<string>? Columns, bool IsShared);

/// <summary>
/// Saved queue views. A view is private to its owner; sharing it with every agent needs
/// <c>settings.manage</c>. Only the owner (or, for shared views, a settings manager) can change it.
/// </summary>
public sealed class SavedViewService(SwitchboardDbContext db, IActorAccessor actors, IPermissionService permissions, ITicketSearch search, IAuditLog audit)
{
    public async Task<IReadOnlyList<SdSavedView>> ListAsync(CancellationToken ct = default)
    {
        var me = RequireUser();
        return await db.Set<SdSavedView>().AsNoTracking()
            .Where(v => v.OwnerId == me || v.IsShared)
            .OrderBy(v => v.IsShared).ThenBy(v => v.Name)
            .ToListAsync(ct);
    }

    /// <summary>A view the current user may use, or <see cref="NotFoundException"/>.</summary>
    public async Task<SdSavedView> GetAsync(Guid id, CancellationToken ct = default)
    {
        var me = RequireUser();
        return await db.Set<SdSavedView>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && (v.OwnerId == me || v.IsShared), ct)
            ?? throw new NotFoundException();
    }

    public async Task<SdSavedView> CreateAsync(SavedViewInput input, CancellationToken ct = default)
    {
        var me = RequireUser();
        var (name, query) = await ValidateAsync(input, ct);
        if (input.IsShared)
        {
            await permissions.DemandAsync(Privileges.SettingsManage, ct: ct);
        }
        var view = new SdSavedView
        {
            OwnerId = me,
            Name = name,
            Query = query,
            Sort = QueueViews.NormalizeSort(input.Sort),
            Columns = QueueColumns.Normalize(input.Columns),
            IsShared = input.IsShared,
        };
        db.Set<SdSavedView>().Add(view);
        if (view.IsShared)
        {
            audit.Record("servicedesk.view_shared", "saved_view", view.Id.ToString(), new { view.Name, view.Query });
        }
        await db.SaveChangesAsync(ct);
        return view;
    }

    public async Task<SdSavedView> UpdateAsync(Guid id, SavedViewInput input, CancellationToken ct = default)
    {
        var view = await LoadEditableAsync(id, ct);
        var (name, query) = await ValidateAsync(input, ct);
        if (input.IsShared != view.IsShared || view.IsShared)
        {
            await permissions.DemandAsync(Privileges.SettingsManage, ct: ct);
        }
        view.Name = name;
        view.Query = query;
        view.Sort = QueueViews.NormalizeSort(input.Sort);
        view.Columns = QueueColumns.Normalize(input.Columns);
        if (view.IsShared != input.IsShared)
        {
            audit.Record(input.IsShared ? "servicedesk.view_shared" : "servicedesk.view_unshared", "saved_view", view.Id.ToString(), new { view.Name });
        }
        view.IsShared = input.IsShared;
        await db.SaveChangesAsync(ct);
        return view;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var view = await LoadEditableAsync(id, ct);
        db.Set<SdSavedView>().Remove(view);
        if (view.IsShared)
        {
            audit.Record("servicedesk.view_deleted", "saved_view", view.Id.ToString(), new { view.Name });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>True when the current user may rename or delete the view.</summary>
    public async Task<bool> CanEditAsync(SdSavedView view, CancellationToken ct = default) =>
        view.IsShared ? await permissions.HasAsync(Privileges.SettingsManage, ct: ct) : view.OwnerId == actors.Current.UserId;

    private async Task<SdSavedView> LoadEditableAsync(Guid id, CancellationToken ct)
    {
        var me = RequireUser();
        var view = await db.Set<SdSavedView>().FirstOrDefaultAsync(v => v.Id == id && (v.OwnerId == me || v.IsShared), ct)
            ?? throw new NotFoundException();
        if (!await CanEditAsync(view, ct))
        {
            throw new ForbiddenException("Only an admin can change a shared view.");
        }
        return view;
    }

    private async Task<(string Name, string Query)> ValidateAsync(SavedViewInput input, CancellationToken ct)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new ValidationException("name", "Name the view.");
        }
        if (name.Length > 100)
        {
            throw new ValidationException("name", "Keep the name under 100 characters.");
        }
        var query = (input.Query ?? "").Trim();
        // Compiling throws a ValidationException with a plain-language message when the query is malformed.
        await search.QueryAsync(query, ct: ct);
        return (name, query);
    }

    private Guid RequireUser() =>
        actors.Current.UserId is { } id && !actors.Current.IsInternal ? id : throw new ForbiddenException();
}
