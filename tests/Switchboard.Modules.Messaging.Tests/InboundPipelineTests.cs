using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Messaging.Admin;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Inbound;
using Switchboard.Modules.Messaging.Notifications;
using Switchboard.Testing;

namespace Switchboard.Modules.Messaging.Tests;

/// <summary>The inbound pipeline end to end: MimeKit-built messages in, tickets and email out.</summary>
public sealed class InboundPipelineTests : IAsyncLifetime
{
    private const string Support = "help@support.test";
    private MessagingHost _m = default!;
    private MsgMailbox _mailbox = default!;
    private User _agent = default!;

    public async Task InitializeAsync()
    {
        _m = await MessagingHost.CreateAsync();
        _mailbox = await _m.CreateMailboxAsync(Support);
        _agent = await _m.Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
    }

    public async Task DisposeAsync() => await _m.DisposeAsync();

    private static string Unique(string name) => $"{name}.{Guid.NewGuid().ToString("N")[..6]}@customer.test";

    [Fact]
    public async Task New_ticket_from_unknown_sender_creates_requester_ticket_and_acknowledgement()
    {
        var from = Unique("new.person");
        var message = Mime.Message(from, Support, "Printer on floor 3 is jammed", "It jams on every page.", fromName: "Nell Person");
        message.Cc.Add(MailboxAddress.Parse("colleague@customer.test"));

        var result = await _m.ReceiveAsync(_mailbox, message);

        Assert.Equal(InboundOutcome.Created, result.Outcome);
        var ticket = await _m.TicketAsync(result.TicketId!.Value);
        var requester = await _m.DbAsync(db => db.Users.AsNoTracking().FirstAsync(u => u.Id == ticket.RequesterId));
        Assert.Equal(from, requester.Email);
        Assert.Equal("Nell Person", requester.DisplayName);
        Assert.False(requester.IsAgent);
        Assert.Equal("Printer on floor 3 is jammed", ticket.Subject);
        Assert.Contains("colleague@customer.test", ticket.CcEmails);
        Assert.NotNull(ticket.GroupId);
        var source = await _m.DbAsync(db => db.TicketSources.AsNoTracking().FirstAsync(s => s.Id == ticket.SourceId));
        Assert.Equal(TicketSourceKeys.Email, source.Key);

        // The message is mapped for threading.
        var mapped = await _m.DbAsync(db => db.Set<MsgMessageMap>().AsNoTracking().FirstAsync(x => x.MessageId == message.MessageId));
        Assert.Equal(ticket.Id, mapped.TicketId);

        await _m.Host.ProcessOutboxAsync();
        var ack = Assert.Single(_m.Mail.To(from));
        Assert.Contains($"[{ticket.DisplayId}]", ack.Subject);
        Assert.Equal("auto-generated", ack.Headers["Auto-Submitted"]);
        Assert.Equal(message.MessageId, ack.InReplyTo);
        Assert.Contains(EmailLayout.ReplyMarker, ack.HtmlBody);
    }

    [Fact]
    public async Task Reply_threads_by_in_reply_to_header()
    {
        var from = Unique("threader");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(from, Support, "VPN keeps dropping", "Every ten minutes."));
        await _m.Host.ProcessOutboxAsync();
        var ack = Assert.Single(_m.Mail.To(from));

        // A reply with a different subject and no token still lands on the ticket through its headers.
        var reply = Mime.Message(from, Support, "Re: something else entirely", "It happened again just now.").InReplyTo(ack.MessageId!);
        var result = await _m.ReceiveAsync(_mailbox, reply);

