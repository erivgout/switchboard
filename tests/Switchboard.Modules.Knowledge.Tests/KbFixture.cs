using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Knowledge.Domain;
using Switchboard.Modules.Knowledge.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Knowledge.Tests;

/// <summary>
/// One throwaway database per test class: people, groups and a folder for every visibility.
/// Tests create their own articles so they don't depend on each other.
/// </summary>
public sealed class KbFixture : IAsyncLifetime
{
    public SwitchboardTestHost Host { get; private set; } = default!;

    public User Manager { get; private set; } = default!;
    public User Supervisor { get; private set; } = default!;
    public User OtherSupervisor { get; private set; } = default!;
    public User Agent { get; private set; } = default!;
    public User HardwareAgent { get; private set; } = default!;
    public User Requester { get; private set; } = default!;
    public User GroupRequester { get; private set; } = default!;
    public User FinanceRequester { get; private set; } = default!;

    public Guid Everyone { get; private set; }
    public Guid SignedIn { get; private set; }
    public Guid AgentsOnly { get; private set; }
    public Guid ForRequesterGroup { get; private set; }
    public Guid ForFinance { get; private set; }
    public Guid ForHardware { get; private set; }
    public Guid NeedsApproval { get; private set; }
    public Guid SubFolder { get; private set; }

    public async Task InitializeAsync()
    {
        Host = await SwitchboardTestHost.CreateAsync(new KnowledgeModule());
        Manager = await Host.CreateAgentAsync("Mona Manager", RoleKeys.WorkspaceAdmin);
        Supervisor = await Host.CreateAgentAsync("Priya Supervisor", RoleKeys.Supervisor, TicketScope.Global, "Service Desk");
        OtherSupervisor = await Host.CreateAgentAsync("Otto Supervisor", RoleKeys.Supervisor, TicketScope.Global, "Network");
        Agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        HardwareAgent = await Host.CreateAgentAsync("Jordan Hardware", RoleKeys.Agent, TicketScope.Group, "Hardware");
        Requester = await Host.CreateRequesterAsync("Rita Requester");
        GroupRequester = await Host.CreateRequesterAsync("Cory Contractor");
        FinanceRequester = await Host.CreateRequesterAsync("Dana Finance");

        var (contractors, finance, hardware) = await Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var group = new RequesterGroup { Name = "Contractors" };
            var department = new Department { Name = "Finance" };
            db.RequesterGroups.Add(group);
            db.Departments.Add(department);
            db.RequesterGroupMembers.Add(new RequesterGroupMember { GroupId = group.Id, UserId = GroupRequester.Id });
            var dana = await db.Users.FirstAsync(u => u.Id == FinanceRequester.Id);
            dana.DepartmentId = department.Id;
            await db.SaveChangesAsync();
            var hw = await db.AgentGroups.FirstAsync(g => g.Name == "Hardware");
            return (group.Id, department.Id, hw.Id);
        });

        await AsAsync(Manager, async sp =>
        {
            var s = sp.GetRequiredService<IKbStructureService>();
            var category = await s.CreateCategoryAsync(Host.Workspace.Id, "IT help", null);
            Task<KbFolder> Folder(string name, KbFolderVisibility v, Guid[]? rg = null, Guid[]? dep = null, Guid[]? ag = null, bool approval = false, Guid[]? approvers = null, Guid? parent = null) =>
                s.CreateFolderAsync(new KbFolderInput
                {
                    CategoryId = category.Id,
                    ParentId = parent,
                    Name = name,
                    Visibility = v,
                    RequesterGroupIds = rg ?? [],
                    DepartmentIds = dep ?? [],
                    AgentGroupIds = ag ?? [],
                    RequiresApproval = approval,
                    ApproverIds = approvers ?? [],
                });
            Everyone = (await Folder("Everyone", KbFolderVisibility.Everyone)).Id;
            SignedIn = (await Folder("Signed in", KbFolderVisibility.SignedIn)).Id;
            AgentsOnly = (await Folder("Runbooks", KbFolderVisibility.AgentsOnly)).Id;
            ForRequesterGroup = (await Folder("Contractors", KbFolderVisibility.Restricted, rg: [contractors])).Id;
            ForFinance = (await Folder("Finance", KbFolderVisibility.Restricted, dep: [finance])).Id;
            ForHardware = (await Folder("Hardware team", KbFolderVisibility.Restricted, ag: [hardware])).Id;
            NeedsApproval = (await Folder("Policies", KbFolderVisibility.SignedIn, approval: true, approvers: [Supervisor.Id])).Id;
            SubFolder = (await Folder("Runbooks / Deep", KbFolderVisibility.Everyone, parent: AgentsOnly)).Id;
        });
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();

    public Task<T> AsAsync<T>(User user, Func<IServiceProvider, Task<T>> action) =>
        Host.RunAsync(SwitchboardTestHost.As(user, user.IsAgent ? Channel.Web : Channel.Portal), action);

    public Task AsAsync(User user, Func<IServiceProvider, Task> action) =>
        Host.RunAsync(SwitchboardTestHost.As(user, user.IsAgent ? Channel.Web : Channel.Portal), action);

    public Task<T> AnonymousAsync<T>(Func<IServiceProvider, Task<T>> action) => Host.RunAsync(Actor.Anonymous, action);

    /// <summary>Creates an article in a folder; publishes it unless <paramref name="publish"/> is false.</summary>
    public async Task<Guid> ArticleAsync(Guid folder, string title, string body = "<p>Steps that fix it.</p>", bool publish = true)
    {
        var author = folder == ForHardware ? HardwareAgent : Agent;
        var id = await AsAsync(author, sp => sp.GetRequiredService<IKnowledgeService>()
            .CreateDraftAsync(new KbDraftInput { FolderId = folder, Title = title, BodyHtml = body }));
        if (publish)
        {
            var publisher = folder == ForHardware ? Manager : Supervisor;
            await AsAsync(publisher, sp => sp.GetRequiredService<IKnowledgeService>().PublishAsync(id.ArticleId));
        }
        return id.ArticleId;
    }

    public Task SetPublicAsync(bool on) =>
        Host.RunAsSystemAsync(async sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            await settings.SetAsync(KbSettingKeys.Public, on);
            await sp.GetRequiredService<SwitchboardDbContext>().SaveChangesAsync();
            return true;
        });

    public Task<KbSearchPage> SearchAsync(User? user, string text, KbSearchMode mode = KbSearchMode.Published)
    {
        var query = new KbSearchQuery { Text = text, Mode = mode, PageSize = 100 };
        return user is null
            ? AnonymousAsync(sp => sp.GetRequiredService<IKnowledgeService>().SearchAsync(query))
            : AsAsync(user, sp => sp.GetRequiredService<IKnowledgeService>().SearchAsync(query));
    }
}
