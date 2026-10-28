using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;
using Switchboard.Core;
using Switchboard.Modules.Messaging.Domain;

namespace Switchboard.Modules.Messaging.Outbound;

/// <summary>Hands a finished message to a mail server. Replaced in tests.</summary>
public interface IMailTransport
{
    Task SendAsync(MimeMessage message, MailboxAddress sender, IReadOnlyList<MailboxAddress> recipients, EffectiveMailSettings settings, CancellationToken ct);
}

public sealed class SmtpMailTransport : IMailTransport
{
    public async Task SendAsync(MimeMessage message, MailboxAddress sender, IReadOnlyList<MailboxAddress> recipients, EffectiveMailSettings settings, CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync(settings.Host ?? throw new InvalidOperationException("No SMTP server is set."), settings.Port, ToSocketOptions(settings.Security), ct);
        if (!string.IsNullOrEmpty(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? "", ct);
        }
        await client.SendAsync(message, sender, recipients, ct);
        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is IOException or MailKit.ProtocolException or MailKit.Net.Smtp.SmtpCommandException)
        {
            // The server already accepted the message; failing now would get it sent again.
        }
    }

    public static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.StartTlsWhenAvailable,
    };
}

public sealed record DispatchResult(DeliveryStatus Status, string MessageId, string? Detail = null);

/// <summary>
/// Final step before the wire: fills in From, Message-ID and Date, applies the non-production
/// redirect (SR-DEP-6), signs with DKIM when configured (SR-EML-4) and sends.
/// </summary>
public sealed class MailDispatcher(
    OutboundSettingsStore settingsStore,
    IMailTransport transport,
    IOptions<SwitchboardOptions> options,
    TimeProvider time,
    ILogger<MailDispatcher> logger)
{
    public async Task<DispatchResult> SendAsync(MimeMessage message, CancellationToken ct = default)
    {
        var settings = await settingsStore.GetEffectiveAsync(ct);
        Prepare(message, settings);
        var messageId = message.MessageId ?? "";

        var recipients = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
            .DistinctBy(m => m.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0)
        {
            return new DispatchResult(DeliveryStatus.Suppressed, messageId, "No recipients.");
        }

        if (!settings.IsConfigured)
        {
            logger.LogWarning("Outgoing mail isn't set up. Message {MessageId} to {Count} recipient(s) was logged, not sent.", message.MessageId, recipients.Count);
            return new DispatchResult(DeliveryStatus.Logged, messageId, "Outgoing mail isn't set up.");
        }

        var sender = MailboxAddress.Parse(settings.EnvelopeFrom is { Length: > 0 } env ? env : settings.FromAddress);

        if (!options.Value.NonProduction)
        {
            Sign(message, settings);
            await transport.SendAsync(message, sender, recipients, settings, ct);
            return new DispatchResult(DeliveryStatus.Sent, messageId);
        }

        // Non-production: only allowlisted recipients get real mail; everything else goes to the capture address or the log.
        var allowed = recipients.Where(r => IsAllowlisted(r.Address, settings.NonProductionAllowlist)).ToList();
        var held = recipients.Except(allowed).ToList();
        if (allowed.Count > 0)
        {
            Sign(message, settings);
            await transport.SendAsync(message, sender, allowed, settings, ct);
        }
        if (held.Count == 0)
        {
            return new DispatchResult(DeliveryStatus.Sent, messageId);
        }
        if (settings.CaptureAddress is not { Length: > 0 } capture)
        {
            logger.LogInformation("Non-production instance: message {MessageId} to {Count} recipient(s) was logged, not sent.", message.MessageId, held.Count);
            return new DispatchResult(DeliveryStatus.Logged, messageId, "Non-production instance with no capture address.");
        }

        var copy = Clone(message);
        copy.Subject = $"[Test] {message.Subject}";
        copy.Headers.Remove("DKIM-Signature");
        copy.Headers.Replace("X-Switchboard-Redirected-From", string.Join(", ", held.Select(h => h.Address)));
        await transport.SendAsync(copy, sender, [MailboxAddress.Parse(capture)], settings, ct);
        return new DispatchResult(DeliveryStatus.Redirected, messageId, $"Redirected to {capture}.");
    }

    private void Prepare(MimeMessage message, EffectiveMailSettings settings)
    {
        if (message.From.Count == 0)
        {
            message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        }
        if (string.IsNullOrEmpty(message.MessageId))
        {
            message.MessageId = MessageIds.New(settings.Domain);
        }
        if (message.Date == default)
        {
            message.Date = time.GetUtcNow();
        }
        // Header injection (SR-EML-4): MimeKit encodes values; also never let a newline into the subject.
        message.Subject = MessageIds.SingleLine(message.Subject ?? "");
    }

    private void Sign(MimeMessage message, EffectiveMailSettings settings)
    {
        if (settings.DkimDomain is not { Length: > 0 } domain || settings.DkimSelector is not { Length: > 0 } selector || settings.DkimPrivateKey is not { Length: > 0 } key)
        {
            return;
        }
        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(key));
            var signer = new DkimSigner(stream, domain, selector)
            {
                HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            };
            message.Prepare(EncodingConstraint.SevenBit);
            signer.Sign(message, [HeaderId.From, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId, HeaderId.To, HeaderId.Cc, HeaderId.InReplyTo, HeaderId.References]);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            logger.LogError("DKIM signing failed for message {MessageId}: {Error}. Sent unsigned.", message.MessageId, ex.Message);
        }
    }

    private static MimeMessage Clone(MimeMessage message)
    {
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        buffer.Position = 0;
        return MimeMessage.Load(buffer);
    }

    internal static bool IsAllowlisted(string address, IReadOnlyList<string> allowlist)
    {
        var lower = address.ToLowerInvariant();
        var domain = lower.Contains('@') ? lower[(lower.IndexOf('@') + 1)..] : "";
        return allowlist.Any(entry =>
        {
            var e = entry.Trim().TrimStart('@').ToLowerInvariant();
            return e.Length > 0 && (e == lower || e == domain);
        });
    }
}

public static class MessageIds
{
    public static string New(string domain) => $"{Guid.CreateVersion7():N}@{domain}";

    /// <summary>Message-IDs are compared without angle brackets and surrounding space.</summary>
    public static string Normalize(string? id) => (id ?? "").Trim().Trim('<', '>').Trim();

    public static string SingleLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();
}
