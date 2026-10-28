using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Modules.Messaging.Domain;

namespace Switchboard.Modules.Messaging.Outbound;

/// <summary>Encrypts stored credentials with ASP.NET Core Data Protection (SR-DATA-4).</summary>
public sealed class MessagingSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Switchboard.Messaging.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    /// <summary>Null when the value is missing or can no longer be decrypted (e.g. the key ring changed).</summary>
    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Facts about the host the messaging module needs, resolved without depending on hosting abstractions.</summary>
public sealed record MessagingEnvironment(bool IsDevelopment);

public enum MailSettingsSource
{
    None,
    Saved,
    Configuration,
    DevelopmentDefault,
}

/// <summary>The SMTP settings in effect, with secrets decrypted. Never log or render this.</summary>
public sealed record EffectiveMailSettings
{
    public MailSettingsSource Source { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
    public SmtpSecurity Security { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string FromName { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string? EnvelopeFrom { get; init; }
    public string? DkimDomain { get; init; }
    public string? DkimSelector { get; init; }
    public string? DkimPrivateKey { get; init; }
    public string? CaptureAddress { get; init; }
    public IReadOnlyList<string> NonProductionAllowlist { get; init; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public string Domain => FromAddress.Contains('@') ? FromAddress[(FromAddress.IndexOf('@') + 1)..] : "switchboard.local";
}

/// <summary>
/// Reads and saves outgoing mail settings. Order: saved settings, then the <c>Switchboard:Mail:Smtp</c>
/// configuration section (for operators), then Mailpit on localhost:1025 in development.
/// </summary>
public sealed class OutboundSettingsStore(
    SwitchboardDbContext db,
    MessagingSecrets secrets,
    IConfiguration configuration,
    IOptions<SwitchboardOptions> options,
    MessagingEnvironment environment)
{
    public Task<MsgOutboundSettings?> GetRowAsync(CancellationToken ct = default) =>
        db.Set<MsgOutboundSettings>().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);

    public async Task<EffectiveMailSettings> GetEffectiveAsync(CancellationToken ct = default)
    {
        var row = await db.Set<MsgOutboundSettings>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        var section = configuration.GetSection("Switchboard:Mail:Smtp");
        var fallbackFrom = $"support@{HostOf(options.Value.BaseUrl)}";
        var organization = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => o.Name).FirstOrDefaultAsync(ct);

        if (row is { Host.Length: > 0 })
        {
            return new EffectiveMailSettings
            {
                Source = MailSettingsSource.Saved,
                Host = row.Host,
                Port = row.Port,
                Security = row.Security,
                Username = row.Username,
                Password = secrets.Unprotect(row.PasswordProtected),
                FromName = row.FromName,
                FromAddress = row.FromAddress ?? fallbackFrom,
                EnvelopeFrom = row.EnvelopeFrom,
                DkimDomain = row.DkimDomain,
                DkimSelector = row.DkimSelector,
                DkimPrivateKey = secrets.Unprotect(row.DkimKeyProtected),
                CaptureAddress = row.CaptureAddress,
                NonProductionAllowlist = row.NonProductionAllowlist,
            };
        }

        var common = new EffectiveMailSettings
        {
            FromName = row?.FromName is { Length: > 0 } n ? n : section["FromName"] ?? organization ?? "Switchboard",
            FromAddress = row?.FromAddress ?? section["FromAddress"] ?? fallbackFrom,
            CaptureAddress = row?.CaptureAddress ?? section["CaptureAddress"],
            NonProductionAllowlist = row?.NonProductionAllowlist ?? [],
        };

        if (section["Host"] is { Length: > 0 } host)
        {
            return common with
            {
                Source = MailSettingsSource.Configuration,
                Host = host,
                Port = section.GetValue("Port", 587),
                Security = section.GetValue("Security", SmtpSecurity.Auto),
                Username = section["Username"],
                Password = section["Password"],
                EnvelopeFrom = section["EnvelopeFrom"],
            };
        }

        if (environment.IsDevelopment)
        {
            return common with { Source = MailSettingsSource.DevelopmentDefault, Host = "localhost", Port = 1025, Security = SmtpSecurity.None };
        }

        return common with { Source = MailSettingsSource.None };
    }

    /// <summary>Every address that receives support mail. The instance never notifies these (SR-EML-1).</summary>
    public async Task<HashSet<string>> OwnAddressesAsync(CancellationToken ct = default)
    {
        var mailboxes = await db.Set<MsgMailbox>().AsNoTracking().Select(m => m.Address).ToListAsync(ct);
        var effective = await GetEffectiveAsync(ct);
        var set = new HashSet<string>(mailboxes.Select(a => a.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase)
        {
            effective.FromAddress.ToLowerInvariant(),
        };
        return set;
    }

    private static string HostOf(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Host.Contains('.') ? uri.Host : "switchboard.local";
}
