using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Notifications;
using Switchboard.Modules.Messaging.Outbound;

namespace Switchboard.Modules.Messaging.Inbound;

public enum TraceTone
{
    Ok,
    Info,
    Warn,
    Stop,
}

/// <summary>One step of how a message was routed; shown to admins on the incoming mail page.</summary>
public sealed record TraceStep(string Title, string Detail, TraceTone Tone = TraceTone.Info);

public sealed record InboundRequest(MsgMailbox Mailbox, byte[] Raw, InboundSource Source, bool BypassSenderRules = false, Guid? ReprocessOf = null);

/// <summary>
/// The one pipeline every incoming message goes through, whether it came by IMAP, webhook or the
/// simulator (FR-CH-1..4, SR-EML-1..3). It never throws for bad mail: the result is recorded on a
/// <see cref="MsgInboundMessage"/> with a step-by-step trace.
/// </summary>
public sealed partial class InboundPipeline(
    SwitchboardDbContext db,
    IActorAccessor actors,
    ITicketService tickets,
    IUserDirectory directory,
    IAttachmentService attachments,
    IHtmlContent html,
    IFileStore files,
    AddressRules rules,
    ThreadMatcher matcher,
    EmailCommands commands,
    BounceProcessor bounces,
    OutboundSettingsStore settingsStore,
    InboundMailContext context,
    TimeProvider time,
    ILogger<InboundPipeline> logger)
{
    public const long MaxMessageBytes = 40L * 1024 * 1024;
    public const string SpoofingTag = "possible-spoofing";
    private const int MaxAttachments = 20;

    public async Task<MsgInboundMessage> ProcessAsync(InboundRequest request, CancellationToken ct = default)
    {
        var trace = new List<TraceStep>();
        var record = new MsgInboundMessage
        {
            MailboxId = request.Mailbox.Id,
            Source = request.Source,
            ReceivedAt = time.GetUtcNow(),
            SizeBytes = request.Raw.LongLength,
            Outcome = InboundOutcome.Processing,
        };

        using var scope = actors.BeginScope(Actor.System("Incoming email", Channel.Email));
        try
        {
            if (request.Raw.LongLength > MaxMessageBytes)
            {
                Finish(record, InboundOutcome.Rejected, $"The message is larger than {MaxMessageBytes / (1024 * 1024)} MB.", trace, TraceTone.Stop, "Checked the size");
            }
            else
            {
                MimeMessage message;
                try
                {
                    message = await MimeMessage.LoadAsync(new MemoryStream(request.Raw, writable: false), ct);
                }
                catch (FormatException ex)
                {
                    Finish(record, InboundOutcome.Rejected, "This isn't a readable email message.", trace, TraceTone.Stop, "Read the message");
                    logger.LogInformation("Unreadable inbound message: {Error}", ex.Message);
                    message = null!;
                }
                if (message is not null)
                {
                    using var linked = context.Begin(record);
                    await RouteAsync(request, message, record, trace, ct);
                }
            }
        }
        catch (ValidationException ex)
        {
            await ResetAfterFailureAsync(record, ct);
            Finish(record, InboundOutcome.Rejected, ex.Message, trace, TraceTone.Stop, "Couldn't add it");
        }
        catch (DbUpdateException ex) when (DeliveryLog.IsUniqueViolation(ex))
        {
            // Another copy of the same Message-ID was linked first (two pollers, or a resend): nothing was saved.
            await ResetAfterFailureAsync(record, ct);
            Finish(record, InboundOutcome.Duplicate, "Another copy of this message was processed at the same time.", trace, TraceTone.Stop, "Checked for duplicates");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ResetAfterFailureAsync(record, ct);
            logger.LogError(ex, "Inbound message for mailbox {Mailbox} failed", request.Mailbox.Id);
            Finish(record, InboundOutcome.Failed, "Something went wrong while processing it. It was kept so you can try again.", trace, TraceTone.Stop, "Failed");
        }

        if (record.Outcome is InboundOutcome.Blocked or InboundOutcome.Failed or InboundOutcome.Rejected && record.RawStorageKey is null)
        {
            record.RawStorageKey = await files.SaveAsync(new MemoryStream(request.Raw, writable: false), ct);
        }
        if (record.MessageId.Length == 0)
        {
            record.MessageId = SyntheticId(request.Raw);
        }
        record.TraceJson = JsonSerializer.Serialize(trace, TraceJson.Options);
        await SaveRecordAsync(record, ct);
        return record;
    }

    private async Task RouteAsync(InboundRequest request, MimeMessage message, MsgInboundMessage record, List<TraceStep> trace, CancellationToken ct)
    {
        var mailbox = request.Mailbox;
        var sender = message.From.Mailboxes.FirstOrDefault() ?? message.Sender;
        record.MessageId = MessageIds.Normalize(message.MessageId) is { Length: > 0 and <= 450 } id ? id : SyntheticId(request.Raw);
        record.Subject = Limit(MessageIds.SingleLine(message.Subject ?? ""), 500);
        record.FromAddress = Limit(sender?.Address ?? "", 320);
        record.FromName = sender?.Name is { Length: > 0 } n ? Limit(n, 200) : null;

        var attachmentCount = message.Attachments.Count();
        trace.Add(new("Read the message", $"From {Describe(sender)}{(attachmentCount > 0 ? $", with {attachmentCount} attachment{(attachmentCount == 1 ? "" : "s")}" : "")}", TraceTone.Ok));

        if (sender is null || !IUserDirectory.IsValidEmail(sender.Address))
        {
            Finish(record, InboundOutcome.Rejected, "The message has no valid sender address.", trace, TraceTone.Stop, "Checked the sender");
            return;
        }
        var from = sender.Address.Trim();

        // Duplicates and loops.
        var known = await db.Set<MsgMessageMap>().AsNoTracking().FirstOrDefaultAsync(m => m.MessageId == record.MessageId, ct);
        if (known is not null)
        {
            var ticketId = known.TicketId;
            record.TicketId = ticketId;
            if (known.Direction == MessageDirection.Outbound)
            {
                Finish(record, InboundOutcome.Ignored, "This is a copy of an email Switchboard sent, so it was ignored to avoid a mail loop.", trace, TraceTone.Stop, "Checked for duplicates");
            }
            else
            {
                Finish(record, InboundOutcome.Duplicate, "This message was already processed.", trace, TraceTone.Stop, "Checked for duplicates");
            }
            return;
        }
        var processed = await db.Set<MsgInboundMessage>().AsNoTracking()
            .AnyAsync(m => m.MessageId == record.MessageId && m.Id != request.ReprocessOf
                && (m.Outcome == InboundOutcome.Ignored || m.Outcome == InboundOutcome.Bounced || m.Outcome == InboundOutcome.Created
                    || m.Outcome == InboundOutcome.Replied || m.Outcome == InboundOutcome.Noted), ct);
        if (processed)
        {
            Finish(record, InboundOutcome.Duplicate, "This message was already processed.", trace, TraceTone.Stop, "Checked for duplicates");
            return;
        }
        trace.Add(new("Checked for duplicates", "Not seen before", TraceTone.Ok));

        var own = await settingsStore.OwnAddressesAsync(ct);
        if (own.Contains(from))
        {
            Finish(record, InboundOutcome.Ignored, $"It was sent from {from}, one of your own support addresses, so it was ignored to avoid a mail loop.", trace, TraceTone.Stop, "Checked the sender");
            return;
        }

        var automatic = MailAnalysis.DetectAutomatic(message);
        trace.Add(new("Checked for automatic mail", automatic.Kind switch
        {
            AutomaticKind.None => "Written by a person",
            AutomaticKind.AutoReply => $"Automatic reply ({automatic.Reason}). It won't get a response.",
            AutomaticKind.Bounce => $"Delivery failure ({automatic.Reason})",
            _ => $"Automatic mail ({automatic.Reason}). It won't get a response.",
        }, automatic.Kind == AutomaticKind.None ? TraceTone.Ok : TraceTone.Warn));
        record.SuppressAutoResponses = automatic.Kind != AutomaticKind.None;

        if (!request.BypassSenderRules && automatic.Kind != AutomaticKind.Bounce)
        {
            var rule = await rules.EvaluateAsync(from, ct);
            if (rule is { Action: AddressRuleAction.Block })
            {
                Finish(record, InboundOutcome.Blocked, $"Blocked by the sender rule for {rule.Rule.Pattern}.", trace, TraceTone.Stop, "Checked sender rules");
                return;
            }
            trace.Add(new("Checked sender rules", rule is null ? "No rule for this sender" : $"Allowed by the rule for {rule.Rule.Pattern}", TraceTone.Ok));
        }
        else if (request.BypassSenderRules)
        {
            trace.Add(new("Checked sender rules", "Skipped: an admin chose to process this message anyway", TraceTone.Warn));
        }

        var auth = MailAnalysis.ReadAuthentication(message, mailbox.TrustedAuthServId);
        var verified = auth.Verdict == AuthVerdict.Pass || (auth.Verdict == AuthVerdict.None && mailbox.TrustUnauthenticated);
        var failed = auth.Verdict == AuthVerdict.Fail;
        record.AuthenticationFailed = failed;
        if (failed)
        {
            record.SuppressAutoResponses = true;
        }
        trace.Add(new("Checked sender authentication",
            failed ? $"Failed: {auth.Describe()}. It can't make changes, and the ticket is flagged as possible spoofing."
                : verified ? (auth.Verdict == AuthVerdict.Pass ? $"Passed: {auth.Describe()}" : "No results; this mailbox trusts its relay")
                : $"{auth.Describe()}. Agents' email from this sender can't make changes.",
            failed ? TraceTone.Warn : verified ? TraceTone.Ok : TraceTone.Info));

        if (automatic.Kind == AutomaticKind.Bounce)
        {
            var summary = await bounces.ProcessAsync(message, record, ct);
            Finish(record, InboundOutcome.Bounced, summary, trace, TraceTone.Info, "Recorded the bounce");
            return;
        }

        var (match, why) = await matcher.FindAsync(message, from, ct);
        trace.Add(new("Matched a ticket", match?.Detail ?? $"{why}. It starts a new ticket.", match is null ? TraceTone.Info : TraceTone.Ok));

        if (match is null && automatic.Kind == AutomaticKind.AutoReply)
        {
            Finish(record, InboundOutcome.Ignored, "An automatic reply with no ticket to add it to.", trace, TraceTone.Stop, "Ignored");
            return;
        }

        var user = await directory.FindByEmailAsync(from, ct);
        var body = ExtractBody(message, isReply: match is not null, trace);

        if (match is null)
        {
            await CreateTicketAsync(mailbox, message, record, from, sender.Name, body, own, failed, auth, trace, ct);
            return;
        }

        var ticket = match.Ticket;
        var isRequester = user is not null && (user.Id == ticket.RequesterId || user.Id == ticket.RequestedForId);
        var isAgent = user is { IsActive: true } && (user.IsAgent || user.IsOrgAdmin);

        if (automatic.Kind != AutomaticKind.None)
        {
            trace.Add(new("Identified the sender", Describe(sender)));
            var ids = await UploadAsync(message, body, isReply: true, trace, ct);
            await AddSystemNoteAsync(ticket.Id, $"<p><strong>Automatic reply from {Encode(Describe(sender))}</strong></p>{ids.Html}", ids.AttachmentIds, ct);
            Finish(record, InboundOutcome.Noted, $"Added to {ticket.DisplayId} as a private note. Automatic mail doesn't reopen tickets.", trace, TraceTone.Ok, "Added a note");
            return;
        }

        if (isRequester || !isAgent)
        {
            var forwardReply = !isRequester && match.ConversationId is { } cid
                && await db.TicketConversations.AnyAsync(c => c.Id == cid && c.Kind == ConversationKind.Forward, ct);
            if (forwardReply)
            {
                trace.Add(new("Identified the sender", $"{Describe(sender)}, replying to a forward"));
                var fwd = await UploadAsync(message, body, isReply: true, trace, ct);
                await AddSystemNoteAsync(ticket.Id, $"<p><strong>Reply from {Encode(Describe(sender))} to a forward</strong></p>{fwd.Html}", fwd.AttachmentIds, ct);
                Finish(record, InboundOutcome.Noted, $"Added to {ticket.DisplayId} as a private note, because it answers a forward.", trace, TraceTone.Ok, "Added a note");
                return;
            }

            var copied = ticket.CcEmails.Contains(from, StringComparer.OrdinalIgnoreCase);
            var who = user?.DisplayName ?? from;
            trace.Add(new("Identified the sender", isRequester ? $"The requester, {who}" : copied ? $"{who}, who is copied on this ticket" : $"{who}, who isn't on this ticket yet but replied to its email"));
            var upload = await UploadAsync(message, body, isReply: true, trace, ct);
            var result = await tickets.AddRequesterMessageAsync(ticket.Id, new RequesterMessageCommand
            {
                BodyHtml = NonEmpty(upload.Html, upload.AttachmentIds),
                FromEmail = from,
                AttachmentIds = upload.AttachmentIds,
                EmailMessageId = record.MessageId,
            }, ct);
            if (isRequester || ticket.CcEmails.Contains(from, StringComparer.OrdinalIgnoreCase))
            {
                await CaptureCcAsync(result.Ticket.Id, message, from, own, trace, ct);
            }
            if (failed)
            {
                await FlagSpoofingAsync(result.Ticket.Id, sender, auth, ct);
            }
            Finish(record, result.CreatedFollowUp ? InboundOutcome.Created : InboundOutcome.Replied,
                result.CreatedFollowUp ? $"{ticket.DisplayId} is closed, so this started the follow-up {result.Ticket.DisplayId}." : $"Added to {result.Ticket.DisplayId} as a reply.",
                trace, TraceTone.Ok, result.CreatedFollowUp ? "Created a follow-up" : "Added a reply");
            record.TicketId = result.Ticket.Id;
            return;
        }

        // An agent writing in. Only verified mail may act as the agent (SR-EML-2).
        trace.Add(new("Identified the sender", $"Agent {user!.DisplayName}"));
        if (!verified)
        {
            var unverified = await UploadAsync(message, body, isReply: true, trace, ct);
            var reason = failed ? "It failed sender authentication" : "Its sender couldn't be verified";
            await AddSystemNoteAsync(ticket.Id,
                $"<p><strong>Unverified email from {Encode(Describe(sender))}.</strong> {reason}, so it was added as a note and any commands in it were ignored.</p>{unverified.Html}",
                unverified.AttachmentIds, ct);
            if (failed)
            {
                await FlagSpoofingAsync(ticket.Id, sender, auth, ct);
            }
            Finish(record, InboundOutcome.Noted, $"Added to {ticket.DisplayId} as a private note from the system, not from {user.DisplayName}.", trace, TraceTone.Warn, "Added a note");
            return;
        }

        using (actors.BeginScope(Actor.ForUser(user, Channel.Email)))
        {
            var noteHtml = body;
            var summary = new List<string>();
            if (mailbox.AllowEmailCommands)
            {
                var parsed = EmailCommands.Parse(html.ToPlainText(html.Sanitize(body)));
                if (parsed.Commands.Count > 0)
                {
                    var outcome = await commands.ApplyAsync(ticket.Id, user, parsed.Commands, ct);
                    noteHtml = html.FromPlainText(parsed.RemainingText);
                    summary.AddRange(outcome.Applied.Select(a => char.ToUpperInvariant(a[0]) + a[1..]));
                    summary.AddRange(outcome.Problems);
                    trace.Add(new("Ran email commands",
                        string.Join(" ", outcome.Applied.Select(a => char.ToUpperInvariant(a[0]) + a[1..] + ".").Concat(outcome.Problems)),
                        outcome.Problems.Count > 0 ? TraceTone.Warn : TraceTone.Ok));
                }
            }

            var upload = await UploadAsync(message, noteHtml, isReply: true, trace, ct);
            var noteBody = upload.Html + (summary.Count > 0 ? $"<p><em>Email commands: {Encode(string.Join("; ", summary))}</em></p>" : "");
            if (html.ToPlainText(html.Sanitize(noteBody)).Length == 0 && upload.AttachmentIds.Count == 0)
            {
                Finish(record, InboundOutcome.Noted, summary.Count > 0 ? $"Ran the commands on {ticket.DisplayId}." : "The email was empty.", trace, TraceTone.Ok, "Done");
                record.TicketId = ticket.Id;
                return;
            }
            try
            {
                await tickets.AddNoteAsync(ticket.Id, new NoteCommand { BodyHtml = NonEmpty(noteBody, upload.AttachmentIds), IsPrivate = true, AttachmentIds = upload.AttachmentIds }, ct);
                Finish(record, InboundOutcome.Noted, $"Added to {ticket.DisplayId} as a private note from {user.DisplayName}.", trace, TraceTone.Ok, "Added a note");
                return;
            }
            catch (Exception ex) when (ex is NotFoundException or ForbiddenException)
            {
                trace.Add(new("Checked access", $"{user.DisplayName} can't add notes to {ticket.DisplayId}", TraceTone.Warn));
                noteHtml = $"<p><strong>Email from {Encode(Describe(sender))}</strong>, who can't add notes to this ticket.</p>{upload.Html}";
                await AddSystemNoteAsync(ticket.Id, noteHtml, upload.AttachmentIds, ct, asSystem: true);
                Finish(record, InboundOutcome.Noted, $"Added to {ticket.DisplayId} as a private note from the system.", trace, TraceTone.Warn, "Added a note");
            }
        }
    }

    private async Task CreateTicketAsync(MsgMailbox mailbox, MimeMessage message, MsgInboundMessage record, string from, string? fromName,
        string body, HashSet<string> own, bool failed, AuthCheck auth, List<TraceStep> trace, CancellationToken ct)
    {
        var known = await directory.FindByEmailAsync(from, ct);
        trace.Add(new("Identified the sender", known is null ? $"{from} is new; they'll be added as a requester" : $"{known.DisplayName}, an existing {(known.IsAgent ? "agent" : "requester")}"));

        var upload = await UploadAsync(message, body, isReply: false, trace, ct);
        var cc = CcCandidates(message, from, own).Take(20).ToList();
        if (cc.Count > 0)
        {
            trace.Add(new("Captured copied people", string.Join(", ", cc), TraceTone.Ok));
        }
        var ticket = await tickets.CreateAsync(new CreateTicketCommand
        {
            WorkspaceId = mailbox.WorkspaceId,
            Type = mailbox.TicketType,
            Subject = string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : Limit(MessageIds.SingleLine(message.Subject), 500),
            DescriptionHtml = upload.Html,
            RequesterEmail = from,
            RequesterName = fromName,
            CcEmails = cc,
            SourceKey = TicketSourceKeys.Email,
            AsRequester = true,
            AttachmentIds = upload.AttachmentIds,
            EmailMessageId = record.MessageId,
        }, ct);
        record.TicketId = ticket.Id;

        if (mailbox.DefaultGroupId is { } groupId)
        {
            try
            {
                await tickets.UpdateAsync(ticket.Id, new TicketPatch { GroupId = groupId }, ct: ct);
            }
            catch (ValidationException ex)
            {
                trace.Add(new("Assigned the group", $"The mailbox's default group couldn't be set: {ex.Message}", TraceTone.Warn));
            }
        }
        if (failed)
        {
            await FlagSpoofingAsync(ticket.Id, new MailboxAddress(fromName ?? "", from), auth, ct);
        }
        Finish(record, InboundOutcome.Created, $"Created {ticket.DisplayId}{(record.SuppressAutoResponses ? ", without an acknowledgement" : "")}.", trace, TraceTone.Ok, "Created a ticket");
    }

    private string ExtractBody(MimeMessage message, bool isReply, List<TraceStep> trace)
    {
        var htmlBody = message.HtmlBody;
        if (htmlBody is { Length: > 0 })
        {
            if (!isReply)
            {
                return htmlBody;
            }
            var stripped = QuotedText.StripHtml(htmlBody);
            if (stripped.Removed)
            {
                trace.Add(new("Removed quoted text", "Kept the new part of the reply and dropped the quoted history", TraceTone.Ok));
            }
            return stripped.Content;
        }
        var text = message.TextBody ?? "";
        if (!isReply)
        {
            return html.FromPlainText(text);
        }
        var plain = QuotedText.StripText(text);
        if (plain.Removed)
        {
            trace.Add(new("Removed quoted text", "Kept the new part of the reply and dropped the quoted history", TraceTone.Ok));
        }
        return html.FromPlainText(plain.Content);
    }

    private sealed record Uploaded(string Html, IReadOnlyList<Guid> AttachmentIds);

    /// <summary>Stores attachments as pending uploads (claimed by the ticket service) and points cid: images at them.</summary>
    private async Task<Uploaded> UploadAsync(MimeMessage message, string body, bool isReply, List<TraceStep> trace, CancellationToken ct)
    {
        var ids = new List<Guid>();
        var skipped = new List<string>();
        var bodyParts = new HashSet<MimeEntity>(ReferenceEqualityComparer.Instance);
        foreach (var part in message.BodyParts.OfType<TextPart>().Where(p => !p.IsAttachment))
        {
            bodyParts.Add(part);
        }

        foreach (var entity in message.BodyParts)
        {
            if (bodyParts.Contains(entity) || ids.Count >= MaxAttachments)
            {
                continue;
            }
            string fileName;
            string contentType;
            Stream content;
            var inline = false;
            string? contentId = null;
            switch (entity)
            {
                case MessagePart { Message: { } attached } mp when mp.ContentType.MimeType != "text/rfc822-headers":
                    fileName = $"{Safe(attached.Subject, "message")}.eml";
                    contentType = "message/rfc822";
                    content = new MemoryStream();
                    await attached.WriteToAsync(content, ct);
                    content.Position = 0;
                    break;
                case MimePart part when part.Content is not null:
                    if (part.ContentType.MimeType is "application/pkcs7-signature" or "application/x-pkcs7-signature" or "application/pgp-signature")
                    {
                        continue;
                    }
                    contentId = part.ContentId;
                    var referenced = contentId is not null && body.Contains("cid:" + contentId, StringComparison.OrdinalIgnoreCase);
                    if (!part.IsAttachment && !referenced && (isReply || !part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Inline parts nobody shows: usually images from quoted history.
                        continue;
                    }
                    inline = referenced;
                    fileName = part.FileName ?? $"{(inline ? "image" : "attachment")}{Extension(part.ContentType.MimeType)}";
                    contentType = part.ContentType.MimeType;
                    content = new MemoryStream();
                    await part.Content.DecodeToAsync(content, ct);
                    content.Position = 0;
                    break;
                default:
                    continue;
            }

            try
            {
                await using (content)
                {
                    var saved = await attachments.UploadAsync(content, fileName, contentType, AttachmentOwners.Pending, null, inline, ct);
                    ids.Add(saved.Id);
                    if (contentId is not null && inline)
                    {
                        body = Regex.Replace(body, "cid:" + Regex.Escape(contentId), $"/files/{saved.Id}/{Uri.EscapeDataString(saved.FileName)}",
                            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
                    }
                }
            }
            catch (ValidationException ex)
            {
                skipped.Add($"{fileName}: {ex.Message}");
            }
        }

        if (ids.Count > 0 || skipped.Count > 0)
        {
            trace.Add(new("Saved attachments",
                $"{ids.Count} saved{(skipped.Count > 0 ? $". Skipped {string.Join("; ", skipped)}" : "")}",
                skipped.Count > 0 ? TraceTone.Warn : TraceTone.Ok));
        }
        if (skipped.Count > 0)
        {
            body += $"<p><em>Not attached: {Encode(string.Join("; ", skipped))}</em></p>";
        }
        return new Uploaded(body, ids);
    }

    private async Task CaptureCcAsync(Guid ticketId, MimeMessage message, string from, HashSet<string> own, List<TraceStep> trace, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticketId, ct);
        var requesterEmail = await db.Users.Where(u => u.Id == ticket.RequesterId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        var added = CcCandidates(message, from, own)
            .Where(a => !ticket.CcEmails.Contains(a, StringComparer.OrdinalIgnoreCase) && !string.Equals(a, requesterEmail, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (added.Count == 0)
        {
            return;
        }
        var all = ticket.CcEmails.Concat(added).Take(50).ToArray();
        await tickets.UpdateAsync(ticketId, new TicketPatch { CcEmails = all }, ct: ct);
        trace.Add(new("Captured copied people", $"Added {string.Join(", ", added)} to the ticket's CC list", TraceTone.Ok));
    }

    private static IEnumerable<string> CcCandidates(MimeMessage message, string from, HashSet<string> own) =>
        message.To.Mailboxes.Concat(message.Cc.Mailboxes)
            .Select(m => m.Address.Trim())
            .Where(a => IUserDirectory.IsValidEmail(a) && !own.Contains(a) && !a.Equals(from, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private async Task AddSystemNoteAsync(Guid ticketId, string bodyHtml, IReadOnlyList<Guid> attachmentIds, CancellationToken ct, bool asSystem = false)
    {
        using var scope = asSystem ? actors.BeginScope(Actor.System("Incoming email", Channel.Email)) : null;
        await tickets.AddNoteAsync(ticketId, new NoteCommand { BodyHtml = NonEmpty(bodyHtml, attachmentIds), IsPrivate = true, AttachmentIds = attachmentIds }, ct);
    }

    /// <summary>SR-EML-3: tag the ticket and leave a note for agents.</summary>
    private async Task FlagSpoofingAsync(Guid ticketId, MailboxAddress sender, AuthCheck auth, CancellationToken ct)
    {
        using var scope = actors.BeginScope(Actor.System("Incoming email", Channel.Email));
        var tags = await db.TicketTags.AsNoTracking().Where(t => t.TicketId == ticketId).Select(t => t.Tag).ToListAsync(ct);
        if (!tags.Contains(SpoofingTag))
        {
            await tickets.UpdateAsync(ticketId, new TicketPatch { Tags = tags.Append(SpoofingTag).ToArray() }, ct: ct);
        }
        await tickets.AddNoteAsync(ticketId, new NoteCommand
        {
            BodyHtml = $"<p><strong>Possible spoofing.</strong> An email from {Encode(Describe(sender))} failed sender authentication ({Encode(auth.Describe())}). Check with the sender another way before acting on it.</p>",
            IsPrivate = true,
        }, ct);
    }

    /// <summary>Drops unsaved work, and forgets a ticket whose creation was rolled back.</summary>
    private async Task ResetAfterFailureAsync(MsgInboundMessage record, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        if (record.TicketId is { } ticketId && !await db.Tickets.AnyAsync(t => t.Id == ticketId, ct))
        {
            record.TicketId = null;
        }
    }

    private async Task SaveRecordAsync(MsgInboundMessage record, CancellationToken ct)
    {
        var entry = db.Entry(record);
        if (entry.State == EntityState.Detached)
        {
            var exists = await db.Set<MsgInboundMessage>().AsNoTracking().AnyAsync(m => m.Id == record.Id, ct);
            if (exists)
            {
                db.Update(record);
            }
            else
            {
                db.Add(record);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static void Finish(MsgInboundMessage record, InboundOutcome outcome, string detail, List<TraceStep> trace, TraceTone tone, string title)
    {
        record.Outcome = outcome;
        record.OutcomeDetail = Limit(detail, 1000);
        trace.Add(new(title, detail, tone));
    }

    private string NonEmpty(string body, IReadOnlyList<Guid> attachmentIds) =>
        html.ToPlainText(html.Sanitize(body)).Length > 0 || body.Contains("<img", StringComparison.OrdinalIgnoreCase)
            ? body
            : attachmentIds.Count > 0 ? "<p><em>Sent attachments only.</em></p>" : "<p><em>The email had no text.</em></p>";

    private static string SyntheticId(byte[] raw) => $"{Convert.ToHexStringLower(SHA256.HashData(raw))[..32]}@no-message-id.switchboard";

    private static string Describe(MailboxAddress? sender) =>
        sender is null ? "an unknown sender" : string.IsNullOrWhiteSpace(sender.Name) ? sender.Address : $"{sender.Name} <{sender.Address}>";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Safe(string? value, string fallback)
    {
        var cleaned = SafeName().Replace(value ?? "", "").Trim();
        return cleaned.Length == 0 ? fallback : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static string Extension(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        "text/plain" => ".txt",
        _ => "",
    };

    [GeneratedRegex(@"[^\w\s.-]", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex SafeName();
}

internal static class TraceJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
