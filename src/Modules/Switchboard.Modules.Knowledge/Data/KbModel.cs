using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Switchboard.Modules.Knowledge.Domain;

namespace Switchboard.Modules.Knowledge.Data;

internal sealed class KbCategoryConfiguration : IEntityTypeConfiguration<KbCategory>
{
    public void Configure(EntityTypeBuilder<KbCategory> e)
    {
        e.ToTable("KbCategories");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Description).HasMaxLength(1000);
        e.HasIndex(x => new { x.WorkspaceId, x.Order });
    }
}

internal sealed class KbFolderConfiguration : IEntityTypeConfiguration<KbFolder>
{
    public void Configure(EntityTypeBuilder<KbFolder> e)
    {
        e.ToTable("KbFolders");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Description).HasMaxLength(1000);
        e.HasOne<KbCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<KbFolder>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
        e.HasMany(x => x.Audiences).WithOne().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Cascade);
        e.HasMany(x => x.Approvers).WithOne().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.CategoryId, x.Order });
    }
}

internal sealed class KbFolderAudienceConfiguration : IEntityTypeConfiguration<KbFolderAudience>
{
    public void Configure(EntityTypeBuilder<KbFolderAudience> e)
    {
        e.ToTable("KbFolderAudiences");
        e.HasKey(x => new { x.FolderId, x.Kind, x.TargetId });
    }
}

internal sealed class KbFolderApproverConfiguration : IEntityTypeConfiguration<KbFolderApprover>
{
    public void Configure(EntityTypeBuilder<KbFolderApprover> e)
    {
        e.ToTable("KbFolderApprovers");
        e.HasKey(x => new { x.FolderId, x.UserId });
        e.HasOne<Core.Domain.User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class KbArticleConfiguration : IEntityTypeConfiguration<KbArticle>
{
    public void Configure(EntityTypeBuilder<KbArticle> e)
    {
        e.ToTable("KbArticles");
        e.Ignore(x => x.DisplayId);
        e.Property(x => x.Title).HasMaxLength(300).IsRequired();
        e.Property(x => x.Keywords).HasMaxLength(500);
        e.Property(x => x.SearchTerms).HasMaxLength(1200);
        e.Property(x => x.ExternalUrl).HasMaxLength(1000);
        e.Property(x => x.SeoTitle).HasMaxLength(200);
        e.Property(x => x.SeoDescription).HasMaxLength(500);
        e.HasOne<KbFolder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Core.Domain.User>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.NoAction);
        e.HasOne<Core.Domain.User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.NoAction);
        e.HasIndex(x => x.Number).IsUnique();
        e.HasIndex(x => new { x.FolderId, x.Status });
        e.HasIndex(x => new { x.WorkspaceId, x.DeletedAt, x.Status });
    }
}

internal sealed class KbArticleVersionConfiguration : IEntityTypeConfiguration<KbArticleVersion>
{
    public void Configure(EntityTypeBuilder<KbArticleVersion> e)
    {
        e.ToTable("KbArticleVersions");
        e.Property(x => x.Title).HasMaxLength(300).IsRequired();
        e.Property(x => x.Excerpt).HasMaxLength(400);
        e.Property(x => x.ChangeNote).HasMaxLength(500);
        // Every save and state change bumps Revision, so a save that raced a submit or publish fails instead of
        // rewriting a version that is no longer a draft.
        e.Property(x => x.Revision).IsConcurrencyToken();
        e.HasOne<KbArticle>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.ArticleId, x.Number }).IsUnique();
        // INV-KB-1: at most one published version per article, enforced by the database too.
        e.HasIndex(x => x.ArticleId)
            .IsUnique()
            .HasFilter($"[State] = {(int)KbVersionState.Published}")
            .HasDatabaseName("IX_KbArticleVersions_OnePublished");
    }
}

internal sealed class KbArticleReviewConfiguration : IEntityTypeConfiguration<KbArticleReview>
{
    public void Configure(EntityTypeBuilder<KbArticleReview> e)
    {
        e.ToTable("KbArticleReviews");
        e.Property(x => x.SubmitNote).HasMaxLength(2000);
        e.Property(x => x.DecisionComment).HasMaxLength(2000);
        e.HasOne<KbArticle>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.ArticleId, x.Decision });
    }
}

internal sealed class KbArticleRelationConfiguration : IEntityTypeConfiguration<KbArticleRelation>
{
    public void Configure(EntityTypeBuilder<KbArticleRelation> e)
    {
        e.ToTable("KbArticleRelations");
        e.HasKey(x => new { x.ArticleId, x.RelatedArticleId });
        e.HasOne<KbArticle>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<KbArticle>().WithMany().HasForeignKey(x => x.RelatedArticleId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class KbArticleFeedbackConfiguration : IEntityTypeConfiguration<KbArticleFeedback>
{
    public void Configure(EntityTypeBuilder<KbArticleFeedback> e)
    {
        e.ToTable("KbArticleFeedback");
        e.Property(x => x.VisitorKey).HasMaxLength(64);
        e.Property(x => x.Comment).HasMaxLength(2000);
        e.HasOne<KbArticle>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.ArticleId, x.UserId }).IsUnique().HasFilter("[UserId] IS NOT NULL");
        e.HasIndex(x => new { x.ArticleId, x.VisitorKey }).IsUnique().HasFilter("[VisitorKey] IS NOT NULL");
    }
}

internal sealed class KbDeflectionConfiguration : IEntityTypeConfiguration<KbDeflection>
{
    public void Configure(EntityTypeBuilder<KbDeflection> e)
    {
        e.ToTable("KbDeflections");
        e.HasOne<KbArticle>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.UserId, x.ViewedAt });
        e.HasIndex(x => new { x.ArticleId, x.TicketId });
    }
}

internal sealed class KbAnnouncementConfiguration : IEntityTypeConfiguration<KbAnnouncement>
{
    public void Configure(EntityTypeBuilder<KbAnnouncement> e)
    {
        e.ToTable("KbAnnouncements");
        e.Property(x => x.Title).HasMaxLength(200).IsRequired();
        e.HasMany(x => x.Targets).WithOne().HasForeignKey(x => x.AnnouncementId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.IsPublished, x.ActiveFrom, x.ActiveTo });
    }
}

internal sealed class KbAnnouncementTargetConfiguration : IEntityTypeConfiguration<KbAnnouncementTarget>
{
    public void Configure(EntityTypeBuilder<KbAnnouncementTarget> e)
    {
        e.ToTable("KbAnnouncementTargets");
        e.HasKey(x => new { x.AnnouncementId, x.Kind, x.TargetId });
    }
}
