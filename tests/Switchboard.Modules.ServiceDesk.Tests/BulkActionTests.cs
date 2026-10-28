using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.ServiceDesk.Data;
using Switchboard.Modules.ServiceDesk.Services;
using Switchboard.Testing;

namespace Switchboard.Modules.ServiceDesk.Tests;

/// <summary>Bulk and merge permission boundaries (FR-TKT-21, SR-AUT-5).</summary>
public sealed class BulkActionTests : ServiceDeskTestBase
{
    private User _agent = default!;
    private User _supervisor = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _agent = await Host.CreateAgentAsync("Sam Agent", RoleKeys.Agent, TicketScope.Global, "Service Desk");
        _supervisor = await Host.CreateAgentAsync("Priya Supervisor", RoleKeys.Supervisor, TicketScope.Global, "Service Desk");
    }

    private async Task<Guid> StatusIdAsync(StatusCategory category) =>
        await Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().TicketStatuses
            .Where(s => s.Category == category).Select(s => s.Id).FirstAsync());

    [Fact]
    public async Task Agents_without_the_bulk_privilege_cannot_run_bulk_actions()
    {
        var a = await FileTicketAsync("One");
        var b = await FileTicketAsync("Two");
        var pending = await StatusIdAsync(StatusCategory.Pending);

        await Assert.ThrowsAsync<ForbiddenException>(() => As(_agent, sp => sp.GetRequiredService<BulkActionService>().RunAsync(new BulkRequest
        {
            Action = BulkActions.Status,
            TicketIds = [a.Id, b.Id],
            StatusId = pending,
        })));

        Assert.Equal(StatusCategory.Open, (await ReloadAsync(a.Id)).Status!.Category);
    }

    [Fact]
    public async Task Supervisors_update_every_selected_ticket_and_get_per_record_results()
    {
        var a = await FileTicketAsync("One");
        var b = await FileTicketAsync("Two");
        var pending = await StatusIdAsync(StatusCategory.Pending);

        var outcome = await As(_supervisor, sp => sp.GetRequiredService<BulkActionService>().RunAsync(new BulkRequest
        {
            Action = BulkActions.Status,
            TicketIds = [a.Id, b.Id],
            StatusId = pending,
        }));

        Assert.Equal(BulkJobStatus.Completed, outcome.Status);
        Assert.Equal(2, outcome.Succeeded);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(StatusCategory.Pending, (await ReloadAsync(a.Id)).Status!.Category);
        Assert.Equal(StatusCategory.Pending, (await ReloadAsync(b.Id)).Status!.Category);
    }

    [Fact]
    public async Task Bulk_does_not_bypass_the_privilege_of_each_action()
    {
        // Bulk without tickets.delete: trashing fails per record and nothing is deleted.
        var bulkOnly = await AgentWithPrivilegesAsync("Bo Bulk", Privileges.TicketsBulk, Privileges.TicketsEdit);
        var a = await FileTicketAsync("One");

        var outcome = await As(bulkOnly, sp => sp.GetRequiredService<BulkActionService>().RunAsync(new BulkRequest
        {
            Action = BulkActions.Trash,
            TicketIds = [a.Id],
        }));

        Assert.Equal(1, outcome.Failed);
        Assert.Null((await ReloadAsync(a.Id)).DeletedAt);
        var items = await As(bulkOnly, sp => sp.GetRequiredService<BulkActionService>().GetItemsAsync(outcome.JobId!.Value));
        Assert.Contains("permission", items.Single().Error);
    }

    [Fact]
    public async Task Bulk_ignores_tickets_the_agent_cannot_see()
    {
        var restricted = await Host.CreateAgentAsync("Rita Restricted", RoleKeys.Supervisor, TicketScope.Restricted);
        var hidden = await FileTicketAsync("Someone else's");

        await Assert.ThrowsAsync<ValidationException>(() => As(restricted, sp => sp.GetRequiredService<BulkActionService>().RunAsync(new BulkRequest
        {
            Action = BulkActions.Trash,
            TicketIds = [hidden.Id],
        })));
        Assert.Null((await ReloadAsync(hidden.Id)).DeletedAt);
    }

    [Fact]
    public async Task Merge_needs_the_merge_privilege_even_with_bulk()
    {
        var bulkNoMerge = await AgentWithPrivilegesAsync("Nell NoMerge", Privileges.TicketsBulk, Privileges.TicketsEdit);
        var a = await FileTicketAsync("Primary");
        var b = await FileTicketAsync("Duplicate");

        await Assert.ThrowsAsync<ForbiddenException>(() => As(bulkNoMerge, sp => sp.GetRequiredService<BulkActionService>().RunAsync(new BulkRequest
        {
            Action = BulkActions.Merge,
            TicketIds = [a.Id, b.Id],
            PrimaryId = a.Id,
        })));
        Assert.Null((await ReloadAsync(b.Id)).MergedIntoId);

        // A plain agent has neither bulk nor merge.
        await Assert.ThrowsAsync<ForbiddenException>(() => As(_agent, sp => sp.GetRequiredService<ITicketService>().MergeAsync(a.Id, [b.Id])));

        await As(_supervisor, sp => sp.GetRequiredService<BulkActionService>().RunAsync(new BulkRequest
        {
            Action = BulkActions.Merge,
            TicketIds = [a.Id, b.Id],
            PrimaryId = a.Id,
        }));
        var merged = await ReloadAsync(b.Id);
        Assert.Equal(a.Id, merged.MergedIntoId);
        Assert.Equal(StatusCategory.Closed, merged.Status!.Category);
    }

    [Fact]
    public async Task Merge_needs_a_primary_among_the_selection()
    {
        var a = await FileTicketAsync("One");
        var b = await FileTicketAsync("Two");
        var other = await FileTicketAsync("Not selected");

        await Assert.ThrowsAsync<ValidationException>(() => As(_supervisor, sp => sp.GetRequiredService<BulkActionService>().RunAsync(new BulkRequest
        {
            Action = BulkActions.Merge,
            TicketIds = [a.Id, b.Id],
            PrimaryId = other.Id,
        })));
    }

    [Fact]
    public async Task Over_the_threshold_needs_the_count_confirmed_and_runs_as_a_tracked_job()
    {
        for (var i = 0; i < BulkActionService.InlineLimit + 1; i++)
        {
            await FileTicketAsync($"Storm {i}");
        }
        var request = new BulkRequest { Action = BulkActions.Priority, AllMatching = true, Query = "\"Storm\"", Priority = Priority.High };

        var error = await Assert.ThrowsAsync<ValidationException>(() => As(_supervisor, sp => sp.GetRequiredService<BulkActionService>().RunAsync(request)));
        Assert.Contains("101", error.Message);

        var outcome = await As(_supervisor, sp => sp.GetRequiredService<BulkActionService>().RunAsync(request with { ConfirmedCount = 101 }));
        Assert.Equal(BulkJobStatus.Queued, outcome.Status);
        Assert.Equal(101, outcome.Total);

        var runner = ActivatorUtilities.CreateInstance<BulkJobRunner>(Host.Services);
        Assert.True(await runner.RunNextAsync(default));

        var job = await As(_supervisor, sp => sp.GetRequiredService<BulkActionService>().GetJobAsync(outcome.JobId!.Value));
        Assert.Equal(BulkJobStatus.Completed, job.Status);
        Assert.Equal(101, job.Succeeded);
        var high = await Host.RunAsSystemAsync(sp => sp.GetRequiredService<SwitchboardDbContext>().Tickets.CountAsync(t => t.Subject.StartsWith("Storm") && t.Priority == Priority.High));
        Assert.Equal(101, high);

        // Other agents can't read someone else's job.
        await Assert.ThrowsAsync<NotFoundException>(() => As(_agent, sp => sp.GetRequiredService<BulkActionService>().GetJobAsync(outcome.JobId!.Value)));
    }

    [Fact]
    public async Task Bulk_tags_are_added_to_existing_tags()
    {
        var a = await FileTicketAsync("Tagged");
        await Host.RunAsSystemAsync(sp => sp.GetRequiredService<ITicketService>().UpdateAsync(a.Id, new TicketPatch { Tags = new[] { "vip" } }));

        await As(_supervisor, sp => sp.GetRequiredService<BulkActionService>().RunAsync(new BulkRequest
        {
            Action = BulkActions.AddTags,
            TicketIds = [a.Id],
            Tags = ["Travel"],
        }));

        var tags = (await ReloadAsync(a.Id)).Tags.Select(t => t.Tag).Order().ToList();
        Assert.Equal(["travel", "vip"], tags);
    }
}
