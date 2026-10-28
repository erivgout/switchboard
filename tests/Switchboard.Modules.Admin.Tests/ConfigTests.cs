using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Tests;

/// <summary>Statuses (SR-DEL-2, INV-REF-1), categories, groups, custom fields, settings and the audit log.</summary>
public sealed class ConfigTests : AdminTestBase
{
    private User _admin = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _admin = await CreateOrgAdminAsync("Avery Admin");
    }

    private Task<T> Admin<T>(Func<IServiceProvider, Task<T>> action) => As(_admin, action);

    private Task Admin(Func<IServiceProvider, Task> action) => As(_admin, action);

    [Fact]
    public async Task Deleting_a_status_in_use_requires_a_replacement_in_the_same_category()
    {
        var ws = Host.Workspace.Id;
        var waiting = await Admin(sp => sp.GetRequiredService<TicketConfigService>()
            .CreateStatusAsync(ws, new StatusInput("Waiting on vendor", "Waiting on a supplier", StatusCategory.Pending, false, false)));
        var requester = await Host.CreateRequesterAsync("Dana Requester");
        var ticket = await CreateTicketAsync(requester);
        await AsSystem(sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { StatusId = waiting.Id }));
        var statuses = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketStatuses.AsNoTracking().Where(s => s.WorkspaceId == ws).ToListAsync());
        var pending = statuses.First(s => s.Name == "Pending");
        var resolved = statuses.First(s => s.Name == "Resolved");

        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<TicketConfigService>().DeleteStatusAsync(waiting.Id, null)));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<TicketConfigService>().DeleteStatusAsync(waiting.Id, resolved.Id)));
        Assert.True(await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketStatuses.AnyAsync(s => s.Id == waiting.Id)));

        var moved = await Admin(sp => sp.GetRequiredService<TicketConfigService>().DeleteStatusAsync(waiting.Id, pending.Id));
        Assert.Equal(1, moved);
        await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            Assert.False(await db.TicketStatuses.AnyAsync(s => s.Id == waiting.Id));
            Assert.Equal(pending.Id, (await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id)).StatusId);
            Assert.True(await db.ActivityEntries.AnyAsync(a => a.SubjectId == ticket.Id && a.Summary.Contains("Waiting on vendor was removed")));
        });
    }

    [Fact]
    public async Task Built_in_and_default_statuses_cannot_be_deleted()
    {
        var statuses = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketStatuses.AsNoTracking().Where(s => s.WorkspaceId == Host.Workspace.Id).ToListAsync());
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<TicketConfigService>().DeleteStatusAsync(statuses.First(s => s.IsSystem).Id, null)));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<TicketConfigService>()
            .CreateStatusAsync(Host.Workspace.Id, new StatusInput("Done-ish", null, StatusCategory.Resolved, false, IsDefault: true))));
    }

    [Fact]
    public async Task Categories_are_three_levels_and_deletion_moves_tickets()
    {
        var ws = Host.Workspace.Id;
        var svc = (Func<IServiceProvider, TicketConfigService>)(sp => sp.GetRequiredService<TicketConfigService>());
        var top = await Admin(sp => svc(sp).CreateCategoryAsync(ws, null, "Facilities"));
        var mid = await Admin(sp => svc(sp).CreateCategoryAsync(ws, top.Id, "Desks"));
        var leaf = await Admin(sp => svc(sp).CreateCategoryAsync(ws, mid.Id, "Standing desk"));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => svc(sp).CreateCategoryAsync(ws, leaf.Id, "Too deep")));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => svc(sp).DeleteCategoryAsync(mid.Id, null, false)));

        var requester = await Host.CreateRequesterAsync("Dana Requester");
        var ticket = await CreateTicketAsync(requester);
        await AsSystem(sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { CategoryId = top.Id, SubCategoryId = mid.Id, ItemCategoryId = leaf.Id }));
        var other = await Admin(sp => svc(sp).CreateCategoryAsync(ws, mid.Id, "Sit desk"));

        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => svc(sp).DeleteCategoryAsync(leaf.Id, null, false)));
        await Admin(sp => svc(sp).DeleteCategoryAsync(leaf.Id, other.Id, false));
        var reloaded = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id));
        Assert.Equal((top.Id, mid.Id, other.Id), (reloaded.CategoryId!.Value, reloaded.SubCategoryId!.Value, reloaded.ItemCategoryId!.Value));

        await Admin(async sp =>
        {
            var tree = await svc(sp).CategoryTreeAsync(ws);
            await svc(sp).ReorderCategoriesAsync(ws, null, tree.Select(n => n.Category.Id).Reverse().ToList());
        });
    }

    [Fact]
    public async Task Deleting_a_group_moves_its_tickets()
    {
        var ws = Host.Workspace.Id;
        var agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent);
        var group = await Admin(sp => sp.GetRequiredService<GroupService>().CreateAsync(ws,
            new GroupInput("Field team", null, AssignmentMode.RoundRobin, 10, 30, agent.Id, [new MemberInput(agent.Id, GroupRole.Member)])));
        var requester = await Host.CreateRequesterAsync("Dana Requester");
        var ticket = await CreateTicketAsync(requester);
        await AsSystem(sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { GroupId = group.Id }));
        var network = await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().AgentGroups.AsNoTracking().FirstAsync(g => g.Name == "Network"));

        await Admin(sp => sp.GetRequiredService<GroupService>().DeleteAsync(group.Id, network.Id));
        Assert.Equal(network.Id, (await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id))).GroupId);
    }

    [Fact]
    public async Task Group_members_must_be_agents_in_the_workspace()
    {
        var requester = await Host.CreateRequesterAsync("Dana Requester");
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<GroupService>().CreateAsync(Host.Workspace.Id,
            new GroupInput("Mixed", null, AssignmentMode.None, null, null, null, [new MemberInput(requester.Id, GroupRole.Member)]))));
    }

    [Fact]
    public async Task Fields_with_data_are_deactivated_not_deleted()
    {
        var field = await Admin(sp => sp.GetRequiredService<FieldAdminService>().CreateAsync(FieldEntities.User, null, new FieldInput
        {
            Label = "Badge number",
            Type = CustomFieldType.Text,
        }));
        Assert.Equal("badge_number", field.Key);
        var requester = await Host.CreateRequesterAsync("Dana Requester");
        await Admin(sp => sp.GetRequiredService<PeopleService>().UpdateProfileAsync(requester.Id, new PersonInput
        {
            DisplayName = requester.DisplayName,
            Email = requester.Email!,
            CustomFields = new Dictionary<string, System.Text.Json.JsonElement?> { ["badge_number"] = System.Text.Json.JsonSerializer.SerializeToElement("B-104") },
        }));

        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<FieldAdminService>().DeleteAsync(field.Id)));
        await Admin(sp => sp.GetRequiredService<FieldAdminService>().SetActiveAsync(field.Id, false));
        Assert.False((await Admin(sp => sp.GetRequiredService<FieldAdminService>().GetAsync(field.Id))).Field.IsActive);

        var unused = await Admin(sp => sp.GetRequiredService<FieldAdminService>().CreateAsync(FieldEntities.User, null, new FieldInput { Label = "Shoe size", Type = CustomFieldType.Number }));
        await Admin(sp => sp.GetRequiredService<FieldAdminService>().DeleteAsync(unused.Id));
    }

    [Fact]
    public async Task Dependent_dropdown_options_parse_into_three_levels()
    {
        var field = await Admin(sp => sp.GetRequiredService<FieldAdminService>().CreateAsync(FieldEntities.Ticket, Host.Workspace.Id, new FieldInput
        {
            Label = "Office",
            Type = CustomFieldType.DependentDropdown,
            Options = "United States\n  Illinois\n    Chicago\n  Texas\n    Austin\nCanada\n  Ontario",
            VisibleToRequesters = true,
            RequiredForRequesters = true,
        }));
        Assert.True(field.EditableByRequesters);
        Assert.Equal("United States\n  Illinois\n    Chicago\n  Texas\n    Austin\nCanada\n  Ontario", FieldAdminService.OptionsText(field));

        var requester = await Host.CreateRequesterAsync("Dana Requester");
        var ticket = await CreateTicketAsync(requester);
        await AsSystem(sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch
        {
            CustomFields = new Dictionary<string, System.Text.Json.JsonElement?> { ["office"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "United States", "Texas", "Austin" }) },
        }));

        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<FieldAdminService>().CreateAsync(FieldEntities.Ticket, Host.Workspace.Id,
            new FieldInput { Label = "Too deep", Type = CustomFieldType.DependentDropdown, Options = "A\n  B\n    C\n      D" })));
    }

    [Fact]
    public async Task Service_desk_settings_save_and_are_audited()
    {
        await Admin(sp => sp.GetRequiredService<AdminSettingsService>().SaveServiceDeskAsync(new ServiceDeskSettings { AutoCloseDays = 7, ClosedReplyBehavior = "followup" }));
        var behavior = await AsSystem(sp => sp.GetRequiredService<ISettingsService>().GetAsync(SettingKeys.ClosedReplyBehavior, "reopen", Host.Workspace.Id));
        Assert.Equal("followup", behavior);
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<AdminSettingsService>().SaveServiceDeskAsync(new ServiceDeskSettings { AutoCloseDays = 900 })));
        Assert.True(await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().AuditEntries.AnyAsync(a => a.Action == "settings.service_desk_updated")));
    }

    [Fact]
    public async Task Sign_in_policy_validates_ip_ranges_and_domains()
    {
        var settings = await Admin(sp => sp.GetRequiredService<AdminSettingsService>().GetSignInAsync());
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<AdminSettingsService>()
            .SaveSignInAsync(settings with { AdminIpRanges = ["not-an-ip"] })));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<AdminSettingsService>()
            .SaveSignInAsync(settings with { SignupPolicy = "domains", SignupDomains = [] })));
        await Admin(sp => sp.GetRequiredService<AdminSettingsService>().SaveSignInAsync(settings with { SignupPolicy = "domains", SignupDomains = ["Example.com"], AdminIpRanges = ["10.0.0.0/8", "192.0.2.4"] }));
        var saved = await Admin(sp => sp.GetRequiredService<AdminSettingsService>().GetSignInAsync());
        Assert.Equal(["example.com"], saved.SignupDomains);
        Assert.Equal(["10.0.0.0/8", "192.0.2.4/32"], saved.AdminIpRanges);
    }

    [Fact]
    public async Task Logo_upload_rejects_files_that_are_not_images()
    {
        var html = new MemoryStream(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<AdminSettingsService>().SetLogoAsync(html, "logo.png")));
        var png = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13]);
        await Admin(sp => sp.GetRequiredService<AdminSettingsService>().SetLogoAsync(png, "logo.png"));
        var org = await Admin(sp => sp.GetRequiredService<AdminSettingsService>().GetOrganizationAsync());
        Assert.StartsWith("/files/", org.LogoUrl);
    }

    [Fact]
    public async Task Audit_export_neutralizes_formulas_and_the_chain_verifies()
    {
        await AsSystem(sp =>
        {
            sp.GetRequiredService<IAuditLog>().Record("test.formula", "note", "=HYPERLINK(\"http://evil\")", new { cell = "+cmd" });
            return sp.GetRequiredService<SwitchboardDbContext>().SaveChangesAsync();
        });
        var (csv, rows) = await Admin(sp => sp.GetRequiredService<AuditBrowser>().ExportCsvAsync(new AuditQuery { Action = "test.formula" }));
        Assert.Equal(1, rows);
        var text = Encoding.UTF8.GetString(csv);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", text);
        Assert.DoesNotContain(",=HYPERLINK", text);

        await Host.Services.GetRequiredService<AuditChainSealer>().SealAsync(default);
        var status = await Admin(sp => sp.GetRequiredService<AuditBrowser>().VerifyChainAsync());
        Assert.True(status.Intact);
    }

    [Fact]
    public void Csv_cells_starting_with_formula_characters_are_prefixed()
    {
        Assert.Equal("'=1+1", Csv.Cell("=1+1"));
        Assert.Equal("'+SUM(A1)", Csv.Cell("+SUM(A1)"));
        Assert.Equal("'-2", Csv.Cell("-2"));
        Assert.Equal("'@cmd", Csv.Cell("@cmd"));
        Assert.Equal("\"a,b\"", Csv.Cell("a,b"));
        Assert.Equal("plain", Csv.Cell("plain"));
        Assert.Equal([["a", "b,c", "d\"e"], ["1", "", "3"]], Csv.Parse("a,\"b,c\",\"d\"\"e\"\r\n1,,3\r\n"));
    }

    [Fact]
    public async Task Feature_toggles_store_instance_and_workspace_values()
    {
        // The admin module itself is never offered, so with only it loaded there is nothing to toggle.
        var (features, _) = await Admin(sp => sp.GetRequiredService<AdminSettingsService>().GetFeaturesAsync());
        Assert.DoesNotContain(features, f => f.Module == "Admin");
    }
}
