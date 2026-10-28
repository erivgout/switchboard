using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Api.Auth;

namespace Switchboard.Modules.Api.Tests;

/// <summary>A scoped key never does more than its scopes allow (SR-AUTHN-7, MCP-18), even for an org admin.</summary>
[Collection(ApiCollection.Name)]
public sealed class ScopedKeyTests(ApiFixture f)
{
    [Fact]
    public async Task Read_only_key_can_read_tickets_but_not_change_them()
    {
        var full = await f.CreateKeyAsync(f.Agent);
        var id = await f.CreateTicketAsync(full, new { subject = "Scope target" });
        var readOnly = await f.CreateKeyAsync(f.Agent, "Read only", 30, ApiScopes.TicketsRead);

        Assert.Equal(HttpStatusCode.OK, (await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}", readOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.SendAsync(HttpMethod.Get, "/api/v1/tickets", readOnly)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Patch, $"/api/v1/tickets/{id}", readOnly, new { subject = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Post, "/api/v1/tickets", readOnly, new { subject = "New" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/notes", readOnly, new { bodyText = "note" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Delete, $"/api/v1/tickets/{id}", readOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Get, "/api/v1/me", readOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Get, "/api/v1/agents", readOnly)).StatusCode);

        var unchanged = await ApiFixture.JsonAsync(await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}", full));
        Assert.Equal("Scope target", unchanged.Str("subject"));
    }

    [Fact]
    public async Task Write_key_without_reply_scope_cannot_send_messages_out()
    {
        var full = await f.CreateKeyAsync(f.Agent);
        var requesterKey = await f.CreateKeyAsync(f.Requester);
        var id = await f.CreateTicketAsync(requesterKey, new { subject = "Needs a reply" });
        var write = await f.CreateKeyAsync(f.Agent, "Write", 30, ApiScopes.TicketsWrite);

        Assert.Equal(HttpStatusCode.Created, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/notes", write, new { bodyText = "private" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.SendAsync(HttpMethod.Patch, $"/api/v1/tickets/{id}", write, new { priority = "high" })).StatusCode);

        // Public notes and replies reach the requester: they need tickets:reply.
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/notes", write, new { bodyText = "public", isPrivate = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/reply", write, new { bodyText = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/reply", full, new { bodyText = "hi" })).StatusCode);
    }

    [Fact]
    public async Task Org_admin_scoped_key_cannot_use_privileges_outside_its_scopes()
    {
        var key = await f.CreateKeyAsync(f.Admin, "Admin read", 30, ApiScopes.TicketsRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Get, "/api/v1/requesters", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Post, "/api/v1/requesters", key, new { name = "X", email = "x@example.com" })).StatusCode);

        // The guard also narrows privilege checks made deeper in services.
        var actor = new Actor(ActorKind.User, f.Admin.Id, f.Admin.DisplayName, Channel.Api, Scopes: new HashSet<string> { ApiScopes.TicketsRead });
        var (settings, view, edit) = await f.Host.RunAsync(actor, async sp =>
        {
            var p = sp.GetRequiredService<IPermissionService>();
            return (await p.HasAsync(Privileges.SettingsManage), await p.HasAsync(Privileges.TicketsView), await p.HasAsync(Privileges.TicketsEdit));
        });
        Assert.False(settings);
        Assert.True(view);
        Assert.False(edit);
    }

    [Fact]
    public async Task Users_read_scope_covers_the_directory_but_not_adding_people()
    {
        var key = await f.CreateKeyAsync(f.Admin, "Directory", 30, ApiScopes.UsersRead);
        Assert.Equal(HttpStatusCode.OK, (await f.SendAsync(HttpMethod.Get, "/api/v1/requesters", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Post, "/api/v1/requesters", key, new { name = "X", email = "x2@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Get, "/api/v1/tickets", key)).StatusCode);
    }

    [Fact]
    public async Task Requester_with_profile_only_key_cannot_read_their_tickets()
    {
        var full = await f.CreateKeyAsync(f.Requester);
        var id = await f.CreateTicketAsync(full, new { subject = "Mine" });
        var profileOnly = await f.CreateKeyAsync(f.Requester, "Profile", 30, ApiScopes.Profile);

        // Requester visibility in Core doesn't consult scope guards; the endpoint scope must stop this.
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Get, $"/api/v1/tickets/{id}", profileOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Get, "/api/v1/tickets", profileOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SendAsync(HttpMethod.Post, $"/api/v1/tickets/{id}/messages", profileOnly, new { bodyText = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.SendAsync(HttpMethod.Get, "/api/v1/me", profileOnly)).StatusCode);
    }

    [Fact]
    public void Write_scopes_bring_read_and_requesters_cannot_ask_for_agent_scopes()
    {
        var agentScopes = ApiScopes.Normalize([ApiScopes.TicketsWrite], isAgent: true, out var rejected);
        Assert.Empty(rejected);
        Assert.Equal([ApiScopes.TicketsRead, ApiScopes.TicketsWrite], agentScopes);

        ApiScopes.Normalize([ApiScopes.TicketsReply, ApiScopes.UsersRead, "nonsense"], isAgent: false, out rejected);
        Assert.Equal([ApiScopes.TicketsReply, ApiScopes.UsersRead, "nonsense"], rejected);
    }

    [Theory]
    [InlineData(Privileges.TicketsView, ApiScopes.TicketsRead)]
    [InlineData(Privileges.TicketsEdit, ApiScopes.TicketsWrite)]
    [InlineData(Privileges.TicketsReply, ApiScopes.TicketsReply)]
    [InlineData(Privileges.TicketsDelete, ApiScopes.TicketsDelete)]
    [InlineData(Privileges.UsersView, ApiScopes.UsersRead)]
    [InlineData("kb.view", "kb:read")]
    [InlineData("kb.manage", "kb:write")]
    [InlineData("catalog.use", "catalog:request")]
    [InlineData(Privileges.UsersManage, null)]
    [InlineData(Privileges.SettingsManage, null)]
    [InlineData(Privileges.RolesManage, null)]
    [InlineData(Privileges.TicketsDeletePermanently, null)]
    [InlineData("unknown.thing", null)]
    public void Privileges_map_to_scopes(string privilege, string? scope) => Assert.Equal(scope, ApiScopes.For(privilege));

    [Fact]
    public void Guard_leaves_other_channels_and_unscoped_keys_alone()
    {
        var guard = new ApiKeyScopeGuard();
        var scopes = new HashSet<string> { ApiScopes.TicketsRead };
        Assert.True(guard.Allows(new Actor(ActorKind.User, Guid.NewGuid(), "A", Channel.Api), Privileges.SettingsManage));
        Assert.True(guard.Allows(new Actor(ActorKind.McpClient, Guid.NewGuid(), "A", Channel.Mcp, Scopes: scopes), Privileges.TicketsEdit));
        Assert.False(guard.Allows(new Actor(ActorKind.User, Guid.NewGuid(), "A", Channel.Api, Scopes: scopes), Privileges.TicketsEdit));
    }
}
