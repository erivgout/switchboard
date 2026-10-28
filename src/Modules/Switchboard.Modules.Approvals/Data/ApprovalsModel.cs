using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Switchboard.Core.Domain;
using Switchboard.Modules.Approvals.Domain;

namespace Switchboard.Modules.Approvals.Data;

internal sealed class AprRequestConfiguration : IEntityTypeConfiguration<AprRequest>
{
    public void Configure(EntityTypeBuilder<AprRequest> e)
    {
        e.ToTable("AprRequests");
        e.Property(x => x.SubjectType).HasMaxLength(64).IsRequired();
        e.Property(x => x.Title).HasMaxLength(300).IsRequired();
        e.Property(x => x.CancelReason).HasMaxLength(1000);
        e.Property(x => x.RowVersion).IsRowVersion();
        e.HasOne<User>().WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.NoAction);
        e.HasMany(x => x.Levels).WithOne().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        e.HasMany(x => x.Decisions).WithOne().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.NoAction);
        e.HasIndex(x => new { x.SubjectType, x.SubjectId });
        e.HasIndex(x => new { x.Status, x.CurrentLevel });
        e.HasIndex(x => x.RequesterId);
    }
}

internal sealed class AprLevelConfiguration : IEntityTypeConfiguration<AprLevel>
{
    public void Configure(EntityTypeBuilder<AprLevel> e)
    {
        e.ToTable("AprLevels");
        e.Property(x => x.Name).HasMaxLength(200);
        e.HasMany(x => x.Approvers).WithOne().HasForeignKey(x => x.LevelId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.RequestId, x.Order }).IsUnique();
    }
}

internal sealed class AprApproverConfiguration : IEntityTypeConfiguration<AprApprover>
{
    public void Configure(EntityTypeBuilder<AprApprover> e)
    {
        e.ToTable("AprApprovers");
        e.Property(x => x.Source).HasMaxLength(200).IsRequired();
        e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        e.HasIndex(x => new { x.LevelId, x.UserId }).IsUnique();
        e.HasIndex(x => x.UserId);
    }
}

internal sealed class AprDecisionConfiguration : IEntityTypeConfiguration<AprDecision>
{
    public const string ImmutableTrigger = "TR_AprDecisions_Immutable";

    public void Configure(EntityTypeBuilder<AprDecision> e)
    {
        e.ToTable("AprDecisions", t => t.HasTrigger(ImmutableTrigger));
        e.Property(x => x.Comment).HasMaxLength(2000);
        e.Property(x => x.ClientName).HasMaxLength(200);
        e.HasOne<AprLevel>().WithMany().HasForeignKey(x => x.LevelId).OnDelete(DeleteBehavior.NoAction);
        e.HasOne<User>().WithMany().HasForeignKey(x => x.ApproverId).OnDelete(DeleteBehavior.NoAction);
        e.HasOne<User>().WithMany().HasForeignKey(x => x.DecidedById).OnDelete(DeleteBehavior.NoAction);
        // One counted decision per designated approver and level.
        e.HasIndex(x => new { x.LevelId, x.ApproverId }).IsUnique();
        e.HasIndex(x => x.DecidedById);
    }
}

internal sealed class AprLinkTokenConfiguration : IEntityTypeConfiguration<AprLinkToken>
{
    public void Configure(EntityTypeBuilder<AprLinkToken> e)
    {
        e.ToTable("AprLinkTokens");
        e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        e.HasIndex(x => x.TokenHash).IsUnique();
        e.HasOne<AprRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
