using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Domain;

namespace Switchboard.Core.Data;

/// <summary>EF mapping for Core entities.</summary>
internal static class CoreModel
{
    public static void Configure(ModelBuilder b)
    {
        ConfigureIdentity(b);
        ConfigureOrganization(b);
        ConfigureTickets(b);
        ConfigureCommon(b);
    }

    private static void ConfigureIdentity(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.JobTitle).HasMaxLength(200);
            e.Property(x => x.Mobile).HasMaxLength(64);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(16);
            e.Property(x => x.AvatarUrl).HasMaxLength(500);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.OutOfOfficeMessage).HasMaxLength(1000);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(x => x.SecondaryEmails).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Memberships).WithOne(x => x.User).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.GroupMemberships).WithOne(x => x.User).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.IsAgent, x.IsActive });
            e.HasIndex(x => x.DisplayName);
            // INV-ID-2: one user per address (secondary addresses are checked in UserDirectory).
            e.HasIndex(x => x.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
        });
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<Guid>>().ToTable("UserClaims");
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>().ToTable("UserLogins");
        b.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<Guid>>().ToTable("UserTokens");

        b.Entity<UserEmail>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
        });

        b.Entity<Role>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.Key).IsUnique().HasFilter("[Key] IS NOT NULL");
        });

        b.Entity<WorkspaceMembership>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.WorkspaceId }).IsUnique();
            e.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Department>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Location>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
            foreach (var p in new[] { "AddressLine1", "AddressLine2", "City", "State", "PostalCode", "Country", "ContactName", "ContactEmail", "ContactPhone" })
            {
                e.Property<string?>(p).HasMaxLength(200);
            }
        });

        b.Entity<AgentGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Members).WithOne(x => x.Group).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
        });
        b.Entity<AgentGroupMember>(e => e.HasKey(x => new { x.GroupId, x.UserId }));

        b.Entity<RequesterGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<RequesterGroupMember>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.UserId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserDelegation>(e =>
        {
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.DelegateUserId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.UserId, x.StartsAt, x.EndsAt });
        });
    }

    private static void ConfigureOrganization(ModelBuilder b)
    {
        b.Entity<Organization>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.LogoUrl).HasMaxLength(500);
            e.Property(x => x.BaseUrl).HasMaxLength(500);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(16);
            e.Property(x => x.DateFormat).HasMaxLength(64);
            e.Property(x => x.Currency).HasMaxLength(3);
        });

        b.Entity<Workspace>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Icon).HasMaxLength(64);
            e.Property(x => x.Template).HasMaxLength(32);
            e.HasIndex(x => x.Key).IsUnique();
        });
    }

    private static void ConfigureTickets(ModelBuilder b)
    {
        b.Entity<TicketStatus>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.RequesterLabel).HasMaxLength(100);
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
        });

        b.Entity<TicketSource>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Icon).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<TicketCategory>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TicketCategory>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.WorkspaceId, x.ParentId, x.Name }).IsUnique().HasFilter(null);
        });

        b.Entity<Ticket>(e =>
        {
            e.Ignore(x => x.DisplayId);
            e.Property(x => x.Subject).HasMaxLength(500).IsRequired();
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.Number).IsUnique();
            e.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.RequestedFor).WithMany().HasForeignKey(x => x.RequestedForId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<TicketCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<TicketCategory>().WithMany().HasForeignKey(x => x.SubCategoryId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<TicketCategory>().WithMany().HasForeignKey(x => x.ItemCategoryId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.MergedIntoId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Conversations).WithOne(x => x.Ticket).HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Shares).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkspaceId, x.StatusId });
            e.HasIndex(x => x.GroupId);
            e.HasIndex(x => x.AgentId);
            e.HasIndex(x => x.RequesterId);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.DueBy);
            e.HasIndex(x => x.UpdatedAt);
        });

        b.Entity<TicketTag>(e =>
        {
            e.HasKey(x => new { x.TicketId, x.Tag });
            e.Property(x => x.Tag).HasMaxLength(64);
            e.HasIndex(x => x.Tag);
        });

        b.Entity<TicketShare>(e =>
        {
            e.HasKey(x => new { x.TicketId, x.UserId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<TicketConversation>(e =>
        {
            e.Property(x => x.FromEmail).HasMaxLength(256);
            e.Property(x => x.EmailMessageId).HasMaxLength(500);
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.TicketId, x.CreatedAt });
            e.HasIndex(x => x.EmailMessageId);
        });

        b.Entity<TicketTask>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.HasOne(x => x.Assignee).WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<AgentGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.AssigneeId, x.Status });
        });

        b.Entity<TicketLink>(e =>
        {
            e.Property(x => x.TargetType).HasMaxLength(64).IsRequired();
            e.Property(x => x.LinkType).HasMaxLength(64).IsRequired();
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TargetType, x.TargetId });
            e.HasIndex(x => new { x.TicketId, x.TargetType, x.TargetId, x.LinkType }).IsUnique();
        });
    }

    private static void ConfigureCommon(ModelBuilder b)
    {
        b.Entity<Attachment>(e =>
        {
            e.Property(x => x.OwnerType).HasMaxLength(64).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(200).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(400).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasIndex(x => new { x.OwnerType, x.OwnerId });
        });

        b.Entity<ActivityEntry>(e =>
        {
            e.Property(x => x.SubjectType).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.ActorName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ClientName).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => new { x.SubjectType, x.SubjectId, x.CreatedAt });
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Sequence);
            e.Property(x => x.Sequence).UseIdentityColumn();
            e.Property(x => x.ActorName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ClientId).HasMaxLength(200);
            e.Property(x => x.ClientName).HasMaxLength(200);
            e.Property(x => x.Ip).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(500);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.ObjectType).HasMaxLength(100);
            e.Property(x => x.ObjectId).HasMaxLength(100);
            e.Property(x => x.RequestId).HasMaxLength(100);
            e.Property(x => x.PrevHash).HasMaxLength(64);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => x.Id).IsUnique();
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.ObjectType, x.ObjectId });
        });

        b.Entity<CustomFieldDefinition>(e =>
        {
            e.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
            e.Property(x => x.Key).HasMaxLength(100).IsRequired();
            e.Property(x => x.Label).HasMaxLength(200).IsRequired();
            e.Property(x => x.RequesterLabel).HasMaxLength(200);
            e.Property(x => x.HelpText).HasMaxLength(1000);
            e.Property(x => x.DefaultValue).HasMaxLength(1000);
            e.Property(x => x.Section).HasMaxLength(200);
            e.HasIndex(x => new { x.EntityType, x.WorkspaceId, x.Key }).IsUnique().HasFilter(null);
        });

        b.Entity<Setting>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.WorkspaceId, x.Key }).IsUnique().HasFilter(null);
        });

        b.Entity<DisplayIdCounter>(e =>
        {
            e.HasKey(x => x.Family);
            e.Property(x => x.Family).HasMaxLength(64);
        });

        b.Entity<ApiKey>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.KeyHash).IsUnique();
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(400).IsRequired();
            e.HasIndex(x => new { x.ProcessedAt, x.NextAttemptAt });
        });

        b.Entity<OutboxHandlerReceipt>(e =>
        {
            e.HasKey(x => new { x.MessageId, x.HandlerName });
            e.Property(x => x.HandlerName).HasMaxLength(400);
        });
    }
}
