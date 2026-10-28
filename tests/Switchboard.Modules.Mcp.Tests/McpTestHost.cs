using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Mcp;
using Switchboard.Modules.Mcp.Data;
using Switchboard.Modules.Mcp.Server;
using Switchboard.Testing;

namespace Switchboard.Modules.Mcp.Tests;

/// <summary>Records live pushes so tests can see the "agent_action.pending" notice.</summary>
public sealed class RecordingLiveNotifier : ILiveNotifier
{
    public List<(Guid UserId, string Type, object Payload)> Sent { get; } = [];

    public Task ToUserAsync(Guid userId, string type, object payload, CancellationToken ct = default)
    {
        lock (Sent)
        {
            Sent.Add((userId, type, payload));
        }
        return Task.CompletedTask;
    }

    public Task ToTicketAsync(Guid ticketId, string type, object payload, CancellationToken ct = default) => Task.CompletedTask;

    public Task ToWorkspaceAgentsAsync(Guid workspaceId, string type, object payload, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Core plus the MCP module on a throwaway database, with helpers to make grants and call tools exactly
/// as the MCP endpoint does (through <see cref="McpToolDispatcher"/>), without the HTTP layer.
/// </summary>
public sealed class McpTestHost : IAsyncDisposable
{
    private McpTestHost(SwitchboardTestHost host, RecordingLiveNotifier live)
    {
        Host = host;
        Live = live;
    }

    public SwitchboardTestHost Host { get; }

    public RecordingLiveNotifier Live { get; }

    public static async Task<McpTestHost> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var live = new RecordingLiveNotifier();
        var host = await SwitchboardTestHost.CreateAsync([new McpModule()], services =>
        {
            services.AddSingleton<ILiveNotifier>(live);
            configure?.Invoke(services);
        });
        return new McpTestHost(host, live);
    }

    public async Task<McpCaller> ConnectAsync(User user, params string[] scopes) => await ConnectAsync(user, "test-agent", scopes);

    /// <summary>Records a consent (grant) for <paramref name="user"/> and returns the caller its tokens would carry.</summary>
    public async Task<McpCaller> ConnectAsync(User user, string clientId, string[] scopes, string slug = "")
    {
        var grantId = Guid.CreateVersion7();
        await Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            if (!await db.Set<McpClient>().AnyAsync(c => c.ClientId == clientId))
            {
                db.Set<McpClient>().Add(new McpClient { ClientId = clientId, Name = $"Agent {clientId}", Source = McpClientSource.Dynamic, RedirectUris = ["http://127.0.0.1/cb"], CreatedAt = DateTimeOffset.UtcNow });
            }
            db.Set<McpGrant>().Add(new McpGrant
            {
                Id = grantId,
                UserId = user.Id,
                ClientId = clientId,
                ClientName = $"Agent {clientId}",
                Scopes = scopes.ToList(),
                Resource = McpPaths.Resource("http://localhost:8080", slug),
                ConfigurationSlug = slug,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            await db.SaveChangesAsync();
            return true;
        });
        return new McpCaller(user.Id, user.DisplayName, clientId, $"Agent {clientId}", grantId, scopes.ToHashSet(StringComparer.Ordinal), slug, ViaApiKey: false);
    }

    public Task<ToolOutcome?> CallAsync(McpCaller caller, string tool, JsonObject? args = null, McpCallChannel? channel = null) =>
        Host.RunAsSystemAsync(async sp =>
        {
            // The dispatcher sets the MCP actor itself; the outer system scope only stands in for "no web user".
            using var _ = sp.GetRequiredService<Core.Security.IActorAccessor>().BeginScope(Core.Security.Actor.Anonymous);
            return await sp.GetRequiredService<McpToolDispatcher>().CallAsync(caller, channel ?? McpCallChannel.None, tool, args ?? [], default);
        });

    public async Task<ToolOutcome> CallOkAsync(McpCaller caller, string tool, JsonObject? args = null)
    {
        var outcome = await CallAsync(caller, tool, args) ?? throw new InvalidOperationException($"Unknown tool {tool}");
        Assert.True(!outcome.IsError, $"{tool} failed: {outcome.Code} {outcome.Text} {outcome.Structured?.ToJsonString()}");
        return outcome;
    }

    public Task<T> DbAsync<T>(Func<SwitchboardDbContext, Task<T>> query) =>
        Host.RunAsSystemAsync(sp => query(sp.GetRequiredService<SwitchboardDbContext>()));

    public Task<Ticket> FileTicketAsync(User requester, string subject, string descriptionHtml) =>
        Host.RunAsync(SwitchboardTestHost.As(requester, Channel.Portal), sp =>
            sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
            {
                Subject = subject,
                DescriptionHtml = descriptionHtml,
                AsRequester = true,
                SourceKey = TicketSourceKeys.Portal,
            }));

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}
