using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MimeKit;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.Modules.Messaging;
using Switchboard.Modules.Messaging.Admin;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Inbound;
using Switchboard.Modules.Messaging.Outbound;
using Switchboard.Testing;

namespace Switchboard.Modules.Messaging.Tests;

/// <summary>Captures what would have gone to the SMTP server.</summary>
public sealed class FakeTransport : IMailTransport
{
    public ConcurrentQueue<(MimeMessage Message, IReadOnlyList<string> Recipients)> Sent { get; } = new();

    /// <summary>Addresses that make the next send to them throw, once each.</summary>
    public ConcurrentDictionary<string, bool> FailOnce { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task SendAsync(MimeMessage message, MailboxAddress sender, IReadOnlyList<MailboxAddress> recipients, EffectiveMailSettings settings, CancellationToken ct)
    {
        foreach (var r in recipients)
        {
            if (FailOnce.TryRemove(r.Address, out _))
            {
                throw new InvalidOperationException($"Mailbox unavailable: {r.Address}");
            }
        }
        Sent.Enqueue((message, recipients.Select(r => r.Address.ToLowerInvariant()).ToList()));
        return Task.CompletedTask;
    }

    public IReadOnlyList<MimeMessage> To(string address) =>
        Sent.Where(s => s.Recipients.Contains(address.ToLowerInvariant())).Select(s => s.Message).ToList();
}

public sealed class CapturingLiveNotifier : ILiveNotifier
{
    public ConcurrentQueue<(Guid UserId, string Type)> ToUsers { get; } = new();

    public Task ToUserAsync(Guid userId, string type, object payload, CancellationToken ct = default)
    {
        ToUsers.Enqueue((userId, type));
        return Task.CompletedTask;
    }

    public Task ToTicketAsync(Guid ticketId, string type, object payload, CancellationToken ct = default) => Task.CompletedTask;

    public Task ToWorkspaceAgentsAsync(Guid workspaceId, string type, object payload, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Core plus Messaging on a throwaway database, with mail captured instead of sent.</summary>
public sealed class MessagingHost : IAsyncDisposable
{
    private MessagingHost(SwitchboardTestHost host, FakeTransport mail, CapturingLiveNotifier live)
    {
        Host = host;
        Mail = mail;
        Live = live;
    }

    public SwitchboardTestHost Host { get; }
    public FakeTransport Mail { get; }
    public CapturingLiveNotifier Live { get; }

    public static async Task<MessagingHost> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var mail = new FakeTransport();
        var live = new CapturingLiveNotifier();
        var host = await SwitchboardTestHost.CreateAsync([new MessagingModule()], services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IMailTransport>(mail));
            services.Replace(ServiceDescriptor.Singleton<ILiveNotifier>(live));
            // Development default: "sends" through the fake transport as if to Mailpit.
            services.Replace(ServiceDescriptor.Singleton(new MessagingEnvironment(IsDevelopment: true)));
            services.Replace(ServiceDescriptor.Singleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()));
            configure?.Invoke(services);
        });
        return new MessagingHost(host, mail, live);
    }

    public Task<MsgMailbox> CreateMailboxAsync(string address, string? defaultGroup = "Service Desk", bool trustUnauthenticated = false) =>
        Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            Guid? groupId = defaultGroup is null ? null : await db.AgentGroups.Where(g => g.Name == defaultGroup).Select(g => (Guid?)g.Id).FirstAsync();
            var saved = await sp.GetRequiredService<MailboxService>().SaveAsync(null, new MailboxInput
            {
                Name = "IT support",
                Address = address,
                WorkspaceId = Host.Workspace.Id,
                DefaultGroupId = groupId,
                Connection = MailboxConnection.Webhook,
                TrustUnauthenticated = trustUnauthenticated,
            });
            return saved.Mailbox;
        });

    public Task<MsgInboundMessage> ReceiveAsync(MsgMailbox mailbox, MimeMessage message) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<InboundPipeline>().ProcessAsync(new InboundRequest(mailbox, Mime.Bytes(message), InboundSource.Simulated)));

    public Task<T> DbAsync<T>(Func<SwitchboardDbContext, Task<T>> query) =>
        Host.RunAsSystemAsync(sp => query(sp.GetRequiredService<SwitchboardDbContext>()));

    public Task<Ticket> TicketAsync(Guid id) =>
        DbAsync(db => db.Tickets.AsNoTracking().Include(t => t.Status).Include(t => t.Tags).FirstAsync(t => t.Id == id));

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

/// <summary>Builds test messages.</summary>
public static class Mime
{
    public static MimeMessage Message(string from, string to, string subject, string text, string? fromName = null, string? html = null)
    {
        var message = new MimeMessage
        {
            MessageId = $"{Guid.NewGuid():N}@{from[(from.IndexOf('@') + 1)..]}",
            Subject = subject,
            Date = DateTimeOffset.UtcNow,
        };
        message.From.Add(new MailboxAddress(fromName ?? "", from));
        message.To.Add(MailboxAddress.Parse(to));
        if (html is null)
        {
            message.Body = new TextPart("plain") { Text = text };
        }
        else
        {
            message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();
        }
        return message;
    }

    public static MimeMessage WithAuth(this MimeMessage message, string verdict, string authServId = "mx.test.local")
    {
        var from = message.From.Mailboxes.First().Address;
        var domain = from[(from.IndexOf('@') + 1)..];
        message.Headers.Add("Authentication-Results", $"{authServId}; spf={verdict} smtp.mailfrom={from}; dkim={verdict} header.d={domain}; dmarc={verdict} header.from={domain}");
        return message;
    }

    public static MimeMessage InReplyTo(this MimeMessage message, string messageId)
    {
        message.InReplyTo = messageId;
        message.References.Add(messageId);
        return message;
    }

    public static byte[] Bytes(MimeMessage message)
    {
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        return buffer.ToArray();
    }
}
