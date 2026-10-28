using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Switchboard.Core.Domain;
using Switchboard.Modules.Catalog.Domain;

namespace Switchboard.Modules.Catalog.Data;

internal sealed class CatCategoryConfiguration : IEntityTypeConfiguration<CatCategory>
{
    public void Configure(EntityTypeBuilder<CatCategory> e)
    {
        e.ToTable("CatCategories");
        e.Property(x => x.Name).HasMaxLength(100).IsRequired();
        e.Property(x => x.Description).HasMaxLength(500);
        e.Property(x => x.Icon).HasMaxLength(64).IsRequired();
        e.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class CatItemConfiguration : IEntityTypeConfiguration<CatItem>
{
    public void Configure(EntityTypeBuilder<CatItem> e)
    {
        e.ToTable("CatItems");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.ShortDescription).HasMaxLength(300).IsRequired();
        e.Property(x => x.Description).HasMaxLength(8000);
        e.Property(x => x.Icon).HasMaxLength(64).IsRequired();
        e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        e.Property(x => x.PostSubmitMessage).HasMaxLength(1000);
        e.HasOne<AgentGroup>().WithMany().HasForeignKey(x => x.FulfillmentGroupId).OnDelete(DeleteBehavior.SetNull);
        e.HasMany(x => x.Categories).WithOne().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        e.HasMany(x => x.Fields).WithOne().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.State);
        e.HasIndex(x => x.Name);
    }
}

internal sealed class CatItemCategoryConfiguration : IEntityTypeConfiguration<CatItemCategory>
{
    public void Configure(EntityTypeBuilder<CatItemCategory> e)
    {
        e.ToTable("CatItemCategories");
        e.HasKey(x => new { x.ItemId, x.CategoryId });
        e.HasOne<CatCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CatItemFieldConfiguration : IEntityTypeConfiguration<CatItemField>
{
    public void Configure(EntityTypeBuilder<CatItemField> e)
    {
        e.ToTable("CatItemFields");
        e.Property(x => x.Key).HasMaxLength(64).IsRequired();
        e.Property(x => x.Label).HasMaxLength(200).IsRequired();
        e.Property(x => x.HelpText).HasMaxLength(500);
        e.HasOne(x => x.SharedField).WithMany().HasForeignKey(x => x.SharedFieldId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => new { x.ItemId, x.Key }).IsUnique();
    }
}

internal sealed class CatSharedFieldConfiguration : IEntityTypeConfiguration<CatSharedField>
{
    public void Configure(EntityTypeBuilder<CatSharedField> e)
    {
        e.ToTable("CatSharedFields");
        e.Property(x => x.Key).HasMaxLength(64).IsRequired();
        e.Property(x => x.Label).HasMaxLength(200).IsRequired();
        e.Property(x => x.HelpText).HasMaxLength(500);
        e.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class CatRequestedItemConfiguration : IEntityTypeConfiguration<CatRequestedItem>
{
    public void Configure(EntityTypeBuilder<CatRequestedItem> e)
    {
        e.ToTable("CatRequestedItems");
        e.Property(x => x.ItemName).HasMaxLength(200).IsRequired();
        e.Property(x => x.Icon).HasMaxLength(64).IsRequired();
        e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<CatItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<CatRequestedItem>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        e.HasIndex(x => x.TicketId);
        e.HasIndex(x => new { x.ItemId, x.CreatedAt });
    }
}
