using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core;
using Switchboard.Core.Events;
using Switchboard.Core.Modules;
using Switchboard.Modules.ServiceLevels.Engine;
using Switchboard.Modules.ServiceLevels.Events;
using Switchboard.Modules.ServiceLevels.Monitor;
using Switchboard.Modules.ServiceLevels.Services;

namespace Switchboard.Modules.ServiceLevels;

/// <summary>Business calendars, SLA policies, due dates, and breach monitoring (SRS §3.11).</summary>
public sealed class ServiceLevelsModule : ISwitchboardModule
{
    public string Name => "ServiceLevels";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPrivileges(new PrivilegeDefinition(SlaAdminService.Privilege, "Manage SLA policies and business calendars", "Service levels"));
        services.AddNavItem(new NavItem(NavArea.Admin, "sla-policies", "SLA policies", "bi-stopwatch", "/admin/service-levels/policies",
            Order: 10, Privilege: SlaAdminService.Privilege, Group: "Service levels"));
        services.AddNavItem(new NavItem(NavArea.Admin, "sla-calendars", "Business calendars", "bi-calendar-week", "/admin/service-levels/calendars",
            Order: 11, Privilege: SlaAdminService.Privilege, Group: "Service levels"));

        services.AddScoped<SlaEngine>();
        services.AddScoped<SlaAdminService>();
        services.AddPreCommitHandler<Core.Events.TicketCreated, SlaPreCommitHandlers>();
        services.AddPreCommitHandler<TicketUpdated, SlaPreCommitHandlers>();
        services.AddPreCommitHandler<TicketStatusChanged, SlaPreCommitHandlers>();
        services.AddPreCommitHandler<TicketDeleted, SlaPreCommitHandlers>();
        services.AddPreCommitHandler<TicketRestored, SlaPreCommitHandlers>();
        services.AddPreCommitHandler<TicketConversationAdded, SlaPreCommitHandlers>();

        services.AddSingleton<SlaMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<SlaMonitor>());
        services.AddEventHandler<SlaReminderDue, SlaEmailHandler>();
        services.AddEventHandler<SlaBreached, SlaEmailHandler>();
        services.AddEventHandler<SlaEscalated, SlaEmailHandler>();

        Components.SlaSlots.Register(services);
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServiceLevelsModule).Assembly);

    public Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        SlaSeeder.SeedAsync(services, cancellationToken);
}
