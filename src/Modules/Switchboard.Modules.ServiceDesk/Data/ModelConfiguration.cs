using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.ServiceDesk.Data;

internal sealed class SdSavedViewConfiguration : IEntityTypeConfiguration<SdSavedView>
{
    public void Configure(EntityTypeBuilder<SdSavedView> e)
    {
        e.ToTable("SdSavedViews");
        e.Property(x => x.Name).HasMaxLength(100).IsRequired();
        e.Property(x => x.Query).HasMaxLength(2000).IsRequired();
        e.Property(x => x.Sort).HasMaxLength(32);
        e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.OwnerId, x.Name });
        e.HasIndex(x => x.IsShared);
    }
}

internal sealed class SdCannedResponseFolderConfiguration : IEntityTypeConfiguration<SdCannedResponseFolder>
{
    public void Configure(EntityTypeBuilder<SdCannedResponseFolder> e)
    {
        e.ToTable("SdCannedResponseFolders");
        e.Property(x => x.Name).HasMaxLength(100).IsRequired();
        e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<AgentGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.Visibility, x.OwnerId });
    }
}

internal sealed class SdCannedResponseConfiguration : IEntityTypeConfiguration<SdCannedResponse>
{
    public void Configure(EntityTypeBuilder<SdCannedResponse> e)
    {
        e.ToTable("SdCannedResponses");
        e.Property(x => x.Title).HasMaxLength(200).IsRequired();
        e.HasOne(x => x.Folder).WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.Title);
    }
}

internal sealed class SdTicketTemplateConfiguration : IEntityTypeConfiguration<SdTicketTemplate>
{
    public void Configure(EntityTypeBuilder<SdTicketTemplate> e)
    {
        e.ToTable("SdTicketTemplates");
        e.Property(x => x.Name).HasMaxLength(100).IsRequired();
        e.Property(x => x.Description).HasMaxLength(500);
        e.Property(x => x.Subject).HasMaxLength(500);
        e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.WorkspaceId, x.Name });
    }
}

internal sealed class SdBulkJobConfiguration : IEntityTypeConfiguration<SdBulkJob>
{
    public void Configure(EntityTypeBuilder<SdBulkJob> e)
    {
        e.ToTable("SdBulkJobs");
        e.Property(x => x.Action).HasMaxLength(32).IsRequired();
        e.Property(x => x.Summary).HasMaxLength(300).IsRequired();
        e.Property(x => x.CreatedByName).HasMaxLength(200);
        e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.Status);
        e.HasIndex(x => x.CreatedById);
    }
}

internal sealed class SdBulkJobItemConfiguration : IEntityTypeConfiguration<SdBulkJobItem>
{
    public void Configure(EntityTypeBuilder<SdBulkJobItem> e)
    {
        e.ToTable("SdBulkJobItems");
        e.Property(x => x.DisplayId).HasMaxLength(32);
        e.Property(x => x.Error).HasMaxLength(1000);
        e.HasIndex(x => new { x.JobId, x.Order });
    }
}
