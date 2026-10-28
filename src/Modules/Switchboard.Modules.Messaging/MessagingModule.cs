using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Events;
using Switchboard.Core.Modules;
using Switchboard.Core.Services;
using Switchboard.Modules.Messaging.Admin;
using Switchboard.Modules.Messaging.Domain;
using Switchboard.Modules.Messaging.Inbound;
using Switchboard.Modules.Messaging.Notifications;
using Switchboard.Modules.Messaging.Outbound;
using Switchboard.Modules.Messaging.ViewComponents;

namespace Switchboard.Modules.Messaging;

/// <summary>
/// Email in and out, notification templates, in-app notifications and the delivery log
/// (FR-CH-1..4, FR-NOT-1..6, SR-EML-1..4).
/// </summary>
public sealed class MessagingModule : ISwitchboardModule
{
    public string Name => "Messaging";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDataProtection();
        services.TryAddSingleton(sp => new MessagingEnvironment(sp.GetService<IHostEnvironment>()?.IsDevelopment() ?? false));
        services.AddSingleton<MessagingSecrets>();
        services.TryAddSingleton<IMailTransport, SmtpMailTransport>();

        // Outbound
        services.AddScoped<OutboundSettingsStore>();
        services.AddScoped<MailDispatcher>();
        services.Replace(ServiceDescriptor.Scoped<ISystemEmailSender, SmtpSystemEmailSender>());

        // Notifications
        services.AddScoped<DeliveryLog>();
        services.AddScoped<TemplateService>();
        services.AddScoped<TicketFactsLoader>();
        services.AddScoped<TicketMailer>();
        services.AddScoped<InAppNotifier>();
        services.AddScoped<NotificationInbox>();
        services.AddScoped<PreferenceService>();
        services.AddScoped<AgentNotifier>();
        services.AddEventHandler<TicketCreated, RequesterEmailHandler>();
        services.AddEventHandler<TicketConversationAdded, RequesterEmailHandler>();
        services.AddEventHandler<TicketStatusChanged, RequesterEmailHandler>();
        services.AddEventHandler<TicketAssigned, AgentNotificationHandler>();
        services.AddEventHandler<TicketConversationAdded, AgentNotificationHandler>();
        services.AddEventHandler<AgentsMentioned, AgentNotificationHandler>();

        // Inbound
        services.AddScoped<InboundMailContext>();
        services.AddPreCommitHandler<TicketCreated, InboundLinker>();
        services.AddPreCommitHandler<TicketConversationAdded, InboundLinker>();
        services.AddScoped<AddressRules>();
        services.AddScoped<ThreadMatcher>();
        services.AddScoped<EmailCommands>();
        services.AddScoped<BounceProcessor>();
        services.AddScoped<InboundPipeline>();
        services.AddHostedService<ImapPoller>();

        // Admin
        services.AddScoped<MessagingAccess>();
        services.AddScoped<MailboxService>();
        services.AddScoped<OutboundSettingsService>();
        services.AddScoped<SenderRuleService>();
        services.AddScoped<InboundAdminService>();

        services.AddPrivileges(MessagingPrivileges.Definition);
        services.AddNavItem(new NavItem(NavArea.Admin, "email", "Email", "bi-envelope", "/admin/email", Order: 10,
            Privilege: MessagingPrivileges.Manage, Group: "Channels", Description: "Support mailboxes, outgoing mail and sender rules."));
        services.AddNavItem(new NavItem(NavArea.Admin, "notifications", "Notifications", "bi-bell", "/admin/notifications", Order: 20,
            Privilege: MessagingPrivileges.Manage, Group: "Channels", Description: "Which notifications go out, and what they say."));

        services.AddSlot<NotificationBellViewComponent>("topbar.end", order: 50);
        services.AddSlot<NotificationPreferencesViewComponent>("profile.sections", order: 50);
        services.AddSlot<TicketNotificationsViewComponent>("ticket.tabs", order: 80);
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessagingModule).Assembly);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => InboundWebhook.Map(endpoints);

    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<SwitchboardDbContext>();
        var time = services.GetRequiredService<TimeProvider>();
        var workspaces = await db.Workspaces.AsNoTracking().Select(w => w.Id).ToListAsync(cancellationToken);
        var rows = await db.Set<MsgTemplate>().ToListAsync(cancellationToken);
        foreach (var workspaceId in workspaces)
        {
            foreach (var definition in NotificationCatalog.All)
            {
                var row = rows.FirstOrDefault(r => r.WorkspaceId == workspaceId && r.EventKey == definition.Key);
                if (row is null)
                {
                    db.Add(new MsgTemplate
                    {
                        WorkspaceId = workspaceId,
                        EventKey = definition.Key,
                        Subject = definition.DefaultSubject,
                        BodyHtml = definition.DefaultBody,
                        IsEnabled = definition.EnabledByDefault,
                        UpdatedAt = time.GetUtcNow(),
                    });
                }
                else if (row.UpdatedById is null && (row.Subject != definition.DefaultSubject || row.BodyHtml != definition.DefaultBody))
                {
                    // Nobody edited it: keep it in step with the built-in wording.
                    row.Subject = definition.DefaultSubject;
                    row.BodyHtml = definition.DefaultBody;
                }
            }
        }

        var options = services.GetRequiredService<IOptions<SwitchboardOptions>>().Value;
        if (options.DemoData && !await db.Set<MsgMailbox>().AnyAsync(cancellationToken))
        {
            var primary = await db.Workspaces.AsNoTracking().FirstAsync(w => w.IsPrimary, cancellationToken);
            var serviceDesk = await db.AgentGroups.AsNoTracking().Where(g => g.WorkspaceId == primary.Id && g.Name == "Service Desk").Select(g => (Guid?)g.Id).FirstOrDefaultAsync(cancellationToken);
            db.Add(new MsgMailbox
            {
                Name = "IT support",
                Address = "support@switchboard.local",
                WorkspaceId = primary.Id,
                DefaultGroupId = serviceDesk,
                Connection = MailboxConnection.Webhook,
                WebhookSecretProtected = services.GetRequiredService<MessagingSecrets>().Protect(MessagingSecrets.NewSecret()),
                CreatedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
