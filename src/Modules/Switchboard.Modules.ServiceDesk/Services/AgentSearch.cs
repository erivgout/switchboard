using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;

namespace Switchboard.Modules.ServiceDesk.Services;

public sealed record AgentSearchResult(
    string Query,
    int? RedirectNumber,
    TicketSearchResult? Tickets,
    IReadOnlyList<PersonOption> People,
    bool CanSeePeople,
    string? TicketError);

/// <summary>Global agent search (/search): ticket references jump straight to the ticket; otherwise tickets and people.</summary>
public sealed class AgentSearch(SwitchboardDbContext db, IPermissionService permissions, ITicketSearch search)
{
    public async Task<AgentSearchResult> SearchAsync(string? q, int page = 1, CancellationToken ct = default)
    {
        var query = (q ?? "").Trim();
        if (query.Length == 0)
        {
            return new AgentSearchResult("", null, null, [], false, null);
        }
        if (query.Length > 500)
        {
            query = query[..500];
        }

        if (await FindTicketNumberAsync(query, ct) is { } number)
        {
            return new AgentSearchResult(query, number, null, [], false, null);
        }

        TicketSearchResult? tickets = null;
        string? error = null;
        try
        {
            // The filter language first, so "priority:high vpn" works; plain text if it doesn't parse.
            tickets = await search.SearchAsync(new TicketSearchRequest { Query = query, Sort = "-updated", Page = page, PageSize = 20 }, ct);
        }
        catch (ValidationException)
        {
            try
            {
                tickets = await search.SearchAsync(new TicketSearchRequest { Query = TicketReference.ToTextQuery(query), Sort = "-updated", Page = page, PageSize = 20 }, ct);
            }
            catch (ValidationException ex)
            {
                error = ex.Message;
            }
        }

        var canSeePeople = await permissions.HasAsync(Privileges.UsersView, ct: ct);
        IReadOnlyList<PersonOption> people = [];
        if (canSeePeople && query.Length >= 2)
        {
            var normalized = query.ToUpperInvariant();
            people = await db.Users.AsNoTracking()
                .Where(u => u.IsActive && !u.IsForgotten && (u.DisplayName.Contains(query) || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(normalized))))
                .OrderBy(u => u.DisplayName)
                .Take(10)
                .Select(u => new PersonOption(u.Id, u.DisplayName, u.Email, u.Department != null ? u.Department.Name : null, u.JobTitle, u.IsVip, u.IsAgent))
                .ToListAsync(ct);
        }
        return new AgentSearchResult(query, null, tickets, people, canSeePeople, error);
    }

    /// <summary>The number of the referenced ticket (INC-123, SR-123, #123) when the current agent can see it.</summary>
    public async Task<int?> FindTicketNumberAsync(string? q, CancellationToken ct = default)
    {
        if (!TicketReference.TryParse(q, out var number))
        {
            return null;
        }
        var visible = await permissions.AgentVisibleTicketsAsync(ct);
        return await visible.AnyAsync(t => t.Number == number && t.PurgedAt == null, ct) ? number : null;
    }
}
