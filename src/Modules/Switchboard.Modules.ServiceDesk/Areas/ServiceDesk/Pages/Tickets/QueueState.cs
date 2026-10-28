using Switchboard.Core.Domain;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Data;
using Switchboard.Modules.ServiceDesk.Services;

namespace Switchboard.Modules.ServiceDesk.Areas.ServiceDesk.Pages.Tickets;

/// <summary>The queue's URL state: which view, filter, sort, page, layout and columns.</summary>
public sealed record QueueState
{
    public string? View { get; init; }
    public Guid? Saved { get; init; }
    public string? Q { get; init; }
    public string? Sort { get; init; }
    public int Page { get; init; } = 1;
    public string Layout { get; init; } = "table";
    public string By { get; init; } = "status";
    public string? Cols { get; init; }

    public static readonly IReadOnlyList<(string Key, string Label)> BoardGroupings =
    [
        ("status", "Status"),
        ("priority", "Priority"),
        ("group", "Group"),
        ("agent", "Agent"),
    ];

    public string ToUrl(string? handler = null) => "/tickets" + ToQueryString(handler);

    public string ToQueryString(string? handler = null)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }
        Add("handler", handler);
        Add("view", View);
        Add("saved", Saved?.ToString());
        Add("q", Q);
        Add("sort", Sort);
        Add("p", Page > 1 ? Page.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        Add("layout", Layout == "table" ? null : Layout);
        Add("by", Layout == "board" && By != "status" ? By : null);
        Add("cols", Cols);
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }
}

/// <summary>A resolved queue: the effective query, the view it belongs to and what the agent may do.</summary>
public sealed class QueueContext
{
    public required QueueState State { get; init; }
    public required string Query { get; init; }
    public required string Sort { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required string Title { get; init; }
    public required string? Description { get; init; }
    public QueueView? ActiveView { get; init; }
    public SdSavedView? ActiveSaved { get; init; }
    public bool CanBulk { get; init; }
    public bool CanExport { get; init; }
    public bool CanPurge { get; init; }
    public bool CanShareViews { get; init; }

    public bool IsTrash => TicketQueryLanguage.Mentions(SafeParse(Query), "is", "deleted");
    public bool IsSpam => TicketQueryLanguage.Mentions(SafeParse(Query), "is", "spam");
    public bool IsBoard => State.Layout == "board";

    /// <summary>True when the filter differs from the view it started from, so it can be saved.</summary>
    public bool IsCustom => ActiveView is null && ActiveSaved is null;

    public bool Shows(string column) => Columns.Contains(column);

    private static TicketQueryLanguage.Node? SafeParse(string query)
    {
        try
        {
            return TicketQueryLanguage.Parse(query);
        }
        catch (Core.Security.ValidationException)
        {
            return null;
        }
    }
}

/// <summary>One column of the Kanban board.</summary>
public sealed record BoardColumn(string Key, string Title, string Value, IReadOnlyList<TicketListItem> Items, StatusCategory? Category = null);

public sealed class QueueResults
{
    public required QueueContext Context { get; init; }
    public TicketSearchResult? Result { get; init; }
    public IReadOnlyList<BoardColumn> Board { get; init; } = [];
    public int BoardTotal { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset Now { get; init; }

    public bool IsOverdue(TicketListItem t) =>
        t.DueBy is { } due && due < Now && t.StatusCategory is StatusCategory.Open or StatusCategory.Pending;
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int max) => value.Length <= max ? value : value[..max].TrimEnd() + "…";
}

