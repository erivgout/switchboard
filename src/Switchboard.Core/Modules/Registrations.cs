using Microsoft.Extensions.DependencyInjection;

namespace Switchboard.Core.Modules;

/// <summary>Which shell a navigation item appears in.</summary>
public enum NavArea
{
    /// <summary>Agent console left rail.</summary>
    Agent,
    /// <summary>Requester portal header.</summary>
    Portal,
    /// <summary>Admin settings navigation.</summary>
    Admin,
}

/// <summary>
/// A navigation entry contributed by a module. <paramref name="Group"/> groups admin items
/// (e.g. "Users and access"); agent and portal items ignore it.
/// </summary>
public sealed record NavItem(
    NavArea Area,
    string Key,
    string Title,
    string Icon,
    string Href,
    int Order = 100,
    string? Privilege = null,
    string? Group = null,
    bool AgentsOnly = false,
    string? Description = null);

/// <summary>
/// A view component rendered into a named UI slot, e.g. <c>ticket.sidebar</c>.
/// The view component receives the slot model as its <c>model</c> argument.
/// </summary>
public sealed record SlotRegistration(string Slot, Type ViewComponentType, int Order = 100);

/// <summary>A permission that roles can grant. Modules declare their own.</summary>
public sealed record PrivilegeDefinition(string Key, string Title, string Group, string? Description = null);

public static class ModuleServiceCollectionExtensions
{
    public static IServiceCollection AddNavItem(this IServiceCollection services, NavItem item)
        => services.AddSingleton(item);

    public static IServiceCollection AddSlot<TViewComponent>(this IServiceCollection services, string slot, int order = 100)
        => services.AddSingleton(new SlotRegistration(slot, typeof(TViewComponent), order));

    public static IServiceCollection AddPrivileges(this IServiceCollection services, params PrivilegeDefinition[] privileges)
    {
        foreach (var p in privileges)
        {
            services.AddSingleton(p);
        }
        return services;
    }
}
