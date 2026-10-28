using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Agents;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Mcp.Security;
using Switchboard.Modules.Mcp.Server;
using Switchboard.Testing;

namespace Switchboard.Modules.Mcp.Tests;

/// <summary>
/// MCP-91 / INV-MCP-1: every tool × role × scope. A tool is listed and callable only when the grant holds
/// its scope and the user is an agent (or the tool is requester-safe); inside that, the user's own
/// privileges still decide.
/// </summary>
public sealed class PermissionMatrixTests : IAsyncLifetime
{
    private McpTestHost _mcp = default!;
    private readonly Dictionary<string, User> _users = [];
    private Ticket _ticket = default!;

    public async Task InitializeAsync()
    {
        _mcp = await McpTestHost.CreateAsync();
        _users["org_admin"] = await _mcp.Host.CreateAgentAsync("Ada Admin", RoleKeys.Agent);
        await _mcp.DbAsync(db => db.Users.Where(u => u.Id == _users["org_admin"].Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsOrgAdmin, true)));
        _users["supervisor"] = await _mcp.Host.CreateAgentAsync("Sue Supervisor", RoleKeys.Supervisor, TicketScope.Global, "Service Desk");
        _users["agent"] = await _mcp.Host.CreateAgentAsync("Al Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _users["read_only"] = await _mcp.Host.CreateAgentAsync("Rita Reader", RoleKeys.ReadOnly, TicketScope.Global);
        _users["requester"] = await _mcp.Host.CreateRequesterAsync("Rex Requester");
        _ticket = await _mcp.FileTicketAsync(_users["requester"], "Monitor flickers", "<p>Through the dock.</p>");
    }

    public async Task DisposeAsync() => await _mcp.DisposeAsync();

    public static TheoryData<string> Roles => ["org_admin", "supervisor", "agent", "read_only", "requester"];

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Each_tool_is_listed_and_callable_only_with_its_scope(string role)
    {
        var user = _users[role];
        var isAgent = role != "requester";
        var definitions = await _mcp.Host.RunAsSystemAsync(sp => Task.FromResult(sp.GetRequiredService<McpToolCatalog>().All.Select(t => t.Definition).ToList()));
        Assert.True(definitions.Count >= 20, "the module's own tools are registered");
        var scopeSets = McpScopes.All.Select(s => new[] { s.Key }).Append(McpScopes.All.Select(s => s.Key).ToArray()).ToList();

        foreach (var scopes in scopeSets)
        {
            var caller = await _mcp.ConnectAsync(user, $"matrix-{role}", scopes);
            var listed = (await _mcp.Host.RunAsSystemAsync(sp => sp.GetRequiredService<McpToolDispatcher>().ListAsync(caller, default))).Tools
                .Select(t => t.Name).ToHashSet();
            var effective = McpScopes.Normalize(scopes, isAgent).ToHashSet();

            foreach (var tool in definitions)
            {
                var expected = effective.Contains(tool.Scope) && (isAgent || tool.RequesterAllowed);
                Assert.True(expected == listed.Contains(tool.Name), $"{role} with [{string.Join(' ', scopes)}]: {tool.Name} listed={listed.Contains(tool.Name)}, expected {expected}");
                if (!expected)
                {
                    // Not listed means not callable either, whatever the arguments.
                    var outcome = await _mcp.CallAsync(caller, tool.Name, new JsonObject());
                    Assert.True(outcome!.Code == McpErrorCodes.PermissionDenied, $"{role} [{string.Join(' ', scopes)}] {tool.Name} returned {outcome.Code}");
                }
            }
        }
    }

