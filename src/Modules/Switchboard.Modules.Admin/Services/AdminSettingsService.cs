using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Services;

namespace Switchboard.Modules.Admin.Services;

/// <summary>
/// Sign-in and session policy keys (FR-IAM-19). Stored here so admins can set them; the host's sign-in
/// pipeline reads them (see the integration notes for which are enforced today).
/// </summary>
public static class SecuritySettingKeys
{
    public const string SessionIdleMinutes = "security.session_idle_minutes";
    public const string SessionAbsoluteHours = "security.session_absolute_hours";
    public const string AgentIpRanges = "security.ip_ranges.agents";
    public const string AdminIpRanges = "security.ip_ranges.admins";
    public const string RequesterIpRanges = "security.ip_ranges.requesters";
    public const string PasswordMinLength = "security.password.min_length";
    public const string PasswordRequireMixed = "security.password.require_mixed";
    public const string PasswordHistory = "security.password.history";
    public const string PasswordExpiryDays = "security.password.expiry_days";
    public const string LockoutAttempts = "security.lockout.attempts";
    public const string LockoutMinutes = "security.lockout.minutes";
    public const string SignOutOnPasswordChange = "security.sign_out_on_password_change";
}

public sealed record ServiceDeskSettings
{
    public int AutoCloseDays { get; init; } = 3;
    /// <summary><c>reopen</c> or <c>followup</c>.</summary>
    public string ClosedReplyBehavior { get; init; } = "reopen";
    public bool PriorityMatrixEnabled { get; init; } = true;
    public bool RequesterCanClose { get; init; } = true;
    public bool RequesterCanReopen { get; init; } = true;
    public bool ShowAgentNamesToRequesters { get; init; } = true;
    public int TrashRetentionDays { get; init; } = 30;
    public bool BlockParentCloseWithOpenChildren { get; init; } = true;
}

