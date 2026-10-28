namespace Switchboard.Core.Domain;

/// <summary>The single organization served by this instance.</summary>
public class Organization : Entity, IHasTimestamps
{
    public string Name { get; set; } = "Switchboard";
    public string? LogoUrl { get; set; }
    public string? BaseUrl { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string Language { get; set; } = "en";
    public string DateFormat { get; set; } = "MMM d, yyyy";
    public string Currency { get; set; } = "USD";
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Monday;
    public DateTimeOffset? SetupCompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A team-scoped service desk. Exactly one is primary (the IT workspace).</summary>
public class Workspace : Entity
{
    /// <summary>URL-safe key, e.g. <c>it</c>, <c>hr</c>.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "bi-pc-display";
    public string Template { get; set; } = "it";
    public bool IsPrimary { get; set; }
    public bool IsArchived { get; set; }
    public bool IsSetupMode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
