using System.Text;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Inbound;
using Switchboard.Modules.Messaging.Notifications;
using Switchboard.Modules.Messaging.Outbound;

namespace Switchboard.Modules.Messaging.Admin;

public sealed class MailboxInput
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public Guid WorkspaceId { get; set; }
    public Guid? DefaultGroupId { get; set; }
    public TicketType TicketType { get; set; } = TicketType.Incident;
    public MailboxConnection Connection { get; set; } = MailboxConnection.Webhook;
    public bool IsEnabled { get; set; } = true;
    public string? ImapHost { get; set; }
    public int ImapPort { get; set; } = 993;
    public SmtpSecurity ImapSecurity { get; set; } = SmtpSecurity.SslOnConnect;
    public string? ImapUsername { get; set; }
    /// <summary>Write-only: blank keeps the saved password.</summary>
    public string? ImapPassword { get; set; }
    public string ImapFolder { get; set; } = "INBOX";
    public int PollIntervalSeconds { get; set; } = 60;
    public string? TrustedAuthServId { get; set; }
    public bool TrustUnauthenticated { get; set; }
    public bool AllowEmailCommands { get; set; } = true;
}

public sealed record MailboxSaved(MsgMailbox Mailbox, string? NewWebhookSecret);

/// <summary>Support mailboxes (FR-CH-1). Every change is audited; secrets never reach the audit log (SR-DATA-4).</summary>
public sealed class MailboxService(
    SwitchboardDbContext db,
    MessagingAccess access,
    MessagingSecrets secrets,
    IAuditLog audit,
    TimeProvider time)
{
    public async Task<IReadOnlyList<MsgMailbox>> ListAsync(CancellationToken ct = default)
    {
        var workspaces = await access.ManageableWorkspacesAsync(ct);
        return await db.Set<MsgMailbox>().AsNoTracking().Where(m => workspaces.Contains(m.WorkspaceId)).OrderBy(m => m.Name).ToListAsync(ct);
    }

    public async Task<MsgMailbox> GetAsync(Guid id, CancellationToken ct = default)
    {
        var mailbox = await db.Set<MsgMailbox>().FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException();
        if (!(await access.ManageableWorkspacesAsync(ct)).Contains(mailbox.WorkspaceId))
        {
            throw new NotFoundException();
        }
        return mailbox;
    }

    public async Task<MailboxSaved> SaveAsync(Guid? id, MailboxInput input, CancellationToken ct = default)
    {
        var mailbox = id is { } existing ? await GetAsync(existing, ct) : null;
        if (mailbox is not null && mailbox.WorkspaceId != input.WorkspaceId)
        {
            await access.DemandWorkspaceAsync(mailbox.WorkspaceId, ct);
        }
        await access.DemandWorkspaceAsync(input.WorkspaceId, ct);
        await ValidateAsync(id, input, ct);

        var created = mailbox is null;
        mailbox ??= new MsgMailbox { CreatedAt = time.GetUtcNow() };
        mailbox.Name = input.Name.Trim();
        mailbox.Address = input.Address.Trim().ToLowerInvariant();
        mailbox.WorkspaceId = input.WorkspaceId;
        mailbox.DefaultGroupId = input.DefaultGroupId;
        mailbox.TicketType = input.TicketType;
        mailbox.Connection = input.Connection;
        mailbox.IsEnabled = input.IsEnabled;
        mailbox.ImapHost = input.ImapHost?.Trim();
        mailbox.ImapPort = input.ImapPort;
        mailbox.ImapSecurity = input.ImapSecurity;
        mailbox.ImapUsername = input.ImapUsername?.Trim();
        mailbox.ImapFolder = string.IsNullOrWhiteSpace(input.ImapFolder) ? "INBOX" : input.ImapFolder.Trim();
        mailbox.PollIntervalSeconds = Math.Clamp(input.PollIntervalSeconds, 30, 3600);
        mailbox.TrustedAuthServId = string.IsNullOrWhiteSpace(input.TrustedAuthServId) ? null : input.TrustedAuthServId.Trim();
        mailbox.TrustUnauthenticated = input.TrustUnauthenticated;
        mailbox.AllowEmailCommands = input.AllowEmailCommands;
        mailbox.UpdatedAt = time.GetUtcNow();
        var passwordChanged = false;
        if (!string.IsNullOrEmpty(input.ImapPassword))
        {
            mailbox.ImapPasswordProtected = secrets.Protect(input.ImapPassword);
            passwordChanged = true;
        }

        string? secret = null;
        if (mailbox.Connection == MailboxConnection.Webhook && mailbox.WebhookSecretProtected is null)
        {
            secret = MessagingSecrets.NewSecret();
            mailbox.WebhookSecretProtected = secrets.Protect(secret);
        }
        if (created)
        {
            db.Add(mailbox);
        }
        audit.Record(created ? "messaging.mailbox.created" : "messaging.mailbox.updated", "mailbox", mailbox.Id.ToString(), new
        {
            mailbox.Name,
            mailbox.Address,
            mailbox.WorkspaceId,
            connection = mailbox.Connection.ToString(),
            mailbox.IsEnabled,
            mailbox.ImapHost,
            passwordChanged,
            secretIssued = secret is not null,
        });
        await db.SaveChangesAsync(ct);
        return new MailboxSaved(mailbox, secret);
    }

    public async Task<string> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        var mailbox = await GetAsync(id, ct);
        await access.DemandWorkspaceAsync(mailbox.WorkspaceId, ct);
        var secret = MessagingSecrets.NewSecret();
        mailbox.WebhookSecretProtected = secrets.Protect(secret);
        mailbox.UpdatedAt = time.GetUtcNow();
        audit.Record("messaging.mailbox.secret_rotated", "mailbox", mailbox.Id.ToString(), new { mailbox.Address });
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var mailbox = await GetAsync(id, ct);
        await access.DemandWorkspaceAsync(mailbox.WorkspaceId, ct);
        db.Remove(mailbox);
        audit.Record("messaging.mailbox.deleted", "mailbox", mailbox.Id.ToString(), new { mailbox.Name, mailbox.Address });
        await db.SaveChangesAsync(ct);
    }

    private async Task ValidateAsync(Guid? id, MailboxInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors["Name"] = ["Give the mailbox a name, such as IT support."];
        }
        else if (input.Name.Trim().Length > 200)
        {
            errors["Name"] = ["Keep the name under 200 characters."];
        }
        var address = input.Address?.Trim().ToLowerInvariant() ?? "";
        if (!IUserDirectory.IsValidEmail(address))
        {
            errors["Address"] = ["Enter the support address, such as help@example.com."];
        }
        else if (await db.Set<MsgMailbox>().AnyAsync(m => m.Address == address && m.Id != id, ct))
        {
            errors["Address"] = ["Another mailbox already uses that address."];
        }
        else if (await db.Users.AnyAsync(u => u.NormalizedEmail == address.ToUpperInvariant(), ct))
        {
            errors["Address"] = ["A person already uses that address. Use an address only the service desk receives."];
        }
        if (!await db.Workspaces.AnyAsync(w => w.Id == input.WorkspaceId && !w.IsArchived, ct))
        {
            errors["WorkspaceId"] = ["Choose a workspace."];
        }
        if (input.DefaultGroupId is { } g && !await db.AgentGroups.AnyAsync(x => x.Id == g && x.WorkspaceId == input.WorkspaceId, ct))
        {
            errors["DefaultGroupId"] = ["That group isn't in the chosen workspace."];
        }
        if (!Enum.IsDefined(input.TicketType))
        {
            errors["TicketType"] = ["Choose incident or service request."];
        }
        if (input.Connection == MailboxConnection.Imap)
        {
            if (string.IsNullOrWhiteSpace(input.ImapHost) || input.ImapHost.Trim().Length > 255 || input.ImapHost.Contains(' '))
            {
                errors["ImapHost"] = ["Enter the IMAP server, such as imap.example.com."];
            }
            if (input.ImapPort is < 1 or > 65535)
            {
                errors["ImapPort"] = ["Use a port between 1 and 65535 (usually 993)."];
            }
            if (string.IsNullOrWhiteSpace(input.ImapUsername))
            {
                errors["ImapUsername"] = ["Enter the username for the mailbox."];
            }
            if (id is null && string.IsNullOrEmpty(input.ImapPassword))
            {
                errors["ImapPassword"] = ["Enter the mailbox password."];
            }
        }
        else if (!Enum.IsDefined(input.Connection))
        {
            errors["Connection"] = ["Choose how mail arrives."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}

public sealed class OutboundSettingsInput
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;
    public string? Username { get; set; }
    /// <summary>Write-only: blank keeps the saved password.</summary>
    public string? Password { get; set; }
    public bool ClearPassword { get; set; }
    public string FromName { get; set; } = "";
    public string? FromAddress { get; set; }
    public string? EnvelopeFrom { get; set; }
    public string? DkimDomain { get; set; }
    public string? DkimSelector { get; set; }
    /// <summary>Write-only PEM.</summary>
    public string? DkimPrivateKey { get; set; }
    public bool ClearDkimKey { get; set; }
    public string? CaptureAddress { get; set; }
    public string? NonProductionAllowlist { get; set; }
}

