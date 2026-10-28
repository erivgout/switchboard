using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Modules;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Automation.Assignment;
using Switchboard.Modules.Automation.Closure;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Engine;
using Switchboard.Modules.Automation.Net;
using Switchboard.Modules.Automation.Scenarios;
using Switchboard.Modules.Automation.Scheduling;
using Switchboard.Modules.Automation.Web;
using Switchboard.Modules.Automation.Workflows;

namespace Switchboard.Modules.Automation;

public sealed class AutomationModule : ISwitchboardModule
{
    public const string ManagePrivilege = AlertService.ManagePrivilege;

    /// <summary>Events from other modules that can start workflows, matched by type name (no compile-time dependency).</summary>
    private static readonly (string TypeName, string Key, string Label, string Hint)[] ExternalEvents =
    [
        ("SlaBreached", TriggerKeys.SlaBreached, "A ticket breaches its SLA", "Runs when a response or resolution target is missed."),
        ("SlaEscalated", TriggerKeys.SlaEscalated, "An SLA escalation fires", "Runs when the service-levels module escalates a ticket."),
    ];

    public string Name => "Automation";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AutomationOptions>().Bind(configuration.GetSection(AutomationOptions.Section));
        services.AddDataProtection();

        services.AddPrivileges(new PrivilegeDefinition(ManagePrivilege, "Manage automations", "Automation",
            "Create and publish workflows, closure rules, scheduled tickets and shared scenarios; pause all automations."));
        services.AddNavItem(new NavItem(NavArea.Admin, "automations", "Automations", "bi-diagram-3", "/admin/automations",
            Order: 10, Privilege: ManagePrivilege, Group: "Automation", Description: "Workflows, closure rules and scheduled tickets."));
        services.AddSlot<TicketScenariosViewComponent>("ticket.actions", order: 50);
        services.AddSlot<AvailabilityViewComponent>("usermenu.items", order: 10);

        // Outbound requests (SR-NET-1).
        services.TryAddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<OutboundNetworkPolicy>();
        services.AddSingleton(sp => new SafeHttpClient(
            sp.GetRequiredService<IHostResolver>(), sp.GetRequiredService<OutboundNetworkPolicy>(), sp.GetRequiredService<IOptions<AutomationOptions>>()));

        // Engine.
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<WorkflowExecutor>();
        services.AddSingleton<ExternalStepSender>();
        services.AddSingleton<WorkflowEngine>();
        services.AddSingleton<AutoAssigner>();
        services.AddSingleton<AutomationDispatcher>();
        services.AddSingleton<ScheduleRunner>();
        services.AddSingleton<TimeRuleRunner>();
        services.AddSingleton<AutomationClockSignal>();
        services.AddSingleton<AutomationTriggerRegistry>();
        services.AddHostedService<AutomationClock>();

        services.AddScoped<AutomationSettings>();
        services.AddScoped<CatalogLoader>();
        services.AddScoped<TicketFactsLoader>();
        services.AddScoped<ActionRunner>();
        services.AddScoped<AssignmentService>();
        services.AddScoped<WorkflowTestRunner>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<ScenarioService>();
        services.AddScoped<ClosureRuleService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<ITicketTransitionGuard, ClosureRuleGuard>();

        services.AddPreCommitHandler<TicketCreated, LineageRecorder>();
        services.AddPreCommitHandler<TicketUpdated, LineageRecorder>();
        services.AddPreCommitHandler<TicketStatusChanged, LineageRecorder>();
        services.AddPreCommitHandler<TicketAssigned, LineageRecorder>();
        services.AddPreCommitHandler<TicketConversationAdded, LineageRecorder>();
        services.AddPreCommitHandler<TicketCreated, StatusClockRecorder>();
        services.AddPreCommitHandler<TicketStatusChanged, StatusClockRecorder>();

        services.AddEventHandler<TicketCreated, AutomationEventHandler>();
        services.AddEventHandler<TicketUpdated, AutomationEventHandler>();
        services.AddEventHandler<TicketStatusChanged, AutomationEventHandler>();
        services.AddEventHandler<TicketAssigned, AutomationEventHandler>();
        services.AddEventHandler<TicketConversationAdded, AutomationEventHandler>();

        foreach (var (typeName, key, label, hint) in ExternalEvents)
        {
            if (FindTicketEvent(typeName) is { } type)
            {
                services.AddSingleton(new ExternalTrigger(key, label, hint, type));
                services.AddScoped(typeof(IEventHandler<>).MakeGenericType(type), typeof(ExternalTicketEventHandler<>).MakeGenericType(type));
            }
        }
    }

    public void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AutomationModule).Assembly);

    public Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        AutomationSeeder.SeedAsync(services, cancellationToken);

    /// <summary>A loaded <see cref="DomainEvent"/> with this name and a <c>TicketId</c> property, from any module.</summary>
    private static Type? FindTicketEvent(string typeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name?.StartsWith("Switchboard.Modules.", StringComparison.Ordinal) == true))
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.OfType<Type>().ToArray();
            }
            var match = types.FirstOrDefault(t => t.Name == typeName && !t.IsAbstract && typeof(DomainEvent).IsAssignableFrom(t)
                && t.GetProperty("TicketId")?.PropertyType == typeof(Guid));
            if (match is not null)
            {
                return match;
            }
        }
        return null;
    }
}
