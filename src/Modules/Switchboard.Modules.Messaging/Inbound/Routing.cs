using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Outbound;

namespace Switchboard.Modules.Messaging.Inbound;

public sealed record RuleMatch(AddressRuleAction Action, MsgAddressRule Rule);

/// <summary>Allow and block lists by address or domain (FR-CH-3). The most specific rule wins; at equal specificity, allow wins.</summary>
public sealed partial class AddressRules(SwitchboardDbContext db)
{
    public async Task<RuleMatch?> EvaluateAsync(string address, CancellationToken ct)
    {
        var lower = address.Trim().ToLowerInvariant();
        var domain = lower.Contains('@') ? lower[(lower.IndexOf('@') + 1)..] : lower;
        var candidates = DomainCandidates(domain).Append(lower).ToList();
        var rules = await db.Set<MsgAddressRule>().AsNoTracking().Where(r => candidates.Contains(r.Pattern)).ToListAsync(ct);
        var best = rules
            .OrderByDescending(r => r.Pattern.Contains('@') ? 1000 : r.Pattern.Length)
            .ThenBy(r => r.Action == AddressRuleAction.Allow ? 0 : 1)
            .FirstOrDefault();
        return best is null ? null : new RuleMatch(best.Action, best);
    }

    /// <summary>"mail.example.com" also matches rules for "example.com".</summary>
    private static IEnumerable<string> DomainCandidates(string domain)
    {
        var parts = domain.Split('.');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            yield return string.Join('.', parts[i..]);
        }
    }

    /// <summary>Accepts "pat@example.com", "example.com" or "@example.com"; returns the stored form or null.</summary>
    public static string? NormalizePattern(string? input)
    {
        var value = (input ?? "").Trim().ToLowerInvariant().TrimStart('@');
        if (value.Length is 0 or > 320 || value.Contains(' '))
        {
            return null;
        }
        if (value.Contains('@'))
        {
            return Core.Services.IUserDirectory.IsValidEmail(value) ? value : null;
        }
        return DomainPattern().IsMatch(value) ? value : null;
    }

    [GeneratedRegex(@"^(?=.{3,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex DomainPattern();
}

public enum MatchMethod
{
    Headers,
    SubjectToken,
}

public sealed record ThreadMatch(Ticket Ticket, MatchMethod Method, Guid? ConversationId, string Detail);

/// <summary>
/// Finds the ticket a message belongs to (FR-CH-2): In-Reply-To and References through
/// <see cref="MsgMessageMap"/>, then the <c>[INC-1042]</c> subject token. A subject token alone
/// only counts when the sender is already on that ticket, so nobody can post into a ticket by guessing its number.
/// </summary>
public sealed partial class ThreadMatcher(SwitchboardDbContext db)
{
    public async Task<(ThreadMatch? Match, string Detail)> FindAsync(MimeMessage message, string fromAddress, CancellationToken ct)
    {
        var ids = new List<string>();
        if (message.InReplyTo is { Length: > 0 } inReplyTo)
        {
            ids.Add(MessageIds.Normalize(inReplyTo));
        }
        ids.AddRange(message.References.Reverse().Select(MessageIds.Normalize));
        ids = ids.Where(i => i.Length is > 0 and <= 450).Distinct(StringComparer.Ordinal).Take(30).ToList();

        if (ids.Count > 0)
        {
            var mapped = await db.Set<MsgMessageMap>().AsNoTracking().Where(m => ids.Contains(m.MessageId)).ToListAsync(ct);
            foreach (var id in ids)
            {
                var hit = mapped.FirstOrDefault(m => m.MessageId == id);
                if (hit is not null && await LoadAsync(hit.TicketId, ct) is { } ticket)
                {
                    var header = message.InReplyTo is { } irt && MessageIds.Normalize(irt) == id ? "In-Reply-To" : "References";
                    return (new ThreadMatch(ticket, MatchMethod.Headers, hit.ConversationId, $"{ticket.DisplayId}, by the {header} header"), "");
                }
            }
            var conversation = await db.TicketConversations.AsNoTracking()
                .Where(c => c.EmailMessageId != null && ids.Contains(c.EmailMessageId))
                .Select(c => new { c.TicketId, c.Id })
                .FirstOrDefaultAsync(ct);
            if (conversation is not null && await LoadAsync(conversation.TicketId, ct) is { } byConversation)
            {
                return (new ThreadMatch(byConversation, MatchMethod.Headers, conversation.Id, $"{byConversation.DisplayId}, by the reply headers"), "");
            }
        }

        var token = SubjectToken().Match(message.Subject ?? "");
        if (!token.Success)
        {
            return (null, ids.Count > 0 ? "No known message in the reply headers, and no ticket number in the subject" : "Not a reply to a known message");
        }
        var type = token.Groups[1].Value.Equals("INC", StringComparison.OrdinalIgnoreCase) ? TicketType.Incident : TicketType.ServiceRequest;
        var number = int.Parse(token.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var displayId = Ticket.FormatDisplayId(type, number);
        var candidate = await db.Tickets.AsNoTracking().Where(t => t.Type == type && t.Number == number).Select(t => t.Id).FirstOrDefaultAsync(ct);
        if (candidate == Guid.Empty || await LoadAsync(candidate, ct) is not { } found)
        {
            return (null, $"The subject mentions {displayId}, which doesn't exist");
        }
        if (!await IsParticipantAsync(found, fromAddress, ct))
        {
            return (null, $"The subject mentions {displayId}, but the sender isn't on that ticket");
        }
        return (new ThreadMatch(found, MatchMethod.SubjectToken, null, $"{found.DisplayId}, by the ticket number in the subject"), "");
    }

    private async Task<Ticket?> LoadAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null || ticket.PurgedAt is not null || ticket.DeletedAt is not null)
        {
            return null;
        }
        // Mail to a merged ticket continues on its primary.
        if (ticket.MergedIntoId is { } primary)
        {
            var merged = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == primary && t.PurgedAt == null && t.DeletedAt == null, ct);
            return merged ?? ticket;
        }
        return ticket;
    }

    private async Task<bool> IsParticipantAsync(Ticket ticket, string address, CancellationToken ct)
    {
        var normalized = address.Trim().ToUpperInvariant();
        if (ticket.CcEmails.Any(c => c.Equals(address, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        var user = await db.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalized).Select(u => new { u.Id, u.IsAgent, u.IsOrgAdmin, u.IsActive }).FirstOrDefaultAsync(ct)
            ?? await db.UserEmails.AsNoTracking().Where(e => e.NormalizedEmail == normalized)
                .Join(db.Users, e => e.UserId, u => u.Id, (e, u) => new { u.Id, u.IsAgent, u.IsOrgAdmin, u.IsActive })
                .FirstOrDefaultAsync(ct);
        if (user is not null && (user.Id == ticket.RequesterId || user.Id == ticket.RequestedForId || (user.IsActive && (user.IsAgent || user.IsOrgAdmin))))
        {
            return true;
        }
        if (user is not null && await db.TicketShares.AnyAsync(s => s.TicketId == ticket.Id && s.UserId == user.Id, ct))
        {
            return true;
        }
        return await db.TicketConversations.AnyAsync(c => c.TicketId == ticket.Id && c.FromEmail == address, ct);
    }

    [GeneratedRegex(@"\[(INC|SR)-(\d{1,9})\]", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex SubjectToken();
}

/// <summary>
/// The incoming message being processed in this scope. The pre-commit linker uses it to record the
/// message against the ticket or conversation it creates, in the same transaction.
/// </summary>
public sealed class InboundMailContext
{
    public MsgInboundMessage? Message { get; private set; }
    public bool Linked { get; private set; }

    public IDisposable Begin(MsgInboundMessage message)
    {
        Message = message;
        Linked = false;
        return new End(this);
    }

    internal void MarkLinked() => Linked = true;

    private sealed class End(InboundMailContext context) : IDisposable
    {
        public void Dispose() => context.Message = null;
    }
}

/// <summary>
/// Links the incoming Message-ID to the ticket or conversation it produced, inside the same save.
/// The unique Message-ID index makes a second copy of the same message fail as a whole, so a
/// message never creates two tickets.
/// </summary>
public sealed class InboundLinker(InboundMailContext context, SwitchboardDbContext db) :
    IPreCommitHandler<TicketCreated>,
    IPreCommitHandler<TicketConversationAdded>
{
    public Task HandleAsync(TicketCreated e, CancellationToken ct)
    {
        Link(e.TicketId, null, InboundOutcome.Created);
        return Task.CompletedTask;
    }

    public Task HandleAsync(TicketConversationAdded e, CancellationToken ct)
    {
        Link(e.TicketId, e.ConversationId, e.Kind == ConversationKind.RequesterMessage ? InboundOutcome.Replied : InboundOutcome.Noted);
        return Task.CompletedTask;
    }

    private void Link(Guid ticketId, Guid? conversationId, InboundOutcome outcome)
    {
        if (context.Message is not { } message || context.Linked)
        {
            return;
        }
        context.MarkLinked();
        message.TicketId = ticketId;
        message.Outcome = outcome;
        db.Add(new MsgMessageMap
        {
            MessageId = message.MessageId,
            TicketId = ticketId,
            ConversationId = conversationId,
            MailboxId = message.MailboxId,
            Direction = MessageDirection.Inbound,
            CreatedAt = message.ReceivedAt,
        });
        if (db.Entry(message).State == EntityState.Detached)
        {
            db.Add(message);
        }
    }
}
