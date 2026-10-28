using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Notifications;
using Switchboard.Testing;

namespace Switchboard.Modules.Messaging.Tests;

/// <summary>Outbound ticket email, in-app notifications, dedupe and who sees what.</summary>
public sealed class NotificationTests : IAsyncLifetime
{
    private MessagingHost _m = default!;
    private User _agent = default!;
    private User _other = default!;
    private User _hardware = default!;
    private User _requester = default!;

    public async Task InitializeAsync()
    {
        _m = await MessagingHost.CreateAsync();
        await _m.CreateMailboxAsync("desk@support.test");
        _agent = await _m.Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _other = await _m.Host.CreateAgentAsync("Olive Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _hardware = await _m.Host.CreateAgentAsync("Hana Hardware", RoleKeys.Agent, TicketScope.Group, "Hardware");
        _requester = await _m.Host.CreateRequesterAsync("Dana Requester");
        await _m.Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var agent = await db.Users.FirstAsync(u => u.Id == _agent.Id);
            agent.Signature = "Sam Agent\nIT Service Desk";
            await db.SaveChangesAsync();
            return true;
        });
    }

    public async Task DisposeAsync() => await _m.DisposeAsync();

    private Task<Ticket> FileAsync(string subject) =>
        _m.Host.RunAsync(SwitchboardTestHost.As(_requester, Channel.Portal), sp =>
            sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand { Subject = subject, DescriptionHtml = "<p>Help</p>", AsRequester = true, SourceKey = TicketSourceKeys.Portal }));

    [Fact]
    public async Task Agent_reply_emails_to_cc_and_bcc_with_threading_and_signature()
    {
        var ticket = await FileAsync("Wi-Fi drops in room 4");
        await _m.Host.ProcessOutboxAsync();
        var ack = Assert.Single(_m.Mail.To(_requester.Email!), m => (m.Subject ?? "").Contains("Wi-Fi drops"));

        var reply = await _m.Host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id,
            new ReplyCommand { BodyHtml = "<p>Try forgetting the network and joining again.</p>", Cc = ["boss@customer.test"], Bcc = ["audit@customer.test"] }));
        await _m.Host.ProcessOutboxAsync();

        var sent = _m.Mail.Sent.Single(s => s.Message.HtmlBody?.Contains("forgetting the network") == true);
        Assert.Contains(_requester.Email!.ToLowerInvariant(), sent.Recipients);
        Assert.Contains("boss@customer.test", sent.Recipients);
        Assert.Contains("audit@customer.test", sent.Recipients);
        Assert.Contains($"[{ticket.DisplayId}]", sent.Message.Subject);
        Assert.Equal(ack.MessageId, sent.Message.InReplyTo);
        Assert.Contains(ack.MessageId, sent.Message.References);
        Assert.Contains("IT Service Desk", sent.Message.HtmlBody);
        Assert.Null(sent.Message.Headers["Auto-Submitted"]);

        var conversation = await _m.DbAsync(db => db.TicketConversations.AsNoTracking().FirstAsync(c => c.Id == reply.Id));
        Assert.Equal(sent.Message.MessageId, conversation.EmailMessageId);
        var mapped = await _m.DbAsync(db => db.Set<MsgMessageMap>().AsNoTracking().FirstAsync(x => x.MessageId == sent.Message.MessageId));
        Assert.Equal(reply.Id, mapped.ConversationId);

        var log = await _m.DbAsync(db => db.Set<MsgDelivery>().AsNoTracking().Where(d => d.TicketId == ticket.Id && d.TemplateKey == NotificationCatalog.RequesterAgentReply).ToListAsync());
        Assert.Equal(3, log.Count);
        Assert.All(log, d => Assert.Equal(DeliveryStatus.Sent, d.Status));
    }

    [Fact]
    public async Task Private_notes_never_reach_the_requester_and_public_notes_do()
    {
        var ticket = await FileAsync("Badge doesn't open the door");
        await _m.Host.ProcessOutboxAsync();
        var before = _m.Mail.To(_requester.Email!).Count;

        await _m.Host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().AddNoteAsync(ticket.Id,
            new NoteCommand { BodyHtml = "<p>Internal: the reader on door 2 is broken.</p>", IsPrivate = true }));
        await _m.Host.ProcessOutboxAsync();
        Assert.Equal(before, _m.Mail.To(_requester.Email!).Count);

        await _m.Host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().AddNoteAsync(ticket.Id,
            new NoteCommand { BodyHtml = "<p>Facilities are replacing the reader today.</p>", IsPrivate = false }));
        await _m.Host.ProcessOutboxAsync();
        var mail = _m.Mail.To(_requester.Email!);
        Assert.Equal(before + 1, mail.Count);
        Assert.DoesNotContain(mail, m => m.HtmlBody?.Contains("Internal:") == true);
        Assert.Contains(mail, m => m.HtmlBody?.Contains("replacing the reader") == true);
    }

    [Fact]
    public async Task Resolving_emails_the_requester_once()
    {
        var ticket = await FileAsync("Teams camera is black");
        var resolved = await _m.DbAsync(db => db.TicketStatuses.AsNoTracking().FirstAsync(s => s.WorkspaceId == ticket.WorkspaceId && s.Category == StatusCategory.Resolved));
        await _m.Host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { StatusId = resolved.Id }));
        await _m.Host.ProcessOutboxAsync();

        Assert.Single(_m.Mail.To(_requester.Email!), m => (m.Subject ?? "").StartsWith("Resolved:", StringComparison.Ordinal) && (m.Subject ?? "").Contains(ticket.DisplayId));
    }

    [Fact]
    public async Task Handler_retries_send_each_recipient_at_most_once()
    {
        var ticket = await FileAsync("Retry me");
        await _m.Host.ProcessOutboxAsync();
        var reply = await _m.Host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id,
            new ReplyCommand { BodyHtml = "<p>Retry body</p>", Cc = ["second@customer.test"] }));

        var e = new TicketConversationAdded(ticket.Id, ticket.WorkspaceId, reply.Id, ConversationKind.AgentReply, false, true)
        {
            EventId = Guid.CreateVersion7(),
            Actor = SwitchboardTestHost.As(_agent).Snapshot(),
        };
        Task Run() => _m.Host.RunAsSystemAsync(async sp =>
        {
            await ActivatorUtilities.CreateInstance<RequesterEmailHandler>(sp).HandleAsync(e, default);
            return true;
        });

        // First attempt: the server refuses; the handler throws so the outbox retries later.
        _m.Mail.FailOnce[_requester.Email!] = true;
        await Assert.ThrowsAsync<InvalidOperationException>(Run);
        Assert.DoesNotContain(_m.Mail.Sent, s => s.Message.HtmlBody?.Contains("Retry body") == true);

        // Retry delivers; a further duplicate delivery of the same event sends nothing more.
        await Run();
        await Run();
        var sends = _m.Mail.Sent.Where(s => s.Message.HtmlBody?.Contains("Retry body") == true).ToList();
        // The outbox itself also delivered the real event once (a different event id), so count by event.
        var rows = await _m.DbAsync(db => db.Set<MsgDelivery>().AsNoTracking().Where(d => d.EventId == e.EventId).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(DeliveryStatus.Sent, r.Status));
        Assert.All(rows, r => Assert.Equal(2, r.Attempts));
        Assert.Single(sends);
    }

    [Fact]
    public async Task Interrupted_sends_are_not_repeated()
    {
        var ticket = await FileAsync("Interrupted");
        var eventId = Guid.CreateVersion7();
        await _m.Host.RunAsSystemAsync(async sp =>
        {
            var log = sp.GetRequiredService<DeliveryLog>();
            var claimed = await log.ClaimAsync(eventId, [new DeliveryClaim("x@customer.test", null)], DeliveryChannel.Email, "test", ticket.Id, "s", false, "id@x", default);
            Assert.Single(claimed);
            // Crash before the result was recorded: the row stays Pending. The retry must not claim it again.
            var again = await log.ClaimAsync(eventId, [new DeliveryClaim("X@customer.test", null)], DeliveryChannel.Email, "test", ticket.Id, "s", false, "id@x", default);
            Assert.Empty(again);
            // Later retries of the same event (e.g. another recipient failed) still never claim it.
            var later = await log.ClaimAsync(eventId, [new DeliveryClaim("x@customer.test", null)], DeliveryChannel.Email, "test", ticket.Id, "s", false, "id@x", default);
            Assert.Empty(later);
            return true;
        });
        var row = await _m.DbAsync(db => db.Set<MsgDelivery>().AsNoTracking().SingleAsync(d => d.EventId == eventId));
        Assert.Equal(DeliveryStatus.Unconfirmed, row.Status);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task Assignment_notifies_the_agent_in_app_and_live_once()
    {
        var ticket = await FileAsync("Assign me");
        await _m.Host.RunAsync(SwitchboardTestHost.As(_other), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { AgentId = _agent.Id }));
        await _m.Host.ProcessOutboxAsync();
        await _m.Host.ProcessOutboxAsync();

        var mine = await _m.DbAsync(db => db.Set<MsgNotification>().AsNoTracking().Where(n => n.UserId == _agent.Id && n.TicketId == ticket.Id).ToListAsync());
        var notification = Assert.Single(mine);
        Assert.Equal(NotificationCatalog.AgentAssigned, notification.Type);
        Assert.Equal("Assigned to you by Olive Agent", notification.Title);
        Assert.Equal(ticket.DisplayId, notification.Reference);
        Assert.Contains(_m.Live.ToUsers, x => x.UserId == _agent.Id && x.Type == InAppNotifier.LiveType);
        Assert.Contains(_m.Mail.To(_agent.Email!), m => (m.Subject ?? "").StartsWith("Assigned to you: Assign me [", StringComparison.Ordinal));
        // The person who assigned it hears nothing about their own action.
        Assert.False(await _m.DbAsync(db => db.Set<MsgNotification>().AnyAsync(n => n.UserId == _other.Id && n.TicketId == ticket.Id)));
    }

    [Fact]
    public async Task Preferences_turn_channels_off()
    {
        await _m.Host.RunAsSystemAsync(async sp =>
        {
            await sp.GetRequiredService<PreferenceService>().SaveAsync(_other.Id, new Dictionary<string, ChannelPreference>
            {
                [NotificationCatalog.AgentAssigned] = new(Email: false, InApp: false),
            });
            return true;
        });
        var ticket = await FileAsync("Quiet please");
        await _m.Host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { AgentId = _other.Id }));
        await _m.Host.ProcessOutboxAsync();

        Assert.False(await _m.DbAsync(db => db.Set<MsgNotification>().AnyAsync(n => n.UserId == _other.Id && n.TicketId == ticket.Id)));
        Assert.DoesNotContain(_m.Mail.To(_other.Email!), m => (m.Subject ?? "").Contains("Quiet please"));
    }

    [Fact]
    public async Task Agents_only_hear_about_tickets_they_can_see()
    {
        var ticket = await FileAsync("Secret project laptop");
        // Hana is a group-scoped agent in Hardware: a Service Desk ticket mentioning her stays invisible to her.
        await _m.Host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().AddNoteAsync(ticket.Id,
            new NoteCommand { BodyHtml = "<p>Hana, any spare stock?</p>", NotifyUserIds = [_hardware.Id, _other.Id] }));
        await _m.Host.ProcessOutboxAsync();

        Assert.False(await _m.DbAsync(db => db.Set<MsgNotification>().AnyAsync(n => n.UserId == _hardware.Id && n.TicketId == ticket.Id)));
        Assert.True(await _m.DbAsync(db => db.Set<MsgNotification>().AnyAsync(n => n.UserId == _other.Id && n.TicketId == ticket.Id && n.Type == NotificationCatalog.AgentMentioned)));
    }

    [Fact]
    public async Task Users_only_see_and_change_their_own_notifications()
    {
        var ticket = await FileAsync("Visibility");
        Guid samsId = default;
        await _m.Host.RunAsSystemAsync(async sp =>
        {
            var inApp = sp.GetRequiredService<InAppNotifier>();
            await inApp.NotifyAsync(Guid.CreateVersion7(), new NotificationDraft(_agent.Id, NotificationCatalog.AgentAssigned, "For Sam", null, "/tickets/x", ticket.DisplayId, ticket.Id), default);
            await inApp.NotifyAsync(Guid.CreateVersion7(), new NotificationDraft(_other.Id, NotificationCatalog.AgentAssigned, "For Olive", null, "/tickets/y", ticket.DisplayId, ticket.Id), default);
            samsId = await sp.GetRequiredService<SwitchboardDbContext>().Set<MsgNotification>().Where(n => n.UserId == _agent.Id && n.Title == "For Sam").Select(n => n.Id).FirstAsync();
            return true;
        });

        var olives = await _m.Host.RunAsync(SwitchboardTestHost.As(_other), sp => sp.GetRequiredService<NotificationInbox>().ListAsync(false, 0, 100));
        Assert.Contains(olives, n => n.Title == "For Olive");
        Assert.DoesNotContain(olives, n => n.Title == "For Sam");

        // Someone else's notification behaves as if it doesn't exist.
        await Assert.ThrowsAsync<NotFoundException>(() => _m.Host.RunAsync(SwitchboardTestHost.As(_other), sp => sp.GetRequiredService<NotificationInbox>().OpenAsync(samsId)));
        await Assert.ThrowsAsync<NotFoundException>(() => _m.Host.RunAsync(SwitchboardTestHost.As(_other), sp => sp.GetRequiredService<NotificationInbox>().SetReadAsync(samsId, true)));

        await _m.Host.RunAsync(SwitchboardTestHost.As(_other), sp => sp.GetRequiredService<NotificationInbox>().MarkAllReadAsync());
        var sams = await _m.DbAsync(db => db.Set<MsgNotification>().AsNoTracking().FirstAsync(n => n.Id == samsId));
        Assert.Null(sams.ReadAt);

        // Anonymous and system callers have no inbox.
        await Assert.ThrowsAsync<ForbiddenException>(() => _m.Host.RunAsSystemAsync(sp => sp.GetRequiredService<NotificationInbox>().UnreadCountAsync()));
    }

    [Fact]
    public async Task Templates_need_the_manage_privilege_and_are_rendered_safely()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => _m.Host.RunAsync(SwitchboardTestHost.As(_agent), async sp =>
        {
            await sp.GetRequiredService<TemplateService>().SaveAsync(_m.Host.Workspace.Id, NotificationCatalog.RequesterAcknowledgement, "Hi", "<p>Hi</p>", true);
            return true;
        }));

        await _m.Host.RunAsSystemAsync(async sp =>
        {
            await sp.GetRequiredService<TemplateService>().SaveAsync(_m.Host.Workspace.Id, NotificationCatalog.RequesterAcknowledgement,
                "Got it: {{ticket.subject}}", "<p>Hello {{requester.name}}, we have {{ticket.subject}}.</p><script>alert(1)</script>", true);
            return true;
        });
        var ticket = await FileAsync("<img src=x onerror=alert(1)> broken");
        await _m.Host.ProcessOutboxAsync();
        var ack = Assert.Single(_m.Mail.To(_requester.Email!), m => (m.Subject ?? "").StartsWith("Got it:", StringComparison.Ordinal));
        Assert.Equal($"Got it: <img src=x onerror=alert(1)> broken [{ticket.DisplayId}]", ack.Subject);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt; broken", ack.HtmlBody);
        Assert.DoesNotContain("<script", ack.HtmlBody);
        Assert.DoesNotContain("<img src=x", ack.HtmlBody);
    }

    [Fact]
    public async Task Disabled_templates_send_nothing()
    {
        await _m.Host.RunAsSystemAsync(async sp =>
        {
            await sp.GetRequiredService<TemplateService>().SetEnabledAsync(_m.Host.Workspace.Id, NotificationCatalog.RequesterClosed, false);
            return true;
        });
        var ticket = await FileAsync("Close quietly");
        var closed = await _m.DbAsync(db => db.TicketStatuses.AsNoTracking().FirstAsync(s => s.WorkspaceId == ticket.WorkspaceId && s.Category == StatusCategory.Closed));
        await _m.Host.RunAsync(SwitchboardTestHost.As(_agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { StatusId = closed.Id }));
        await _m.Host.ProcessOutboxAsync();

        Assert.DoesNotContain(_m.Mail.To(_requester.Email!), m => (m.Subject ?? "").StartsWith("Closed:", StringComparison.Ordinal) && (m.Subject ?? "").Contains(ticket.DisplayId));
    }
}
