using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard.Core.Events;

/// <summary>Resolves and invokes handlers for a runtime event type.</summary>
internal static class EventDispatch
{
    private static readonly ConcurrentDictionary<(Type Open, Type Event), Type> Closed = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> HandleMethods = new();

    public static IEnumerable<object> ResolveHandlers(IServiceProvider services, Type openHandlerType, Type eventType)
    {
        var handlerType = Closed.GetOrAdd((openHandlerType, eventType), k => k.Open.MakeGenericType(k.Event));
        return services.GetServices(handlerType).Where(h => h is not null)!;
    }

    public static async Task InvokeAsync(IServiceProvider services, Type openHandlerType, DomainEvent domainEvent, CancellationToken ct)
    {
        foreach (var handler in ResolveHandlers(services, openHandlerType, domainEvent.GetType()))
        {
            await InvokeHandlerAsync(handler, openHandlerType, domainEvent, ct);
        }
    }

    public static Task InvokeHandlerAsync(object handler, Type openHandlerType, DomainEvent domainEvent, CancellationToken ct)
    {
        var handlerInterface = Closed.GetOrAdd((openHandlerType, domainEvent.GetType()), k => k.Open.MakeGenericType(k.Event));
        var method = HandleMethods.GetOrAdd(handlerInterface, t => t.GetMethod("HandleAsync")!);
        return (Task)method.Invoke(handler, [domainEvent, ct])!;
    }
}
