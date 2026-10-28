using Switchboard.UI;

namespace Switchboard.Modules.ServiceDesk.Services;

/// <summary>A built-in queue view: a filter-language query with a default sort.</summary>
public sealed record QueueView(string Key, string Title, string Query, string Sort, string Icon, string Description);

/// <summary>The default views every agent sees in the queue sidebar (FR-TKT-18).</summary>
public static class QueueViews
{
    public const string Waiting = "waiting";
    public const string Spam = "spam";
    public const string Trash = "trash";

    public static IReadOnlyList<QueueView> All { get; } =
    [
        new(Waiting, "Waiting on you", ShellContext.WaitingQuery, "lamp", "bi-lightbulb",
            "Tickets waiting on an agent: assigned to you, or unassigned in your groups."),
        new("mine", "My open", "agent:me state:unresolved", "lamp", "bi-person",
            "Unresolved tickets assigned to you."),
        new("groups", "My groups", "group:mine state:unresolved", "lamp", "bi-people",
            "Unresolved tickets in the groups you work in."),
        new("unassigned", "Unassigned", "agent:none state:unresolved", "lamp", "bi-person-dash",
            "Unresolved tickets nobody has picked up yet."),
        new("unresolved", "All unresolved", "state:unresolved", "lamp", "bi-inbox",
            "Every open or pending ticket you can see."),
        new("overdue", "Overdue", "is:overdue", "due", "bi-alarm",
            "Unresolved tickets past their resolution target."),
        new("today", "Due today", "due:today state:unresolved", "due", "bi-calendar-event",
            "Unresolved tickets due before the end of today (UTC)."),
        new("major", "Major incidents", "is:major state:unresolved", "lamp", "bi-broadcast",
            "Unresolved major incidents."),
        new("resolved", "Recently resolved", "resolved>-7d", "-updated", "bi-check2-circle",
            "Tickets resolved or closed in the last 7 days."),
        new(Spam, "Spam", "is:spam", "-updated", "bi-slash-circle",
            "Tickets marked as spam. Restore anything that isn't."),
        new(Trash, "Trash", "is:deleted", "-updated", "bi-trash3",
            "Deleted tickets. They're permanently deleted after the retention period."),
    ];

    public static QueueView? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sort options shown in the queue, with plain labels.</summary>
    public static IReadOnlyList<(string Value, string Label)> Sorts { get; } =
    [
        ("lamp", "Waiting first, then due"),
        ("-updated", "Recently updated"),
        ("updated", "Least recently updated"),
        ("-created", "Newest first"),
        ("created", "Oldest first"),
        ("due", "Due soonest"),
        ("-priority", "Highest priority"),
        ("-number", "Highest number"),
    ];

    public static string NormalizeSort(string? sort) =>
        Sorts.Any(s => s.Value == sort) ? sort! : "lamp";
}

/// <summary>Optional queue columns. Lamp, ID and subject are always shown.</summary>
public static class QueueColumns
{
    public static IReadOnlyList<(string Key, string Label)> All { get; } =
    [
        ("status", "Status"),
        ("priority", "Priority"),
        ("assignee", "Group and agent"),
        ("due", "Due"),
        ("created", "Created"),
        ("updated", "Updated"),
        ("source", "Source"),
    ];

    public static IReadOnlyList<string> Default { get; } = ["status", "priority", "assignee", "due"];

    public static List<string> Normalize(IEnumerable<string>? columns)
    {
        var picked = (columns ?? [])
            .SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(c => c.ToLowerInvariant())
            .Where(c => All.Any(a => a.Key == c))
            .Distinct()
            .ToList();
        return picked.Count == 0 ? [.. Default] : All.Select(a => a.Key).Where(picked.Contains).ToList();
    }
}
