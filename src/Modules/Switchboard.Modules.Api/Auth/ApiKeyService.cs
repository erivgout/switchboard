using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Api.Auth;

public sealed record ApiKeySummary(
    Guid Id,
    Guid UserId,
    string UserName,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt)
{
    public ApiKeyState State(DateTimeOffset now) =>
        RevokedAt is not null ? ApiKeyState.Revoked : ExpiresAt <= now ? ApiKeyState.Expired : ApiKeyState.Active;
}

public enum ApiKeyState
{
    Active,
    Expired,
    Revoked,
}

public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string>? Scopes, int? ExpiresInDays);

/// <summary>The only time the full key exists outside the caller's hands.</summary>
public sealed record CreatedApiKey(ApiKeySummary Key, string Secret);

public interface IApiKeyService
{
    Task<IReadOnlyList<ApiKeySummary>> ListMineAsync(CancellationToken ct = default);

    Task<CreatedApiKey> CreateAsync(CreateApiKeyRequest request, CancellationToken ct = default);

    /// <summary>Revokes one of the current user's keys, or any key for holders of settings.manage.</summary>
    Task RevokeAsync(Guid keyId, CancellationToken ct = default);

    /// <summary>Every key in the instance (settings.manage).</summary>
    Task<(IReadOnlyList<ApiKeySummary> Items, int Total)> ListAllAsync(ApiKeyState? state, string? search, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Longest lifetime an admin allows for new keys, in days; null means no limit.</summary>
    Task<int?> GetMaxLifetimeDaysAsync(CancellationToken ct = default);

    Task SetMaxLifetimeDaysAsync(int? days, CancellationToken ct = default);
}

public sealed class ApiKeyService(
    SwitchboardDbContext db,
    IActorAccessor actors,
    IPermissionService permissions,
    ISettingsService settings,
    IAuditLog audit,
    TimeProvider time) : IApiKeyService
{
    public const string MaxLifetimeSetting = "api.key_max_lifetime_days";
    public const int MaxActiveKeysPerUser = 25;
    public const int MaxNameLength = 100;
    public static readonly int[] LifetimeChoices = [30, 90, 180, 365];

    public async Task<IReadOnlyList<ApiKeySummary>> ListMineAsync(CancellationToken ct = default)
    {
        var userId = RequireUser();
        var rows = await Project(db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId))
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(r => r.ToSummary()).ToList();
    }

    public async Task<CreatedApiKey> CreateAsync(CreateApiKeyRequest request, CancellationToken ct = default)
    {
        var userId = RequireUser();
        var actor = actors.Current;
        if (actor.Channel == Channel.Api || actor.Channel == Channel.Mcp)
        {
            // A key must not be able to mint wider keys for itself.
            throw new ForbiddenException("Create API keys from your profile page.");
        }

        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId && u.IsActive)
            .Select(u => new { u.Id, u.DisplayName, IsAgent = u.IsAgent || u.IsOrgAdmin })
            .FirstOrDefaultAsync(ct) ?? throw new ForbiddenException("Your account is inactive.");

