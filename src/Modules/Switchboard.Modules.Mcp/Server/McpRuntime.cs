using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>Per-grant and per-user call rate limits (MCP-80).</summary>
public sealed class McpRateLimiter(IConfiguration configuration) : IDisposable
{
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new();
    private readonly int _perGrant = configuration.GetValue("Switchboard:Mcp:CallsPerMinutePerGrant", 60);
    private readonly int _grantBurst = configuration.GetValue("Switchboard:Mcp:BurstPerGrant", 20);
    private readonly int _perUser = configuration.GetValue("Switchboard:Mcp:CallsPerMinutePerUser", 120);

    /// <summary>Takes one call from both buckets; on refusal returns how long to wait.</summary>
    public TimeSpan? TryAcquire(Guid userId, Guid? grantId, string clientId)
    {
        var grantKey = grantId?.ToString() ?? $"{userId}:{clientId}";
        var grant = _limiters.GetOrAdd($"g:{grantKey}", _ => Bucket(_grantBurst, _perGrant));
        var user = _limiters.GetOrAdd($"u:{userId}", _ => Bucket(Math.Max(_perUser / 3, 1), _perUser));
        foreach (var limiter in new[] { grant, user })
        {
            using var lease = limiter.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                return lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : TimeSpan.FromSeconds(1);
            }
        }
        return null;
    }

    private static TokenBucketRateLimiter Bucket(int burst, int perMinute) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = Math.Max(burst, 1),
        TokensPerPeriod = Math.Max(perMinute / 60, 1),
        ReplenishmentPeriod = TimeSpan.FromSeconds(Math.Max(60 / Math.Max(perMinute, 1), 1)),
        QueueLimit = 0,
        AutoReplenishment = true,
    });

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }
}

/// <summary>
/// Cancels in-flight calls when a kill switch is thrown (MCP-73): a grant revoked, a client blocked or
/// MCP turned off. New calls are refused by <see cref="McpAccessPolicy"/>.
/// </summary>
public sealed class McpKillSwitch
{
    private readonly ConcurrentDictionary<Guid, Running> _running = new();

    public IDisposable Track(Guid? grantId, string clientId, Guid userId, out CancellationToken token)
    {
        var entry = new Running(grantId, clientId, userId, new CancellationTokenSource());
        var id = Guid.NewGuid();
        _running[id] = entry;
        token = entry.Cts.Token;
        return new Untrack(this, id);
    }

    public void RevokeGrant(Guid grantId) => Cancel(r => r.GrantId == grantId);

    public void BlockClient(string clientId) => Cancel(r => r.ClientId == clientId);

    public void RevokeUser(Guid userId) => Cancel(r => r.UserId == userId);

    public void StopAll() => Cancel(_ => true);

    private void Cancel(Func<Running, bool> match)
    {
        foreach (var running in _running.Values.Where(match))
        {
            try
            {
                running.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Finished between the lookup and the cancel.
            }
        }
    }

    private sealed record Running(Guid? GrantId, string ClientId, Guid UserId, CancellationTokenSource Cts);

    private sealed class Untrack(McpKillSwitch owner, Guid id) : IDisposable
    {
        public void Dispose()
        {
            if (owner._running.TryRemove(id, out var entry))
            {
                entry.Cts.Dispose();
            }
        }
    }
}

/// <summary>
/// Binds each Mcp-Session-Id to the (user, grant, endpoint) that opened it (MCP-4), and remembers the live
/// sessions so configuration changes can send <c>notifications/tools/list_changed</c> (MCP-22).
/// </summary>
public sealed class McpSessionRegistry(TimeProvider time)
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, McpServer> _servers = new(StringComparer.Ordinal);

    public sealed record Binding(Guid UserId, Guid? GrantId, string ClientId, string Slug)
    {
        public DateTimeOffset LastSeen { get; set; }
    }

    public void Bind(string sessionId, McpCaller caller) =>
        _bindings[sessionId] = new Binding(caller.UserId, caller.GrantId, caller.ClientId, caller.Slug) { LastSeen = time.GetUtcNow() };

    /// <summary>
    /// True when the session is unknown (the SDK answers 404 itself) or belongs to this caller; false when
    /// another user, grant or endpoint tries to use it.
    /// </summary>
    public bool Matches(string sessionId, McpCaller caller)
    {
        if (!_bindings.TryGetValue(sessionId, out var binding))
        {
            return true;
        }
        var now = time.GetUtcNow();
        if (now - binding.LastSeen > IdleTimeout)
        {
            Forget(sessionId);
            return true;
        }
        var same = binding.UserId == caller.UserId && binding.GrantId == caller.GrantId
            && binding.ClientId == caller.ClientId && binding.Slug == caller.Slug;
        if (same)
        {
            binding.LastSeen = now;
        }
        return same;
    }

    public void Forget(string sessionId)
    {
        _bindings.TryRemove(sessionId, out _);
        _servers.TryRemove(sessionId, out _);
    }

    public void RememberServer(McpServer server)
    {
        if (server.SessionId is { } id)
        {
            _servers[id] = server;
        }
    }

    /// <summary>Sessions of a grant, to close after revocation.</summary>
    public IReadOnlyList<string> SessionsOf(Func<Binding, bool> match) =>
        _bindings.Where(b => match(b.Value)).Select(b => b.Key).ToList();

    /// <summary>Tells every connected client of an endpoint that its tool list changed.</summary>
    public async Task NotifyToolsChangedAsync(string? slug = null)
    {
        foreach (var (sessionId, server) in _servers.ToList())
        {
            if (slug is not null && _bindings.TryGetValue(sessionId, out var binding) && binding.Slug != slug)
            {
                continue;
            }
            try
            {
                await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification);
            }
            catch (Exception)
            {
                // A closed session: forget it.
                Forget(sessionId);
            }
        }
    }
}
