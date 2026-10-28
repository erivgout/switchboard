using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;

namespace Switchboard.Modules.Approvals.Web;

/// <summary>
/// Name/email search behind the people picker (approvers, delegates, "requested for", user lookup fields).
/// Signed-in users only; returns at most 8 active people.
/// </summary>
public static class PeopleSearch
{
    public sealed record Person(Guid Id, string Name, string? Detail);

    public static async Task<IResult> HandleAsync(string? q, bool? agents, SwitchboardDbContext db, IActorAccessor actors, CancellationToken ct)
    {
        if (actors.Current.UserId is null)
        {
            return Results.Unauthorized();
        }
        var term = (q ?? "").Trim();
        if (term.Length < 2 || term.Length > 100)
        {
            return Results.Json(Array.Empty<Person>());
        }
        var normalized = term.ToUpperInvariant();
        var query = db.Users.AsNoTracking()
            .Where(u => u.IsActive && !u.IsForgotten)
            .Where(u => u.DisplayName.Contains(term) || (u.NormalizedEmail != null && u.NormalizedEmail.StartsWith(normalized)));
        if (agents == true)
        {
            query = query.Where(u => u.IsAgent || u.IsOrgAdmin);
        }
        var people = await query
            .OrderBy(u => u.DisplayName)
            .Take(8)
            .Select(u => new Person(u.Id, u.DisplayName, u.JobTitle != null ? u.JobTitle + ", " + u.Email : u.Email))
            .ToListAsync(ct);
        return Results.Json(people);
    }
}
