using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Admin.Services;

namespace Switchboard.Modules.Admin.Tests;

/// <summary>Unique addresses (INV-ID-2), manager cycles, departments, locations and the CSV import (FR-ADM-7).</summary>
public sealed class DirectoryTests : AdminTestBase
{
    private User _admin = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _admin = await CreateOrgAdminAsync("Avery Admin");
    }

    private Task<T> Admin<T>(Func<IServiceProvider, Task<T>> action) => As(_admin, action);

    private Task Admin(Func<IServiceProvider, Task> action) => As(_admin, action);

    private Task<User> CreatePersonAsync(string name, string email, params string[] others) =>
        Admin(sp => sp.GetRequiredService<PeopleService>().CreateAsync(new PersonInput { DisplayName = name, Email = email, SecondaryEmails = others }, null));

    [Fact]
    public async Task Addresses_are_unique_across_primary_and_secondary_emails()
    {
        await CreatePersonAsync("Dana Whitfield", "dana@test.local", "dana.home@test.local");

        var primaryClash = await Assert.ThrowsAsync<ValidationException>(() => CreatePersonAsync("Imposter", "DANA.HOME@test.local"));
        Assert.Contains("already used by Dana Whitfield", primaryClash.Message);
        await Assert.ThrowsAsync<ValidationException>(() => CreatePersonAsync("Imposter", "imposter@test.local", "Dana@Test.Local"));
        await Assert.ThrowsAsync<ValidationException>(() => CreatePersonAsync("Twice", "twice@test.local", "twice@test.local"));

        var marcus = await CreatePersonAsync("Marcus Chen", "marcus@test.local");
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<PeopleService>().UpdateProfileAsync(marcus.Id,
            new PersonInput { DisplayName = "Marcus Chen", Email = "marcus@test.local", SecondaryEmails = ["dana.home@test.local"] })));
    }

    [Fact]
    public async Task A_person_can_swap_their_primary_and_secondary_addresses()
    {
        var dana = await CreatePersonAsync("Dana Whitfield", "dana@test.local", "dana.home@test.local");
        await Admin(sp => sp.GetRequiredService<PeopleService>().UpdateProfileAsync(dana.Id,
            new PersonInput { DisplayName = "Dana Whitfield", Email = "dana.home@test.local", SecondaryEmails = ["dana@test.local"] }));
        var detail = await Admin(sp => sp.GetRequiredService<PeopleService>().GetAsync(dana.Id));
        Assert.Equal("dana.home@test.local", detail.User.Email);
        Assert.Equal(["dana@test.local"], detail.SecondaryEmails);
    }

    [Fact]
    public async Task Manager_chains_cannot_loop()
    {
        var a = await CreatePersonAsync("Ana Top", "ana@test.local");
        var b = await CreatePersonAsync("Ben Middle", "ben@test.local");
        var c = await CreatePersonAsync("Cai Bottom", "cai@test.local");
        var people = (Func<IServiceProvider, PeopleService>)(sp => sp.GetRequiredService<PeopleService>());
        await Admin(sp => people(sp).UpdateProfileAsync(b.Id, new PersonInput { DisplayName = b.DisplayName, Email = b.Email!, ManagerId = a.Id }));
        await Admin(sp => people(sp).UpdateProfileAsync(c.Id, new PersonInput { DisplayName = c.DisplayName, Email = c.Email!, ManagerId = b.Id }));

        var loop = await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => people(sp).UpdateProfileAsync(a.Id, new PersonInput { DisplayName = a.DisplayName, Email = a.Email!, ManagerId = c.Id })));
        Assert.Contains("already reports to", loop.Message);
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => people(sp).UpdateProfileAsync(a.Id, new PersonInput { DisplayName = a.DisplayName, Email = a.Email!, ManagerId = a.Id })));
    }

    [Fact]
    public async Task Locations_cannot_be_moved_inside_themselves()
    {
        var org = (Func<IServiceProvider, OrgStructureService>)(sp => sp.GetRequiredService<OrgStructureService>());
        var emea = await Admin(sp => org(sp).SaveLocationAsync(null, new LocationInput { Name = "EMEA" }));
        var london = await Admin(sp => org(sp).SaveLocationAsync(null, new LocationInput { Name = "London", ParentId = emea.Id }));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => org(sp).SaveLocationAsync(emea.Id, new LocationInput { Name = "EMEA", ParentId = london.Id })));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => org(sp).DeleteLocationAsync(emea.Id, null)));
    }

    [Fact]
    public async Task Department_domains_belong_to_one_department()
    {
        var org = (Func<IServiceProvider, OrgStructureService>)(sp => sp.GetRequiredService<OrgStructureService>());
        await Admin(sp => org(sp).SaveDepartmentAsync(null, new DepartmentInput("Finance", null, null, null, ["finance.example.com"], null)));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => org(sp).SaveDepartmentAsync(null, new DepartmentInput("Accounts", null, null, null, ["@Finance.Example.com"], null))));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => org(sp).SaveDepartmentAsync(null, new DepartmentInput("Bad", null, null, null, ["not a domain"], null))));
    }

    [Fact]
    public async Task Csv_import_validates_previews_and_reports_errors()
    {
        await Admin(sp => sp.GetRequiredService<OrgStructureService>().SaveDepartmentAsync(null, new DepartmentInput("Finance", null, null, null, [], null)));
        await CreatePersonAsync("Existing Person", "existing@test.local");
        var csv = string.Join("\r\n",
            "Full name,E-mail,Department,Manager,VIP",
            "Grace Kim,grace@test.local,Finance,,no",
            "Hank Lee,hank@test.local,,grace@test.local,yes",
            "Bad Email,not-an-email,,,",
            "Grace Again,GRACE@test.local,,,",
            "Nobody Dept,nodept@test.local,Marketing,,",
            "=HYPERLINK(\"x\"),formula@test.local,,,maybe",
            "Existing Person,existing@test.local,,,",
            "Ivy Ng,ivy@test.local,,boss@test.local,");

        var import = await Admin(sp => sp.GetRequiredService<RequesterImportService>().UploadAsync("people.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv))));
        var (_, headers, mapping) = await Admin(sp => sp.GetRequiredService<RequesterImportService>().GetAsync(import.Id));
        Assert.Equal(5, headers.Length);
        Assert.Equal("Full name", mapping["name"]);
        Assert.Equal("E-mail", mapping["email"]);
        Assert.Equal("Manager", mapping["manager_email"]);

        var preview = await Admin(sp => sp.GetRequiredService<RequesterImportService>().PreviewAsync(import.Id));
        Assert.Equal(8, preview.Total);
        Assert.Equal(2, preview.Create);
        Assert.Equal(1, preview.Skip);
        Assert.Equal(5, preview.Errors);
        Assert.False(await AsSystem(sp => sp.GetRequiredService<SwitchboardDbContext>().Users.AnyAsync(u => u.NormalizedEmail == "GRACE@TEST.LOCAL")));

        var result = await Admin(sp => sp.GetRequiredService<RequesterImportService>().RunAsync(import.Id));
        Assert.Equal(2, result.Create);
        await AsSystem(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var grace = await db.Users.AsNoTracking().FirstAsync(u => u.NormalizedEmail == "GRACE@TEST.LOCAL");
            var hank = await db.Users.AsNoTracking().FirstAsync(u => u.NormalizedEmail == "HANK@TEST.LOCAL");
            Assert.Equal(grace.Id, hank.ManagerId);
            Assert.True(hank.IsVip);
            Assert.False(grace.IsAgent);
            Assert.NotNull(grace.DepartmentId);
        });

        var report = Encoding.UTF8.GetString(await Admin(sp => sp.GetRequiredService<RequesterImportService>().ErrorReportAsync(import.Id)));
        Assert.Contains("not-an-email isn't a valid email address", report);
        Assert.Contains("is already on row 2", report);
        Assert.Contains("No department is called Marketing", report);
        Assert.Contains("'=HYPERLINK", report);
        Assert.Contains("boss@test.local", report);

        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<RequesterImportService>().RunAsync(import.Id)));
    }

    [Fact]
    public async Task Csv_import_requires_name_and_email_columns()
    {
        var import = await Admin(sp => sp.GetRequiredService<RequesterImportService>().UploadAsync("x.csv", new MemoryStream(Encoding.UTF8.GetBytes("Phone,City\n1,Chicago\n"))));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<RequesterImportService>()
            .SaveMappingAsync(import.Id, new Dictionary<string, string?> { ["phone"] = "Phone" }, false)));
        await Assert.ThrowsAsync<ValidationException>(() => Admin(sp => sp.GetRequiredService<RequesterImportService>()
            .UploadAsync("x.exe", new MemoryStream(Encoding.UTF8.GetBytes("a,b\n1,2\n")))));
    }

    [Fact]
    public async Task People_list_filters_by_kind_status_and_search()
    {
        await CreatePersonAsync("Zed Requester", "zed@test.local");
        var agents = await Admin(sp => sp.GetRequiredService<PeopleService>().ListAsync(new PeopleQuery { Kind = "agents" }));
        Assert.Contains(agents.Items, p => p.Id == _admin.Id);
        var found = await Admin(sp => sp.GetRequiredService<PeopleService>().ListAsync(new PeopleQuery { Kind = "requesters", Search = "ZED@" }));
        Assert.Equal("Zed Requester", Assert.Single(found.Items).Name);
    }
}
