using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Switchboard.Core.Domain;

namespace Switchboard.Core.Security;

/// <summary>Who is performing the current operation, and through which channel.</summary>
public sealed record Actor(
    ActorKind Kind,
    Guid? UserId,
    string DisplayName,
    Channel Channel,
    string? ClientId = null,
    string? ClientName = null,
    Guid? GrantId = null,
    IReadOnlySet<string>? Scopes = null,
    int CascadeDepth = 0)
{
    public static Actor Anonymous { get; } = new(ActorKind.Anonymous, null, "Anonymous", Channel.Web);

    public static Actor System(string name = "System", Channel channel = Channel.System, int cascadeDepth = 0) =>
        new(ActorKind.System, null, name, channel, CascadeDepth: cascadeDepth);

    public static Actor Automation(string name, int cascadeDepth) =>
        new(ActorKind.Automation, null, name, Channel.Automation, CascadeDepth: cascadeDepth);

    public static Actor ForUser(User user, Channel channel) =>
        new(ActorKind.User, user.Id, user.DisplayName, channel);

    /// <summary>System and automation actors are trusted internal callers: permission checks pass.</summary>
    public bool IsInternal => Kind is ActorKind.System or ActorKind.Automation;

    public bool IsAuthenticatedUser => UserId is not null && Kind is ActorKind.User or ActorKind.McpClient or ActorKind.Plugin;

    public ActorSnapshot Snapshot() => new(Kind, UserId, DisplayName, Channel, ClientName);
}

/// <summary>Serializable copy of an <see cref="Actor"/> carried on domain events.</summary>
public sealed record ActorSnapshot(ActorKind Kind, Guid? UserId, string DisplayName, Channel Channel, string? ClientName);

/// <summary>Claim types Switchboard adds to principals (API keys, MCP tokens).</summary>
public static class SwitchboardClaims
{
    public const string Channel = "sb:channel";
    public const string ClientId = "sb:client_id";
    public const string ClientName = "sb:client_name";
    public const string GrantId = "sb:grant_id";
    public const string Scope = "scope";
    public const string DisplayName = "sb:name";
}

/// <summary>
/// Resolves the current <see cref="Actor"/>. Inside an HTTP request it reads the principal;
/// background code pushes an explicit actor with <see cref="BeginScope"/>.
/// </summary>
public interface IActorAccessor
{
    Actor Current { get; }

    /// <summary>Run code as <paramref name="actor"/> until the returned handle is disposed.</summary>
    IDisposable BeginScope(Actor actor);
}

public sealed class ActorAccessor(IHttpContextAccessor httpContextAccessor) : IActorAccessor
{
    private static readonly AsyncLocal<Stack<Actor>?> Overrides = new();

    public Actor Current
    {
        get
        {
            var stack = Overrides.Value;
            if (stack is { Count: > 0 })
            {
                return stack.Peek();
            }

            var http = httpContextAccessor.HttpContext;
            if (http?.User.Identity?.IsAuthenticated == true)
            {
                return FromPrincipal(http.User, http.Request.Path);
            }

            return Actor.Anonymous;
        }
    }

    public IDisposable BeginScope(Actor actor)
    {
        // Copy-on-write so sibling async flows never share a mutable stack.
        var next = new Stack<Actor>((Overrides.Value ?? new Stack<Actor>()).Reverse());
        next.Push(actor);
        var previous = Overrides.Value;
        Overrides.Value = next;
        return new Restore(previous);
    }

    public static Actor FromPrincipal(ClaimsPrincipal principal, PathString path)
    {
        var idValue = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        Guid? userId = Guid.TryParse(idValue, out var id) ? id : null;
        var name = principal.FindFirstValue(SwitchboardClaims.DisplayName)
            ?? principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.Identity?.Name
            ?? "User";

        var channel = Enum.TryParse<Channel>(principal.FindFirstValue(SwitchboardClaims.Channel), out var c)
            ? c
            : path.StartsWithSegments("/portal") ? Channel.Portal : Channel.Web;

        var scopes = principal.FindAll(SwitchboardClaims.Scope)
            .SelectMany(s => s.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);

        var kind = channel == Channel.Mcp ? ActorKind.McpClient : ActorKind.User;
        Guid? grantId = Guid.TryParse(principal.FindFirstValue(SwitchboardClaims.GrantId), out var g) ? g : null;

        return new Actor(
            kind,
            userId,
            name,
            channel,
            principal.FindFirstValue(SwitchboardClaims.ClientId),
            principal.FindFirstValue(SwitchboardClaims.ClientName),
            grantId,
            scopes.Count > 0 ? scopes : null);
    }

    private sealed class Restore(Stack<Actor>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Overrides.Value = previous;
        }
    }
}
