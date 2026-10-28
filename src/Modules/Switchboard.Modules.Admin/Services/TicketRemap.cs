using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;

namespace Switchboard.Modules.Admin.Services;

/// <summary>
/// Moves tickets off configuration that is being deleted (SR-DEL-2, INV-REF-1): statuses, categories, sources,
/// groups and departments. Trashed and merged tickets are included, since they still hold the reference.
/// Each ticket gets one activity entry and a <see cref="TicketUpdated"/> event, like any other ticket change.
/// </summary>
internal static class TicketRemap
{
    private const int BatchSize = 500;

    /// <param name="mutate">Changes the ticket so it no longer matches <paramref name="filter"/>; returns the field changes.</param>
    /// <param name="extra">Further events per ticket, e.g. <see cref="TicketStatusChanged"/>.</param>
    public static async Task<int> ApplyAsync(
        SwitchboardDbContext db,
        Expression<Func<Ticket, bool>> filter,
        Func<Ticket, IReadOnlyList<FieldChange>> mutate,
        string summary,
        CancellationToken ct,
        Func<Ticket, IEnumerable<DomainEvent>>? extra = null)
    {
        var total = await db.Tickets.CountAsync(filter, ct);
        for (var done = 0; done < total;)
        {
            var batch = await db.Tickets.Where(filter).OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }
            foreach (var t in batch)
            {
                // Extra events are built first so they can read the old values.
                var events = extra?.Invoke(t).ToList() ?? [];
                var changes = mutate(t);
                db.Activity(t, "updated", summary, changes);
                db.Raise(new TicketUpdated(t.Id, t.WorkspaceId, changes));
                foreach (var e in events)
                {
                    db.Raise(e);
                }
            }
            await db.SaveChangesAsync(ct);
            done += batch.Count;
        }
        return total;
    }
}
