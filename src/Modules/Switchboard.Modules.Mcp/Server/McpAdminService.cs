using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.OAuth;

namespace Switchboard.Modules.Mcp.Server;

public sealed record McpSettingsView(bool Enabled, bool OpenRegistration, bool ApiKeysAllowed, string AuthorityConfirmation, IReadOnlyList<string> AllowedOrigins);

public sealed record GrantRow(McpGrant Grant, string UserName, string? UserEmail, int CallsLast7Days);

public sealed record ClientRow(McpClient Client, int ActiveGrants, int CallsLast7Days, DateTimeOffset? LastUsedAt);

public sealed record UsageRow(string Key, string Label, int Calls, int Errors);

/// <summary>Admin controls (MCP-20..26) and the kill switches behind them (MCP-73).</summary>
public sealed class McpAdminService(
    SwitchboardDbContext db,
    ISettingsService settings,
    IPermissionService permissions,
    IOpenIddictAuthorizationManager authorizations,
    IOpenIddictTokenManager tokens,
    McpClientRegistry registry,
    McpKillSwitch killSwitch,
    McpSessionRegistry sessions,
    McpToolCatalog catalog,
    IAuditLog audit,
    IActorAccessor actors,
    TimeProvider time)
{
    private Task DemandAsync(CancellationToken ct) => permissions.DemandAsync(McpModule.ManagePrivilege, ct: ct);

    public async Task<McpSettingsView> GetSettingsAsync(CancellationToken ct) => new(
        await settings.GetAsync(McpSettings.Enabled, true, ct: ct),
        await settings.GetAsync(McpSettings.OpenRegistration, true, ct: ct),
        await settings.GetAsync(McpSettings.ApiKeysAllowed, false, ct: ct),
        await settings.GetAsync(McpSettings.AuthorityConfirmation, "out_of_band", ct: ct),
        await settings.GetAsync<string[]>(McpSettings.AllowedOrigins, [], ct: ct));

    public async Task SaveSettingsAsync(McpSettingsView value, CancellationToken ct)
    {
        await DemandAsync(ct);
        var before = await GetSettingsAsync(ct);
        var origins = value.AllowedOrigins.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.AbsolutePath != "/")
            {
                throw new ValidationException("AllowedOrigins", $"{origin} isn't an origin. Use the form https://app.example.com.");
            }
        }
        await settings.SetAsync(McpSettings.Enabled, value.Enabled, ct: ct);
        await settings.SetAsync(McpSettings.OpenRegistration, value.OpenRegistration, ct: ct);
        await settings.SetAsync(McpSettings.ApiKeysAllowed, value.ApiKeysAllowed, ct: ct);
        await settings.SetAsync(McpSettings.AuthorityConfirmation, value.AuthorityConfirmation == "in_client" ? "in_client" : "out_of_band", ct: ct);
        await settings.SetAsync(McpSettings.AllowedOrigins, origins, ct: ct);
        audit.Record("mcp.settings.updated", "mcp_settings", null, new { before, after = value with { AllowedOrigins = origins } });
        await db.SaveChangesAsync(ct);
        if (before.Enabled && !value.Enabled)
        {
            killSwitch.StopAll();
        }
    }

    public async Task<McpServerConfiguration> GetConfigurationAsync(string slug, CancellationToken ct) =>
        await db.Set<McpServerConfiguration>().FirstOrDefaultAsync(c => c.Slug == slug, ct) ?? throw new NotFoundException();

    public async Task<IReadOnlyList<McpServerConfiguration>> ListConfigurationsAsync(CancellationToken ct) =>
        await db.Set<McpServerConfiguration>().AsNoTracking().OrderBy(c => c.Slug).ToListAsync(ct);

    public async Task SaveAccessAsync(string slug, bool enabled, bool allowRequesters, IReadOnlyList<Guid> roleIds, CancellationToken ct)
    {
        await DemandAsync(ct);
        var config = await GetConfigurationAsync(slug, ct);
        var validRoles = await db.Roles.Where(r => roleIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        audit.Record("mcp.configuration.updated", "mcp_configuration", slug, new
        {
            before = new { config.IsEnabled, config.AllowRequesters, config.AllowedRoleIds },
            after = new { IsEnabled = enabled, AllowRequesters = allowRequesters, AllowedRoleIds = validRoles },
        });
        config.IsEnabled = slug.Length == 0 || enabled;
        config.AllowRequesters = allowRequesters;
        config.AllowedRoleIds = validRoles;
        config.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await sessions.NotifyToolsChangedAsync(slug);
    }

    public async Task SetToolEnabledAsync(string slug, string tool, bool enabled, CancellationToken ct)
    {
        await DemandAsync(ct);
        if (catalog.Find(tool) is null)
        {
            throw new NotFoundException();
        }
        var config = await GetConfigurationAsync(slug, ct);
        var disabled = config.DisabledTools.ToHashSet(StringComparer.Ordinal);
        if (enabled ? !disabled.Remove(tool) : !disabled.Add(tool))
        {
            return;
        }
        config.DisabledTools = disabled.Order(StringComparer.Ordinal).ToList();
        config.UpdatedAt = time.GetUtcNow();
        audit.Record(enabled ? "mcp.tool.enabled" : "mcp.tool.disabled", "mcp_tool", tool, new { configuration = slug });
        await db.SaveChangesAsync(ct);
        // MCP-22: connected clients learn the tool list changed.
        await sessions.NotifyToolsChangedAsync(slug);
    }

    /// <summary>Sets which tools are off for an endpoint; everything else stays on, including tools added later.</summary>
    public async Task SaveEnabledToolsAsync(string slug, IReadOnlyCollection<string> enabled, CancellationToken ct)
    {
        await DemandAsync(ct);
        var config = await GetConfigurationAsync(slug, ct);
        var disabled = catalog.All.Select(t => t.Definition.Name).Where(n => !enabled.Contains(n)).Order(StringComparer.Ordinal).ToList();
        if (disabled.SequenceEqual(config.DisabledTools))
        {
            return;
        }
        audit.Record("mcp.tools.updated", "mcp_configuration", slug, new { before = config.DisabledTools, after = disabled });
        config.DisabledTools = disabled;
        config.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        // MCP-22: connected clients learn the tool list changed.
        await sessions.NotifyToolsChangedAsync(slug);
    }

    public async Task<IReadOnlyList<McpCallRecord>> RecentCallsAsync(int take, CancellationToken ct) =>
        await db.Set<McpCallRecord>().AsNoTracking().OrderByDescending(r => r.At).Take(take).ToListAsync(ct);

    public async Task<McpServerConfiguration> CreateConfigurationAsync(string slug, string name, CancellationToken ct)
    {
        await DemandAsync(ct);
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (!McpPaths.IsValidSlug(slug))
        {
            throw new ValidationException("Slug", "Use 1 to 40 lower-case letters, digits and hyphens, for example sales-team.");
        }
        if (await db.Set<McpServerConfiguration>().AnyAsync(c => c.Slug == slug, ct))
        {
            throw new ValidationException("Slug", "Another endpoint already uses that address.");
        }
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            throw new ValidationException("Name", "Give the endpoint a name.");
        }
        var now = time.GetUtcNow();
        var config = new McpServerConfiguration { Slug = slug, Name = name, CreatedAt = now, UpdatedAt = now };
        db.Set<McpServerConfiguration>().Add(config);
        audit.Record("mcp.configuration.created", "mcp_configuration", slug, new { name });
        await db.SaveChangesAsync(ct);
        return config;
    }

    public async Task DeleteConfigurationAsync(string slug, CancellationToken ct)
    {
        await DemandAsync(ct);
        if (slug.Length == 0)
        {
            throw new ValidationException("Slug", "The default endpoint can't be deleted. Turn MCP off instead.");
        }
        var config = await GetConfigurationAsync(slug, ct);
        db.Set<McpServerConfiguration>().Remove(config);
        audit.Record("mcp.configuration.deleted", "mcp_configuration", slug, new { config.Name });
        await db.SaveChangesAsync(ct);
        var grants = await db.Set<McpGrant>().Where(g => g.ConfigurationSlug == slug && g.RevokedAt == null).Select(g => g.Id).ToListAsync(ct);
        foreach (var id in grants)
        {
            await RevokeGrantCoreAsync(id, "configuration_deleted", ct);
        }
    }

    // ------------------------------------------------------------------ clients

    public async Task<IReadOnlyList<ClientRow>> ListClientsAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-7);
        var now = time.GetUtcNow();
        var clients = await db.Set<McpClient>().AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(500).ToListAsync(ct);
        var grants = await db.Set<McpGrant>().AsNoTracking().Where(g => g.RevokedAt == null && g.ExpiresAt > now)
            .GroupBy(g => g.ClientId).Select(g => new { g.Key, Count = g.Count(), Last = g.Max(x => x.LastUsedAt) }).ToListAsync(ct);
        var calls = await db.Set<McpCallRecord>().AsNoTracking().Where(r => r.At >= since)
            .GroupBy(r => r.ClientId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return clients.Select(c => new ClientRow(c,
            grants.FirstOrDefault(g => g.Key == c.ClientId)?.Count ?? 0,
            calls.FirstOrDefault(x => x.Key == c.ClientId)?.Count ?? 0,
            grants.FirstOrDefault(g => g.Key == c.ClientId)?.Last)).ToList();
    }

    public async Task<RegisteredClient> RegisterClientAsync(string name, IReadOnlyList<string> redirectUris, bool confidential, CancellationToken ct)
    {
        await DemandAsync(ct);
        return await registry.RegisterAsync(new ClientRegistration(name, redirectUris, null, confidential, McpClientSource.Admin), null, ct);
    }

    /// <summary>Approves an agent app that identifies itself by a metadata URL, even with open registration off.</summary>
    public async Task<string?> AllowMetadataClientAsync(string url, CancellationToken ct)
    {
        await DemandAsync(ct);
        url = (url ?? "").Trim();
        if (!McpClientRegistry.IsMetadataClientId(url, out _))
        {
            return "Use the app's HTTPS client metadata URL, for example https://app.example.com/oauth/client.json.";
        }
        var error = await registry.EnsureMetadataClientAsync(url, adminApproved: true, ct);
        if (error is null)
        {
            var client = await db.Set<McpClient>().FirstOrDefaultAsync(c => c.ClientId == url, ct);
            if (client is not null && client.Source == McpClientSource.MetadataDocument && client.CreatedById is null)
            {
                client.CreatedById = actors.Current.UserId;
                await db.SaveChangesAsync(ct);
            }
        }
        return error;
    }

    public async Task SetClientBlockedAsync(string clientId, bool blocked, CancellationToken ct)
    {
        await DemandAsync(ct);
        await registry.BlockAsync(clientId, blocked, ct);
        if (blocked)
        {
            killSwitch.BlockClient(clientId);
        }
    }

    public async Task<int> RevokeClientGrantsAsync(string clientId, CancellationToken ct)
    {
        await DemandAsync(ct);
        var ids = await db.Set<McpGrant>().Where(g => g.ClientId == clientId && g.RevokedAt == null).Select(g => g.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await RevokeGrantCoreAsync(id, "admin", ct);
        }
        return ids.Count;
    }

    // ------------------------------------------------------------------ grants

    public async Task<IReadOnlyList<GrantRow>> ListGrantsAsync(Guid? userId, string? clientId, bool includeEnded, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = now.AddDays(-7);
        var query = db.Set<McpGrant>().AsNoTracking();
        if (!includeEnded)
        {
            query = query.Where(g => g.RevokedAt == null && g.ExpiresAt > now);
        }
        if (userId is { } u)
        {
            query = query.Where(g => g.UserId == u);
        }
        if (clientId is { Length: > 0 } c)
        {
            query = query.Where(g => g.ClientId == c);
        }
        var grants = await query.OrderByDescending(g => g.LastUsedAt ?? g.CreatedAt).Take(500).ToListAsync(ct);
        var userIds = grants.Select(g => g.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => new { x.DisplayName, x.Email }, ct);
        var grantIds = grants.Select(g => (Guid?)g.Id).ToList();
        var calls = await db.Set<McpCallRecord>().AsNoTracking().Where(r => r.At >= since && grantIds.Contains(r.GrantId))
            .GroupBy(r => r.GrantId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return grants.Select(g => new GrantRow(g,
            users.GetValueOrDefault(g.UserId)?.DisplayName ?? "Unknown",
            users.GetValueOrDefault(g.UserId)?.Email,
            calls.FirstOrDefault(x => x.Key == g.Id)?.Count ?? 0)).ToList();
    }

    /// <summary>An admin revokes any grant; a user revokes their own (MCP-25).</summary>
    public async Task<bool> RevokeGrantAsync(Guid grantId, CancellationToken ct)
    {
        var grant = await db.Set<McpGrant>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId, ct);
        var me = actors.Current.UserId;
        if (grant is null)
        {
            return false;
        }
        var own = grant.UserId == me;
        if (!own && !await permissions.HasAsync(McpModule.ManagePrivilege, ct: ct))
        {
            // Someone else's grant looks the same as a missing one (SR-AUTHZ-6).
            return false;
        }
        return await RevokeGrantCoreAsync(grantId, own ? "user" : "admin", ct);
    }

    public async Task<int> RevokeUserGrantsAsync(Guid userId, CancellationToken ct)
    {
        await DemandAsync(ct);
        var ids = await db.Set<McpGrant>().Where(g => g.UserId == userId && g.RevokedAt == null).Select(g => g.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await RevokeGrantCoreAsync(id, "admin", ct);
        }
        killSwitch.RevokeUser(userId);
        return ids.Count;
    }

    private async Task<bool> RevokeGrantCoreAsync(Guid grantId, string reason, CancellationToken ct)
    {
        var grant = await db.Set<McpGrant>().FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (grant is null || grant.RevokedAt is not null)
        {
            return false;
        }
        grant.RevokedAt = time.GetUtcNow();
        grant.RevokedReason = reason;
        grant.RevokedById = actors.Current.UserId;
        audit.Record("mcp.grant.revoked", "mcp_grant", grantId.ToString(), new { reason, grant.ClientId, grant.UserId });
        await db.SaveChangesAsync(ct);

        if (await authorizations.FindByIdAsync(grantId.ToString(), ct) is { } authorization)
        {
            await authorizations.TryRevokeAsync(authorization, ct);
        }
        await tokens.RevokeByAuthorizationIdAsync(grantId.ToString(), ct);
        killSwitch.RevokeGrant(grantId);
        foreach (var session in sessions.SessionsOf(b => b.GrantId == grantId))
        {
            sessions.Forget(session);
        }
        return true;
    }

    // ------------------------------------------------------------------ usage

    public async Task<(int Today, int Week, int Errors, int Pending, IReadOnlyList<UsageRow> Tools, IReadOnlyList<UsageRow> Users)> UsageAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var day = now.AddDays(-1);
        var week = now.AddDays(-7);
        var calls = db.Set<McpCallRecord>().AsNoTracking().Where(r => r.At >= week);
        var today = await calls.CountAsync(r => r.At >= day, ct);
        var total = await calls.CountAsync(ct);
        var errors = await calls.CountAsync(r => r.ResultCode != McpErrorCodes.Ok && r.ResultCode != McpErrorCodes.ConfirmationRequired, ct);
        var pending = await db.Set<McpPendingAction>().CountAsync(p => p.Status == McpPendingStatus.Pending && p.ExpiresAt > now, ct);
        var tools = await calls.GroupBy(r => r.Tool)
            .Select(g => new { g.Key, Count = g.Count(), Errors = g.Count(r => r.ResultCode != McpErrorCodes.Ok && r.ResultCode != McpErrorCodes.ConfirmationRequired) })
            .OrderByDescending(x => x.Count).Take(15).ToListAsync(ct);
        var users = await calls.GroupBy(r => r.UserId)
            .Select(g => new { g.Key, Count = g.Count(), Errors = g.Count(r => r.ResultCode != McpErrorCodes.Ok && r.ResultCode != McpErrorCodes.ConfirmationRequired) })
            .OrderByDescending(x => x.Count).Take(15).ToListAsync(ct);
        var userIds = users.Select(u => u.Key).ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return (today, total, errors, pending,
            tools.Select(t => new UsageRow(t.Key, catalog.Find(t.Key)?.Definition.Title ?? t.Key, t.Count, t.Errors)).ToList(),
            users.Select(u => new UsageRow(u.Key.ToString(), names.GetValueOrDefault(u.Key) ?? "Unknown", u.Count, u.Errors)).ToList());
    }

    public IReadOnlyList<AgentToolDefinition> AllTools() => catalog.All.Select(t => t.Definition).ToList();
}
