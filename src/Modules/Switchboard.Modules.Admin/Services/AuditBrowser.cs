using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Admin.Services;

public sealed record AuditQuery
{
    public const int PageSize = 50;
    public string? Actor { get; init; }
    public string? Action { get; init; }
    public string? ObjectType { get; init; }
    public string? ObjectId { get; init; }
    /// <summary>Inclusive start (UTC).</summary>
    public DateTimeOffset? From { get; init; }
    /// <summary>Exclusive end (UTC).</summary>
    public DateTimeOffset? To { get; init; }
    public int Page { get; init; } = 1;
}

/// <summary>Result of verifying the hash chain (SR-AUD-2).</summary>
public sealed record ChainStatus(bool Intact, long? BrokenAt, int Unsealed, DateTimeOffset CheckedAt);

/// <summary>The audit log admin UI (FR-ADM-4): filtering, one entry, CSV export and chain integrity.</summary>
public sealed class AuditBrowser(SwitchboardDbContext db, AdminAccess access, IAuditLog audit, TimeProvider time)
{
    public const int MaxExportRows = 50_000;

    public async Task<Paged<AuditEntry>> SearchAsync(AuditQuery query, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.AuditView, ct: ct);
        var filtered = Filter(query);
        var total = await filtered.CountAsync(ct);
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (total + AuditQuery.PageSize - 1) / AuditQuery.PageSize));
        var items = await filtered.OrderByDescending(a => a.Sequence)
            .Skip((page - 1) * AuditQuery.PageSize).Take(AuditQuery.PageSize).ToListAsync(ct);
        return new Paged<AuditEntry>(items, total, page, AuditQuery.PageSize);
    }

    public async Task<AuditEntry> GetAsync(long sequence, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.AuditView, ct: ct);
        return await db.AuditEntries.AsNoTracking().FirstOrDefaultAsync(a => a.Sequence == sequence, ct) ?? throw new NotFoundException();
    }

    /// <summary>Distinct object types, for the filter list.</summary>
    public async Task<IReadOnlyList<string>> ObjectTypesAsync(CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.AuditView, ct: ct);
        return await db.AuditEntries.AsNoTracking().Where(a => a.ObjectType != null).Select(a => a.ObjectType!).Distinct().OrderBy(t => t).Take(200).ToListAsync(ct);
    }

    /// <summary>Exports matching entries as CSV. The export is itself recorded (SR-AUD-1).</summary>
    public async Task<(byte[] Content, int Rows)> ExportCsvAsync(AuditQuery query, CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.AuditView, ct: ct);
        var entries = await Filter(query).OrderBy(a => a.Sequence).Take(MaxExportRows).ToListAsync(ct);
        var rows = new List<IEnumerable<string?>>
        {
            new[] { "Sequence", "Time (UTC)", "Actor", "Actor kind", "Actor ID", "On behalf of", "Channel", "Client", "IP", "User agent", "Action", "Object type", "Object ID", "Details", "Request ID", "Hash" },
        };
        rows.AddRange(entries.Select(e => new[]
        {
            e.Sequence.ToString(CultureInfo.InvariantCulture),
            e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            e.ActorName, e.ActorKind.ToString(), e.ActorUserId?.ToString(), e.OnBehalfOfUserId?.ToString(), e.Channel.ToString(),
            e.ClientName, e.Ip, e.UserAgent, e.Action, e.ObjectType, e.ObjectId, e.DetailsJson, e.RequestId, e.Hash,
        }));
        audit.Record("audit.exported", "audit_log", null, new
        {
            rows = entries.Count,
            filter = new { query.Actor, query.Action, query.ObjectType, query.ObjectId, query.From, query.To },
        });
        await db.SaveChangesAsync(ct);
        return (Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Csv.Write(rows))).ToArray(), entries.Count);
    }

    /// <summary>Walks the whole hash chain with <see cref="AuditChainSealer.VerifyAsync"/>.</summary>
    public async Task<ChainStatus> VerifyChainAsync(CancellationToken ct = default)
    {
        await access.DemandAsync(Privileges.AuditView, ct: ct);
        var broken = await AuditChainSealer.VerifyAsync(db, ct);
        var unsealed = await db.AuditEntries.CountAsync(a => a.Hash == null, ct);
        return new ChainStatus(broken is null, broken, unsealed, time.GetUtcNow());
    }

    private IQueryable<AuditEntry> Filter(AuditQuery q)
    {
        var entries = db.AuditEntries.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Actor))
        {
            var actor = q.Actor.Trim();
            entries = Guid.TryParse(actor, out var id)
                ? entries.Where(a => a.ActorUserId == id)
                : entries.Where(a => a.ActorName.Contains(actor) || (a.ClientName != null && a.ClientName.Contains(actor)));
        }
        if (!string.IsNullOrWhiteSpace(q.Action))
        {
            var action = q.Action.Trim();
            entries = entries.Where(a => a.Action.Contains(action));
        }
        if (!string.IsNullOrWhiteSpace(q.ObjectType))
        {
            var type = q.ObjectType.Trim();
            entries = entries.Where(a => a.ObjectType == type);
        }
        if (!string.IsNullOrWhiteSpace(q.ObjectId))
        {
            var objectId = q.ObjectId.Trim();
            entries = entries.Where(a => a.ObjectId == objectId);
        }
        if (q.From is { } from)
        {
            entries = entries.Where(a => a.OccurredAt >= from);
        }
        if (q.To is { } to)
        {
            entries = entries.Where(a => a.OccurredAt < to);
        }
        return entries;
    }
}
