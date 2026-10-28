using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.Admin.Data;

public enum ImportStatus
{
    /// <summary>File parsed; columns not mapped yet.</summary>
    Uploaded = 1,
    Completed = 2,
}

/// <summary>A CSV import of requesters (FR-ADM-7): the file, its column mapping and the result.</summary>
public sealed class AdmImport : Entity
{
    public string Kind { get; set; } = "requesters";
    public string FileName { get; set; } = "";
    public string CsvText { get; set; } = "";
    /// <summary>Target field key → source column header.</summary>
    public string MappingJson { get; set; } = "{}";
    public bool UpdateExisting { get; set; }
    public ImportStatus Status { get; set; } = ImportStatus.Uploaded;
    public Guid? CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int TotalRows { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    /// <summary>JSON array of <see cref="Services.ImportRowError"/>.</summary>
    public string ErrorsJson { get; set; } = "[]";
}

/// <summary>Records that a duplicate requester was merged into another (FR-IAM-2). The source row stays for references.</summary>
public sealed class AdmUserMerge : Entity
{
    public Guid SourceUserId { get; set; }
    public Guid SurvivorUserId { get; set; }
    public Guid? MergedById { get; set; }
    public DateTimeOffset MergedAt { get; set; }
}

internal sealed class AdmImportConfiguration : IEntityTypeConfiguration<AdmImport>
{
    public void Configure(EntityTypeBuilder<AdmImport> e)
    {
        e.ToTable("AdmImports");
        e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
        e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        e.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class AdmUserMergeConfiguration : IEntityTypeConfiguration<AdmUserMerge>
{
    public void Configure(EntityTypeBuilder<AdmUserMerge> e)
    {
        e.ToTable("AdmUserMerges");
        e.HasOne<User>().WithMany().HasForeignKey(x => x.SourceUserId).OnDelete(DeleteBehavior.NoAction);
        e.HasOne<User>().WithMany().HasForeignKey(x => x.SurvivorUserId).OnDelete(DeleteBehavior.NoAction);
        e.HasIndex(x => x.SourceUserId).IsUnique();
        e.HasIndex(x => x.SurvivorUserId);
    }
}
