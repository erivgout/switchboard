using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;
using Switchboard.Modules.Catalog.Domain;
using Switchboard.Modules.Catalog.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.Catalog.Tests;

public sealed class CatalogRequestTests(CatalogFixture f) : IClassFixture<CatalogFixture>
{
    [Fact]
    public async Task Requesters_only_see_items_their_group_or_department_may_request()
    {
        async Task<List<Guid>> Browse(User user) => (await f.Host.RunAsync(SwitchboardTestHost.As(user, Channel.Portal),
            sp => sp.GetRequiredService<ICatalogService>().BrowseAsync(null, null, forAgent: false))).Select(i => i.Id).ToList();

        Assert.Contains(f.FinanceOnlyId, await Browse(f.Requester));
        Assert.DoesNotContain(f.FinanceOnlyId, await Browse(f.Outsider));
        Assert.Contains(f.MonitorId, await Browse(f.Outsider));

        // Hidden items can't be opened or requested either; they look like they don't exist.
        await Assert.ThrowsAsync<NotFoundException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Outsider, Channel.Portal),
            sp => sp.GetRequiredService<ICatalogService>().GetAsync(f.FinanceOnlyId, forAgent: false)));
        await Assert.ThrowsAsync<NotFoundException>(() => f.SubmitAsync(f.Outsider, f.FinanceOnlyId, []));
        // Agents can't use requesting on someone's behalf to get around the audience.
        var onBehalf = await Assert.ThrowsAsync<ValidationException>(() => f.SubmitAsync(f.Agent, f.FinanceOnlyId, [], requesterId: f.Outsider.Id));
        Assert.True(onBehalf.Errors.ContainsKey("requester"));
    }

    [Fact]
    public async Task Department_audiences_work_too()
    {
        var engineer = await f.Host.CreateRequesterAsync("Elena Engineer");
        var engineering = await f.Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            var dept = new Department { Name = "Engineering" };
            db.Departments.Add(dept);
            (await db.Users.FirstAsync(u => u.Id == engineer.Id)).DepartmentId = dept.Id;
            await db.SaveChangesAsync();
            return dept.Id;
        });
        Guid id = default;
        await f.AsAdminAsync(async admin =>
        {
            id = await f.PublishedAsync(admin, "IDE license", new ItemDetailsInput());
            await admin.UpdateAudienceAsync(id, [], [engineering]);
        });

        Assert.True(await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<ICatalogService>().IsVisibleToAsync(id, engineer.Id)));
        Assert.False(await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<ICatalogService>().IsVisibleToAsync(id, f.Requester.Id)));
    }

    [Fact]
    public async Task Submitting_creates_a_service_request_with_items_and_hands_it_to_the_fulfillment_group()
    {
        var result = await f.SubmitAsync(f.Outsider, f.MonitorId, CatalogFixture.Answers(("size", "27-inch"), ("notes", "For the standing desk")), quantity: 2);

        var ticket = await f.TicketAsync(result.Ticket.Id);
        Assert.Equal(TicketType.ServiceRequest, ticket.Type);
        Assert.Equal(f.Outsider.Id, ticket.RequesterId);
        Assert.Equal("Monitor", ticket.Subject);
        Assert.Equal(f.HardwareGroupId, ticket.GroupId);
        Assert.Contains("27-inch", ticket.DescriptionHtml);

        var row = Assert.Single(await f.RowsAsync(ticket.Id));
        Assert.Equal(2, row.Quantity);
        Assert.Equal(49800, row.CostMinor);
        Assert.Equal(RequestedItemStage.Requested, row.Stage);
        Assert.Contains(CatalogForm.Deserialize(row.AnswersJson), a => a.Key == "size" && a.Display == "27-inch");
        Assert.Null(result.Approval);
    }

    [Fact]
    public async Task Invalid_forms_create_nothing()
    {
        var before = await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.CountAsync());
        var error = await Assert.ThrowsAsync<ValidationException>(() => f.SubmitAsync(f.Outsider, f.MonitorId, CatalogFixture.Answers(("size", "40-inch")), quantity: 3));
        Assert.True(error.Errors.ContainsKey("f.size"));
        Assert.True(error.Errors.ContainsKey("quantity"));
        Assert.Equal(before, await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.CountAsync()));
    }

    [Fact]
    public async Task Items_that_need_approval_wait_for_it_and_go_to_the_group_once_approved()
    {
        var result = await f.SubmitAsync(f.Requester, f.LaptopId, CatalogFixture.Answers(("operating_system", "macOS")));
        Assert.NotNull(result.Approval);
        Assert.Equal(ApprovalStatus.Requested, result.Approval!.Status);
        Assert.Null((await f.TicketAsync(result.Ticket.Id)).GroupId);

        // Not approved yet, so the work can't start.
        var row = Assert.Single(await f.RowsAsync(result.Ticket.Id));
        await Assert.ThrowsAsync<ValidationException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Agent),
            sp => sp.GetRequiredService<ICatalogRequestService>().ChangeStageAsync(row.Id, RequestedItemStage.InProgress)));

        await f.Host.RunAsync(SwitchboardTestHost.As(f.Manager, Channel.Portal), sp => sp.GetRequiredService<IApprovalService>()
            .DecideAsync(result.Approval.Id, new DecideApprovalCommand { Verdict = ApprovalVerdict.Approved }));
        await f.Host.ProcessOutboxAsync();

        Assert.Equal(f.HardwareGroupId, (await f.TicketAsync(result.Ticket.Id)).GroupId);
        await f.Host.RunAsync(SwitchboardTestHost.As(f.Agent), sp => sp.GetRequiredService<ICatalogRequestService>().ChangeStageAsync(row.Id, RequestedItemStage.InProgress));
        Assert.Equal(RequestedItemStage.InProgress, Assert.Single(await f.RowsAsync(result.Ticket.Id)).Stage);

        // Requesters can see stages but not change them.
        await Assert.ThrowsAsync<NotFoundException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal),
            sp => sp.GetRequiredService<ICatalogRequestService>().ChangeStageAsync(row.Id, RequestedItemStage.Delivered)));
    }

    [Fact]
    public async Task Requested_for_someone_else_uses_their_manager_and_blocks_both_people()
    {
        var newHire = await f.Host.CreateRequesterAsync("Nia New");
        var boss = await f.Host.CreateRequesterAsync("Bo Boss");
        await f.Host.RunAsSystemAsync(async sp =>
        {
            var db = sp.GetRequiredService<SwitchboardDbContext>();
            (await db.Users.FirstAsync(u => u.Id == newHire.Id)).ManagerId = boss.Id;
            await db.SaveChangesAsync();
            return true;
        });

        var result = await f.SubmitAsync(f.Requester, f.LaptopId, CatalogFixture.Answers(("operating_system", "Windows")), requestedFor: newHire.Id);
        var ticket = await f.TicketAsync(result.Ticket.Id);
        Assert.Equal(newHire.Id, ticket.RequestedForId);
        Assert.Equal("Laptop for Nia New", ticket.Subject);

        var approval = await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<IApprovalService>().GetAsync(result.Approval!.Id));
        Assert.Equal([boss.Id], approval.Request.Levels.Single().Approvers.Select(a => a.UserId));
        Assert.Contains(f.Requester.Id, approval.Request.ConflictUserIds);
        Assert.Contains(newHire.Id, approval.Request.ConflictUserIds);

        // Items that don't allow it refuse "requested for".
        var error = await Assert.ThrowsAsync<ValidationException>(() => f.SubmitAsync(f.Requester, f.MonitorId, CatalogFixture.Answers(("size", "24-inch")), requestedFor: newHire.Id));
        Assert.True(error.Errors.ContainsKey("requestedFor"));
    }

    [Fact]
    public async Task A_rejection_resolves_the_ticket_with_the_reason_and_the_requester_can_change_and_resubmit()
    {
        var result = await f.SubmitAsync(f.Requester, f.LaptopId, CatalogFixture.Answers(("operating_system", "macOS")));
        await f.Host.RunAsync(SwitchboardTestHost.As(f.Manager, Channel.Portal), sp => sp.GetRequiredService<IApprovalService>()
            .DecideAsync(result.Approval!.Id, new DecideApprovalCommand { Verdict = ApprovalVerdict.Rejected, Comment = "We standardise on Windows." }));
        await f.Host.ProcessOutboxAsync();
        // A repeated delivery changes nothing (after-commit handlers must be idempotent).
        await f.Host.ProcessOutboxAsync();

        var resolved = await f.TicketAsync(result.Ticket.Id);
        Assert.Equal(StatusCategory.Resolved, resolved.Status!.Category);
        var notes = await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketConversations.AsNoTracking()
            .Where(c => c.TicketId == result.Ticket.Id && c.Kind == ConversationKind.Note && !c.IsPrivate).ToListAsync());
        Assert.Contains("We standardise on Windows.", Assert.Single(notes).BodyHtml);
        Assert.Equal(RequestedItemStage.Cancelled, Assert.Single(await f.RowsAsync(result.Ticket.Id)).Stage);

        var view = await f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal),
            sp => sp.GetRequiredService<ICatalogRequestService>().GetForRequesterAsync(result.Ticket.Id));
        Assert.True(view!.CanResubmit);
        Assert.Equal("We standardise on Windows.", view.RejectionReason);

        // Only the requester may resubmit.
        await Assert.ThrowsAsync<NotFoundException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Outsider, Channel.Portal),
            sp => sp.GetRequiredService<ICatalogRequestService>().ResubmitAsync(result.Ticket.Id, new ResubmitRequestCommand { Answers = CatalogFixture.Answers(("operating_system", "Windows")) })));

        var again = await f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal),
            sp => sp.GetRequiredService<ICatalogRequestService>().ResubmitAsync(result.Ticket.Id, new ResubmitRequestCommand { Answers = CatalogFixture.Answers(("operating_system", "Windows")) }));

        Assert.NotEqual(result.Approval!.Id, again.Approval!.Id);
        Assert.Equal(ApprovalStatus.Requested, again.Approval.Status);
        var reopened = await f.TicketAsync(result.Ticket.Id);
        Assert.Equal(StatusCategory.Open, reopened.Status!.Category);
        var row = Assert.Single(await f.RowsAsync(result.Ticket.Id));
        Assert.Equal(RequestedItemStage.Requested, row.Stage);
        Assert.Contains(CatalogForm.Deserialize(row.AnswersJson), a => a.Display == "Windows");

        // The first decision stays on record (INV-APR-1).
        var first = await f.Host.RunAsSystemAsync(sp => sp.GetRequiredService<IApprovalService>().GetAsync(result.Approval.Id));
        Assert.Equal(ApprovalStatus.Rejected, first.Request.Status);

        // Can't resubmit while the new approval is pending.
        await Assert.ThrowsAsync<ValidationException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal),
            sp => sp.GetRequiredService<ICatalogRequestService>().ResubmitAsync(result.Ticket.Id, new ResubmitRequestCommand { Answers = CatalogFixture.Answers(("operating_system", "Windows")) })));
    }

    [Fact]
    public async Task Agents_request_on_someones_behalf_and_requesters_cannot()
    {
        var result = await f.SubmitAsync(f.Agent, f.MonitorId, CatalogFixture.Answers(("size", "24-inch")), requesterId: f.Outsider.Id);
        var ticket = await f.TicketAsync(result.Ticket.Id);
        Assert.Equal(f.Outsider.Id, ticket.RequesterId);

        await Assert.ThrowsAsync<ForbiddenException>(() => f.SubmitAsync(f.Requester, f.MonitorId, CatalogFixture.Answers(("size", "24-inch")), requesterId: f.Outsider.Id));
    }

    [Fact]
    public async Task On_behalf_approvals_record_the_agent_and_keep_them_from_approving()
    {
        var result = await f.SubmitAsync(f.Agent, f.LaptopId, CatalogFixture.Answers(("operating_system", "Windows")), requesterId: f.Requester.Id);
        var approval = result.Approval!;
        Assert.Equal(f.Agent.Id, approval.RequestedById);
        Assert.Contains(f.Agent.Id, approval.ConflictUserIds);
        Assert.Equal([f.Manager.Id], approval.Levels.Single().Approvers.Select(a => a.UserId));
    }

    [Fact]
    public async Task Bundles_create_one_row_per_included_item()
    {
        Guid bundleId = default;
        await f.AsAdminAsync(async admin =>
        {
            bundleId = await f.PublishedAsync(admin, "Desk kit", new ItemDetailsInput { Kind = CatItemKind.Bundle, BundleItemIds = [f.MonitorId, f.LaptopId] });
        });
        var result = await f.SubmitAsync(f.Outsider, bundleId, []);
        var rows = await f.RowsAsync(result.Ticket.Id);
        var parent = Assert.Single(rows, r => r.ParentId is null);
        Assert.Equal(2, rows.Count(r => r.ParentId == parent.Id));
    }

    [Fact]
    public async Task Resubmitting_a_bundle_carries_quantity_and_recipient_to_its_items()
    {
        var newHire = await f.Host.CreateRequesterAsync("Nico New");
        Guid bundleId = default;
        await f.AsAdminAsync(async admin =>
        {
            bundleId = await f.PublishedAsync(admin, "Starter kit", new ItemDetailsInput
            {
                Kind = CatItemKind.Bundle, BundleItemIds = [f.MonitorId], MaxQuantity = 3, AllowRequestedFor = true,
            });
            await admin.UpdateApprovalAsync(bundleId, new ApprovalConfig
            {
                Levels = [new ApprovalLevelConfig { Sources = [new ApproverSource { Kind = ApproverSourceKind.Users, UserIds = [f.Manager.Id] }] }],
            });
        });
        var result = await f.SubmitAsync(f.Requester, bundleId, []);
        await f.Host.RunAsync(SwitchboardTestHost.As(f.Manager, Channel.Portal), sp => sp.GetRequiredService<IApprovalService>()
            .DecideAsync(result.Approval!.Id, new DecideApprovalCommand { Verdict = ApprovalVerdict.Rejected, Comment = "Who is it for?" }));
        await f.Host.ProcessOutboxAsync();

        await f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal), sp => sp.GetRequiredService<ICatalogRequestService>()
            .ResubmitAsync(result.Ticket.Id, new ResubmitRequestCommand { Quantity = 2, RequestedForId = newHire.Id }));

        var rows = await f.RowsAsync(result.Ticket.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(2, r.Quantity);
            Assert.Equal(newHire.Id, r.RequestedForId);
            Assert.Equal(RequestedItemStage.Requested, r.Stage);
        });
    }

    [Fact]
    public async Task Catalog_admin_needs_the_manage_privilege()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Agent),
            sp => sp.GetRequiredService<ICatalogAdmin>().CreateItemAsync("Sneaky", null)));
        await Assert.ThrowsAsync<ForbiddenException>(() => f.Host.RunAsync(SwitchboardTestHost.As(f.Requester, Channel.Portal),
            sp => sp.GetRequiredService<ICatalogAdmin>().ListItemsAsync(null)));
    }

    [Fact]
    public async Task Drafts_are_invisible_and_publishing_needs_a_summary_and_category()
    {
        Guid draft = default;
        await f.AsAdminAsync(async admin => draft = (await admin.CreateItemAsync("Half done", null)).Id);

        await Assert.ThrowsAsync<NotFoundException>(() => f.SubmitAsync(f.Outsider, draft, []));
        await Assert.ThrowsAsync<ValidationException>(() => f.AsAdminAsync(admin => admin.SetStateAsync(draft, CatItemState.Published)));
    }

    [Fact]
    public async Task Approval_lookups_and_fields_stay_consistent()
    {
        Guid id = default;
        await f.AsAdminAsync(async admin =>
        {
            id = await f.PublishedAsync(admin, "Mailbox access", new ItemDetailsInput());
            await admin.AddFieldAsync(id, new FieldInput { Label = "Mailbox owner", FieldType = CatFieldType.UserLookup, Required = true });
            await admin.UpdateApprovalAsync(id, new ApprovalConfig
            {
                Levels = [new ApprovalLevelConfig { Sources = [new ApproverSource { Kind = ApproverSourceKind.LookupField, FieldKey = "mailbox_owner" }] }],
            });
        });

        // The picked person approves.
        var result = await f.SubmitAsync(f.Outsider, id, CatalogFixture.Answers(("mailbox_owner", f.Manager.Id.ToString())));
        Assert.Equal([f.Manager.Id], result.Approval!.Levels.Single().Approvers.Select(a => a.UserId));

        // A question used by approvals can't be removed from under it.
        var details = await f.Host.RunAsync(SwitchboardTestHost.As(f.Admin), sp => sp.GetRequiredService<ICatalogAdmin>().GetItemAsync(id));
        var fieldId = details.Item.Fields.Single().Id;
        await Assert.ThrowsAsync<ValidationException>(() => f.AsAdminAsync(admin => admin.RemoveFieldAsync(id, fieldId)));
    }
}
