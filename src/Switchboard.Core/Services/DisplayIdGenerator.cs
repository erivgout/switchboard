using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;

namespace Switchboard.Core.Services;

/// <summary>
/// Allocates display numbers (INC-1042). Numbers are taken atomically and never reused; a rolled-back
/// transaction leaves a gap, which is allowed (INV-TKT-1).
/// </summary>
public interface IDisplayIdGenerator
{
    Task<int> NextAsync(string family, CancellationToken ct = default);
}

public sealed class DisplayIdGenerator(SwitchboardDbContext db) : IDisplayIdGenerator
{
    public async Task<int> NextAsync(string family, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var taken = await db.Database
                .SqlQuery<int>($"UPDATE [DisplayIdCounters] WITH (UPDLOCK, ROWLOCK) SET [NextValue] = [NextValue] + 1 OUTPUT deleted.[NextValue] AS [Value] WHERE [Family] = {family}")
                .ToListAsync(ct);
            if (taken.Count == 1)
            {
                return taken[0];
            }

            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [DisplayIdCounters] ([Family], [NextValue]) VALUES ({family}, 1000)", ct);
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601)
            {
                // Another request created the counter first; take a number on the next loop.
            }
        }

        throw new InvalidOperationException($"Could not allocate a display number for '{family}'.");
    }
}