public sealed record SignInSettings
{
    /// <summary><c>open</c>, <c>domains</c> or <c>invite</c> (FR-IAM-18).</summary>
    public string SignupPolicy { get; init; } = "open";
    public IReadOnlyList<string> SignupDomains { get; init; } = [];
    public bool AutoCreateRequestersFromEmail { get; init; } = true;
    public int SessionIdleMinutes { get; init; } = 720;
    /// <summary>0 means no absolute limit.</summary>
    public int SessionAbsoluteHours { get; init; }
    public IReadOnlyList<string> AgentIpRanges { get; init; } = [];
    public IReadOnlyList<string> AdminIpRanges { get; init; } = [];
    public IReadOnlyList<string> RequesterIpRanges { get; init; } = [];
    public int PasswordMinLength { get; init; } = 12;
    public bool PasswordRequireMixed { get; init; }
    public int PasswordHistory { get; init; }
    /// <summary>0 means passwords don't expire.</summary>
    public int PasswordExpiryDays { get; init; }
    public int LockoutAttempts { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;
    public bool SignOutOnPasswordChange { get; init; } = true;
}

public sealed record OrganizationInput(
    string Name, string? BaseUrl, string TimeZone, string Language, string DateFormat, string Currency, DayOfWeek FirstDayOfWeek);

/// <summary>One module's toggle: the instance value and any per-workspace overrides (null = same as instance).</summary>
public sealed record FeatureState(string Module, bool Enabled, IReadOnlyDictionary<Guid, bool?> Workspaces);

/// <summary>Service desk settings (FR-ADM-3), organization settings (FR-ORG-2), feature toggles (FR-ORG-8) and sign-in policy (FR-IAM-18, FR-IAM-19).</summary>
public sealed class AdminSettingsService(
    SwitchboardDbContext db,
    AdminAccess access,
    ISettingsService settings,
    IAttachmentService attachments,
    IFileStore files,
    ModuleCatalog modules,
    IAuditLog audit)
{
    public const long MaxLogoBytes = 1024 * 1024;

    public static readonly IReadOnlyList<string> DateFormats = ["MMM d, yyyy", "d MMM yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "dd.MM.yyyy"];

    public static readonly IReadOnlyList<(string Code, string Name)> Languages = [("en", "English")];

    /// <summary>ISO 4217 codes known to the runtime.</summary>
    public static IReadOnlyList<string> Currencies { get; } = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(c =>
        {
            try
            {
                return new RegionInfo(c.Name).ISOCurrencySymbol;
            }
            catch (ArgumentException)
            {
                return null;
            }
        })
        .OfType<string>().Where(c => c.Length == 3).Distinct().Order().ToList();

    // ---------------------------------------------------------------- service desk

    public async Task<ServiceDeskSettings> GetServiceDeskAsync(CancellationToken ct = default)
    {
        await DemandServiceDeskAsync(ct);
        var d = new ServiceDeskSettings();
        return new ServiceDeskSettings
        {
            AutoCloseDays = await settings.GetAsync(SettingKeys.AutoCloseDays, d.AutoCloseDays, ct: ct),
            ClosedReplyBehavior = await settings.GetAsync(SettingKeys.ClosedReplyBehavior, d.ClosedReplyBehavior, ct: ct),
            PriorityMatrixEnabled = await settings.GetAsync(SettingKeys.PriorityMatrixEnabled, d.PriorityMatrixEnabled, ct: ct),
            RequesterCanClose = await settings.GetAsync(SettingKeys.RequesterCanClose, d.RequesterCanClose, ct: ct),
            RequesterCanReopen = await settings.GetAsync(SettingKeys.RequesterCanReopen, d.RequesterCanReopen, ct: ct),
            ShowAgentNamesToRequesters = await settings.GetAsync(SettingKeys.ShowAgentNamesToRequesters, d.ShowAgentNamesToRequesters, ct: ct),
            TrashRetentionDays = await settings.GetAsync(SettingKeys.TrashRetentionDays, d.TrashRetentionDays, ct: ct),
            BlockParentCloseWithOpenChildren = await settings.GetAsync(SettingKeys.BlockParentCloseWithOpenChildren, d.BlockParentCloseWithOpenChildren, ct: ct),
        };
    }

    /// <summary>Saves instance-wide values; every workspace inherits them.</summary>
    public async Task SaveServiceDeskAsync(ServiceDeskSettings value, CancellationToken ct = default)
    {
        var before = await GetServiceDeskAsync(ct);
        var errors = new Dictionary<string, string[]>();
        if (value.AutoCloseDays is < 0 or > 365)
        {
            errors[nameof(value.AutoCloseDays)] = ["Use 0 to 365 days. 0 turns auto-close off."];
        }
        if (value.ClosedReplyBehavior is not ("reopen" or "followup"))
        {
            errors[nameof(value.ClosedReplyBehavior)] = ["Choose what a reply to a closed ticket does."];
        }
        if (value.TrashRetentionDays is < 1 or > 365)
        {
            errors[nameof(value.TrashRetentionDays)] = ["Keep deleted tickets for 1 to 365 days."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
        var changed = new List<string>();
        async Task Set<T>(string key, T old, T next)
        {
            await settings.SetAsync(key, next, ct: ct);
            if (!EqualityComparer<T>.Default.Equals(old, next))
            {
                changed.Add(key);
            }
        }
        await Set(SettingKeys.AutoCloseDays, before.AutoCloseDays, value.AutoCloseDays);
        await Set(SettingKeys.ClosedReplyBehavior, before.ClosedReplyBehavior, value.ClosedReplyBehavior);
        await Set(SettingKeys.PriorityMatrixEnabled, before.PriorityMatrixEnabled, value.PriorityMatrixEnabled);
        await Set(SettingKeys.RequesterCanClose, before.RequesterCanClose, value.RequesterCanClose);
        await Set(SettingKeys.RequesterCanReopen, before.RequesterCanReopen, value.RequesterCanReopen);
        await Set(SettingKeys.ShowAgentNamesToRequesters, before.ShowAgentNamesToRequesters, value.ShowAgentNamesToRequesters);
        await Set(SettingKeys.TrashRetentionDays, before.TrashRetentionDays, value.TrashRetentionDays);
        await Set(SettingKeys.BlockParentCloseWithOpenChildren, before.BlockParentCloseWithOpenChildren, value.BlockParentCloseWithOpenChildren);
        audit.Record("settings.service_desk_updated", "settings", "service_desk", new { changed });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Instance-wide service desk settings affect every workspace, so they need settings.manage in the primary one.</summary>
    private async Task DemandServiceDeskAsync(CancellationToken ct)
    {
        var primary = await db.Workspaces.Where(w => w.IsPrimary).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ct);
        await access.DemandAsync(Privileges.SettingsManage, primary, ct);
    }

    // ---------------------------------------------------------------- sign-in and sign-up

    public async Task<SignInSettings> GetSignInAsync(CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        var d = new SignInSettings();
        return new SignInSettings
        {
            SignupPolicy = await settings.GetAsync(SettingKeys.RequesterSignupPolicy, d.SignupPolicy, ct: ct),
            SignupDomains = await settings.GetAsync(SettingKeys.RequesterSignupDomains, new List<string>(), ct: ct),
            AutoCreateRequestersFromEmail = await settings.GetAsync(SettingKeys.AutoCreateRequestersFromEmail, d.AutoCreateRequestersFromEmail, ct: ct),
            SessionIdleMinutes = await settings.GetAsync(SecuritySettingKeys.SessionIdleMinutes, d.SessionIdleMinutes, ct: ct),
            SessionAbsoluteHours = await settings.GetAsync(SecuritySettingKeys.SessionAbsoluteHours, d.SessionAbsoluteHours, ct: ct),
            AgentIpRanges = await settings.GetAsync(SecuritySettingKeys.AgentIpRanges, new List<string>(), ct: ct),
            AdminIpRanges = await settings.GetAsync(SecuritySettingKeys.AdminIpRanges, new List<string>(), ct: ct),
            RequesterIpRanges = await settings.GetAsync(SecuritySettingKeys.RequesterIpRanges, new List<string>(), ct: ct),
            PasswordMinLength = await settings.GetAsync(SecuritySettingKeys.PasswordMinLength, d.PasswordMinLength, ct: ct),
            PasswordRequireMixed = await settings.GetAsync(SecuritySettingKeys.PasswordRequireMixed, d.PasswordRequireMixed, ct: ct),
            PasswordHistory = await settings.GetAsync(SecuritySettingKeys.PasswordHistory, d.PasswordHistory, ct: ct),
            PasswordExpiryDays = await settings.GetAsync(SecuritySettingKeys.PasswordExpiryDays, d.PasswordExpiryDays, ct: ct),
            LockoutAttempts = await settings.GetAsync(SecuritySettingKeys.LockoutAttempts, d.LockoutAttempts, ct: ct),
            LockoutMinutes = await settings.GetAsync(SecuritySettingKeys.LockoutMinutes, d.LockoutMinutes, ct: ct),
            SignOutOnPasswordChange = await settings.GetAsync(SecuritySettingKeys.SignOutOnPasswordChange, d.SignOutOnPasswordChange, ct: ct),
        };
    }

    public async Task SaveSignInAsync(SignInSettings value, CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        var errors = new Dictionary<string, string[]>();
        if (value.SignupPolicy is not ("open" or "domains" or "invite"))
        {
            errors[nameof(value.SignupPolicy)] = ["Choose who can create a requester account."];
        }
        var domains = (value.SignupDomains ?? []).Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();
        if (domains.Any(d => !d.Contains('.') || d.Length > 253 || d.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))))
        {
            errors[nameof(value.SignupDomains)] = ["Enter domains like example.com, one per line."];
        }
        else if (value.SignupPolicy == "domains" && domains.Count == 0)
        {
            errors[nameof(value.SignupDomains)] = ["Add at least one domain, or choose another sign-up option."];
        }
        void Range(string key, int v, int min, int max, string message)
        {
            if (v < min || v > max)
            {
                errors[key] = [message];
            }
        }
        Range(nameof(value.SessionIdleMinutes), value.SessionIdleMinutes, 5, 43200, "Use 5 minutes to 30 days.");
        Range(nameof(value.SessionAbsoluteHours), value.SessionAbsoluteHours, 0, 2160, "Use 0 to 2,160 hours (90 days). 0 means no limit.");
        Range(nameof(value.PasswordMinLength), value.PasswordMinLength, 12, 128, "Passwords must be at least 12 characters; you can require up to 128.");
        Range(nameof(value.PasswordHistory), value.PasswordHistory, 0, 24, "Remember 0 to 24 previous passwords.");
        Range(nameof(value.PasswordExpiryDays), value.PasswordExpiryDays, 0, 365, "Use 0 to 365 days. 0 means passwords don't expire.");
        Range(nameof(value.LockoutAttempts), value.LockoutAttempts, 3, 20, "Allow 3 to 20 attempts.");
        Range(nameof(value.LockoutMinutes), value.LockoutMinutes, 1, 1440, "Lock for 1 to 1,440 minutes.");
        var agentRanges = Ranges(nameof(value.AgentIpRanges), value.AgentIpRanges, errors);
        var adminRanges = Ranges(nameof(value.AdminIpRanges), value.AdminIpRanges, errors);
        var requesterRanges = Ranges(nameof(value.RequesterIpRanges), value.RequesterIpRanges, errors);
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var before = await GetSignInAsync(ct);
        await settings.SetAsync(SettingKeys.RequesterSignupPolicy, value.SignupPolicy, ct: ct);
        await settings.SetAsync(SettingKeys.RequesterSignupDomains, domains, ct: ct);
        await settings.SetAsync(SettingKeys.AutoCreateRequestersFromEmail, value.AutoCreateRequestersFromEmail, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.SessionIdleMinutes, value.SessionIdleMinutes, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.SessionAbsoluteHours, value.SessionAbsoluteHours, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.AgentIpRanges, agentRanges, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.AdminIpRanges, adminRanges, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.RequesterIpRanges, requesterRanges, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.PasswordMinLength, value.PasswordMinLength, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.PasswordRequireMixed, value.PasswordRequireMixed, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.PasswordHistory, value.PasswordHistory, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.PasswordExpiryDays, value.PasswordExpiryDays, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.LockoutAttempts, value.LockoutAttempts, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.LockoutMinutes, value.LockoutMinutes, ct: ct);
        await settings.SetAsync(SecuritySettingKeys.SignOutOnPasswordChange, value.SignOutOnPasswordChange, ct: ct);
        audit.Record("settings.sign_in_updated", "settings", "sign_in", new
        {
            before = before with { },
            after = value with { SignupDomains = domains, AgentIpRanges = agentRanges, AdminIpRanges = adminRanges, RequesterIpRanges = requesterRanges },
        });
        await db.SaveChangesAsync(ct);
    }

    private static List<string> Ranges(string key, IReadOnlyList<string>? raw, Dictionary<string, string[]> errors)
    {
        var list = new List<string>();
        foreach (var r in (raw ?? []).Select(r => r.Trim()).Where(r => r.Length > 0))
        {
            var candidate = r.Contains('/') ? r : r + (r.Contains(':') ? "/128" : "/32");
            if (!IPNetwork.TryParse(candidate, out var network))
            {
                errors.TryAdd(key, [$"{r} isn't an IP address or range. Use forms like 203.0.113.0/24 or 2001:db8::/32."]);
                continue;
            }
            list.Add(network.ToString());
        }
        return list.Distinct().ToList();
    }

    // ---------------------------------------------------------------- organization

    public async Task<Organization> GetOrganizationAsync(CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        return await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).FirstAsync(ct);
    }

    public async Task SaveOrganizationAsync(OrganizationInput input, CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            errors[nameof(input.Name)] = [name.Length == 0 ? "Enter the organization's name." : "Keep the name under 200 characters."];
        }
        var baseUrl = string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl is not null && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || baseUrl.Length > 500))
        {
            errors[nameof(input.BaseUrl)] = ["Enter the address people use to reach Switchboard, like https://help.example.com."];
        }
        if (input.TimeZone != "UTC" && !TimeZoneInfo.TryFindSystemTimeZoneById(input.TimeZone ?? "", out _))
        {
            errors[nameof(input.TimeZone)] = ["Choose a time zone from the list."];
        }
        if (Languages.All(l => l.Code != input.Language))
        {
            errors[nameof(input.Language)] = ["Choose a language from the list."];
        }
        if (!DateFormats.Contains(input.DateFormat))
        {
            errors[nameof(input.DateFormat)] = ["Choose a date format from the list."];
        }
        if (!Currencies.Contains(input.Currency))
        {
            errors[nameof(input.Currency)] = ["Choose a currency from the list."];
        }
        if (!Enum.IsDefined(input.FirstDayOfWeek))
        {
            errors[nameof(input.FirstDayOfWeek)] = ["Choose the first day of the week."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
        var changed = new List<string>();
        void Set<T>(string field, T current, T next, Action<T> apply)
        {
            if (!EqualityComparer<T>.Default.Equals(current, next))
            {
                changed.Add(field);
                apply(next);
            }
        }
        Set("name", org.Name, name, v => org.Name = v);
        Set("baseUrl", org.BaseUrl, baseUrl, v => org.BaseUrl = v);
        Set("timeZone", org.TimeZone, input.TimeZone!, v => org.TimeZone = v);
        Set("language", org.Language, input.Language, v => org.Language = v);
        Set("dateFormat", org.DateFormat, input.DateFormat, v => org.DateFormat = v);
        Set("currency", org.Currency, input.Currency, v => org.Currency = v);
        Set("firstDayOfWeek", org.FirstDayOfWeek, input.FirstDayOfWeek, v => org.FirstDayOfWeek = v);
        if (changed.Count == 0)
        {
            return;
        }
        audit.Record("organization.updated", "organization", org.Id.ToString(), new { fields = changed });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Stores a PNG, JPEG, GIF or WebP logo (checked by content, not just the name) and links it from the organization.</summary>
    public async Task SetLogoAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length == 0 || buffer.Length > MaxLogoBytes)
        {
            throw new ValidationException("Logo", "Upload an image smaller than 1 MB.");
        }
        var type = SniffImage(buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 16)))
            ?? throw new ValidationException("Logo", "Upload a PNG, JPEG, GIF or WebP image.");
        buffer.Position = 0;
        var upload = await attachments.UploadAsync(buffer, fileName, type, LogoAttachmentPolicy.Owner, null, ct: ct);

        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync(ct);
        upload.OwnerId = org.Id;
        var old = await db.Attachments.Where(a => a.OwnerType == LogoAttachmentPolicy.Owner && a.Id != upload.Id).ToListAsync(ct);
        db.Attachments.RemoveRange(old);
        org.LogoUrl = $"/files/{upload.Id}/{Uri.EscapeDataString(upload.FileName)}";
        audit.Record("organization.logo_updated", "organization", org.Id.ToString(), new { attachmentId = upload.Id, upload.Size });
        await db.SaveChangesAsync(ct);
        foreach (var a in old)
        {
            await files.DeleteAsync(a.StorageKey, ct);
        }
    }

    public async Task RemoveLogoAsync(CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync(ct);
        var old = await db.Attachments.Where(a => a.OwnerType == LogoAttachmentPolicy.Owner).ToListAsync(ct);
        if (org.LogoUrl is null && old.Count == 0)
        {
            return;
        }
        db.Attachments.RemoveRange(old);
        org.LogoUrl = null;
        audit.Record("organization.logo_removed", "organization", org.Id.ToString());
        await db.SaveChangesAsync(ct);
        foreach (var a in old)
        {
            await files.DeleteAsync(a.StorageKey, ct);
        }
    }

    internal static string? SniffImage(ReadOnlySpan<byte> h) => h switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null,
    };

    // ---------------------------------------------------------------- feature toggles

    /// <summary>Modules that can be switched off. The admin console itself can't, or nobody could switch it back on.</summary>
    public IReadOnlyList<ISwitchboardModule> ToggleableModules => modules.Modules.Where(m => !string.Equals(m.Name, "Admin", StringComparison.OrdinalIgnoreCase)).ToList();

    public async Task<(IReadOnlyList<FeatureState> Features, IReadOnlyList<Workspace> Workspaces)> GetFeaturesAsync(CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        var workspaces = await db.Workspaces.AsNoTracking().Where(w => !w.IsArchived).OrderByDescending(w => w.IsPrimary).ThenBy(w => w.Name).ToListAsync(ct);
        var rows = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith(SettingKeys.FeaturePrefix)).ToListAsync(ct);
        var result = new List<FeatureState>();
        foreach (var m in ToggleableModules)
        {
            var key = FeatureKey(m.Name);
            var instance = rows.FirstOrDefault(r => r.Key == key && r.WorkspaceId == null);
            var overrides = workspaces.ToDictionary(w => w.Id, w => Parse(rows.FirstOrDefault(r => r.Key == key && r.WorkspaceId == w.Id)));
            result.Add(new FeatureState(m.Name, Parse(instance) ?? true, overrides));
        }
        return (result, workspaces);
    }

    /// <param name="instance">Module → on/off for everyone.</param>
    /// <param name="overrides">(Module, workspace) → on/off for that workspace, or null to follow the instance.</param>
    public async Task SaveFeaturesAsync(IReadOnlyDictionary<string, bool> instance, IReadOnlyDictionary<(string Module, Guid WorkspaceId), bool?> overrides, CancellationToken ct = default)
    {
        await access.DemandOrgAdminAsync(ct);
        var (current, workspaces) = await GetFeaturesAsync(ct);
        var names = current.Select(f => f.Module).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new List<object>();
        foreach (var (module, enabled) in instance.Where(i => names.Contains(i.Key)))
        {
            var state = current.First(f => f.Module.Equals(module, StringComparison.OrdinalIgnoreCase));
            if (state.Enabled != enabled)
            {
                await settings.SetAsync(FeatureKey(state.Module), enabled, ct: ct);
                changes.Add(new { module = state.Module, enabled });
            }
        }
        foreach (var ((module, workspaceId), enabled) in overrides.Where(o => names.Contains(o.Key.Module) && workspaces.Any(w => w.Id == o.Key.WorkspaceId)))
        {
            var state = current.First(f => f.Module.Equals(module, StringComparison.OrdinalIgnoreCase));
            if (state.Workspaces.GetValueOrDefault(workspaceId) == enabled)
            {
                continue;
            }
            var key = FeatureKey(state.Module);
            if (enabled is { } on)
            {
                await settings.SetAsync(key, on, workspaceId, ct);
            }
            else
            {
                db.Settings.RemoveRange(await db.Settings.Where(s => s.Key == key && s.WorkspaceId == workspaceId).ToListAsync(ct));
            }
            changes.Add(new { module = state.Module, workspaceId, enabled });
        }
        if (changes.Count == 0)
        {
            return;
        }
        audit.Record("features.updated", "settings", "features", new { changes });
        await db.SaveChangesAsync(ct);
    }

    public static string FeatureKey(string module) => SettingKeys.FeaturePrefix + module.ToLowerInvariant();

    private static bool? Parse(Setting? row) => row?.ValueJson switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };
}
