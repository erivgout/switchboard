using System.Globalization;
using System.Text.Json;

namespace Switchboard.Modules.Insights.Services;

/// <summary>Axis scale with clean round ticks: 0, step, 2·step, ... up to a max that covers the data.</summary>
public sealed record ChartScale(int Max, IReadOnlyList<int> Ticks)
{
    public static ChartScale For(int dataMax)
    {
        if (dataMax <= 4)
        {
            return new ChartScale(4, [0, 2, 4]);
        }
        int[] bases = [1, 2, 5];
        var magnitude = 1;
        while (true)
        {
            foreach (var b in bases)
            {
                var step = b * magnitude;
                var count = (int)Math.Ceiling(dataMax / (double)step);
                if (count <= 4)
                {
                    var max = count * step;
                    return new ChartScale(max, Enumerable.Range(0, count + 1).Select(i => i * step).ToList());
                }
            }
            magnitude *= 10;
        }
    }

    /// <summary>Position of a value as a percentage of the plot height.</summary>
    public string Percent(double value) => (Max == 0 ? 0 : value * 100.0 / Max).ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// Created (columns, de-emphasis grey) against resolved (line, accent) per day. Two marks of the same
/// unit on one axis; identity is carried by mark type, a legend, direct end labels and a table twin.
/// </summary>
public sealed class TrendChartModel
{
    public TrendChartModel(string id, IReadOnlyList<DayPoint> points)
    {
        Id = id;
        Points = points;
        Scale = ChartScale.For(points.Count == 0 ? 0 : points.Max(p => Math.Max(p.Created, p.Resolved)));
        TotalCreated = points.Sum(p => p.Created);
        TotalResolved = points.Sum(p => p.Resolved);
    }

    public string Id { get; }
    public IReadOnlyList<DayPoint> Points { get; }
    public ChartScale Scale { get; }
    public int TotalCreated { get; }
    public int TotalResolved { get; }

    public static string Format(string format, double value) => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>X centre of day <paramref name="i"/> as a percentage of the plot width.</summary>
    public string X(int i) => Format("0.###", (i + 0.5) * 100.0 / Points.Count);

    /// <summary>Points for the resolved line in a viewBox of width = days, height = 100.</summary>
    public string ResolvedLine => string.Join(" ", Points.Select((p, i) =>
        $"{Format("0.###", i + 0.5)},{Format("0.###", 100 - (Scale.Max == 0 ? 0 : p.Resolved * 100.0 / Scale.Max))}"));

    /// <summary>End labels only when they sit far enough apart; otherwise the legend and tooltip carry identity.</summary>
    public bool EndLabels => Points.Count > 0
        && Math.Abs(Points[^1].Created - Points[^1].Resolved) * 100.0 / Math.Max(1, Scale.Max) >= 14;

    public string DayLabel(DateOnly d) => d.ToString("MMM d", CultureInfo.InvariantCulture);

    public string Json => JsonSerializer.Serialize(Points.Select(p => new
    {
        d = p.Day.ToString("ddd, MMM d", CultureInfo.InvariantCulture),
        c = p.Created,
        r = p.Resolved,
    }));
}

/// <summary>A one-series horizontal bar list: a magnitude per label, value at the bar tip.</summary>
public sealed record BarItem(string Label, int Value, string? Href = null, string? Note = null);

public sealed record BarListModel(IReadOnlyList<BarItem> Items, string ValueLabel, int? Cap = null);