    [Fact]
    public async Task Requester_scopes_are_dropped_for_requesters()
    {
        var caller = await _mcp.ConnectAsync(_users["requester"], "rq", [McpScopes.Profile, McpScopes.UsersRead, McpScopes.TicketsDelete, "admin:read"]);
        var me = await _mcp.CallOkAsync(caller, "whoami");
        var scopes = me.Structured!["scopes"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        Assert.Equal([McpScopes.Profile], scopes);
    }

    [Fact]
    public async Task Role_privileges_still_decide_within_granted_scopes()
    {
        string[] all = [.. McpScopes.All.Select(s => s.Key)];
        var readOnly = await _mcp.ConnectAsync(_users["read_only"], "ro", all);
        Assert.Equal(McpErrorCodes.PermissionDenied, (await _mcp.CallAsync(readOnly, "add_private_note", new JsonObject { ["ticket_id"] = _ticket.DisplayId, ["body"] = "x" }))!.Code);
        await _mcp.CallOkAsync(readOnly, "get_ticket", new JsonObject { ["ticket_id"] = _ticket.DisplayId });
        await _mcp.CallOkAsync(readOnly, "search_users", new JsonObject { ["query"] = "Rex" });

        // Agents can't merge (supervisor privilege): the approved action fails on Core's check.
        var agent = await _mcp.ConnectAsync(_users["agent"], "ag", all);
        var other = await _mcp.FileTicketAsync(_users["requester"], "Monitor flickers again", "<p>Duplicate.</p>");
        var merge = await _mcp.CallAsync(agent, "merge_tickets", new JsonObject { ["primary_ticket_id"] = _ticket.DisplayId, ["ticket_ids"] = new JsonArray(other.DisplayId) });
        Assert.Equal(McpErrorCodes.ConfirmationRequired, merge!.Code);
        var id = Guid.Parse(merge.Structured!["pending_action"]!["id"]!.GetValue<string>());
        var (_, outcome) = await _mcp.Host.RunAsync(SwitchboardTestHost.As(_users["agent"]), sp =>
            sp.GetRequiredService<PendingActionService>().ApproveAsync(id, _users["agent"].Id, reauthenticated: true, default));
        Assert.Equal(McpErrorCodes.PermissionDenied, outcome!.Code);
        Assert.Null((await _mcp.DbAsync(db => db.Tickets.AsNoTracking().FirstAsync(t => t.Id == other.Id))).MergedIntoId);

        // Org admins hold every privilege, but MCP still never reaches permanent deletion or admin configuration (MCP-34).
        var admin = new Actor(ActorKind.McpClient, _users["org_admin"].Id, "Ada", Channel.Mcp, "c", "C", Guid.NewGuid(), all.ToHashSet());
        Assert.False(await _mcp.Host.RunAsync(admin, sp => sp.GetRequiredService<IPermissionService>().HasAsync(Privileges.TicketsDeletePermanently)));
        Assert.False(await _mcp.Host.RunAsync(admin, sp => sp.GetRequiredService<IPermissionService>().HasAsync(Privileges.RolesManage)));
        Assert.False(await _mcp.Host.RunAsync(admin, sp => sp.GetRequiredService<IPermissionService>().HasAsync(McpModule.ManagePrivilege)));
        Assert.True(await _mcp.Host.RunAsync(admin, sp => sp.GetRequiredService<IPermissionService>().HasAsync(Privileges.TicketsMerge)));
    }

    [Fact]
    public void Tool_definitions_are_well_formed()
    {
        var host = _mcp.Host;
        using var scope = host.Services.CreateScope();
        foreach (var tool in scope.ServiceProvider.GetRequiredService<McpToolCatalog>().All)
        {
            var d = tool.Definition;
            Assert.Matches("^[a-z][a-z0-9_]*$", d.Name);
            Assert.True(McpScopes.IsKnown(d.Scope), $"{d.Name} uses unknown scope {d.Scope}");
            Assert.Equal("object", d.InputSchema["type"]!.GetValue<string>());
            Assert.NotNull(d.OutputSchema);
            Assert.Equal("object", d.OutputSchema!["type"]!.GetValue<string>());
            var wire = McpToolDispatcher.ToProtocolTool(d);
            Assert.Equal(d.Class == AgentActionClass.Read, wire.Annotations!.ReadOnlyHint);
            Assert.Equal(d.Class == AgentActionClass.External, wire.Annotations.OpenWorldHint);
            Assert.Equal(d.Class == AgentActionClass.Destructive, wire.Annotations.DestructiveHint);
            if (d.Class != AgentActionClass.Read)
            {
                Assert.True(wire.InputSchema.GetProperty("properties").TryGetProperty("idempotency_key", out _), $"{d.Name} offers idempotency_key");
            }
        }
    }
}
