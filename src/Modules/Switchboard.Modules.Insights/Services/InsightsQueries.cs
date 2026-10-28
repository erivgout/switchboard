using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.UI;

namespace Switchboard.Modules.Insights.Services;

public sealed record MetricTile(string Label, int Value, string Query);

public sealed record DayPoint(DateOnly Day, int Created, int Resolved);

public sealed record GroupLoad(Guid Id, string Name, int Open, int Waiting, int Unassigned, int Overdue, int DueToday);

public sealed record DashboardData(
    IReadOnlyList<MetricTile> Metrics,
    IReadOnlyList<TicketListItem> Queue,
    int QueueTotal,
    IReadOnlyList<DayPoint> Trend,
    IReadOnlyList<GroupLoad> Groups,
    bool GroupsAreMine);

public sealed record AgentLoad(Guid Id, string Name, int Open);

public sealed record TeamGroup(
    Guid Id,
    string Name,
    int Open,
    int Overdue,
    int Unassigned,
    double? AverageFirstResponseMinutes,
    int Responded,
    int Resolved,
    int? CapPerAgent,
    IReadOnlyList<AgentLoad> Agents);

public sealed record VolumeRow(string Key, string Label, int Created, int Resolved, int StillOpen);

public sealed record VolumeReport(string By, IReadOnlyList<VolumeRow> Rows, int Created, int Resolved, int StillOpen);

public sealed record AgentRow(Guid Id, string Name, int Assigned, int Resolved, int OpenNow, double? MedianFirstResponseMinutes, int Responded);

/// <summary>
/// Dashboard and report numbers. Every query starts from <see cref="ITicketSearch"/>, so it counts only
/// tickets the viewer can see and never trash, spam or merged tickets (FR-ANL-8, SR-AUTHZ-2, INV-TKT-9).
/// </summary>
public sealed class InsightsQueries(SwitchboardDbContext db, ITicketSearch search, IPermissionService permissions, IActorAccessor actors, TimeProvider time)
{
    private static readonly StatusCategory[] Unresolved = [StatusCategory.Open, StatusCategory.Pending];

    public const string QueueQuery = "(is:lit OR is:overdue) (agent:me OR (agent:none group:mine))";
    public const string OverdueQuery = "is:overdue group:mine -state:pending";

    // ------------------------------------------------------------------ agent dashboard

    public async Task<DashboardData> DashboardAsync(TimeZoneInfo zone, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var (todayStart, todayEnd) = InsightsTime.Today(zone, now);
        var start = Q(todayStart);
        var end = Q(todayEnd);

        var dueToday = $"is:unresolved due>={start} due<{end} (agent:me OR group:mine)";
        var resolvedToday = $"agent:me resolved>={start}";
        var metrics = new List<MetricTile>
        {
            new("Waiting on you", await search.CountAsync(ShellContext.WaitingQuery, ct), ShellContext.WaitingQuery),
            // A stopped clock isn't overdue, so leave out statuses that pause the SLA.
            new("Overdue in your groups", await (await search.QueryAsync(OverdueQuery, ct: ct)).CountAsync(t => t.Status!.SlaTimerOn, ct), OverdueQuery),
            new("Due today", await search.CountAsync(dueToday, ct), dueToday),
            new("Unassigned in your groups", await search.CountAsync("is:unassigned is:unresolved group:mine", ct), "is:unassigned is:unresolved group:mine"),
            new("Resolved by you today", await search.CountAsync(resolvedToday, ct), resolvedToday),
        };

        var queue = await search.SearchAsync(new TicketSearchRequest { Query = QueueQuery, Sort = "lamp", PageSize = 8 }, ct);
        var trend = await TrendAsync(zone, 14, ct);
        var (groups, mine) = await WorkloadAsync(todayStart, todayEnd, ct);
        return new DashboardData(metrics, queue.Items, queue.Total, trend, groups, mine);
    }

    /// <summary>Tickets created and resolved per local day, oldest first, ending today.</summary>
    public async Task<List<DayPoint>> TrendAsync(TimeZoneInfo zone, int days, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = today.AddDays(-(days - 1));
        var from = InsightsTime.Midnight(first, zone);
        var visible = await search.QueryAsync(null, ct: ct);
        var created = await visible.Where(t => t.CreatedAt >= from).Select(t => t.CreatedAt).ToListAsync(ct);
        var resolved = await visible.Where(t => t.ResolvedAt != null && t.ResolvedAt >= from).Select(t => t.ResolvedAt!.Value).ToListAsync(ct);

        DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var c = created.GroupBy(Day).ToDictionary(g => g.Key, g => g.Count());
        var r = resolved.GroupBy(Day).ToDictionary(g => g.Key, g => g.Count());
        return Enumerable.Range(0, days).Select(i => first.AddDays(i))
            .Select(d => new DayPoint(d, c.GetValueOrDefault(d), r.GetValueOrDefault(d)))
            .ToList();
    }

