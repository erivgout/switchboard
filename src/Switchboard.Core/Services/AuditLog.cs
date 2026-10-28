using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Core.Services;

/// <summary>Audit trail for security-relevant actions (SR-AUD).</summary>
public interface IAuditLog
{
    /// <summary>Adds an entry to the current unit of work; it commits with the business change.</summary>
    void Record(string action, string? objectType = null, string? objectId = null, object? details = null);

    /// <summary>Writes an entry immediately in its own transaction (e.g. failed logins).</summary>
    Task WriteAsync(string action, string? objectType = null, string? objectId = null, object? details = null, CancellationToken ct = default);
}

public sealed class AuditLog(
    SwitchboardDbContext db,
    IServiceScopeFactory scopes,
    IActorAccessor actors,
    IHttpContextAccessor http,
    TimeProvider time) : IAuditLog
{
    public void Record(string action, string? objectType = null, string? objectId = null, object? details = null) =>
        db.AuditEntries.Add(Create(action, objectType, objectId, details));

    public async Task WriteAsync(string action, string? objectType = null, string? objectId = null, object? details = null, CancellationToken ct = default)
    {
        var entry = Create(action, objectType, objectId, details);
        await using var scope = scopes.CreateAsyncScope();
        var isolated = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        isolated.AuditEntries.Add(entry);
        await isolated.SaveChangesAsync(ct);
    }

    private AuditEntry Create(string action, string? objectType, string? objectId, object? details)
    {
        var actor = actors.Current;
        var ctx = http.HttpContext;
        var ua = ctx?.Request.Headers.UserAgent.ToString();
        return new AuditEntry
        {
            OccurredAt = time.GetUtcNow(),
            ActorKind = actor.Kind,
            ActorUserId = actor.UserId,
            ActorName = actor.DisplayName,
            Channel = actor.Channel,
            ClientId = actor.ClientId,
            ClientName = actor.ClientName,
            Ip = ctx?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = ua is { Length: > 500 } ? ua[..500] : ua,
            Action = action,
            ObjectType = objectType,
            ObjectId = objectId,
            DetailsJson = details is null ? "{}" : JsonSerializer.Serialize(details, JsonDefaults.Options),
            RequestId = ctx?.TraceIdentifier,
        };
    }
}

/// <summary>
/// Seals audit entries into a SHA-256 hash chain in sequence order (INV-AUD-1). Sealing runs
/// asynchronously so writers never contend on the chain head.
/// </summary>
public sealed class AuditChainSealer(IServiceScopeFactory scopes, ILogger<AuditChainSealer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SealAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Audit sealing failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public async Task<int> SealAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One sealer at a time across instances.
        await db.Database.ExecuteSqlRawAsync("EXEC sp_getapplock @Resource = 'audit-chain', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000", ct);

        var prev = await db.AuditEntries.Where(a => a.Hash != null).OrderByDescending(a => a.Sequence).Select(a => a.Hash).FirstOrDefaultAsync(ct) ?? "";
        var unsealed = await db.AuditEntries.Where(a => a.Hash == null).OrderBy(a => a.Sequence).Take(500).ToListAsync(ct);
        foreach (var entry in unsealed)
        {
            entry.PrevHash = prev;
            entry.Hash = ComputeHash(entry, prev);
            prev = entry.Hash;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return unsealed.Count;
    }

    /// <summary>Returns the sequence of the first entry whose hash does not verify, or null if the chain is intact.</summary>
    public static async Task<long?> VerifyAsync(SwitchboardDbContext db, CancellationToken ct)
    {
        var prev = "";
        await foreach (var entry in db.AuditEntries.AsNoTracking().Where(a => a.Hash != null).OrderBy(a => a.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (entry.PrevHash != prev || entry.Hash != ComputeHash(entry, prev))
            {
                return entry.Sequence;
            }
            prev = entry.Hash!;
        }
        return null;
    }

    public static string ComputeHash(AuditEntry e, string prev)
    {
        var payload = string.Join('|',
            prev, e.Sequence, e.Id, e.OccurredAt.UtcDateTime.ToString("O"), (int)e.ActorKind, e.ActorUserId, e.ActorName,
            (int)e.Channel, e.ClientId, e.Action, e.ObjectType, e.ObjectId, e.DetailsJson);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
