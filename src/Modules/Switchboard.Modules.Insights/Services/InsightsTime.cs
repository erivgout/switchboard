using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;

namespace Switchboard.Modules.Insights.Services;

/// <summary>Local days in the viewer's timezone, as instants.</summary>
public static class InsightsTime
{
    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>The viewer's timezone, else the organization's, else UTC.</summary>
    public static async Task<TimeZoneInfo> ViewerZoneAsync(SwitchboardDbContext db, IActorAccessor actors, CancellationToken ct)
    {
        var uid = actors.Current.UserId;
        var mine = uid is null ? null : await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.TimeZone).FirstOrDefaultAsync(ct);
        var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => o.TimeZone).FirstOrDefaultAsync(ct);
        return FindZone(mine) ?? FindZone(org) ?? TimeZoneInfo.Utc;
    }

    /// <summary>The instant a local day starts. A midnight skipped by a DST change starts at the jump.</summary>
    public static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static (DateTimeOffset Start, DateTimeOffset End) Today(TimeZoneInfo zone, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return (Midnight(today, zone), Midnight(today.AddDays(1), zone));
    }
}

/// <summary>A report's date range: whole local days.</summary>
public sealed record InsightsPeriod(string Key, DateTimeOffset From, DateTimeOffset To, DateOnly FirstDay, DateOnly LastDay)
{
    public static readonly (string Key, string Label)[] Presets = [("7", "Last 7 days"), ("30", "Last 30 days"), ("90", "Last 90 days"), ("custom", "Custom dates")];

    public static InsightsPeriod Resolve(string? period, string? from, string? to, TimeZoneInfo zone, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (period == "custom"
            && DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            && DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            && f <= t && t.DayNumber - f.DayNumber <= 366)
        {
            return new InsightsPeriod("custom", InsightsTime.Midnight(f, zone), InsightsTime.Midnight(t.AddDays(1), zone), f, t);
        }
        var days = period switch { "30" => 30, "90" => 90, _ => 7 };
        var first = today.AddDays(-(days - 1));
        return new InsightsPeriod(days.ToString(CultureInfo.InvariantCulture), InsightsTime.Midnight(first, zone), InsightsTime.Midnight(today.AddDays(1), zone), first, today);
    }

    public string Label => Key == "custom"
        ? $"{FirstDay.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} to {LastDay.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}"
        : Presets.First(p => p.Key == Key).Label;

    public string Sentence =>
        $"From {FirstDay.ToString("MMM d", CultureInfo.InvariantCulture)} to {LastDay.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}.";

    /// <summary>The query string that reproduces this range.</summary>
    public string QueryPart => Key == "custom"
        ? $"period=custom&from={FirstDay:yyyy-MM-dd}&to={LastDay:yyyy-MM-dd}"
        : $"period={Key}";
}

/// <summary>RFC 4180 CSV with spreadsheet formula injection neutralized (SR-INP-6).</summary>
public static class InsightsCsv
{
    public static byte[] Write(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(',', row.Select(Cell));
            sb.Append("\r\n");
        }
        return [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string Number(double? value, int decimals = 0) =>
        value is { } v ? Math.Round(v, decimals).ToString(CultureInfo.InvariantCulture) : "";
}
