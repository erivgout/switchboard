using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Catalog.Domain;
using Switchboard.Modules.Catalog.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Catalog.Tests;

/// <summary>Core plus approvals and catalog on one database per test class, with a small org and a few items.</summary>
public sealed class CatalogFixture : IAsyncLifetime
{
    public SwitchboardTestHost Host { get; private set; } = default!;
    public User Admin { get; private set; } = default!;
    public User Agent { get; private set; } = default!;
    public User Requester { get; private set; } = default!;
    public User Manager { get; private set; } = default!;
    public User Outsider { get; private set; } = default!;
    public Guid FinanceGroupId { get; private set; }
    public Guid HardwareGroupId { get; private set; }
    public Guid CategoryId { get; private set; }

    /// <summary>No approval; delivered by Hardware; two questions.</summary>
    public Guid MonitorId { get; private set; }
    /// <summary>Approved by the requester's manager.</summary>
    public Guid LaptopId { get; private set; }
    /// <summary>Only the Finance requester group may see it.</summary>
    public Guid FinanceOnlyId { get; private set; }

    public async Task InitializeAsync()
    {
        Host = await SwitchboardTestHost.CreateAsync(new ApprovalsModule(), new CatalogModule());
        Admin = await Host.CreateAgentAsync("Avery Admin", RoleKeys.WorkspaceAdmin, TicketScope.Global);
        Agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        Requester = await Host.CreateRequesterAsync("Dana Requester");
        Manager = await Host.CreateRequesterAsync("Grace Manager");
        Outsider = await Host.CreateRequesterAsync("Olive Outsider");

        (FinanceGroupId, HardwareGroupId) = await Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var finance = new RequesterGroup { Name = "Finance team" };
            finance.Members.Add(new RequesterGroupMember { GroupId = finance.Id, UserId = Requester.Id });
            db.RequesterGroups.Add(finance);
            (await db.Users.FirstAsync(u => u.Id == Requester.Id)).ManagerId = Manager.Id;
            await db.SaveChangesAsync();
            return (finance.Id, await db.AgentGroups.Where(g => g.Name == "Hardware").Select(g => g.Id).FirstAsync());
        });

        await AsAdminAsync(async admin =>
        {
            CategoryId = (await admin.SaveCategoryAsync(null, "Devices", null, "bi-laptop")).Id;

            MonitorId = await PublishedAsync(admin, "Monitor", new ItemDetailsInput { MaxQuantity = 2, CostMinor = 24900, FulfillmentGroupId = HardwareGroupId });
            await admin.AddFieldAsync(MonitorId, new FieldInput { Label = "Size", FieldType = CatFieldType.Dropdown, Required = true, Options = ["24-inch", "27-inch"] });
            await admin.AddFieldAsync(MonitorId, new FieldInput { Label = "Notes", FieldType = CatFieldType.Paragraph });

            LaptopId = await PublishedAsync(admin, "Laptop", new ItemDetailsInput { CostMinor = 129900, FulfillmentGroupId = HardwareGroupId, AllowRequestedFor = true });
            await admin.AddFieldAsync(LaptopId, new FieldInput { Label = "Operating system", FieldType = CatFieldType.Dropdown, Required = true, Options = ["Windows", "macOS"] });
            await admin.UpdateApprovalAsync(LaptopId, new ApprovalConfig
            {
                Levels = [new ApprovalLevelConfig { Rule = ApprovalRule.Any, Sources = [new ApproverSource { Kind = ApproverSourceKind.Manager, ManagerLevel = 1 }] }],
            });

            FinanceOnlyId = await PublishedAsync(admin, "Budget tool", new ItemDetailsInput());
            await admin.UpdateAudienceAsync(FinanceOnlyId, [FinanceGroupId], []);
        });
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    public Task AsAdminAsync(Func<ICatalogAdmin, Task> action) =>
        Host.RunAsync(SwitchboardTestHost.As(Admin), sp => action(sp.GetRequiredService<ICatalogAdmin>()));

    /// <summary>Creates and publishes an item in the Devices category.</summary>
    public async Task<Guid> PublishedAsync(ICatalogAdmin admin, string name, ItemDetailsInput details)
    {
        var item = await admin.CreateItemAsync(name, CategoryId);
        await admin.UpdateItemAsync(item.Id, details with { Name = name, ShortDescription = $"A {name.ToLowerInvariant()}.", CategoryIds = [CategoryId] });
        await admin.SetStateAsync(item.Id, CatItemState.Published);
        return item.Id;
    }

    public static Dictionary<string, IReadOnlyList<string>> Answers(params (string Key, string Value)[] values) =>
        values.GroupBy(v => v.Key).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(v => v.Value).ToList());

    public Task<SubmitResult> SubmitAsync(User user, Guid itemId, Dictionary<string, IReadOnlyList<string>> answers, int quantity = 1, Guid? requestedFor = null, Guid? requesterId = null) =>
        Host.RunAsync(SwitchboardTestHost.As(user, user.IsAgent ? Channel.Web : Channel.Portal), sp =>
            sp.GetRequiredService<ICatalogRequestService>().SubmitAsync(new SubmitRequestCommand
            {
                ItemId = itemId,
                Quantity = quantity,
                Answers = answers,
                RequestedForId = requestedFor,
                RequesterId = requesterId,
            }));

    public Task<Ticket> TicketAsync(Guid id) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.AsNoTracking().Include(t => t.Status).FirstAsync(t => t.Id == id));

    public Task<List<CatRequestedItem>> RowsAsync(Guid ticketId) =>
        Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Set<CatRequestedItem>().AsNoTracking().Where(r => r.TicketId == ticketId).ToListAsync());
}
