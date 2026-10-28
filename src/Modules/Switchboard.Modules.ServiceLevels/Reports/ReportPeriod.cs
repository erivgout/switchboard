using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceLevels.Time;

namespace Switchboard.Modules.ServiceLevels.Reports;

/// <summary>A report's date range: whole local days in the viewer's timezone, as instants.</summary>
public sealed record ReportPeriod(string Key, DateTimeOffset From, DateTimeOffset To, DateOnly FirstDay, DateOnly LastDay)
{
    public static readonly (string Key, string Label)[] Presets = [("7", "Last 7 days"), ("30", "Last 30 days"), ("90", "Last 90 days"), ("custom", "Custom dates")];

    public static ReportPeriod Resolve(string? period, string? from, string? to, TimeZoneInfo zone, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (period == "custom"
            && DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            && DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            && f <= t && t.DayNumber - f.DayNumber <= 366)
        {
            return new ReportPeriod("custom", Midnight(f, zone), Midnight(t.AddDays(1), zone), f, t);
        }
        var days = period switch { "30" => 30, "90" => 90, _ => 7 };
        var first = today.AddDays(-(days - 1));
        return new ReportPeriod(days.ToString(CultureInfo.InvariantCulture), Midnight(first, zone), Midnight(today.AddDays(1), zone), first, today);
    }

    public string Label => Key == "custom"
        ? $"{FirstDay.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} to {LastDay.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}"
        : Presets.First(p => p.Key == Key).Label;

    private static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone) =>
        BusinessCalendar.ToInstant(day.ToDateTime(TimeOnly.MinValue), zone);

    /// <summary>The viewer's timezone, else the organization's, else UTC.</summary>
    public static async Task<TimeZoneInfo> ViewerZoneAsync(SwitchboardDbContext db, IActorAccessor actors, CancellationToken ct)
    {
        var uid = actors.Current.UserId;
        var mine = uid is null ? null : await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.TimeZone).FirstOrDefaultAsync(ct);
        var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => o.TimeZone).FirstOrDefaultAsync(ct);
        return BusinessCalendar.FindZone(mine) ?? BusinessCalendar.FindZone(org) ?? TimeZoneInfo.Utc;
    }
}
