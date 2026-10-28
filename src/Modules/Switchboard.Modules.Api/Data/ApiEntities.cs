using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Switchboard.Core.Domain;

namespace Switchboard.Modules.Api.Data;

/// <summary>A POST that carried an <c>Idempotency-Key</c>; replayed for 24 hours (FR-INT-1).</summary>
public class ApiIdempotencyRecord : Entity
{
    public Guid UserId { get; set; }
    public string Key { get; set; } = "";
    /// <summary>Method and path, e.g. <c>POST /api/v1/tickets/…/reply</c>.</summary>
    public string Route { get; set; } = "";
    /// <summary>SHA-256 of the request body and query string.</summary>
    public string RequestHash { get; set; } = "";
    /// <summary>SHA-256 of the caller's permissions and key scopes; a replay needs the same access.</summary>
    public string AccessHash { get; set; } = "";
    /// <summary>Null while the first request is still running.</summary>
    public int? StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? Location { get; set; }
    public string? ETag { get; set; }
    public byte[]? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>An outbound subscription to ticket events (FR-INT-3).</summary>
public class ApiWebhook : Entity, IHasTimestamps
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Signing secret, encrypted with ASP.NET Core data protection.</summary>
    public string ProtectedSecret { get; set; } = "";
    /// <summary>Last characters of the secret, so admins can tell secrets apart.</summary>
    public string SecretHint { get; set; } = "";
    public List<string> Events { get; set; } = [];
    /// <summary>Only events from this workspace; null for every workspace (org admins only).</summary>
    public Guid? WorkspaceId { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? CreatedById { get; set; }
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public bool? LastDeliverySucceeded { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum WebhookDeliveryStatus
{
    Pending = 1,
    /// <summary>Failed at least once; another attempt is scheduled.</summary>
    Retrying = 2,
    Succeeded = 3,
    /// <summary>Gave up after the last retry: the dead-letter list. Can be replayed.</summary>
    Dead = 4,
}

/// <summary>One event sent (or to be sent) to one webhook, with its delivery log.</summary>
public class ApiWebhookDelivery : Entity
{
    public Guid WebhookId { get; set; }
    /// <summary>Domain event id; with <see cref="WebhookId"/> unique, so re-delivered outbox events don't duplicate.</summary>
    public Guid EventId { get; set; }
    public string EventType { get; set; } = "";
    public Guid? TicketId { get; set; }
    public string PayloadJson { get; set; } = "";
    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    /// <summary>First kilobytes of the receiver's response, for the delivery log.</summary>
    public string? ResponseSnippet { get; set; }
    public int? DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class ApiIdempotencyRecordConfiguration : IEntityTypeConfiguration<ApiIdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<ApiIdempotencyRecord> e)
    {
        e.ToTable("ApiIdempotencyRecords");
        e.Property(x => x.Key).HasMaxLength(255).IsRequired();
        e.Property(x => x.Route).HasMaxLength(450).IsRequired();
        e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        e.Property(x => x.AccessHash).HasMaxLength(64).IsRequired();
        e.Property(x => x.ETag).HasMaxLength(200);
        e.Property(x => x.ContentType).HasMaxLength(200);
        e.Property(x => x.Location).HasMaxLength(2000);
        e.HasIndex(x => new { x.UserId, x.Key, x.Route }).IsUnique();
        e.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class ApiWebhookConfiguration : IEntityTypeConfiguration<ApiWebhook>
{
    public void Configure(EntityTypeBuilder<ApiWebhook> e)
    {
        e.ToTable("ApiWebhooks");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Url).HasMaxLength(2000).IsRequired();
        e.Property(x => x.ProtectedSecret).HasMaxLength(2000).IsRequired();
        e.Property(x => x.SecretHint).HasMaxLength(16);
    }
}

internal sealed class ApiWebhookDeliveryConfiguration : IEntityTypeConfiguration<ApiWebhookDelivery>
{
    public void Configure(EntityTypeBuilder<ApiWebhookDelivery> e)
    {
        e.ToTable("ApiWebhookDeliveries");
        e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        e.Property(x => x.LastError).HasMaxLength(2000);
        e.Property(x => x.ResponseSnippet).HasMaxLength(4000);
        e.HasOne<ApiWebhook>().WithMany().HasForeignKey(x => x.WebhookId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => new { x.WebhookId, x.EventId }).IsUnique();
        e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        e.HasIndex(x => new { x.WebhookId, x.CreatedAt });
    }
}
