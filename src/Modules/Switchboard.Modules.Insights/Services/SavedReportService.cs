using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.Insights.Domain;

namespace Switchboard.Modules.Insights.Services;

public sealed record SavedReportInput(Guid? Id, string Name, string? Folder, string Path, string? Query, ReportSharing Sharing, IReadOnlyList<Guid> GroupIds);

public sealed record SavedReportView(InsSavedReport Report, string OwnerName, bool IsMine, bool IsFavorite);

/// <summary>
/// Saved reports: folders, favorites, private or shared, trash and restore (FR-ANL-4). Only the owner
/// changes a report; a shared report opens with the viewer's own permissions.
/// </summary>
public sealed partial class SavedReportService(SwitchboardDbContext db, IPermissionService permissions, IActorAccessor actors, TimeProvider time)
{
    public const string Privilege = InsightsPrivileges.ReportsView;

    public async Task<List<SavedReportView>> ListAsync(CancellationToken ct = default)
    {
        var me = await DemandAsync(ct);
        var groups = await MyGroupsAsync(me, ct);
        var reports = await db.Set<InsSavedReport>().AsNoTracking()
            .Where(r => r.DeletedAt == null && (r.OwnerId == me || r.Sharing == ReportSharing.Everyone || r.Sharing == ReportSharing.Groups))
            .OrderBy(r => r.Folder).ThenBy(r => r.Name)
            .ToListAsync(ct);
        reports = reports.Where(r => r.OwnerId == me || r.Sharing == ReportSharing.Everyone || r.SharedGroupIds.Any(groups.Contains)).ToList();
        var favorites = (await db.Set<InsReportFavorite>().AsNoTracking().Where(f => f.UserId == me).Select(f => f.ReportId).ToListAsync(ct)).ToHashSet();
        var ownerIds = reports.Select(r => r.OwnerId).Distinct().ToList();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return reports.Select(r => new SavedReportView(r, owners.GetValueOrDefault(r.OwnerId, "Someone"), r.OwnerId == me, favorites.Contains(r.Id))).ToList();
    }

    public async Task<List<InsSavedReport>> TrashAsync(CancellationToken ct = default)
    {
        var me = await DemandAsync(ct);
        return await db.Set<InsSavedReport>().AsNoTracking().Where(r => r.OwnerId == me && r.DeletedAt != null)
            .OrderByDescending(r => r.DeletedAt).ToListAsync(ct);
    }

    public async Task<InsSavedReport> GetOwnAsync(Guid id, CancellationToken ct = default)
    {
        var me = await DemandAsync(ct);
        return await db.Set<InsSavedReport>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.OwnerId == me && r.DeletedAt == null, ct)
            ?? throw new NotFoundException();
    }

    public async Task<InsSavedReport> SaveAsync(SavedReportInput input, CancellationToken ct = default)
    {
        var me = await DemandAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            errors["name"] = [name.Length == 0 ? "Give the report a name." : "Keep the name under 200 characters."];
        }
        var folder = string.IsNullOrWhiteSpace(input.Folder) ? null : input.Folder.Trim();
        if (folder is { Length: > 100 })
        {
            errors["folder"] = ["Keep the folder name under 100 characters."];
        }
        if (!IsReportPath(input.Path))
        {
            errors["path"] = ["Only reports can be saved."];
        }
        var query = (input.Query ?? "").TrimStart('?');
        if (query.Length > 1000 || !QueryPattern().IsMatch(query))
        {
            errors["query"] = ["Those filters can't be saved. Open the report again and save it from there."];
        }
        var groupIds = input.Sharing == ReportSharing.Groups ? input.GroupIds.Distinct().ToList() : [];
        if (input.Sharing == ReportSharing.Groups)
        {
            if (groupIds.Count == 0)
            {
                errors["groups"] = ["Choose at least one group to share with."];
            }
            else if (await db.AgentGroups.CountAsync(g => groupIds.Contains(g.Id), ct) != groupIds.Count)
            {
                errors["groups"] = ["One of those groups no longer exists."];
            }
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        InsSavedReport report;
        if (input.Id is { } id)
        {
            report = await db.Set<InsSavedReport>().FirstOrDefaultAsync(r => r.Id == id && r.OwnerId == me && r.DeletedAt == null, ct)
                ?? throw new NotFoundException();
        }
        else
        {
            report = new InsSavedReport { OwnerId = me };
            db.Set<InsSavedReport>().Add(report);
        }
        report.Name = name;
        report.Folder = folder;
        report.Path = input.Path;
        report.Query = query;
        report.Sharing = input.Sharing;
        report.SharedGroupIds = groupIds;
        await db.SaveChangesAsync(ct);
        return report;
    }

    public async Task MoveToTrashAsync(Guid id, CancellationToken ct = default)
    {
        var report = await OwnAsync(id, deleted: false, ct);
        report.DeletedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task RestoreAsync(Guid id, CancellationToken ct = default)
    {
        var report = await OwnAsync(id, deleted: true, ct);
        report.DeletedAt = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteForeverAsync(Guid id, CancellationToken ct = default)
    {
        var report = await OwnAsync(id, deleted: true, ct);
        db.Set<InsSavedReport>().Remove(report);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Stars or unstars a report the viewer can see. Returns the new state.</summary>
    public async Task<bool> ToggleFavoriteAsync(Guid id, CancellationToken ct = default)
    {
        var me = await DemandAsync(ct);
        if ((await ListAsync(ct)).All(v => v.Report.Id != id))
        {
            throw new NotFoundException();
        }
        var existing = await db.Set<InsReportFavorite>().FirstOrDefaultAsync(f => f.UserId == me && f.ReportId == id, ct);
        if (existing is null)
        {
            db.Set<InsReportFavorite>().Add(new InsReportFavorite { UserId = me, ReportId = id, CreatedAt = time.GetUtcNow() });
        }
        else
        {
            db.Set<InsReportFavorite>().Remove(existing);
        }
        await db.SaveChangesAsync(ct);
        return existing is null;
    }

    /// <summary>A report page: <c>/reports/&lt;name&gt;</c>, nothing else (no open redirects through saved links).</summary>
    public static bool IsReportPath(string? path) =>
        path is not null && PathPattern().IsMatch(path) && !path.StartsWith("/reports/saved", StringComparison.Ordinal) && path != "/reports/trash";

    private async Task<InsSavedReport> OwnAsync(Guid id, bool deleted, CancellationToken ct)
    {
        var me = await DemandAsync(ct);
        return await db.Set<InsSavedReport>().FirstOrDefaultAsync(r => r.Id == id && r.OwnerId == me && (r.DeletedAt != null) == deleted, ct)
            ?? throw new NotFoundException();
    }

    private async Task<Guid> DemandAsync(CancellationToken ct)
    {
        await permissions.DemandAsync(Privilege, ct: ct);
        return actors.Current.UserId ?? throw new ForbiddenException();
    }

    private async Task<HashSet<Guid>> MyGroupsAsync(Guid me, CancellationToken ct) =>
        (await db.AgentGroupMembers.AsNoTracking().Where(m => m.UserId == me).Select(m => m.GroupId).ToListAsync(ct)).ToHashSet();

    [GeneratedRegex("^/reports/[a-z0-9-]{1,60}$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[A-Za-z0-9=&%._\-+:]*$")]
    private static partial Regex QueryPattern();
}