public sealed class OutboundSettingsService(
    SwitchboardDbContext db,
    MessagingAccess access,
    MessagingSecrets secrets,
    OutboundSettingsStore store,
    MailDispatcher dispatcher,
    DeliveryLog log,
    IAuditLog audit,
    TimeProvider time)
{
    public async Task SaveAsync(OutboundSettingsInput input, CancellationToken ct = default)
    {
        await access.DemandInstanceAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var host = input.Host?.Trim();
        if (host is { Length: > 0 } && (host.Length > 255 || host.Contains(' ') || host.Contains('/')))
        {
            errors["Host"] = ["Enter just the server name, such as smtp.example.com."];
        }
        if (input.Port is < 1 or > 65535)
        {
            errors["Port"] = ["Use a port between 1 and 65535 (usually 587)."];
        }
        if (string.IsNullOrWhiteSpace(input.FromName) || input.FromName.Trim().Length > 200)
        {
            errors["FromName"] = ["Enter the name people see, such as IT Service Desk."];
        }
        if (!IUserDirectory.IsValidEmail(input.FromAddress))
        {
            errors["FromAddress"] = ["Enter the address mail comes from, such as help@example.com."];
        }
        if (input.EnvelopeFrom is { Length: > 0 } env && !IUserDirectory.IsValidEmail(env))
        {
            errors["EnvelopeFrom"] = ["Enter a valid bounce address, or leave it empty."];
        }
        if (input.CaptureAddress is { Length: > 0 } capture && !IUserDirectory.IsValidEmail(capture))
        {
            errors["CaptureAddress"] = ["Enter a valid capture address, or leave it empty."];
        }
        var allowlist = (input.NonProductionAllowlist ?? "").Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AddressRules.NormalizePattern).ToList();
        if (allowlist.Any(a => a is null))
        {
            errors["NonProductionAllowlist"] = ["List addresses or domains, separated by commas."];
        }
        var dkimAny = !string.IsNullOrWhiteSpace(input.DkimDomain) || !string.IsNullOrWhiteSpace(input.DkimSelector);
        if (dkimAny && (string.IsNullOrWhiteSpace(input.DkimDomain) || string.IsNullOrWhiteSpace(input.DkimSelector)))
        {
            errors["DkimDomain"] = ["DKIM signing needs both a domain and a selector."];
        }
        if (input.DkimPrivateKey is { Length: > 0 } pem && !pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            errors["DkimPrivateKey"] = ["Paste the private key in PEM format, starting with -----BEGIN PRIVATE KEY-----."];
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var row = await store.GetRowAsync(ct);
        if (row is null)
        {
            row = new MsgOutboundSettings();
            db.Add(row);
        }
        var passwordChanged = !string.IsNullOrEmpty(input.Password) || input.ClearPassword;
        row.Host = host is { Length: > 0 } ? host : null;
        row.Port = input.Port;
        row.Security = input.Security;
        row.Username = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim();
        if (input.ClearPassword)
        {
            row.PasswordProtected = null;
        }
        else if (!string.IsNullOrEmpty(input.Password))
        {
            row.PasswordProtected = secrets.Protect(input.Password);
        }
        row.FromName = input.FromName.Trim();
        row.FromAddress = input.FromAddress!.Trim();
        row.EnvelopeFrom = string.IsNullOrWhiteSpace(input.EnvelopeFrom) ? null : input.EnvelopeFrom.Trim();
        row.DkimDomain = string.IsNullOrWhiteSpace(input.DkimDomain) ? null : input.DkimDomain.Trim().ToLowerInvariant();
        row.DkimSelector = string.IsNullOrWhiteSpace(input.DkimSelector) ? null : input.DkimSelector.Trim();
        var dkimKeyChanged = !string.IsNullOrEmpty(input.DkimPrivateKey) || input.ClearDkimKey;
        if (input.ClearDkimKey)
        {
            row.DkimKeyProtected = null;
        }
        else if (!string.IsNullOrEmpty(input.DkimPrivateKey))
        {
            row.DkimKeyProtected = secrets.Protect(input.DkimPrivateKey.Trim());
        }
        row.CaptureAddress = string.IsNullOrWhiteSpace(input.CaptureAddress) ? null : input.CaptureAddress.Trim();
        row.NonProductionAllowlist = allowlist.Select(a => a!).Distinct().ToList();
        row.UpdatedAt = time.GetUtcNow();

        audit.Record("messaging.outbound.updated", "mail_settings", row.Id.ToString(), new
        {
            row.Host,
            row.Port,
            security = row.Security.ToString(),
            row.Username,
            row.FromAddress,
            row.EnvelopeFrom,
            row.DkimDomain,
            row.DkimSelector,
            row.CaptureAddress,
            passwordChanged,
            dkimKeyChanged,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends a short test message now and reports what happened.</summary>
    public async Task<DispatchResult> SendTestAsync(string to, CancellationToken ct = default)
    {
        await access.DemandInstanceAsync(ct);
        if (!IUserDirectory.IsValidEmail(to))
        {
            throw new ValidationException("TestAddress", "Enter the address to send the test to.");
        }
        var settings = await store.GetEffectiveAsync(ct);
        var message = new MimeMessage { Subject = "Test email from Switchboard", MessageId = MessageIds.New(settings.Domain), Date = time.GetUtcNow() };
        message.To.Add(MailboxAddress.Parse(to.Trim()));
        message.Body = new BodyBuilder
        {
            HtmlBody = EmailLayout.Wrap("<p>This is a test email from Switchboard.</p><p>If you can read this, outgoing mail works.</p>", settings.FromName, null, false),
            TextBody = "This is a test email from Switchboard. If you can read this, outgoing mail works.",
        }.ToMessageBody();

        var claimed = await log.ClaimAsync(null, [new DeliveryClaim(to.Trim(), null)], DeliveryChannel.Email, "test", null, message.Subject, false, message.MessageId, ct);
        audit.Record("messaging.outbound.test_sent", "mail_settings", null, new { to = to.Trim() });
        try
        {
            var result = await dispatcher.SendAsync(message, ct);
            await log.CompleteAsync(claimed, result.Status, result.Detail, ct);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await log.CompleteAsync(claimed, DeliveryStatus.Failed, ex.Message, ct);
            return new DispatchResult(DeliveryStatus.Failed, message.MessageId, ex.Message);
        }
    }
}

/// <summary>Allow and block lists, and the bounced-address list.</summary>
public sealed class SenderRuleService(SwitchboardDbContext db, MessagingAccess access, IAuditLog audit, TimeProvider time)
{
    public Task<List<MsgAddressRule>> ListAsync(CancellationToken ct = default) =>
        db.Set<MsgAddressRule>().AsNoTracking().OrderBy(r => r.Action).ThenBy(r => r.Pattern).ToListAsync(ct);

    public Task<List<MsgBouncedAddress>> BouncedAsync(CancellationToken ct = default) =>
        db.Set<MsgBouncedAddress>().AsNoTracking().OrderByDescending(b => b.LastBouncedAt).Take(200).ToListAsync(ct);

    public async Task AddAsync(string pattern, AddressRuleAction action, string? note, CancellationToken ct = default)
    {
        await access.DemandInstanceAsync(ct);
        var normalized = AddressRules.NormalizePattern(pattern)
            ?? throw new ValidationException("Pattern", "Enter an address such as pat@example.com, or a domain such as example.com.");
        if (!Enum.IsDefined(action))
        {
            throw new ValidationException("Action", "Choose allow or block.");
        }
        if (await db.Set<MsgAddressRule>().AnyAsync(r => r.Pattern == normalized, ct))
        {
            throw new ValidationException("Pattern", $"There's already a rule for {normalized}.");
        }
        var rule = new MsgAddressRule { Pattern = normalized, Action = action, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 300)], CreatedAt = time.GetUtcNow() };
        db.Add(rule);
        audit.Record("messaging.sender_rule.created", "sender_rule", rule.Id.ToString(), new { rule.Pattern, action = action.ToString() });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandInstanceAsync(ct);
        var rule = await db.Set<MsgAddressRule>().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException();
        db.Remove(rule);
        audit.Record("messaging.sender_rule.deleted", "sender_rule", rule.Id.ToString(), new { rule.Pattern, action = rule.Action.ToString() });
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearBounceAsync(Guid id, CancellationToken ct = default)
    {
        await access.DemandInstanceAsync(ct);
        var bounced = await db.Set<MsgBouncedAddress>().FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException();
        db.Remove(bounced);
        audit.Record("messaging.bounce.cleared", "bounced_address", bounced.Id.ToString(), new { bounced.Address });
        await db.SaveChangesAsync(ct);
    }
}

public sealed class SimulatedEmail
{
    public string FromName { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string? Cc { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>A ticket number such as INC-1042 to reply to.</summary>
    public string? ReplyTo { get; set; }
    /// <summary>pass, fail or none.</summary>
    public string Authentication { get; set; } = "pass";
    public bool AutoReply { get; set; }
    /// <summary>When set, used instead of the fields above.</summary>
    public string? RawMime { get; set; }
}

/// <summary>The incoming mail log, the simulator, and processing held mail again.</summary>
public sealed class InboundAdminService(
    SwitchboardDbContext db,
    MessagingAccess access,
    MailboxService mailboxes,
    InboundPipeline pipeline,
    IFileStore files,
    IAuditLog audit,
    TimeProvider time)
{
    public async Task<IReadOnlyList<MsgInboundMessage>> ListAsync(InboundOutcome? outcome, int take, CancellationToken ct = default)
    {
        var mailboxIds = (await mailboxes.ListAsync(ct)).Select(m => (Guid?)m.Id).ToList();
        var instance = await access.CanManageInstanceAsync(ct);
        var query = db.Set<MsgInboundMessage>().AsNoTracking().Where(m => mailboxIds.Contains(m.MailboxId) || (instance && m.MailboxId == null));
        if (outcome is { } o)
        {
            query = o == InboundOutcome.Ignored
                ? query.Where(m => m.Outcome == InboundOutcome.Ignored || m.Outcome == InboundOutcome.Duplicate)
                : query.Where(m => m.Outcome == o);
        }
        return await query.OrderByDescending(m => m.ReceivedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    public async Task<MsgInboundMessage> GetAsync(Guid id, CancellationToken ct = default)
    {
        var message = await db.Set<MsgInboundMessage>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new NotFoundException();
        if (message.MailboxId is { } mailboxId)
        {
            await mailboxes.GetAsync(mailboxId, ct);
        }
        else
        {
            await access.DemandInstanceAsync(ct);
        }
        return message;
    }

    public async Task<MsgInboundMessage> SimulateAsync(Guid mailboxId, SimulatedEmail input, CancellationToken ct = default)
    {
        var mailbox = await mailboxes.GetAsync(mailboxId, ct);
        await access.DemandWorkspaceAsync(mailbox.WorkspaceId, ct);
        var raw = input.RawMime is { Length: > 0 } pasted ? Encoding.UTF8.GetBytes(pasted.Replace("\r\n", "\n").Replace("\n", "\r\n")) : await BuildAsync(mailbox, input, ct);
        audit.Record("messaging.inbound.simulated", "mailbox", mailbox.Id.ToString(), new { mailbox.Address });
        return await pipeline.ProcessAsync(new InboundRequest(mailbox, raw, InboundSource.Simulated), ct);
    }

    /// <summary>Runs a held (blocked or failed) message through the pipeline again, skipping sender rules for blocked mail.</summary>
    public async Task<MsgInboundMessage> ReprocessAsync(Guid id, CancellationToken ct = default)
    {
        var held = await GetAsync(id, ct);
        if (held.RawStorageKey is null || held.MailboxId is null || held.Outcome is not (InboundOutcome.Blocked or InboundOutcome.Failed))
        {
            throw new ValidationException("message", "Only blocked or failed messages can be processed again.");
        }
        var mailbox = await mailboxes.GetAsync(held.MailboxId.Value, ct);
        await access.DemandWorkspaceAsync(mailbox.WorkspaceId, ct);
        await using var stream = await files.OpenReadAsync(held.RawStorageKey, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        audit.Record("messaging.inbound.reprocessed", "inbound_message", held.Id.ToString(), new { previous = held.Outcome.ToString() });
        return await pipeline.ProcessAsync(new InboundRequest(mailbox, buffer.ToArray(), held.Source, BypassSenderRules: held.Outcome == InboundOutcome.Blocked, ReprocessOf: held.Id), ct);
    }

    private async Task<byte[]> BuildAsync(MsgMailbox mailbox, SimulatedEmail input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!IUserDirectory.IsValidEmail(input.FromAddress))
        {
            errors["FromAddress"] = ["Enter the sender's address."];
        }
        if (string.IsNullOrWhiteSpace(input.Body))
        {
            errors["Body"] = ["Write the message."];
        }
        Ticket? replyTo = null;
        if (input.ReplyTo is { Length: > 0 } reference)
        {
            var match = System.Text.RegularExpressions.Regex.Match(reference.Trim(), @"^(INC|SR)-(\d{1,9})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var type = match.Groups[1].Value.Equals("INC", StringComparison.OrdinalIgnoreCase) ? TicketType.Incident : TicketType.ServiceRequest;
                var number = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                replyTo = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Type == type && t.Number == number && t.WorkspaceId == mailbox.WorkspaceId, ct);
            }
            if (replyTo is null)
            {
                errors["ReplyTo"] = ["There's no ticket with that number in this mailbox's workspace. Use a number such as INC-1042."];
            }
        }
        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var from = input.FromAddress.Trim();
        var domain = from[(from.IndexOf('@') + 1)..];
        var message = new MimeMessage
        {
            MessageId = MessageIds.New(domain),
            Date = time.GetUtcNow(),
            Subject = replyTo is null
                ? (string.IsNullOrWhiteSpace(input.Subject) ? "(no subject)" : input.Subject.Trim())
                : $"Re: {replyTo.Subject} [{replyTo.DisplayId}]",
        };
        message.From.Add(new MailboxAddress(input.FromName?.Trim() ?? "", from));
        message.To.Add(new MailboxAddress(mailbox.Name, mailbox.Address));
        foreach (var cc in (input.Cc ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MailboxAddress.TryParse(cc, out var address))
            {
                message.Cc.Add(address);
            }
        }
        if (replyTo is not null)
        {
            var thread = await db.Set<MsgMessageMap>().AsNoTracking().Where(m => m.TicketId == replyTo.Id).OrderBy(m => m.CreatedAt).Select(m => m.MessageId).ToListAsync(ct);
            if (thread.Count > 0)
            {
                message.InReplyTo = thread[^1];
                foreach (var id in thread.TakeLast(10))
                {
                    message.References.Add(id);
                }
            }
        }
        var verdict = input.Authentication?.ToLowerInvariant() switch
        {
            "fail" => "fail",
            "none" => null,
            _ => "pass",
        };
        if (verdict is not null)
        {
            var authServId = mailbox.TrustedAuthServId ?? "switchboard-simulator";
            message.Headers.Add("Authentication-Results",
                $"{authServId}; spf={verdict} smtp.mailfrom={from}; dkim={verdict} header.d={domain}; dmarc={verdict} header.from={domain}");
        }
        if (input.AutoReply)
        {
            message.Headers.Add("Auto-Submitted", "auto-replied");
        }
        message.Headers.Add("X-Switchboard-Simulated", "true");
        message.Body = new TextPart("plain") { Text = input.Body };

        using var buffer = new MemoryStream();
        await message.WriteToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
