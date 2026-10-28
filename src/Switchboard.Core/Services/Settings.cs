using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;

namespace Switchboard.Core.Services;

/// <summary>Well-known setting keys. Modules define their own with a <c>module.</c> prefix.</summary>
public static class SettingKeys
{
    /// <summary>Days after resolution before a ticket closes automatically (0 disables).</summary>
    public const string AutoCloseDays = "tickets.auto_close_days";
    /// <summary><c>reopen</c> or <c>followup</c>: what a requester reply to a closed ticket does.</summary>
    public const string ClosedReplyBehavior = "tickets.closed_reply_behavior";
    public const string PriorityMatrixEnabled = "tickets.priority_matrix_enabled";
    public const string RequesterCanClose = "tickets.requester_can_close";
    public const string RequesterCanReopen = "tickets.requester_can_reopen";
    public const string ShowAgentNamesToRequesters = "tickets.show_agent_names";
    public const string TrashRetentionDays = "tickets.trash_retention_days";
    public const string BlockParentCloseWithOpenChildren = "tickets.block_parent_close_with_open_children";
    public const string AutoCreateRequestersFromEmail = "requesters.auto_create_from_email";
    /// <summary><c>open</c>, <c>domains</c> or <c>invite</c> (FR-IAM-18).</summary>
    public const string RequesterSignupPolicy = "requesters.signup_policy";
    public const string RequesterSignupDomains = "requesters.signup_domains";
    /// <summary>Feature toggles: <c>features.&lt;module&gt;</c> → bool (FR-ORG-8).</summary>
    public const string FeaturePrefix = "features.";
}

public interface ISettingsService
{
    Task<T> GetAsync<T>(string key, T defaultValue, Guid? workspaceId = null, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, Guid? workspaceId = null, CancellationToken ct = default);
    /// <summary>Feature toggles default to on (FR-ORG-8).</summary>
    Task<bool> IsFeatureEnabledAsync(string module, Guid? workspaceId = null, CancellationToken ct = default);
}

public sealed class SettingsService(SwitchboardDbContext db, TimeProvider time) : ISettingsService
{
    public async Task<T> GetAsync<T>(string key, T defaultValue, Guid? workspaceId = null, CancellationToken ct = default)
    {
        Setting? row = null;
        if (workspaceId is not null)
        {
            row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key && s.WorkspaceId == workspaceId, ct);
        }
        row ??= await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key && s.WorkspaceId == null, ct);
        if (row is null)
        {
            return defaultValue;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(row.ValueJson, JsonDefaults.Options) ?? defaultValue;
        }
        catch (JsonException)
        {
            return defaultValue;
        }
    }

    public async Task SetAsync<T>(string key, T value, Guid? workspaceId = null, CancellationToken ct = default)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key && s.WorkspaceId == workspaceId, ct);
        if (row is null)
        {
            row = new Setting { Key = key, WorkspaceId = workspaceId };
            db.Settings.Add(row);
        }
        row.ValueJson = JsonSerializer.Serialize(value, JsonDefaults.Options);
        row.UpdatedAt = time.GetUtcNow();
    }

    public Task<bool> IsFeatureEnabledAsync(string module, Guid? workspaceId = null, CancellationToken ct = default) =>
        GetAsync(SettingKeys.FeaturePrefix + module.ToLowerInvariant(), true, workspaceId, ct);
}
