using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Data;
using Switchboard.Modules.ServiceDesk.Services;

namespace Switchboard.Modules.ServiceDesk.Tests;

/// <summary>Canned responses: placeholder encoding and folder visibility (FR-AUT-7).</summary>
public sealed class CannedResponseTests : ServiceDeskTestBase
{
    private User _sam = default!;
    private User _jo = default!;
    private User _admin = default!;
    private User _hardware = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _sam = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _jo = await Host.CreateAgentAsync("Jo Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _admin = await Host.CreateAgentAsync("Wes Admin", RoleKeys.WorkspaceAdmin, TicketScope.Global, "Service Desk");
        _hardware = await Host.CreateAgentAsync("Hal Hardware", RoleKeys.Agent, TicketScope.Global, "Hardware");
    }

    [Fact]
    public void Placeholder_values_are_html_encoded()
    {
        var html = CannedResponseService.FillPlaceholders(
            "<p>Hi {{requester.name}}, about {{ticket.subject}} and {{unknown.thing}}</p>",
            new Dictionary<string, string?>
            {
                ["requester.name"] = "<b>Mallory</b>",
                ["ticket.subject"] = "\"><img src=x onerror=alert(1)>",
            });

        Assert.Equal("<p>Hi &lt;b&gt;Mallory&lt;/b&gt;, about &quot;&gt;&lt;img src=x onerror=alert(1)&gt; and {{unknown.thing}}</p>", html);
    }

    [Fact]
    public async Task Rendering_for_a_ticket_fills_placeholders_without_letting_markup_through()
    {
        var ticket = await FileTicketAsync("<img src=x onerror=alert(1)> VPN down");
        var response = await As(_sam, sp => sp.GetRequiredService<CannedResponseService>().CreateAsync(
            new CannedResponseInput(null, "Ack", "<p>Hi {{requester.first_name}}, {{ticket.id}}: {{ticket.subject}}. {{agent.name}}</p>")));

        var html = await As(_sam, sp => sp.GetRequiredService<CannedResponseService>().RenderAsync(response.Id, ticket.Id));

        Assert.Contains($"Hi Dana, {ticket.DisplayId}: &lt;img src=x onerror=alert(1)&gt; VPN down. Sam Agent", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public async Task Personal_responses_are_private_to_their_owner()
    {
        var response = await As(_sam, sp => sp.GetRequiredService<CannedResponseService>().CreateAsync(new CannedResponseInput(null, "My sign-off", "<p>Cheers</p>")));

        Assert.Contains(await As(_sam, sp => sp.GetRequiredService<CannedResponseService>().SearchAsync(null)), r => r.Id == response.Id);
        Assert.DoesNotContain(await As(_jo, sp => sp.GetRequiredService<CannedResponseService>().SearchAsync(null)), r => r.Id == response.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => As(_jo, sp => sp.GetRequiredService<CannedResponseService>().GetAsync(response.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => As(_jo, sp => sp.GetRequiredService<CannedResponseService>().DeleteAsync(response.Id)));
    }

    [Fact]
    public async Task Shared_and_group_folders_need_settings_manage_and_follow_group_membership()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => As(_sam, sp => sp.GetRequiredService<CannedResponseService>().CreateFolderAsync(
            new CannedFolderInput("Everyone", CannedVisibility.Shared, null))));

        var hardwareGroup = await Host.RunAsSystemAsync(sp => Task.FromResult(sp.GetRequiredService<Core.Data.SwitchboardDbContext>().AgentGroups.First(g => g.Name == "Hardware").Id));
        var folder = await As(_admin, sp => sp.GetRequiredService<CannedResponseService>().CreateFolderAsync(
            new CannedFolderInput("Hardware replies", CannedVisibility.Group, hardwareGroup)));
        var response = await As(_admin, sp => sp.GetRequiredService<CannedResponseService>().CreateAsync(
            new CannedResponseInput(folder.Id, "Loaner ready", "<p>Your loaner laptop is ready at the desk.</p>")));

        Assert.Contains(await As(_hardware, sp => sp.GetRequiredService<CannedResponseService>().SearchAsync("loaner")), r => r.Id == response.Id);
        Assert.DoesNotContain(await As(_sam, sp => sp.GetRequiredService<CannedResponseService>().SearchAsync("loaner")), r => r.Id == response.Id);

        // Group members can use the response but not change it.
        await Assert.ThrowsAsync<ForbiddenException>(() => As(_hardware, sp => sp.GetRequiredService<CannedResponseService>().UpdateAsync(response.Id,
            new CannedResponseInput(null, "Changed", "<p>Changed</p>"))));
    }

    [Fact]
    public async Task Response_bodies_are_sanitized_on_save()
    {
        var response = await As(_sam, sp => sp.GetRequiredService<CannedResponseService>().CreateAsync(
            new CannedResponseInput(null, "Sneaky", "<p onclick=\"steal()\">Hello<script>alert(1)</script></p>")));
        Assert.DoesNotContain("script", response.BodyHtml);
        Assert.DoesNotContain("onclick", response.BodyHtml);
    }
}
