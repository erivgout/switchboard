using Switchboard.Core.Domain;

namespace Switchboard.Modules.Messaging.Domain;

public enum SmtpSecurity
{
    /// <summary>STARTTLS when the server offers it.</summary>
    Auto = 0,
    None = 1,
    /// <summary>STARTTLS required.</summary>
    StartTls = 2,
    /// <summary>Implicit TLS (usually port 465).</summary>
    SslOnConnect = 3,
}

/// <summary>Outgoing mail settings. One row per instance. Secrets are encrypted (SR-DATA-4).</summary>
public class MsgOutboundSettings : Entity
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;
    public string? Username { get; set; }
    /// <summary>Encrypted with Data Protection; never shown again once saved.</summary>
    public string? PasswordProtected { get; set; }
    public string FromName { get; set; } = "";
    public string? FromAddress { get; set; }
    /// <summary>SMTP envelope sender (MAIL FROM), when it differs from the From header (SR-EML-4).</summary>
    public string? EnvelopeFrom { get; set; }

    public string? DkimDomain { get; set; }
    public string? DkimSelector { get; set; }
    /// <summary>Encrypted PEM private key.</summary>
    public string? DkimKeyProtected { get; set; }

    /// <summary>Non-production instances send everything here instead (SR-DEP-6). Empty means log only.</summary>
    public string? CaptureAddress { get; set; }
    /// <summary>Addresses or domains that still get real mail on a non-production instance.</summary>
    public List<string> NonProductionAllowlist { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum MailboxConnection
{
    /// <summary>A mail provider or relay POSTs raw messages to <c>/email/inbound/{id}</c>.</summary>
    Webhook = 1,
    Imap = 2,
}

/// <summary>A support address that turns incoming mail into tickets (FR-CH-1).</summary>
public class MsgMailbox : Entity
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
    public string? ImapPasswordProtected { get; set; }
    public string ImapFolder { get; set; } = "INBOX";
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Encrypted shared secret for the inbound webhook's HMAC signature (SR-NET-5).</summary>
    public string? WebhookSecretProtected { get; set; }

    /// <summary>Only trust <c>Authentication-Results</c> headers from this server (authserv-id). Empty trusts the topmost header.</summary>
    public string? TrustedAuthServId { get; set; }
    /// <summary>Treat mail without authentication results as verified. Only for internal relays that don't add them.</summary>
    public bool TrustUnauthenticated { get; set; }
    /// <summary>Let verified agents change tickets with #commands in replies (FR-CH-4).</summary>
    public bool AllowEmailCommands { get; set; } = true;

    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum MessageDirection
{
    Inbound = 1,
    Outbound = 2,
}

/// <summary>Message-ID to ticket, for threading replies (FR-CH-2). Message-IDs are unique, which also dedupes inbound mail.</summary>
public class MsgMessageMap : Entity
{
    /// <summary>Without angle brackets.</summary>
    public string MessageId { get; set; } = "";
    public Guid TicketId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? MailboxId { get; set; }
    public MessageDirection Direction { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum InboundOutcome
{
    Processing = 0,
    Created = 1,
    Replied = 2,
    Noted = 3,
    Ignored = 4,
    Duplicate = 5,
    Blocked = 6,
    Bounced = 7,
    Rejected = 8,
    Failed = 9,
}

public enum InboundSource
{
    Webhook = 1,
    Imap = 2,
    Simulated = 3,
}

/// <summary>One incoming message and what happened to it. Blocked and failed messages keep their raw MIME so they can be processed later.</summary>
public class MsgInboundMessage : Entity
{
    public Guid? MailboxId { get; set; }
    public InboundSource Source { get; set; }
    public string MessageId { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }
    public string Subject { get; set; } = "";
    public long SizeBytes { get; set; }
    public InboundOutcome Outcome { get; set; }
    public string? OutcomeDetail { get; set; }
    public Guid? TicketId { get; set; }
    /// <summary>Suppress acknowledgements and other automatic replies (SR-EML-1).</summary>
    public bool SuppressAutoResponses { get; set; }
    public bool AuthenticationFailed { get; set; }
    /// <summary>JSON array of routing steps.</summary>
    public string TraceJson { get; set; } = "[]";
    /// <summary>File store key of the raw message, kept for blocked and failed mail.</summary>
    public string? RawStorageKey { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public enum DeliveryChannel
{
    Email = 1,
    InApp = 2,
}

public enum DeliveryStatus
{
    Pending = 0,
    Sent = 1,
    /// <summary>Non-production: sent to the capture address instead.</summary>
    Redirected = 2,
    /// <summary>Not sent; written to the log (no transport, or non-production without a capture address).</summary>
    Logged = 3,
    Failed = 4,
    /// <summary>Deliberately not sent: rate limit, bounced address, own address, deactivated user.</summary>
    Suppressed = 5,
    Bounced = 6,
    /// <summary>Sending started but its result was never recorded. It may have gone out, so it is never retried (INV-NOT-1).</summary>
    Unconfirmed = 7,
}

/// <summary>
/// Every notification sent, per recipient and channel (FR-NOT-5). The unique index on
/// (EventId, Recipient, Channel) makes each event produce at most one message per recipient and channel (INV-NOT-1).
/// </summary>
public class MsgDelivery : Entity
{
    /// <summary>The domain event that caused it; null for ad-hoc system mail.</summary>
    public Guid? EventId { get; set; }
    /// <summary>Lower-case email address, or the user id for in-app notifications.</summary>
    public string Recipient { get; set; } = "";
    public DeliveryChannel Channel { get; set; }
    public Guid? RecipientUserId { get; set; }
    public Guid? TicketId { get; set; }
    public string TemplateKey { get; set; } = "";
    public string Subject { get; set; } = "";
    public DeliveryStatus Status { get; set; }
    public string? Error { get; set; }
    public string? MessageId { get; set; }
    /// <summary>Automatic notifications count towards the per-recipient and per-ticket rate limits.</summary>
    public bool IsAutomatic { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>An in-app notification shown in the bell and on /notifications.</summary>
public class MsgNotification : Entity
{
    public Guid UserId { get; set; }
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string? Link { get; set; }
    /// <summary>Record stamp shown beside the title, e.g. INC-1042.</summary>
    public string? Reference { get; set; }
    public Guid? TicketId { get; set; }
    public Guid? EventId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>A notification template for one event in one workspace (FR-NOT-2). Missing rows fall back to the built-in default.</summary>
public class MsgTemplate : Entity
{
    public Guid WorkspaceId { get; set; }
    public string EventKey { get; set; } = "";
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
}

/// <summary>A user's channel choice for one agent notification (FR-NOT-4).</summary>
public class MsgPreference
{
    public Guid UserId { get; set; }
    public string EventKey { get; set; } = "";
    public bool Email { get; set; }
    public bool InApp { get; set; }
}

public enum AddressRuleAction
{
    Allow = 1,
    Block = 2,
}

/// <summary>Allow or block incoming mail by address or domain (FR-CH-3).</summary>
public class MsgAddressRule : Entity
{
    /// <summary>A full address (<c>pat@example.com</c>) or a domain (<c>example.com</c>), lower case.</summary>
    public string Pattern { get; set; } = "";
    public AddressRuleAction Action { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Addresses that bounced (SR-EML-4). Automatic notifications skip them.</summary>
public class MsgBouncedAddress : Entity
{
    public string Address { get; set; } = "";
    public string? Reason { get; set; }
    public int Count { get; set; }
    public DateTimeOffset FirstBouncedAt { get; set; }
    public DateTimeOffset LastBouncedAt { get; set; }
}
