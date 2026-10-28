using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Components;
using Switchboard.Modules.ServiceLevels.Domain;

namespace Switchboard.Modules.ServiceLevels.Reports;

public enum SlaOutcome
{
    NoTarget,
    Pending,
    Met,
    Missed,
}

public sealed record SlaReportRow(
    Guid TicketId,
    int Number,
    TicketType Type,
    string Subject,
    Priority Priority,
    Guid? GroupId,
    string? GroupName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FirstResponseDueBy,
    DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? DueBy,
    DateTimeOffset? ResolvedAt,
    Guid? PolicyId,
    string? PolicyName,
    DateTimeOffset? FirstResponseTargetAt,
    DateTimeOffset? ResolutionTargetAt,
    bool FirstResponseOverridden,
    bool ResolutionOverridden,
    string? OverrideReason,
    DateTimeOffset? PausedSince,
    int Breaches)
{
    public string DisplayId => Ticket.FormatDisplayId(Type, Number);
    public bool Overridden => FirstResponseOverridden || ResolutionOverridden;
}

public sealed record SlaJudgedRow(SlaReportRow Row, SlaOutcome FirstResponse, SlaOutcome Resolution, SlaOutcome ResolutionAgainstOriginal);

public sealed record SlaGroupLine(string Key, string Label, int Tickets, int FirstResponseMet, int FirstResponseMissed, int ResolutionMet, int ResolutionMissed, int Overridden, int Breaches)
{
    public double? FirstResponseRate => FirstResponseMet + FirstResponseMissed == 0 ? null : FirstResponseMet / (double)(FirstResponseMet + FirstResponseMissed);
    public double? ResolutionRate => ResolutionMet + ResolutionMissed == 0 ? null : ResolutionMet / (double)(ResolutionMet + ResolutionMissed);
}

public sealed record SlaComplianceReport(
    DateTimeOffset From,
    DateTimeOffset To,
    string GroupBy,
    SlaGroupLine Total,
    IReadOnlyList<SlaGroupLine> Lines,
    IReadOnlyList<SlaJudgedRow> Exceptions,
    IReadOnlyList<SlaJudgedRow> Rows,
    bool Truncated);

/// <summary>
/// SLA reporting over the tickets the viewer can see (FR-ANL-8, SR-AUTHZ-2). Outcomes are judged
/// against the effective due date; the original SLA target is kept beside any manual override (FR-SLA-6).
/// </summary>
public sealed class SlaReportService(SwitchboardDbContext db, IPermissionService permissions, TimeProvider time)
{
    public const int MaxRows = 20_000;

    /// <summary>Targets decided (met, or past due unmet) between <paramref name="from"/> and <paramref name="to"/>.</summary>
    public async Task<SlaPerformanceModel> PerformanceAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var visible = await VisibleAsync(ct);
        var states = db.Set<SlaTicketState>();
        var q = from t in visible
                join s in states on t.Id equals s.TicketId into sj
                from s in sj.DefaultIfEmpty()
                select new { t, PausedSince = (DateTimeOffset?)s.PausedSince };

