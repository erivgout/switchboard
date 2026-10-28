using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Switchboard.Modules.Mcp.Data;

// The authorization server's OpenIddict entities. Own types (not the OpenIddict defaults) so they
// can never collide with another module's OpenIddict model, and Mcp-prefixed tables.
public sealed class McpOAuthApplication : OpenIddictEntityFrameworkCoreApplication<Guid, McpOAuthAuthorization, McpOAuthToken>;

public sealed class McpOAuthAuthorization : OpenIddictEntityFrameworkCoreAuthorization<Guid, McpOAuthApplication, McpOAuthToken>;

public sealed class McpOAuthScope : OpenIddictEntityFrameworkCoreScope<Guid>;

public sealed class McpOAuthToken : OpenIddictEntityFrameworkCoreToken<Guid, McpOAuthApplication, McpOAuthAuthorization>;

internal static class McpModel
{
    public static void Configure(ModelBuilder b)
    {
        b.UseOpenIddict<McpOAuthApplication, McpOAuthAuthorization, McpOAuthScope, McpOAuthToken, Guid>();
        b.Entity<McpOAuthApplication>().ToTable("McpOAuthApplications");
        b.Entity<McpOAuthAuthorization>().ToTable("McpOAuthAuthorizations");
        b.Entity<McpOAuthScope>().ToTable("McpOAuthScopes");
        b.Entity<McpOAuthToken>().ToTable("McpOAuthTokens");

        b.Entity<McpClient>(e =>
        {
            e.ToTable("McpClients");
            e.Property(x => x.ClientId).HasMaxLength(400).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.MetadataUrl).HasMaxLength(400);
            e.Property(x => x.ClientUri).HasMaxLength(400);
            e.Property(x => x.RegisteredFromIp).HasMaxLength(64);
            e.HasIndex(x => x.ClientId).IsUnique();
        });

        b.Entity<McpGrant>(e =>
        {
            e.ToTable("McpGrants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ClientId).HasMaxLength(400).IsRequired();
            e.Property(x => x.ClientName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Resource).HasMaxLength(400).IsRequired();
            e.Property(x => x.ConfigurationSlug).HasMaxLength(64);
            e.Property(x => x.RedirectHost).HasMaxLength(255);
            e.Property(x => x.RevokedReason).HasMaxLength(64);
            e.HasIndex(x => new { x.UserId, x.RevokedAt });
            e.HasIndex(x => x.ClientId);
        });

        b.Entity<McpPendingAction>(e =>
        {
            e.ToTable("McpPendingActions");
            e.Property(x => x.ClientId).HasMaxLength(400).IsRequired();
            e.Property(x => x.ClientName).HasMaxLength(200).IsRequired();
            e.Property(x => x.SessionId).HasMaxLength(200);
            e.Property(x => x.ConfigurationSlug).HasMaxLength(64);
            e.Property(x => x.Tool).HasMaxLength(100).IsRequired();
            e.Property(x => x.ToolTitle).HasMaxLength(200);
            e.Property(x => x.ParameterHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Method).HasMaxLength(32);
            e.Property(x => x.ResultCode).HasMaxLength(64);
            e.Property(x => x.ActionClass).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.UserId, x.Status, x.CreatedAt });
        });

        b.Entity<McpCallRecord>(e =>
        {
            e.ToTable("McpCallRecords");
            e.Property(x => x.ClientId).HasMaxLength(400).IsRequired();
            e.Property(x => x.ClientName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Tool).HasMaxLength(100).IsRequired();
            e.Property(x => x.ResultCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.ActionClass).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.GrantId, x.At });
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasIndex(x => x.At);
        });

        b.Entity<McpIdempotencyRecord>(e =>
        {
            e.ToTable("McpIdempotencyRecords");
            e.Property(x => x.ClientId).HasMaxLength(400).IsRequired();
            e.Property(x => x.Tool).HasMaxLength(100).IsRequired();
            e.Property(x => x.Key).HasMaxLength(200).IsRequired();
            e.Property(x => x.ParameterHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.ConfigurationSlug).HasMaxLength(64);
            e.HasIndex(x => new { x.UserId, x.ClientId, x.GrantId, x.ConfigurationSlug, x.Tool, x.Key }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<McpServerConfiguration>(e =>
        {
            e.ToTable("McpServerConfigurations");
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<McpSigningKey>(e =>
        {
            e.ToTable("McpSigningKeys");
            e.Property(x => x.Use).HasMaxLength(32).IsRequired();
        });
    }
}
