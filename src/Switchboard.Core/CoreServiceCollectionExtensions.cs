using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;

namespace Switchboard.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the database, security, events and domain services shared by every module.</summary>
    public static IServiceCollection AddSwitchboardCore(this IServiceCollection services, IConfiguration configuration, ModuleCatalog modules)
    {
        services.Configure<SwitchboardOptions>(configuration.GetSection(SwitchboardOptions.Section));
        services.Configure<StorageOptions>(configuration.GetSection($"{SwitchboardOptions.Section}:Storage"));

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddSingleton(modules);
        services.AddSingleton<IActorAccessor, ActorAccessor>();

        var connectionString = configuration.GetConnectionString("Default") is { Length: > 0 } cs ? cs : null;
        _ = connectionString ?? throw new InvalidOperationException("Set the ConnectionStrings:Default connection string (or the ConnectionStrings__Default environment variable).");
        services.AddDbContext<SwitchboardDbContext>(options =>
            options
                .UseSqlServer(connectionString, sql => sql
                    .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)
                    // Migrations live in the host, which references every module.
                    .MigrationsAssembly(configuration["Switchboard:MigrationsAssembly"] ?? "Switchboard.Web"))
                // A host built with a subset of modules has a smaller model than the migrations; log, don't fail.
                .ConfigureWarnings(w => w.Log(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<IOutboxSignal>(sp => sp.GetRequiredService<OutboxSignal>());
        services.AddHostedService<OutboxProcessor>();
        services.AddSingleton<AuditChainSealer>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditChainSealer>());
        services.AddHostedService<TicketMaintenanceJob>();

        services.AddSingleton<IHtmlContent, HtmlContent>();
        services.AddSingleton<IFileStore, LocalFileStore>();
        services.TryAddSingleton<ILiveNotifier, NullLiveNotifier>();
        services.TryAddScoped<ISystemEmailSender, LoggingEmailSender>();
        services.AddSwitchboardAuthorization();

        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IDisplayIdGenerator, DisplayIdGenerator>();
        services.AddScoped<ICustomFieldService, CustomFieldService>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<ITicketService, TicketService>();
        services.AddScoped<TicketQueryCompiler>();
        services.AddScoped<ITicketSearch, TicketSearch>();
        services.AddScoped<ITicketTransitionGuard, CoreTicketTransitionGuard>();
        services.AddScoped<IAttachmentAccessPolicy>(sp => new TicketAttachmentPolicy(
            sp.GetRequiredService<SwitchboardDbContext>(), sp.GetRequiredService<IPermissionService>(), AttachmentOwners.Ticket));
        services.AddScoped<IAttachmentAccessPolicy>(sp => new TicketAttachmentPolicy(
            sp.GetRequiredService<SwitchboardDbContext>(), sp.GetRequiredService<IPermissionService>(), AttachmentOwners.Conversation));

        services.AddPrivileges([.. Privileges.CoreDefinitions]);

        // Default REST API limit (FR-INT-1): per authenticated principal, else per client IP.
        var perMinute = configuration.GetValue("Switchboard:Api:RequestsPerMinute", 500);
        services.AddRateLimiter(o => o.AddPolicy(Api.SwitchboardApi.RateLimitPolicy, http =>
            System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(
                http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
                {
                    TokenLimit = perMinute,
                    TokensPerPeriod = perMinute,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })));
        return services;
    }

    /// <summary>Register an after-commit handler (runs via the outbox, at least once).</summary>
    public static IServiceCollection AddEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : DomainEvent
        where THandler : class, IEventHandler<TEvent>
        => services.AddScoped<IEventHandler<TEvent>, THandler>();

    /// <summary>Register a pre-commit handler (runs inside SaveChanges, same transaction).</summary>
    public static IServiceCollection AddPreCommitHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : DomainEvent
        where THandler : class, IPreCommitHandler<TEvent>
        => services.AddScoped<IPreCommitHandler<TEvent>, THandler>();
}
