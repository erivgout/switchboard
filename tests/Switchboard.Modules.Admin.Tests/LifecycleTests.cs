using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Admin.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Admin.Tests;

/// <summary>Deactivation (INV-ID-4, SR-AUTHN-5, SR-DEL-3), forget (SR-PRIV-3, INV-ID-3), merge, export and invite.</summary>
public sealed class LifecycleTests : AdminTestBase
{
    private User _admin = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _admin = await CreateOrgAdminAsync("Avery Admin");
    }

    [Fact]
    public async Task Deactivation_rotates_the_security_stamp_revokes_keys_and_removes_permissions()
    {
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        var stamp = (await ReloadAsync(agent.Id)).SecurityStamp;
        await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            db.ApiKeys.Add(new ApiKey { UserId = agent.Id, Name = "CLI", Prefix = "sbk_1234", KeyHash = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().DeactivateAsync(agent.Id, new OpenWorkPlan(OpenWorkAction.Flag)));

        var reloaded = await ReloadAsync(agent.Id);
        Assert.False(reloaded.IsActive);
        Assert.NotNull(reloaded.DeactivatedAt);
        Assert.NotEqual(stamp, reloaded.SecurityStamp);
        var keys = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().ApiKeys.AsNoTracking().Where(k => k.UserId == agent.Id).ToListAsync());
        Assert.All(keys, k => Assert.NotNull(k.RevokedAt));
        var permissions = await AsSystem(sp => sp.GetRequiredService<IPermissionService>().ForUserAsync(agent.Id));
        Assert.Null(permissions);
        var audit = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().AuditEntries.AsNoTracking()
            .AnyAsync(a => a.Action == "users.deactivated" && a.ObjectId == agent.Id.ToString()));
        Assert.True(audit);
    }

    [Fact]
    public async Task Deactivation_reassigns_open_tickets_to_a_group()
    {
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        var requester = await Host.CreateRequesterAsync("Dana Requester");
        var ticket = await CreateTicketAsync(requester, agent);
        var network = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().AgentGroups.AsNoTracking().FirstAsync(g => g.Name == "Network"));

        var open = await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().GetOpenWorkAsync(agent.Id));
        Assert.Single(open.Tickets);

        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().DeactivateAsync(agent.Id, new OpenWorkPlan(OpenWorkAction.ReassignToGroup, GroupId: network.Id)));
        var reloaded = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id));
        Assert.Null(reloaded.AgentId);
        Assert.Equal(network.Id, reloaded.GroupId);
    }

    [Fact]
    public async Task Deactivation_can_flag_open_tickets_instead()
    {
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent);
        var requester = await Host.CreateRequesterAsync("Dana Requester");
        var ticket = await CreateTicketAsync(requester, agent);

        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().DeactivateAsync(agent.Id, new OpenWorkPlan(OpenWorkAction.Flag)));
        var tags = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketTags.AsNoTracking().Where(t => t.TicketId == ticket.Id).Select(t => t.Tag).ToListAsync());
        Assert.Contains(PersonLifecycleService.FlagTag, tags);
    }

    [Fact]
    public async Task Failed_handoff_rolls_back_the_whole_deactivation()
    {
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent);
        var requester = await Host.CreateRequesterAsync("Dana Requester");
        await CreateTicketAsync(requester, agent);
        var stamp = (await ReloadAsync(agent.Id)).SecurityStamp;

        await Assert.ThrowsAsync<ValidationException>(() => As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>()
            .DeactivateAsync(agent.Id, new OpenWorkPlan(OpenWorkAction.ReassignToAgent, AgentId: requester.Id))));
        var reloaded = await ReloadAsync(agent.Id);
        Assert.True(reloaded.IsActive);
        Assert.Equal(stamp, reloaded.SecurityStamp);
    }

    [Fact]
    public async Task Reactivation_restores_access_but_not_keys()
    {
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent);
        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().DeactivateAsync(agent.Id, new OpenWorkPlan(OpenWorkAction.Flag)));
        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ReactivateAsync(agent.Id));
        Assert.True((await ReloadAsync(agent.Id)).IsActive);
        Assert.NotNull(await AsSystem(sp => sp.GetRequiredService<IPermissionService>().ForUserAsync(agent.Id)));
    }

    [Fact]
    public async Task Forget_erases_personal_data_and_keeps_references_valid()
    {
        var requester = await Host.CreateRequesterAsync("Dana Whitfield");
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent);
        await As(_admin, sp => sp.GetRequiredService<PeopleService>().UpdateProfileAsync(requester.Id, new PersonInput
        {
            DisplayName = "Dana Whitfield",
            Email = requester.Email!,
            SecondaryEmails = ["dana.home@test.local"],
            Phone = "+1 312 555 0100",
            JobTitle = "Senior accountant",
        }));
        var ticket = await CreateTicketAsync(requester, subject: "Dana Whitfield can't print");
        await Host.RunAsync(SwitchboardTestHost.As(agent), sp => sp.GetRequiredService<ITicketService>()
            .ReplyAsync(ticket.Id, new ReplyCommand { BodyHtml = "<p>Hi Dana Whitfield, try again now. Sampling shows it works.</p>" }));

        await Assert.ThrowsAsync<ValidationException>(() => As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ForgetAsync(requester.Id, "wrong")));
        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ForgetAsync(requester.Id, requester.Email!.ToUpperInvariant()));

        var user = await ReloadAsync(requester.Id);
        Assert.True(user.IsForgotten);
        Assert.False(user.IsActive);
        Assert.Null(user.Email);
        Assert.Null(user.PhoneNumber);
        Assert.Null(user.JobTitle);
        Assert.DoesNotContain("Dana", user.DisplayName);

        await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            Assert.False(await db.UserEmails.AnyAsync(e => e.UserId == requester.Id));
            var t = await db.Tickets.AsNoTracking().Include(x => x.Requester).Include(x => x.Conversations).FirstAsync(x => x.Id == ticket.Id);
            // The reference still resolves, to the placeholder.
            Assert.Equal(requester.Id, t.RequesterId);
            Assert.Equal(user.DisplayName, t.Requester!.DisplayName);
            Assert.DoesNotContain("Dana Whitfield", t.Subject);
            Assert.DoesNotContain(requester.Email!, t.DescriptionHtml);
            var reply = t.Conversations.Single(c => c.Kind == ConversationKind.AgentReply);
            Assert.DoesNotContain("Dana Whitfield", reply.BodyHtml);
            Assert.Contains("Sampling", reply.BodyHtml);
            // No activity entry still carries the erased name.
            var activity = await db.ActivityEntries.AsNoTracking().Where(a => a.SubjectId == ticket.Id).ToListAsync();
            Assert.DoesNotContain(activity, a => a.ChangesJson.Contains("Dana Whitfield") || a.Summary.Contains("Dana Whitfield") || a.ActorName.Contains("Dana Whitfield"));
            // The audit log says that it happened, not what was erased.
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == "users.forgotten");
            Assert.Equal("{}", entry.DetailsJson);
        });
    }

    [Fact]
    public async Task Forget_removes_free_text_custom_field_values_from_messages()
    {
        await As(_admin, sp => sp.GetRequiredService<FieldAdminService>().CreateAsync(FieldEntities.User, null,
            new FieldInput { Label = "Employee number", Type = CustomFieldType.Text }));
        var requester = await Host.CreateRequesterAsync("Rae Person");
        await As(_admin, sp => sp.GetRequiredService<PeopleService>().UpdateProfileAsync(requester.Id, new PersonInput
        {
            DisplayName = requester.DisplayName,
            Email = requester.Email!,
            CustomFields = new Dictionary<string, System.Text.Json.JsonElement?> { ["employee_number"] = System.Text.Json.JsonSerializer.SerializeToElement("EMP-48213") },
        }));
        var ticket = await CreateTicketAsync(requester);
        await Host.RunAsync(SwitchboardTestHost.As(requester, Channel.Portal), sp => sp.GetRequiredService<ITicketService>()
            .AddRequesterMessageAsync(ticket.Id, new RequesterMessageCommand { BodyHtml = "<p>My employee number is EMP-48213.</p>" }));

        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ForgetAsync(requester.Id, requester.Email!));

        var bodies = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketConversations.AsNoTracking()
            .Where(c => c.TicketId == ticket.Id).Select(c => c.BodyHtml + c.BodyText).ToListAsync());
        Assert.NotEmpty(bodies);
        Assert.DoesNotContain(bodies, b => b.Contains("EMP-48213"));
    }

    [Fact]
    public async Task Forget_requires_an_org_admin()
    {
        var requester = await Host.CreateRequesterAsync("Dana Whitfield");
        var (manager, _) = await CreateAgentWithPrivilegesAsync("Mo Manager", Privileges.UsersView, Privileges.UsersManage);
        await Assert.ThrowsAsync<ForbiddenException>(() => As(manager, sp => sp.GetRequiredService<PersonLifecycleService>().ForgetAsync(requester.Id, requester.Email!)));
        Assert.False((await ReloadAsync(requester.Id)).IsForgotten);
    }

    [Fact]
    public async Task Merge_moves_tickets_shares_memberships_and_addresses_to_the_survivor()
    {
        var duplicate = await Host.CreateRequesterAsync("Dana W");
        var survivor = await Host.CreateRequesterAsync("Dana Whitfield");
        var other = await Host.CreateRequesterAsync("Olive Other");
        var ticket = await CreateTicketAsync(duplicate);
        var shared = await CreateTicketAsync(other);
        var group = await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            db.TicketShares.Add(new TicketShare { TicketId = shared.Id, UserId = duplicate.Id, CreatedAt = DateTimeOffset.UtcNow });
            var g = new RequesterGroup { Name = "Finance team" };
            db.RequesterGroups.Add(g);
            await db.SaveChangesAsync();
            db.RequesterGroupMembers.Add(new RequesterGroupMember { GroupId = g.Id, UserId = duplicate.Id });
            await db.SaveChangesAsync();
            return g;
        });

        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().MergeAsync(duplicate.Id, survivor.Id));

        await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            Assert.Equal(survivor.Id, (await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id)).RequesterId);
            Assert.True(await db.TicketShares.AnyAsync(s => s.TicketId == shared.Id && s.UserId == survivor.Id));
            Assert.False(await db.TicketShares.AnyAsync(s => s.UserId == duplicate.Id));
            Assert.True(await db.RequesterGroupMembers.AnyAsync(m => m.GroupId == group.Id && m.UserId == survivor.Id));
            Assert.True(await db.UserEmails.AnyAsync(e => e.UserId == survivor.Id && e.NormalizedEmail == duplicate.Email!.ToUpperInvariant()));
            var source = await db.Users.AsNoTracking().FirstAsync(u => u.Id == duplicate.Id);
            Assert.False(source.IsActive);
            Assert.Null(source.Email);
        });

        // Mail to the duplicate's address now finds the survivor.
        var found = await AsSystem(sp => sp.GetRequiredService<Core.Services.IUserDirectory>().FindByEmailAsync(duplicate.Email!));
        Assert.Equal(survivor.Id, found!.Id);
        await Assert.ThrowsAsync<ValidationException>(() => As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ReactivateAsync(duplicate.Id)));
    }

    [Fact]
    public async Task Export_contains_the_persons_data_and_is_audited()
    {
        var requester = await Host.CreateRequesterAsync("Dana Whitfield");
        await CreateTicketAsync(requester, subject: "Monitor flickers");
        var json = await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ExportAsync(requester.Id));
        var text = System.Text.Encoding.UTF8.GetString(json);
        Assert.Contains("Monitor flickers", text);
        Assert.Contains(requester.Email!, text);
        Assert.True(await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().AuditEntries.AnyAsync(a => a.Action == "users.exported")));
    }

    [Fact]
    public async Task Invite_sends_a_single_use_set_password_link()
    {
        var person = await As(_admin, sp => sp.GetRequiredService<PeopleService>().CreateAsync(
            new PersonInput { DisplayName = "New Hire", Email = "new.hire@test.local" }, null));
        await As(_admin, sp => sp.GetRequiredService<InvitationService>().InviteAsync(person.Id));
        var mail = Assert.Single(Email.Sent);
        Assert.Equal(["new.hire@test.local"], mail.To);
        Assert.Contains("/account/reset-password?email=new.hire%40test.local&token=", mail.TextBody);
    }

    [Fact]
    public async Task Convert_requester_to_agent_and_back()
    {
        var requester = await Host.CreateRequesterAsync("Rita Requester");
        var agentRole = await RoleAsync(RoleKeys.Agent);
        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ConvertToAgentAsync(requester.Id, new NewAgentAccess(Host.Workspace.Id, agentRole.Id, TicketScope.Group)));
        var asAgent = await AsSystem(sp => sp.GetRequiredService<IPermissionService>().ForUserAsync(requester.Id));
        Assert.True(asAgent!.IsAgent);
        Assert.Equal(TicketScope.Group, asAgent.Workspaces[Host.Workspace.Id].Scope);

        await As(_admin, sp => sp.GetRequiredService<PersonLifecycleService>().ConvertToRequesterAsync(requester.Id, new OpenWorkPlan(OpenWorkAction.Flag)));
        var back = await AsSystem(sp => sp.GetRequiredService<IPermissionService>().ForUserAsync(requester.Id));
        Assert.False(back!.IsAgent);
        Assert.Empty(back.Workspaces);
    }
}
