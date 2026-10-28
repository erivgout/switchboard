using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Tests;

/// <summary>INV-ID-1 and SR-AUTHZ-3: the last organization admin, and no privilege escalation.</summary>
public sealed class AccessTests : AdminTestBase
{
    private User _admin = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _admin = await CreateOrgAdminAsync("Avery Admin");
    }

    private static OpenWorkPlan Flag => new(OpenWorkAction.Flag);

    // ---------------------------------------------------------------- last organization admin, every path

    [Fact]
    public async Task Last_org_admin_cannot_revoke_their_own_admin()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => As(_admin, sp => sp.GetRequiredService<PeopleService>().SetOrgAdminAsync(_admin.Id, false)));
        Assert.Contains("last active organization admin", ex.Message);
        Assert.True((await ReloadAsync(_admin.Id)).IsOrgAdmin);
    }

    [Fact]
    public async Task Last_org_admin_cannot_be_deactivated_even_by_the_system()
    {
        await Assert.ThrowsAsync<ValidationException>(() => AsSystem(sp => sp.GetRequiredService<PersonLifecycleService>().DeactivateAsync(_admin.Id, Flag)));
        Assert.True((await ReloadAsync(_admin.Id)).IsActive);
    }

    [Fact]
    public async Task Org_admin_cannot_deactivate_themselves()
    {
        var second = await CreateOrgAdminAsync("Second Admin");
        await Assert.ThrowsAsync<ValidationException>(() => As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().DeactivateAsync(_admin.Id, Flag)));
        Assert.True((await ReloadAsync(_admin.Id)).IsActive);
        Assert.True((await ReloadAsync(second.Id)).IsActive);
    }

    [Fact]
    public async Task Org_admins_cannot_be_forgotten_merged_or_converted_to_requesters()
    {
        var second = await CreateOrgAdminAsync("Second Admin");
        await Assert.ThrowsAsync<ValidationException>(() =>
            As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ForgetAsync(second.Id, second.Email!)));
        await Assert.ThrowsAsync<ValidationException>(() =>
            As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ConvertToRequesterAsync(second.Id, Flag)));
        var requester = await Host.CreateRequesterAsync("Rita Requester");
        await Assert.ThrowsAsync<ValidationException>(() =>
            As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().MergeAsync(second.Id, requester.Id)));
        var reloaded = await ReloadAsync(second.Id);
        Assert.True(reloaded.IsOrgAdmin && reloaded.IsActive && !reloaded.IsForgotten);
    }

    [Fact]
    public async Task With_two_admins_one_can_be_revoked_but_not_the_other()
    {
        var second = await CreateOrgAdminAsync("Second Admin");
        await As(_admin, sp => sp.GetRequiredService<PeopleService>().SetOrgAdminAsync(second.Id, false));
        Assert.False((await ReloadAsync(second.Id)).IsOrgAdmin);
        await Assert.ThrowsAsync<ValidationException>(() => As(_admin, sp => sp.GetRequiredService<PeopleService>().SetOrgAdminAsync(_admin.Id, false)));
    }

    [Fact]
    public async Task Concurrent_mutual_demotion_leaves_one_admin()
    {
        var second = await CreateOrgAdminAsync("Second Admin");
        var a = As(_admin, sp => sp.GetRequiredService<PeopleService>().SetOrgAdminAsync(second.Id, false));
        var b = As(second, sp => sp.GetRequiredService<PeopleService>().SetOrgAdminAsync(_admin.Id, false));
        var results = await Task.WhenAll(Capture(a), Capture(b));

        Assert.Single(results, r => r is null);
        var admins = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().Users.CountAsync(u => u.IsOrgAdmin && u.IsActive));
        Assert.Equal(1, admins);
    }

    [Fact]
    public async Task Concurrent_mutual_deactivation_leaves_one_admin()
    {
        var second = await CreateOrgAdminAsync("Second Admin");
        var a = As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().DeactivateAsync(second.Id, Flag));
        var b = As(second, sp => sp.GetRequiredService<PersonLifecycleService>().DeactivateAsync(_admin.Id, Flag));
        await Task.WhenAll(Capture(a), Capture(b));

        var admins = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().Users.CountAsync(u => u.IsOrgAdmin && u.IsActive));
        Assert.Equal(1, admins);
    }

    private static async Task<Exception?> Capture(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // ---------------------------------------------------------------- privilege escalation

    [Fact]
    public async Task Only_org_admins_grant_org_admin()
    {
        var (manager, _) = await CreateAgentWithPrivilegesAsync("Mo Manager", Privileges.UsersView, Privileges.UsersManage);
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<PeopleService>().SetOrgAdminAsync(manager.Id, true)));
        Assert.False((await ReloadAsync(manager.Id)).IsOrgAdmin);
    }

    [Fact]
    public async Task Cannot_add_a_privilege_you_do_not_hold_to_a_role()
    {
        var (manager, _) = await CreateAgentWithPrivilegesAsync("Rhea Roles", Privileges.RolesManage, Privileges.TicketsView);
        var target = await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var r = new Role { Name = "Helpers", Privileges = [Privileges.TicketsView] };
            db.Roles.Add(r);
            await db.SaveChangesAsync();
            return r;
        });

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<RoleService>()
            .UpdateAsync(target.Id, new RoleInput("Helpers", null, [Privileges.TicketsView, Privileges.TicketsDeletePermanently]))));
        Assert.Contains("you don't have it yourself", ex.Message);

        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<RoleService>()
            .CreateAsync(new RoleInput("Escalated", null, [Privileges.WorkspaceAdmin]))));

        // Holding the privilege is enough.
        await As(manager, sp => sp.GetRequiredService<RoleService>().UpdateAsync(target.Id, new RoleInput("Helpers", null, [Privileges.TicketsView, Privileges.RolesManage])));
    }

    [Fact]
    public async Task Cannot_remove_a_privilege_you_do_not_hold_from_a_role()
    {
        var (manager, _) = await CreateAgentWithPrivilegesAsync("Rhea Roles", Privileges.RolesManage, Privileges.TicketsView);
        var supervisor = await RoleAsync(RoleKeys.Supervisor);
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<RoleService>()
            .UpdateAsync(supervisor.Id, new RoleInput(supervisor.Name, null, supervisor.Privileges.Where(p => p != Privileges.TicketsMerge).ToList()))));
    }

    [Fact]
    public async Task Cannot_edit_or_delete_your_own_role()
    {
        var (manager, role) = await CreateAgentWithPrivilegesAsync("Rhea Roles", Privileges.RolesManage, Privileges.TicketsView);
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<RoleService>()
            .UpdateAsync(role.Id, new RoleInput(role.Name, null, [Privileges.RolesManage]))));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<RoleService>().DeleteAsync(role.Id, null)));
    }

    [Fact]
    public async Task Cannot_give_someone_a_role_with_more_access_than_you()
    {
        var (manager, _) = await CreateAgentWithPrivilegesAsync("Mo Manager", Privileges.UsersView, Privileges.UsersManage, Privileges.TicketsView);
        var agent = await Host.CreateAgentAsync("Ada Agent", RoleKeys.ReadOnly);
        var supervisor = await RoleAsync(RoleKeys.Supervisor);

        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<PeopleService>().UpdateAccessAsync(agent.Id,
            new AccessInput { Memberships = [new MembershipInput(Host.Workspace.Id, supervisor.Id, TicketScope.Global)] })));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<PeopleService>().CreateAsync(
            new PersonInput { DisplayName = "New Hire", Email = "new.hire@test.local" }, new NewAgentAccess(Host.Workspace.Id, supervisor.Id, TicketScope.Global))));
    }

    [Fact]
    public async Task Cannot_change_your_own_access()
    {
        var (manager, _) = await CreateAgentWithPrivilegesAsync("Mo Manager", Privileges.UsersView, Privileges.UsersManage, Privileges.TicketsView);
        var readOnly = await RoleAsync(RoleKeys.ReadOnly);
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<PeopleService>().UpdateAccessAsync(manager.Id,
            new AccessInput { Memberships = [new MembershipInput(Host.Workspace.Id, readOnly.Id, TicketScope.Global)] })));
    }

    [Fact]
    public async Task People_managers_cannot_take_over_more_powerful_accounts()
    {
        var (manager, _) = await CreateAgentWithPrivilegesAsync("Mo Manager", Privileges.UsersView, Privileges.UsersManage);
        var supervisor = await Host.CreateAgentAsync("Sue Supervisor", RoleKeys.Supervisor);
        var input = new PersonInput { DisplayName = "Avery Admin", Email = "attacker@evil.test" };

        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<PeopleService>().UpdateProfileAsync(_admin.Id, input)));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<PeopleService>().UpdateProfileAsync(supervisor.Id, input with { DisplayName = "Sue" })));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<PersonLifecycleService>().DeactivateAsync(supervisor.Id, Flag)));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<InvitationService>().InviteAsync(_admin.Id)));
        Assert.Equal(_admin.Email, (await ReloadAsync(_admin.Id)).Email);
    }

    [Fact]
    public async Task Requesters_and_read_only_agents_cannot_use_admin_services()
    {
        var requester = await Host.CreateRequesterAsync("Rita Requester");
        var readOnly = await Host.CreateAgentAsync("Rob ReadOnly", RoleKeys.ReadOnly);
        await Assert.ThrowsAsync<ForbiddenException>(() => As(requester, sp => sp.GetRequiredService<PeopleService>().ListAsync(new PeopleQuery())));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(readOnly, sp => sp.GetRequiredService<RoleService>().ListAsync()));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(readOnly, sp => sp.GetRequiredService<AuditBrowser>().SearchAsync(new AuditQuery())));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(readOnly, sp => sp.GetRequiredService<AdminSettingsService>().GetOrganizationAsync()));
        await Assert.ThrowsAsync<ForbiddenException>(() => As(readOnly, sp => sp.GetRequiredService<PersonLifecycleService>().ForgetAsync(requester.Id, requester.Email!)));
    }

    [Fact]
    public async Task Role_in_use_needs_a_replacement_before_deletion()
    {
        var custom = await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var r = new Role { Name = "Night shift", Privileges = [Privileges.TicketsView] };
            db.Roles.Add(r);
            await db.SaveChangesAsync();
            return r;
        });
        var agent = await Host.CreateAgentAsync("Nia Night", RoleKeys.ReadOnly);
        await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            (await db.WorkspaceMemberships.FirstAsync(m => m.UserId == agent.Id)).RoleId = custom.Id;
            await db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<ValidationException>(() => As(_admin, sp => sp.GetRequiredService<RoleService>().DeleteAsync(custom.Id, null)));
        var readOnly = await RoleAsync(RoleKeys.ReadOnly);
        await As(_admin, sp => sp.GetRequiredService<RoleService>().DeleteAsync(custom.Id, readOnly.Id));

        var membership = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().WorkspaceMemberships.AsNoTracking().FirstAsync(m => m.UserId == agent.Id));
        Assert.Equal(readOnly.Id, membership.RoleId);
        await Assert.ThrowsAsync<ValidationException>(() => As(_admin, sp => sp.GetRequiredService<RoleService>().DeleteAsync(readOnly.Id, null)));
    }
}