        Assert.Equal(InboundOutcome.Replied, result.Outcome);
        Assert.Equal(first.TicketId, result.TicketId);
        var conversation = await _m.DbAsync(db => db.TicketConversations.AsNoTracking().SingleAsync(c => c.TicketId == first.TicketId));
        Assert.Equal(ConversationKind.RequesterMessage, conversation.Kind);
        Assert.Equal(reply.MessageId, conversation.EmailMessageId);
        Assert.Equal(from, conversation.FromEmail);
    }

    [Fact]
    public async Task Reply_threads_by_subject_token_when_headers_are_missing()
    {
        var from = Unique("token");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(from, Support, "Monitor flickers", "Since the update."));
        var ticket = await _m.TicketAsync(first.TicketId!.Value);

        var reply = Mime.Message(from, Support, $"RE: Monitor flickers [{ticket.DisplayId}]", "Still flickering.");
        var result = await _m.ReceiveAsync(_mailbox, reply);

        Assert.Equal(InboundOutcome.Replied, result.Outcome);
        Assert.Equal(ticket.Id, result.TicketId);
    }

    [Fact]
    public async Task Subject_token_from_a_stranger_starts_a_new_ticket()
    {
        var owner = Unique("owner");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(owner, Support, "Laptop battery", "Drains fast."));
        var ticket = await _m.TicketAsync(first.TicketId!.Value);

        var stranger = Unique("stranger");
        var result = await _m.ReceiveAsync(_mailbox, Mime.Message(stranger, Support, $"Re: [{ticket.DisplayId}]", "Let me in."));

        Assert.Equal(InboundOutcome.Created, result.Outcome);
        Assert.NotEqual(ticket.Id, result.TicketId);
    }

    [Fact]
    public async Task Quoted_history_is_removed_from_replies()
    {
        var from = Unique("quoter");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(from, Support, "Keyboard missing keys", "The F and J keys."));
        var ticket = await _m.TicketAsync(first.TicketId!.Value);

        var text = $"Thanks, that fixed it.\n\nOn Tue, Oct 27, 2026 at 9:14 AM IT support <{Support}> wrote:\n> Hi, try the spare keyboard.\n> Sam";
        var result = await _m.ReceiveAsync(_mailbox, Mime.Message(from, Support, $"Re: Keyboard missing keys [{ticket.DisplayId}]", text));

        Assert.Equal(InboundOutcome.Replied, result.Outcome);
        var conversation = await _m.DbAsync(db => db.TicketConversations.AsNoTracking().SingleAsync(c => c.TicketId == ticket.Id));
        Assert.Contains("Thanks, that fixed it.", conversation.BodyText);
        Assert.DoesNotContain("spare keyboard", conversation.BodyText);
        Assert.DoesNotContain("wrote:", conversation.BodyText);
    }

    [Fact]
    public async Task Agent_reply_by_email_becomes_a_private_note_and_runs_commands()
    {
        var requester = Unique("needs.help");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(requester, Support, "Can't print", "Nothing comes out."));
        var ticket = await _m.TicketAsync(first.TicketId!.Value);

        var reply = Mime.Message(_agent.Email!, Support, $"Re: Can't print [{ticket.DisplayId}]",
            "#priority high\n#assign me\n\nChecked the queue; the spooler is stuck. Restarting it now.").WithAuth("pass");
        var result = await _m.ReceiveAsync(_mailbox, reply);

        Assert.Equal(InboundOutcome.Noted, result.Outcome);
        var note = await _m.DbAsync(db => db.TicketConversations.AsNoTracking().SingleAsync(c => c.TicketId == ticket.Id));
        Assert.Equal(ConversationKind.Note, note.Kind);
        Assert.True(note.IsPrivate);
        Assert.Equal(_agent.Id, note.AuthorId);
        Assert.Contains("spooler is stuck", note.BodyText);
        Assert.DoesNotContain("#priority", note.BodyText);

        var updated = await _m.TicketAsync(ticket.Id);
        Assert.Equal(Priority.High, updated.Priority);
        Assert.Equal(_agent.Id, updated.AgentId);

        // A private note is never emailed to the requester.
        await _m.Host.ProcessOutboxAsync();
        Assert.DoesNotContain(_m.Mail.To(requester), m => m.HtmlBody?.Contains("spooler") == true);
    }

    [Fact]
    public async Task Unverified_agent_mail_cannot_act_as_the_agent()
    {
        var requester = Unique("spoof.target");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(requester, Support, "Password reset", "Locked out."));
        var ticket = await _m.TicketAsync(first.TicketId!.Value);

        var forged = Mime.Message(_agent.Email!, Support, $"Re: Password reset [{ticket.DisplayId}]", "#status Closed\n\nDone.").WithAuth("fail");
        var result = await _m.ReceiveAsync(_mailbox, forged);

        Assert.Equal(InboundOutcome.Noted, result.Outcome);
        Assert.True(result.AuthenticationFailed);
        var updated = await _m.TicketAsync(ticket.Id);
        Assert.NotEqual(StatusCategory.Closed, updated.Status!.Category);
        Assert.Contains(updated.Tags, t => t.Tag == InboundPipeline.SpoofingTag);
        var notes = await _m.DbAsync(db => db.TicketConversations.AsNoTracking().Where(c => c.TicketId == ticket.Id).ToListAsync());
        Assert.All(notes, n => Assert.True(n.IsPrivate));
        Assert.All(notes, n => Assert.Null(n.AuthorId));
    }

    [Fact]
    public async Task Authentication_for_another_domain_cannot_act_as_an_agent()
    {
        var requester = Unique("aligned.target");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(requester, Support, "Printer offline", "Again."));
        var ticket = await _m.TicketAsync(first.TicketId!.Value);

        // The attacker's own domain passes SPF and DKIM, but the From header claims to be the agent.
        var forged = Mime.Message(_agent.Email!, Support, $"Re: Printer offline [{ticket.DisplayId}]", "#status Closed\n\nClosing this.");
        forged.Headers.Add("Authentication-Results", "mx.test.local; spf=pass smtp.mailfrom=x@evil.test; dkim=pass header.d=evil.test");
        var result = await _m.ReceiveAsync(_mailbox, forged);

        Assert.Equal(InboundOutcome.Noted, result.Outcome);
        var updated = await _m.TicketAsync(ticket.Id);
        Assert.NotEqual(StatusCategory.Closed, updated.Status!.Category);
        var note = await _m.DbAsync(db => db.TicketConversations.AsNoTracking().SingleAsync(c => c.TicketId == ticket.Id));
        Assert.Null(note.AuthorId);
    }

    [Fact]
    public async Task Requester_reply_failing_authentication_is_flagged_as_possible_spoofing()
    {
        var from = Unique("flagged");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(from, Support, "Shared drive", "Can't see it."));
        var ticket = await _m.TicketAsync(first.TicketId!.Value);

        var result = await _m.ReceiveAsync(_mailbox, Mime.Message(from, Support, $"Re: Shared drive [{ticket.DisplayId}]", "Any news?").WithAuth("fail"));

        Assert.Equal(InboundOutcome.Replied, result.Outcome);
        var updated = await _m.TicketAsync(ticket.Id);
        Assert.Contains(updated.Tags, t => t.Tag == InboundPipeline.SpoofingTag);
        var notes = await _m.DbAsync(db => db.TicketConversations.AsNoTracking().Where(c => c.TicketId == ticket.Id && c.Kind == ConversationKind.Note).ToListAsync());
        Assert.Contains(notes, n => n.IsPrivate && n.BodyText.Contains("Possible spoofing"));
    }

    [Fact]
    public async Task Out_of_office_reply_is_noted_without_reopening_or_answering()
    {
        var from = Unique("away");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(from, Support, "Docking station", "No video."));
        var ticket = await _m.TicketAsync(first.TicketId!.Value);
        var resolved = await _m.DbAsync(db => db.TicketStatuses.AsNoTracking().FirstAsync(s => s.WorkspaceId == ticket.WorkspaceId && s.Category == StatusCategory.Resolved));
        await _m.Host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { StatusId = resolved.Id }));
        await _m.Host.ProcessOutboxAsync();
        var mailBefore = _m.Mail.To(from).Count;

        var ooo = Mime.Message(from, Support, $"Automatic reply: Resolved: Docking station [{ticket.DisplayId}]", "I'm out until Monday.");
        ooo.Headers.Add("Auto-Submitted", "auto-replied");
        var result = await _m.ReceiveAsync(_mailbox, ooo);

        Assert.Equal(InboundOutcome.Noted, result.Outcome);
        Assert.True(result.SuppressAutoResponses);
        var after = await _m.TicketAsync(ticket.Id);
        Assert.Equal(StatusCategory.Resolved, after.Status!.Category);
        await _m.Host.ProcessOutboxAsync();
        Assert.Equal(mailBefore, _m.Mail.To(from).Count);
    }

    [Fact]
    public async Task Automatic_reply_with_no_ticket_is_ignored_and_never_answered()
    {
        var from = Unique("robot");
        var ooo = Mime.Message(from, Support, "Out of office", "Back next week.");
        ooo.Headers.Add("X-Autoreply", "yes");

        var result = await _m.ReceiveAsync(_mailbox, ooo);

        Assert.Equal(InboundOutcome.Ignored, result.Outcome);
        Assert.Null(result.TicketId);
        await _m.Host.ProcessOutboxAsync();
        Assert.Empty(_m.Mail.To(from));
    }

    [Fact]
    public async Task Bulk_mail_creates_a_ticket_without_an_acknowledgement()
    {
        var from = Unique("monitoring");
        var alert = Mime.Message(from, Support, "Disk 92% full on FS01", "Threshold crossed.");
        alert.Headers.Add("Precedence", "bulk");

        var result = await _m.ReceiveAsync(_mailbox, alert);

        Assert.Equal(InboundOutcome.Created, result.Outcome);
        await _m.Host.ProcessOutboxAsync();
        Assert.Empty(_m.Mail.To(from));
    }

    [Fact]
    public async Task The_same_message_twice_creates_one_ticket()
    {
        var from = Unique("twice");
        var message = Mime.Message(from, Support, "Phone won't sync", "Calendar is empty.");

        var first = await _m.ReceiveAsync(_mailbox, message);
        var second = await _m.ReceiveAsync(_mailbox, message);

        Assert.Equal(InboundOutcome.Created, first.Outcome);
        Assert.Equal(InboundOutcome.Duplicate, second.Outcome);
        var count = await _m.DbAsync(db => db.Tickets.CountAsync(t => t.Subject == "Phone won't sync"));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Blocked_sender_is_held_and_can_be_processed_anyway()
    {
        var from = Unique("blocked");
        await _m.Host.RunAsSystemAsync(async sp =>
        {
            await sp.GetRequiredService<SenderRuleService>().AddAsync(from, AddressRuleAction.Block, "test");
            return true;
        });

        var held = await _m.ReceiveAsync(_mailbox, Mime.Message(from, Support, "Buy cheap watches", "..."));
        Assert.Equal(InboundOutcome.Blocked, held.Outcome);
        Assert.NotNull(held.RawStorageKey);
        Assert.Null(held.TicketId);

        var again = await _m.Host.RunAsSystemAsync(sp => sp.GetRequiredService<InboundAdminService>().ReprocessAsync(held.Id));
        Assert.Equal(InboundOutcome.Created, again.Outcome);
    }

    [Fact]
    public async Task Mail_from_our_own_support_address_is_ignored()
    {
        var result = await _m.ReceiveAsync(_mailbox, Mime.Message(Support, Support, "Loop", "Should never become a ticket."));
        Assert.Equal(InboundOutcome.Ignored, result.Outcome);
        Assert.Null(result.TicketId);
    }

    [Fact]
    public async Task Inline_images_and_attachments_are_attached_and_cid_links_rewritten()
    {
        var from = Unique("screenshots");
        var builder = new BodyBuilder { HtmlBody = "<p>See the error:</p><img src=\"cid:shot1\">", TextBody = "See the error" };
        var image = builder.LinkedResources.Add("error.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3], new ContentType("image", "png"));
        image.ContentId = "shot1";
        builder.Attachments.Add("log.txt", "line one\nline two"u8.ToArray(), new ContentType("text", "plain"));
        var message = Mime.Message(from, Support, "Error on login", "");
        message.Body = builder.ToMessageBody();

        var result = await _m.ReceiveAsync(_mailbox, message);

        Assert.Equal(InboundOutcome.Created, result.Outcome);
        var ticket = await _m.TicketAsync(result.TicketId!.Value);
        var files = await _m.DbAsync(db => db.Attachments.AsNoTracking().Where(a => a.OwnerType == AttachmentOwners.Ticket && a.OwnerId == ticket.Id).ToListAsync());
        Assert.Equal(2, files.Count);
        var png = files.Single(f => f.FileName == "error.png");
        Assert.Contains($"/files/{png.Id}/", ticket.DescriptionHtml);
        Assert.DoesNotContain("cid:", ticket.DescriptionHtml);
    }

    [Fact]
    public async Task Bounce_marks_the_delivery_and_pauses_automatic_mail()
    {
        var from = Unique("gone");
        var first = await _m.ReceiveAsync(_mailbox, Mime.Message(from, Support, "Old laptop return", "Where do I send it?"));
        await _m.Host.ProcessOutboxAsync();
        var ack = Assert.Single(_m.Mail.To(from));

        var bounce = new MimeMessage { Subject = "Undelivered Mail Returned to Sender", MessageId = $"{Guid.NewGuid():N}@mx.test" };
        bounce.From.Add(new MailboxAddress("Mail Delivery System", "MAILER-DAEMON@mx.test"));
        bounce.To.Add(MailboxAddress.Parse(Support));
        var report = new MultipartReport("delivery-status");
        report.Add(new TextPart("plain") { Text = "Your message could not be delivered." });
        var status = new MessageDeliveryStatus();
        status.StatusGroups.Add(new HeaderList { { "Reporting-MTA", "dns; mx.test" } });
        status.StatusGroups.Add(new HeaderList
        {
            { "Final-Recipient", $"rfc822; {from}" },
            { "Action", "failed" },
            { "Status", "5.1.1" },
            { "Diagnostic-Code", "smtp; 550 5.1.1 User unknown" },
        });
        report.Add(status);
        var headers = new MimeMessage { MessageId = ack.MessageId!, Subject = ack.Subject ?? "" };
        report.Add(new MessagePart { Message = headers });
        bounce.Body = report;

        var result = await _m.ReceiveAsync(_mailbox, bounce);

        Assert.Equal(InboundOutcome.Bounced, result.Outcome);
        Assert.Equal(first.TicketId, result.TicketId);
        var delivery = await _m.DbAsync(db => db.Set<MsgDelivery>().AsNoTracking().SingleAsync(d => d.MessageId == ack.MessageId));
        Assert.Equal(DeliveryStatus.Bounced, delivery.Status);
        Assert.True(await _m.DbAsync(db => db.Set<MsgBouncedAddress>().AnyAsync(b => b.Address == from.ToLowerInvariant())));
    }

    [Fact]
    public void Webhook_signature_is_checked_with_a_replay_window()
    {
        var body = "From: a@b.test\r\n\r\nhello"u8.ToArray();
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var good = "sha256=" + Convert.ToHexStringLower(InboundWebhook.Sign("s3cret", ts, body));

        Assert.True(InboundWebhook.Verify("s3cret", ts, good, body, now));
        Assert.False(InboundWebhook.Verify("wrong", ts, good, body, now));
        Assert.False(InboundWebhook.Verify("s3cret", ts, good, [.. body, 0x20], now));
        Assert.False(InboundWebhook.Verify("s3cret", ts, good, body, now.AddMinutes(6)));
        Assert.False(InboundWebhook.Verify("s3cret", null, good, body, now));
        Assert.False(InboundWebhook.Verify("s3cret", ts, "sha256=nothex", body, now));
    }
}
