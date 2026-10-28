using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.Insights.Domain;

public enum ReportSharing
{
    /// <summary>Only the owner.</summary>
    Private = 0,
    /// <summary>Everyone who can view reports.</summary>
    Everyone = 1,
    /// <summary>Members of the chosen agent groups.</summary>
    Groups = 2,
}

/// <summary>
/// A curated report saved with its filters (FR-ANL-4). It stores where to go, not results: whoever opens
/// it sees the numbers their own permissions allow (SR-AUTHZ-2).
/// </summary>
public class InsSavedReport : Entity, IHasTimestamps
{
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string? Folder { get; set; }
    /// <summary>A report page under <c>/reports/</c>.</summary>
    public string Path { get; set; } = "";
    /// <summary>The filter query string, without the leading <c>?</c>.</summary>
    public string Query { get; set; } = "";
    public ReportSharing Sharing { get; set; }
    public string SharedGroupIdsJson { get; set; } = "[]";
    /// <summary>In the owner's trash; restorable.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Guid> SharedGroupIds
    {
        get => JsonSerializer.Deserialize<List<Guid>>(SharedGroupIdsJson, JsonDefaults.Options) ?? [];
        set => SharedGroupIdsJson = JsonSerializer.Serialize(value, JsonDefaults.Options);
    }

    public string Href => string.IsNullOrEmpty(Query) ? Path : $"{Path}?{Query}";
}

public class InsReportFavorite
{
    public Guid UserId { get; set; }
    public Guid ReportId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class InsSavedReportConfiguration : IEntityTypeConfiguration<InsSavedReport>
{
    public void Configure(EntityTypeBuilder<InsSavedReport> e)
    {
        e.ToTable("InsSavedReports");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Folder).HasMaxLength(100);
        e.Property(x => x.Path).HasMaxLength(200).IsRequired();
        e.Property(x => x.Query).HasMaxLength(1000);
        e.Ignore(x => x.SharedGroupIds);
        e.Ignore(x => x.Href);
        e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.OwnerId, x.DeletedAt });
        e.HasIndex(x => new { x.Sharing, x.DeletedAt });
    }
}

internal sealed class InsReportFavoriteConfiguration : IEntityTypeConfiguration<InsReportFavorite>
{
    public void Configure(EntityTypeBuilder<InsReportFavorite> e)
    {
        e.ToTable("InsReportFavorites");
        e.HasKey(x => new { x.UserId, x.ReportId });
        e.HasOne<InsSavedReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}