        var frMet = await q.CountAsync(x => x.t.FirstResponseDueBy != null && x.t.FirstRespondedAt != null
            && x.t.FirstRespondedAt <= x.t.FirstResponseDueBy && x.t.FirstRespondedAt >= from && x.t.FirstRespondedAt <= to, ct);
        var frMissed = await q.CountAsync(x => x.t.FirstResponseDueBy != null && x.t.FirstResponseDueBy >= from && x.t.FirstResponseDueBy <= to
            && (x.t.FirstRespondedAt != null
                ? x.t.FirstRespondedAt > x.t.FirstResponseDueBy
                : x.PausedSince == null || x.PausedSince > x.t.FirstResponseDueBy), ct);
        var resMet = await q.CountAsync(x => x.t.DueBy != null && x.t.ResolvedAt != null
            && x.t.ResolvedAt <= x.t.DueBy && x.t.ResolvedAt >= from && x.t.ResolvedAt <= to, ct);
        var resMissed = await q.CountAsync(x => x.t.DueBy != null && x.t.DueBy >= from && x.t.DueBy <= to
            && (x.t.ResolvedAt != null
                ? x.t.ResolvedAt > x.t.DueBy
                : x.PausedSince == null || x.PausedSince > x.t.DueBy), ct);
        return new SlaPerformanceModel(frMet, frMet + frMissed, resMet, resMet + resMissed);
    }

    /// <summary>Tickets created in [from, to), judged against their targets.</summary>
    public async Task<SlaComplianceReport> ComplianceAsync(DateTimeOffset from, DateTimeOffset to, string? groupBy, Guid? groupId, Priority? priority, CancellationToken ct)
    {
        groupBy = groupBy is "group" or "policy" ? groupBy : "priority";
        var rows = await RowsAsync(from, to, groupId, priority, ct);
        var now = time.GetUtcNow();
        var judged = rows.Take(MaxRows).Select(r => Judge(r, now)).ToList();

        var lines = judged
            .GroupBy(j => groupBy switch
            {
                "group" => (Key: j.Row.GroupId?.ToString() ?? "", Label: j.Row.GroupName ?? "No group", Sort: j.Row.GroupName ?? "￿"),
                "policy" => (Key: j.Row.PolicyId?.ToString() ?? "", Label: j.Row.PolicyName ?? "No policy", Sort: j.Row.PolicyName ?? "￿"),
                _ => (Key: j.Row.Priority.ToString(), Label: j.Row.Priority.ToString(), Sort: (9 - (int)j.Row.Priority).ToString()),
            })
            .OrderBy(g => g.Key.Sort, StringComparer.OrdinalIgnoreCase)
            .Select(g => Line(g.Key.Key, g.Key.Label, g.ToList()))
            .ToList();
        var exceptions = judged
            .Where(j => j.FirstResponse == SlaOutcome.Missed || j.Resolution == SlaOutcome.Missed || j.Row.Overridden)
            .OrderByDescending(j => j.Row.CreatedAt)
            .ToList();
        return new SlaComplianceReport(from, to, groupBy, Line("", "All tickets", judged), lines, exceptions, judged, rows.Count > MaxRows);
    }

    public static SlaOutcome Outcome(DateTimeOffset? due, DateTimeOffset? doneAt, DateTimeOffset? pausedSince, DateTimeOffset now)
    {
        if (due is not { } d)
        {
            return SlaOutcome.NoTarget;
        }
        if (doneAt is { } done)
        {
            return done <= d ? SlaOutcome.Met : SlaOutcome.Missed;
        }
        // A paused clock hasn't run out, unless it ran out before the pause.
        if (d <= now && (pausedSince is not { } p || p > d))
        {
            return SlaOutcome.Missed;
        }
        return SlaOutcome.Pending;
    }

    private static SlaJudgedRow Judge(SlaReportRow r, DateTimeOffset now) => new(
        r,
        Outcome(r.FirstResponseDueBy, r.FirstRespondedAt, r.PausedSince, now),
        Outcome(r.DueBy, r.ResolvedAt, r.PausedSince, now),
        Outcome(r.ResolutionTargetAt ?? r.DueBy, r.ResolvedAt, r.PausedSince, now));

    private static SlaGroupLine Line(string key, string label, List<SlaJudgedRow> rows) => new(
        key, label, rows.Count,
        rows.Count(r => r.FirstResponse == SlaOutcome.Met),
        rows.Count(r => r.FirstResponse == SlaOutcome.Missed),
        rows.Count(r => r.Resolution == SlaOutcome.Met),
        rows.Count(r => r.Resolution == SlaOutcome.Missed),
        rows.Count(r => r.Row.Overridden),
        rows.Sum(r => r.Row.Breaches));

    private async Task<List<SlaReportRow>> RowsAsync(DateTimeOffset from, DateTimeOffset to, Guid? groupId, Priority? priority, CancellationToken ct)
    {
        var visible = await VisibleAsync(ct);
        visible = visible.Where(t => t.CreatedAt >= from && t.CreatedAt < to);
        if (groupId is { } g)
        {
            visible = visible.Where(t => t.GroupId == g);
        }
        if (priority is { } p)
        {
            visible = visible.Where(t => t.Priority == p);
        }
        var states = db.Set<SlaTicketState>();
        var events = db.Set<SlaEvent>();
        return await (
            from t in visible
            join s in states on t.Id equals s.TicketId into sj
            from s in sj.DefaultIfEmpty()
            orderby t.CreatedAt descending
            select new SlaReportRow(
                t.Id, t.Number, t.Type, t.Subject, t.Priority, t.GroupId, t.Group != null ? t.Group.Name : null, t.CreatedAt,
                t.FirstResponseDueBy, t.FirstRespondedAt, t.DueBy, t.ResolvedAt,
                s.PolicyId, s.PolicyName, s.FirstResponseTargetAt, s.ResolutionTargetAt,
                s != null && s.FirstResponseOverridden, s != null && s.ResolutionOverridden, s.OverrideReason, (DateTimeOffset?)s.PausedSince,
                events.Count(e => e.TicketId == t.Id && e.Kind == SlaEventKind.Breach)))
            .Take(MaxRows + 1)
            .ToListAsync(ct);
    }

    /// <summary>Visible tickets, minus trash, spam, merged and purged ones (INV-TKT-9).</summary>
    private async Task<IQueryable<Ticket>> VisibleAsync(CancellationToken ct) =>
        (await permissions.AgentVisibleTicketsAsync(ct)).AsNoTracking()
            .Where(t => t.DeletedAt == null && !t.IsSpam && t.MergedIntoId == null && t.PurgedAt == null);
}
