using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Modules;
using Switchboard.Core.Security;

namespace Switchboard.Testing;

/// <summary>
/// Integration test host: Core plus the given modules against a throwaway SQL Server database.
/// Hosted services do not run; call <see cref="ProcessOutboxAsync"/> to deliver after-commit events.
/// </summary>
/// <remarks>
/// Uses the dev container (<c>docker compose -f docker-compose.dev.yml up -d</c>) unless
/// <c>SWITCHBOARD_TEST_SQL</c> points elsewhere. Each host creates and drops its own database.
/// </remarks>
public sealed class SwitchboardTestHost : IAsyncDisposable
{
    public const string DefaultServer = "Server=localhost,14333;User Id=sa;Password=Switchboard-Dev-2026!;TrustServerCertificate=True;Encrypt=True";

    private SwitchboardTestHost(ServiceProvider services, string database)
    {
        Services = services;
        Database = database;
    }

    public ServiceProvider Services { get; }
    public string Database { get; }

    /// <summary>The primary IT workspace seeded by Core.</summary>
    public Workspace Workspace { get; private set; } = default!;

    public static async Task<SwitchboardTestHost> CreateAsync(params ISwitchboardModule[] modules) =>
        await CreateAsync(modules, configure: null);

    public static async Task<SwitchboardTestHost> CreateAsync(IEnumerable<ISwitchboardModule> modules, Action<IServiceCollection>? configure)
    {
        var database = $"sb_test_{Guid.NewGuid():N}";
        var server = Environment.GetEnvironmentVariable("SWITCHBOARD_TEST_SQL") ?? DefaultServer;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"{server};Database={database}",
                ["Switchboard:Storage:Path"] = Path.Combine(Path.GetTempPath(), database),
            })
            .Build();

        var catalog = new ModuleCatalog(modules);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSwitchboardCore(configuration, catalog);
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        foreach (var module in catalog.Modules)
        {
            module.ConfigureServices(services, configuration);
        }
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new SwitchboardTestHost(provider, database);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            await db.Database.EnsureCreatedAsync();
            using var _ = scope.ServiceProvider.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Test setup"));
            await CoreSeeder.SeedAsync(scope.ServiceProvider, default);
            foreach (var module in catalog.Modules)
            {
                await module.SeedAsync(scope.ServiceProvider, default);
            }
            host.Workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.IsPrimary);
        }
        return host;
    }

    /// <summary>Runs <paramref name="action"/> in a fresh DI scope as <paramref name="actor"/>.</summary>
    public async Task<T> RunAsync<T>(Actor actor, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<IActorAccessor>().BeginScope(actor);
        return await action(scope.ServiceProvider);
    }

    public Task RunAsync(Actor actor, Func<IServiceProvider, Task> action) =>
        RunAsync<bool>(actor, async sp =>
        {
            await action(sp);
            return true;
        });

    /// <summary>Runs as the system actor.</summary>
    public Task<T> RunAsSystemAsync<T>(Func<IServiceProvider, Task<T>> action) => RunAsync(Actor.System("Test"), action);

    /// <summary>Creates an agent with a role in the primary workspace, optionally in groups.</summary>
    public async Task<User> CreateAgentAsync(string name, string roleKey = RoleKeys.Agent, TicketScope scope = TicketScope.Global, params string[] groupNames)
    {
        return await RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var user = NewUser(name, isAgent: true);
            db.Users.Add(user);
            var role = await db.Roles.FirstAsync(r => r.Key == roleKey);
            db.WorkspaceMemberships.Add(new WorkspaceMembership { UserId = user.Id, WorkspaceId = Workspace.Id, RoleId = role.Id, TicketScope = scope });
            foreach (var groupName in groupNames)
            {
                var group = await db.AgentGroups.FirstAsync(g => g.Name == groupName);
                db.AgentGroupMembers.Add(new AgentGroupMember { GroupId = group.Id, UserId = user.Id });
            }
            await db.SaveChangesAsync();
            return user;
        });
    }

    public async Task<User> CreateRequesterAsync(string name)
    {
        return await RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var user = NewUser(name, isAgent: false);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user;
        });
    }

    /// <summary>Delivers pending outbox events to after-commit handlers until none remain.</summary>
    public async Task<int> ProcessOutboxAsync(int maxBatches = 20)
    {
        var processor = ActivatorUtilities.CreateInstance<OutboxProcessor>(Services);
        var total = 0;
        for (var i = 0; i < maxBatches; i++)
        {
            var n = await processor.ProcessBatchAsync(default);
            total += n;
            if (n == 0)
            {
                break;
            }
        }
        return total;
    }

    public static Actor As(User user, Channel channel = Channel.Web) => Actor.ForUser(user, channel);

    private static User NewUser(string name, bool isAgent)
    {
        var email = $"{name.ToLowerInvariant().Replace(' ', '.')}.{Guid.NewGuid().ToString("N")[..6]}@test.local";
        return new User
        {
            DisplayName = name,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            IsAgent = isAgent,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>().Database.EnsureDeletedAsync();
        }
        finally
        {
            await Services.DisposeAsync();
        }
    }
}