    /// <summary>The viewer's groups (as member or observer); for someone in none, every group they can see into.</summary>
    private async Task<(List<GroupLoad> Groups, bool Mine)> WorkloadAsync(DateTimeOffset todayStart, DateTimeOffset todayEnd, CancellationToken ct)
    {
        var p = await permissions.CurrentAsync(ct);
        var mineIds = p is null ? [] : p.GroupIds.Concat(p.ObserverGroupIds).ToHashSet();
        var mine = mineIds.Count > 0;
        var groupQuery = db.AgentGroups.AsNoTracking();
        if (mine)
        {
            groupQuery = groupQuery.Where(g => mineIds.Contains(g.Id));
        }
        else if (p is not null && !p.IsOrgAdmin)
        {
            var workspaces = p.WorkspacesWith(Privileges.TicketsView).ToList();
            groupQuery = groupQuery.Where(g => workspaces.Contains(g.WorkspaceId));
        }
        var groups = await groupQuery.OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).Take(50).ToListAsync(ct);
        var ids = groups.Select(g => (Guid?)g.Id).ToList();

        var now = time.GetUtcNow();
        var visible = await search.QueryAsync(null, ct: ct);
        var counts = await visible
            .Where(t => ids.Contains(t.GroupId) && Unresolved.Contains(t.Status!.Category))
            .GroupBy(t => t.GroupId!.Value)
            .Select(g => new
            {
                g.Key,
                Open = g.Count(),
                Waiting = g.Count(t => t.AwaitingAgentSince != null),
                Unassigned = g.Count(t => t.AgentId == null),
                Overdue = g.Count(t => t.DueBy != null && t.DueBy < now && t.Status!.SlaTimerOn),
                DueToday = g.Count(t => t.DueBy >= todayStart && t.DueBy < todayEnd),
            })
            .ToDictionaryAsync(x => x.Key, ct);
        return (groups.Select(g => counts.TryGetValue(g.Id, out var c)
            ? new GroupLoad(g.Id, g.Name, c.Open, c.Waiting, c.Unassigned, c.Overdue, c.DueToday)
            : new GroupLoad(g.Id, g.Name, 0, 0, 0, 0, 0)).ToList(), mine);
    }

    // ------------------------------------------------------------------ team dashboard

    /// <summary>Groups in the workspaces where the viewer holds <paramref name="privilege"/>.</summary>
    public async Task<List<TeamGroup>> TeamAsync(string privilege, CancellationToken ct)
    {
        var p = await permissions.CurrentAsync(ct);
        if (p is null)
        {
            return [];
        }
        var workspaces = p.WorkspacesWith(privilege).ToList();
        var groups = await db.AgentGroups.AsNoTracking()
            .Where(g => p.IsOrgAdmin || workspaces.Contains(g.WorkspaceId))
            .OrderBy(g => g.Name)
            .Select(g => new { g.Id, g.Name, g.MaxOpenPerAgent })
            .ToListAsync(ct);
        var ids = groups.Select(g => (Guid?)g.Id).ToList();
        var now = time.GetUtcNow();
        var weekAgo = now.AddDays(-7);
        var visible = await search.QueryAsync(null, ct: ct);

        var open = await visible
            .Where(t => ids.Contains(t.GroupId) && Unresolved.Contains(t.Status!.Category))
            .GroupBy(t => t.GroupId!.Value)
            .Select(g => new
            {
                g.Key,
                Open = g.Count(),
                Unassigned = g.Count(t => t.AgentId == null),
                Overdue = g.Count(t => t.DueBy != null && t.DueBy < now && t.Status!.SlaTimerOn),
            })
            .ToDictionaryAsync(x => x.Key, ct);
        var resolved = await visible
            .Where(t => ids.Contains(t.GroupId) && t.ResolvedAt != null && t.ResolvedAt >= weekAgo)
            .GroupBy(t => t.GroupId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var responses = await visible
            .Where(t => ids.Contains(t.GroupId) && t.FirstRespondedAt != null && t.FirstRespondedAt >= weekAgo)
            .Select(t => new { GroupId = t.GroupId!.Value, t.CreatedAt, Responded = t.FirstRespondedAt!.Value })
            .ToListAsync(ct);

        var members = await db.AgentGroupMembers.AsNoTracking()
            .Where(m => ids.Contains(m.GroupId) && !m.IsObserver && m.User!.IsActive)
            .Select(m => new { m.GroupId, m.UserId, m.User!.DisplayName })
            .ToListAsync(ct);
        var memberIds = members.Select(m => (Guid?)m.UserId).Distinct().ToList();
        var load = await visible
            .Where(t => memberIds.Contains(t.AgentId) && Unresolved.Contains(t.Status!.Category))
            .GroupBy(t => t.AgentId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return groups.Select(g =>
        {
            var o = open.GetValueOrDefault(g.Id);
            var rs = responses.Where(r => r.GroupId == g.Id).Select(r => (r.Responded - r.CreatedAt).TotalMinutes).ToList();
            var agents = members.Where(m => m.GroupId == g.Id)
                .Select(m => new AgentLoad(m.UserId, m.DisplayName, load.GetValueOrDefault(m.UserId)))
                .OrderByDescending(a => a.Open).ThenBy(a => a.Name)
                .ToList();
            return new TeamGroup(g.Id, g.Name, o?.Open ?? 0, o?.Overdue ?? 0, o?.Unassigned ?? 0,
                rs.Count == 0 ? null : rs.Average(), rs.Count, resolved.GetValueOrDefault(g.Id), g.MaxOpenPerAgent, agents);
        }).ToList();
    }

    // ------------------------------------------------------------------ reports

    public static readonly (string Key, string Label)[] VolumeDimensions =
        [("status", "Status"), ("priority", "Priority"), ("group", "Group"), ("category", "Category"), ("source", "Source"), ("type", "Type")];

    /// <summary>Tickets created and resolved in [from, to), broken down by one field.</summary>
    public async Task<VolumeReport> VolumeAsync(DateTimeOffset from, DateTimeOffset to, string? by, Guid? groupId, CancellationToken ct)
    {
        by = VolumeDimensions.Any(d => d.Key == by) ? by! : "status";
        var visible = await search.QueryAsync(null, ct: ct);
        if (groupId is { } gid)
        {
            visible = visible.Where(t => t.GroupId == gid);
        }
        var createdQ = visible.Where(t => t.CreatedAt >= from && t.CreatedAt < to);
        var resolvedQ = visible.Where(t => t.ResolvedAt != null && t.ResolvedAt >= from && t.ResolvedAt < to);
        var openQ = createdQ.Where(t => Unresolved.Contains(t.Status!.Category));

        var created = await CountByAsync(createdQ, by, ct);
        var resolved = await CountByAsync(resolvedQ, by, ct);
        var stillOpen = await CountByAsync(openQ, by, ct);
        var labels = await LabelsAsync(by, created.Keys.Concat(resolved.Keys).Distinct().ToList(), ct);

        var rows = labels
            .Select(l => new VolumeRow(l.Key, l.Label, created.GetValueOrDefault(l.Key), resolved.GetValueOrDefault(l.Key), stillOpen.GetValueOrDefault(l.Key)))
            .OrderByDescending(r => r.Created).ThenByDescending(r => r.Resolved).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (by is "priority")
        {
            rows = rows.OrderByDescending(r => int.Parse(r.Key, CultureInfo.InvariantCulture)).ToList();
        }
        return new VolumeReport(by, rows, created.Values.Sum(), resolved.Values.Sum(), stillOpen.Values.Sum());
    }

    private static async Task<Dictionary<string, int>> CountByAsync(IQueryable<Ticket> q, string by, CancellationToken ct)
    {
        switch (by)
        {
            case "priority":
                return (await q.GroupBy(t => t.Priority).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct))
                    .ToDictionary(x => ((int)x.Key).ToString(CultureInfo.InvariantCulture), x => x.C);
            case "type":
                return (await q.GroupBy(t => t.Type).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct))
                    .ToDictionary(x => ((int)x.Key).ToString(CultureInfo.InvariantCulture), x => x.C);
            case "status":
                return (await q.GroupBy(t => t.StatusId).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct))
                    .ToDictionary(x => x.Key.ToString(), x => x.C);
            case "source":
                return (await q.GroupBy(t => t.SourceId).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct))
                    .ToDictionary(x => x.Key.ToString(), x => x.C);
            case "group":
                return (await q.GroupBy(t => t.GroupId).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct))
                    .ToDictionary(x => x.Key?.ToString() ?? "", x => x.C);
            default:
                return (await q.GroupBy(t => t.CategoryId).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct))
                    .ToDictionary(x => x.Key?.ToString() ?? "", x => x.C);
        }
    }

    private async Task<List<(string Key, string Label)>> LabelsAsync(string by, List<string> keys, CancellationToken ct)
    {
        var ids = keys.Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        Dictionary<Guid, string> names = by switch
        {
            "status" => await db.TicketStatuses.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct),
            "source" => await db.TicketSources.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct),
            "group" => await db.AgentGroups.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct),
            "category" => await db.TicketCategories.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct),
            _ => [],
        };
        string Label(string key) => by switch
        {
            "priority" => ((Priority)int.Parse(key, CultureInfo.InvariantCulture)).ToString(),
            "type" => (TicketType)int.Parse(key, CultureInfo.InvariantCulture) == TicketType.Incident ? "Incident" : "Service request",
            "group" when key.Length == 0 => "No group",
            "category" when key.Length == 0 => "No category",
            _ => Guid.TryParse(key, out var g) && names.TryGetValue(g, out var n) ? n : "Unknown",
        };
        return keys.Select(k => (k, Label(k))).ToList();
    }

    /// <summary>Per assigned agent: tickets created in the period, resolved in it, open now, and the median first response.</summary>
    public async Task<List<AgentRow>> AgentsAsync(DateTimeOffset from, DateTimeOffset to, Guid? groupId, CancellationToken ct)
    {
        var visible = await search.QueryAsync(null, ct: ct);
        visible = visible.Where(t => t.AgentId != null);
        if (groupId is { } gid)
        {
            visible = visible.Where(t => t.GroupId == gid);
        }
        var assigned = await visible.Where(t => t.CreatedAt >= from && t.CreatedAt < to)
            .GroupBy(t => t.AgentId!.Value).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var resolved = await visible.Where(t => t.ResolvedAt != null && t.ResolvedAt >= from && t.ResolvedAt < to)
            .GroupBy(t => t.AgentId!.Value).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var open = await visible.Where(t => Unresolved.Contains(t.Status!.Category))
            .GroupBy(t => t.AgentId!.Value).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var responses = await visible.Where(t => t.CreatedAt >= from && t.CreatedAt < to && t.FirstRespondedAt != null)
            .Select(t => new { Agent = t.AgentId!.Value, t.CreatedAt, Responded = t.FirstRespondedAt!.Value })
            .Take(50_000)
            .ToListAsync(ct);

        var agentIds = assigned.Keys.Concat(resolved.Keys).Concat(open.Keys).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => agentIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return agentIds.Select(id =>
            {
                var minutes = responses.Where(r => r.Agent == id).Select(r => (r.Responded - r.CreatedAt).TotalMinutes).ToList();
                return new AgentRow(id, names.GetValueOrDefault(id, "Unknown"), assigned.GetValueOrDefault(id), resolved.GetValueOrDefault(id),
                    open.GetValueOrDefault(id), Median(minutes), minutes.Count);
            })
            .OrderByDescending(a => a.Resolved).ThenByDescending(a => a.Assigned).ThenBy(a => a.Name)
            .ToList();
    }

    /// <summary>Groups the viewer can filter by.</summary>
    public async Task<List<(Guid Id, string Name)>> GroupsAsync(CancellationToken ct)
    {
        var p = await permissions.CurrentAsync(ct);
        if (p is null)
        {
            return [];
        }
        var workspaces = p.WorkspacesWith(Privileges.TicketsView).ToList();
        return (await db.AgentGroups.AsNoTracking().Where(g => p.IsOrgAdmin || workspaces.Contains(g.WorkspaceId))
                .OrderBy(g => g.Name).Select(g => new { g.Id, g.Name }).ToListAsync(ct))
            .Select(g => (g.Id, g.Name)).ToList();
    }

    public Task<TimeZoneInfo> ViewerZoneAsync(CancellationToken ct) => InsightsTime.ViewerZoneAsync(db, actors, ct);

    public static double? Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    /// <summary>An instant the filter language reads unambiguously.</summary>
    private static string Q(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
