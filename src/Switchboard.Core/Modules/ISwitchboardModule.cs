using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard.Core.Modules;

/// <summary>
/// Entry point of a feature module. The host discovers every non-abstract implementation in
/// assemblies named <c>Switchboard.Modules.*</c> and calls these hooks at startup.
/// </summary>
public interface ISwitchboardModule
{
    /// <summary>Stable module name, e.g. "Knowledge".</summary>
    string Name { get; }

    /// <summary>Register services, navigation, slots, privileges, hosted services and event handlers.</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Add this module's entities to the EF model (use <c>ApplyConfigurationsFromAssembly</c>).</summary>
    void ConfigureModel(ModelBuilder modelBuilder) { }

    /// <summary>Map minimal API endpoints, hubs, etc. Razor Pages are discovered automatically.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    /// <summary>Add middleware. Runs after authentication/authorization, before endpoints.</summary>
    void ConfigureApp(IApplicationBuilder app) { }

    /// <summary>Seed module defaults. Runs once per startup after the schema exists; must be idempotent.</summary>
    Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The set of modules loaded in this process.</summary>
public sealed class ModuleCatalog
{
    public ModuleCatalog(IEnumerable<ISwitchboardModule> modules)
    {
        Modules = modules.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        Fingerprint = string.Join("|", Modules.Select(m => m.GetType().FullName));
    }

    public IReadOnlyList<ISwitchboardModule> Modules { get; }

    /// <summary>Used as part of the EF model cache key.</summary>
    public string Fingerprint { get; }

    public static ModuleCatalog Empty { get; } = new([]);

    public bool Has(string name) => Modules.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
