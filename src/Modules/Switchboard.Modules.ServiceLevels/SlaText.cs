using System.Globalization;
using Switchboard.Modules.ServiceLevels.Domain;

namespace Switchboard.Modules.ServiceLevels;

/// <summary>Wording shared by the engine, monitor, emails and pages.</summary>
public static class SlaText
{
    public static string ConditionLabel(string field) => field switch
    {
        SlaConditionFields.Type => "Type",
        SlaConditionFields.Source => "Source",
        SlaConditionFields.RequesterGroup => "Requester group",
        SlaConditionFields.Department => "Department",
        SlaConditionFields.Category => "Category",
        SlaConditionFields.Group => "Group",
        _ when field.StartsWith(SlaConditionFields.CustomPrefix, StringComparison.Ordinal) => field[SlaConditionFields.CustomPrefix.Length..],
        _ => field,
    };

    public static string Target(SlaTargetKind kind) => kind switch
    {
        SlaTargetKind.FirstResponse => "first response",
        SlaTargetKind.NextResponse => "next response",
        _ => "resolution",
    };

    public static string TargetTitle(SlaTargetKind kind) => kind switch
    {
        SlaTargetKind.FirstResponse => "First response",
        SlaTargetKind.NextResponse => "Next response",
        _ => "Resolution",
    };

    public static string JoinOr(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "(none)",
        1 => items[0],
        2 => $"{items[0]} or {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} or {items[^1]}",
    };

    public static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };

    /// <summary>"45m", "3h 20m", "2d 4h".</summary>
    public static string Duration(TimeSpan span)
    {
        var s = span.Duration();
        if (s.TotalDays >= 1)
        {
            return s.Hours == 0 ? $"{(int)s.TotalDays}d" : $"{(int)s.TotalDays}d {s.Hours}h";
        }
        if (s.TotalHours >= 1)
        {
            return s.Minutes == 0 ? $"{(int)s.TotalHours}h" : $"{(int)s.TotalHours}h {s.Minutes}m";
        }
        return $"{Math.Max(1, (int)Math.Round(s.TotalMinutes))}m";
    }

    /// <summary>
    /// A target length: "30 minutes", "4 hours", "1 hour 30 minutes". Never days: a business "day" is
    /// ambiguous (8 working hours or 24?), so targets stay in hours.
    /// </summary>
    public static string Minutes(int? minutes)
    {
        if (minutes is not { } m)
        {
            return "No target";
        }
        var hours = m / 60;
        var rest = m % 60;
        return (hours, rest) switch
        {
            (0, _) => Plural(rest, "minute"),
            (_, 0) => Plural(hours, "hour"),
            _ => $"{Plural(hours, "hour")} {Plural(rest, "minute")}",
        };
    }

    /// <summary>Compact form for tables: "30m", "4h", "1h 30m"; an en dash for no target.</summary>
    public static string Short(int? minutes)
    {
        if (minutes is not { } m)
        {
            return "–";
        }
        var hours = m / 60;
        var rest = m % 60;
        return hours == 0 ? $"{rest}m" : rest == 0 ? $"{hours}h" : $"{hours}h {rest}m";
    }

    public static string Plural(int n, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {unit}{(n == 1 ? "" : "s")}");

    public static string Clock(int minuteOfDay)
    {
        var m = minuteOfDay % 1440;
        return string.Create(CultureInfo.InvariantCulture, $"{m / 60:00}:{m % 60:00}");
    }
}
