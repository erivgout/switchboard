using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.ServiceDesk.Services;

namespace Switchboard.Modules.ServiceDesk.Tests;

/// <summary>Saved views are private unless shared, and only settings managers share or change shared views.</summary>
public sealed class SavedViewTests : ServiceDeskTestBase
{
    private User _sam = default!;
    private User _jo = default!;
    private User _admin = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _sam = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _jo = await Host.CreateAgentAsync("Jo Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _admin = await Host.CreateAgentAsync("Wes Admin", RoleKeys.WorkspaceAdmin, TicketScope.Global, "Service Desk");
    }

    [Fact]
    public async Task Private_views_are_invisible_to_other_agents()
    {
        var view = await As(_sam, sp => sp.GetRequiredService<SavedViewService>().CreateAsync(new SavedViewInput("My VIPs", "tag:vip state:unresolved", "lamp", null, false)));

        var samViews = await As(_sam, sp => sp.GetRequiredService<SavedViewService>().ListAsync());
        var joViews = await As(_jo, sp => sp.GetRequiredService<SavedViewService>().ListAsync());

        Assert.Contains(samViews, v => v.Id == view.Id);
        Assert.DoesNotContain(joViews, v => v.Id == view.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => As(_jo, sp => sp.GetRequiredService<SavedViewService>().GetAsync(view.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => As(_jo, sp => sp.GetRequiredService<SavedViewService>().DeleteAsync(view.Id)));
        await Assert.ThrowsAsync<NotFoundException>(() => As(_jo, sp => sp.GetRequiredService<SavedViewService>().UpdateAsync(view.Id,
            new SavedViewInput("Mine now", "is:lit", null, null, false))));
    }

    [Fact]
    public async Task Sharing_a_view_needs_settings_manage()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => As(_sam, sp => sp.GetRequiredService<SavedViewService>().CreateAsync(
            new SavedViewInput("Team board", "group:mine", null, null, true))));

        var mine = await As(_sam, sp => sp.GetRequiredService<SavedViewService>().CreateAsync(new SavedViewInput("Mine", "agent:me", null, null, false)));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(_sam, sp => sp.GetRequiredService<SavedViewService>().UpdateAsync(mine.Id,
            new SavedViewInput("Mine", "agent:me", null, null, true))));
    }

    [Fact]
    public async Task Shared_views_are_visible_to_all_agents_but_only_admins_change_them()
    {
        var shared = await As(_admin, sp => sp.GetRequiredService<SavedViewService>().CreateAsync(
            new SavedViewInput("Network backlog", "group:Network state:unresolved", "due", ["status", "due"], true)));

        var joViews = await As(_jo, sp => sp.GetRequiredService<SavedViewService>().ListAsync());
        Assert.Contains(joViews, v => v.Id == shared.Id);

        await Assert.ThrowsAsync<ForbiddenException>(() => As(_jo, sp => sp.GetRequiredService<SavedViewService>().DeleteAsync(shared.Id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(_jo, sp => sp.GetRequiredService<SavedViewService>().UpdateAsync(shared.Id,
            new SavedViewInput("Hijacked", "is:lit", null, null, false))));

        await As(_admin, sp => sp.GetRequiredService<SavedViewService>().DeleteAsync(shared.Id));
        Assert.DoesNotContain(await As(_jo, sp => sp.GetRequiredService<SavedViewService>().ListAsync()), v => v.Id == shared.Id);
    }

    [Fact]
    public async Task Invalid_queries_are_rejected_with_a_readable_message()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() => As(_sam, sp => sp.GetRequiredService<SavedViewService>().CreateAsync(
            new SavedViewInput("Broken", "colour:blue", null, null, false))));
        Assert.Contains("colour", error.Message);
    }
}