        var errors = new Dictionary<string, string[]>();
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0)
        {
            errors["name"] = ["Name the key after what will use it, for example \"Monitoring sync\"."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors["name"] = [$"Keep the name under {MaxNameLength} characters."];
        }

        var scopes = ApiScopes.Normalize(request.Scopes, user.IsAgent, out var unknown);
        if (unknown.Count > 0)
        {
            errors["scopes"] = [$"Unknown scope: {string.Join(", ", unknown)}."];
        }

        var max = await GetMaxLifetimeDaysAsync(ct);
        int? days = request.ExpiresInDays;
        if (days is <= 0 or > 3650)
        {
            errors["expiresInDays"] = ["Choose an expiry between 1 day and 10 years."];
        }
        else if (max is { } limit && (days is null || days > limit))
        {
            errors["expiresInDays"] = [$"Keys can last at most {limit} days on this instance."];
        }

        var active = await db.ApiKeys.CountAsync(k => k.UserId == userId && k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > time.GetUtcNow()), ct);
        if (active >= MaxActiveKeysPerUser)
        {
            errors["name"] = [$"You already have {MaxActiveKeysPerUser} active keys. Revoke one you no longer use first."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var now = time.GetUtcNow();
        var secret = ApiKeyFormat.Generate();
        var key = new ApiKey
        {
            UserId = userId,
            Name = name,
            Prefix = ApiKeyFormat.PrefixOf(secret),
            KeyHash = ApiKeyFormat.Hash(secret),
            Scopes = scopes,
            ExpiresAt = days is { } d ? now.AddDays(d) : null,
            CreatedAt = now,
        };
        db.ApiKeys.Add(key);
        audit.Record("api_keys.created", "api_key", key.Id.ToString(), new
        {
            key.Name,
            key.Prefix,
            ownerId = userId,
            scopes = scopes.Count == 0 ? ["(full access)"] : scopes,
            key.ExpiresAt,
        });
        await db.SaveChangesAsync(ct);

        return new CreatedApiKey(
            new ApiKeySummary(key.Id, userId, user.DisplayName, key.Name, key.Prefix, key.Scopes, key.CreatedAt, null, key.ExpiresAt, null),
            secret);
    }

    public async Task RevokeAsync(Guid keyId, CancellationToken ct = default)
    {
        var userId = RequireUser();
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct) ?? throw new NotFoundException();
        if (key.UserId != userId && !await permissions.HasAsync(Privileges.SettingsManage, ct: ct))
        {
            // Someone else's key: same answer as a missing one (SR-AUTHZ-6).
            throw new NotFoundException();
        }
        if (key.RevokedAt is not null)
        {
            return;
        }
        key.RevokedAt = time.GetUtcNow();
        audit.Record("api_keys.revoked", "api_key", key.Id.ToString(), new { key.Name, key.Prefix, ownerId = key.UserId, byOwner = key.UserId == userId });
        await db.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<ApiKeySummary> Items, int Total)> ListAllAsync(ApiKeyState? state, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        await permissions.DemandAsync(Privileges.SettingsManage, ct: ct);
        var now = time.GetUtcNow();
        var query = db.ApiKeys.AsNoTracking();
        query = state switch
        {
            ApiKeyState.Active => query.Where(k => k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now)),
            ApiKeyState.Expired => query.Where(k => k.RevokedAt == null && k.ExpiresAt != null && k.ExpiresAt <= now),
            ApiKeyState.Revoked => query.Where(k => k.RevokedAt != null),
            _ => query,
        };
        var projected = Project(query);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            projected = projected.Where(k => k.Name.Contains(s) || k.UserName.Contains(s) || k.Prefix.StartsWith(s));
        }
        var total = await projected.CountAsync(ct);
        var items = await projected
            .OrderByDescending(k => k.LastUsedAt ?? k.CreatedAt)
            .Skip((Math.Max(1, page) - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items.Select(r => r.ToSummary()).ToList(), total);
    }

    public async Task<int?> GetMaxLifetimeDaysAsync(CancellationToken ct = default)
    {
        var days = await settings.GetAsync<int?>(MaxLifetimeSetting, null, ct: ct);
        return days is > 0 ? days : null;
    }

    public async Task SetMaxLifetimeDaysAsync(int? days, CancellationToken ct = default)
    {
        await permissions.DemandAsync(Privileges.SettingsManage, ct: ct);
        if (days is <= 0 or > 3650)
        {
            throw new ValidationException("maxLifetimeDays", "Choose between 1 and 3650 days, or no limit.");
        }
        var before = await GetMaxLifetimeDaysAsync(ct);
        await settings.SetAsync<int?>(MaxLifetimeSetting, days, ct: ct);
        audit.Record("api_keys.policy_updated", "setting", MaxLifetimeSetting, new { from = before, to = days });
        await db.SaveChangesAsync(ct);
    }

    private Guid RequireUser() =>
        actors.Current is { IsAuthenticatedUser: true, UserId: { } id } ? id : throw new ForbiddenException("Sign in first.");

    private IQueryable<KeyRow> Project(IQueryable<ApiKey> keys) =>
        keys.Join(db.Users, k => k.UserId, u => u.Id, (k, u) => new KeyRow
        {
            Id = k.Id,
            UserId = k.UserId,
            UserName = u.DisplayName,
            Name = k.Name,
            Prefix = k.Prefix,
            Scopes = k.Scopes,
            CreatedAt = k.CreatedAt,
            LastUsedAt = k.LastUsedAt,
            ExpiresAt = k.ExpiresAt,
            RevokedAt = k.RevokedAt,
        });

    /// <summary>Member-init projection, so EF can filter and sort on it.</summary>
    private sealed class KeyRow
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public string UserName { get; init; } = "";
        public string Name { get; init; } = "";
        public string Prefix { get; init; } = "";
        public List<string> Scopes { get; init; } = [];
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }

        public ApiKeySummary ToSummary() => new(Id, UserId, UserName, Name, Prefix, Scopes, CreatedAt, LastUsedAt, ExpiresAt, RevokedAt);
    }
}
