using System.Reflection;
using Microsoft.Extensions.DependencyModel;
using Switchboard.Core.Modules;

namespace Switchboard.Web.Hosting;

/// <summary>Finds every <see cref="ISwitchboardModule"/> in the referenced <c>Switchboard.Modules.*</c> assemblies.</summary>
public static class ModuleLoader
{
    public static ModuleCatalog Discover()
    {
        // Load the host's own deps file so discovery also works under dotnet-ef, whose entry assembly is the tool.
        var context = DependencyContext.Load(typeof(ModuleLoader).Assembly) ?? DependencyContext.Default;
        var names = context?.RuntimeLibraries
            .Where(l => l.Name.StartsWith("Switchboard.Modules.", StringComparison.Ordinal))
            .Select(l => l.Name)
            .ToList() ?? [];

        var modules = new List<ISwitchboardModule>();
        foreach (var name in names)
        {
            var assembly = Assembly.Load(new AssemblyName(name));
            modules.AddRange(assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISwitchboardModule).IsAssignableFrom(t))
                .Select(t => (ISwitchboardModule)Activator.CreateInstance(t)!));
        }
        return new ModuleCatalog(modules);
    }
}
