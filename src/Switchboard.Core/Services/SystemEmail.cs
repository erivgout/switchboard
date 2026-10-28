using Microsoft.Extensions.Logging;

namespace Switchboard.Core.Services;

public sealed record OutboundEmail(
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string? TextBody = null,
    IReadOnlyList<string>? Cc = null,
    IReadOnlyList<string>? Bcc = null,
    string? ReplyTo = null,
    /// <summary>Message-ID this mail replies to, for threading.</summary>
    string? InReplyTo = null,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// Sends one email now. The messaging module provides the real implementation (SMTP or a provider
/// adapter); until then messages are written to the log.
/// </summary>
public interface ISystemEmailSender
{
    /// <returns>The Message-ID assigned to the sent message.</returns>
    Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default);
}

public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : ISystemEmailSender
{
    public Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var id = $"<{Guid.NewGuid():N}@switchboard.local>";
        logger.LogWarning("No mail transport configured. Email to {To} with subject '{Subject}' was not sent:\n{Body}",
            string.Join(", ", email.To), email.Subject, email.TextBody ?? email.HtmlBody);
        return Task.FromResult(id);
    }
}
