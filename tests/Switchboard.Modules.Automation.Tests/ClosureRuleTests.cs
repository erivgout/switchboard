using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Automation.Closure;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Testing;

namespace Switchboard.Modules.Automation.Tests;

/// <summary>FR-TKT-25: admin-defined conditions block resolving or closing.</summary>
public sealed class ClosureRuleTests : AutomationTestBase
{
    private Task AddRuleAsync(ClosureRuleKind kind, TicketType? type = null, string? field = null, string? message = null) =>
        Host.RunAsync(SwitchboardTestHost.As(Admin), sp =>
            sp.GetRequiredService<ClosureRuleService>().AddAsync(Host.Workspace.Id, new ClosureRuleInput(kind, type, field, message)));

    private Task ResolveAsync(Guid ticketId) =>
        Host.RunAsync(SwitchboardTestHost.As(Agent), async sp =>
            await sp.GetRequiredService<ITicketService>().UpdateAsync(ticketId, new TicketPatch { StatusId = await StatusIdAsync("Resolved") }));

    [Fact]
    public async Task Open_tasks_block_resolving_until_completed()
    {
        await AddRuleAsync(ClosureRuleKind.AllTasksCompleted);
        var ticket = await FileAsync();
        var task = await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().AddTaskAsync(ticket.Id, new TaskCommand { Title = "Swap toner" }));

        var error = await Assert.ThrowsAsync<ValidationException>(() => ResolveAsync(ticket.Id));
        Assert.Contains("Complete every task before resolving. 1 task is still open.", error.Errors["status"]);

        await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().UpdateTaskAsync(task.Id, new TaskPatch { Status = TicketTaskStatus.Completed }));
        await ResolveAsync(ticket.Id);
        Assert.Equal(StatusCategory.Resolved, (await TicketAsync(ticket.Id)).Status!.Category);
    }

    [Fact]
    public async Task A_reply_sent_with_resolve_satisfies_the_reply_rule()
    {
        await AddRuleAsync(ClosureRuleKind.PublicReplySent);
        var ticket = await FileAsync();
        var error = await Assert.ThrowsAsync<ValidationException>(() => ResolveAsync(ticket.Id));
        Assert.Contains("Send the requester at least one reply before resolving.", error.Errors["status"]);

        await Host.RunAsync(SwitchboardTestHost.As(Agent), async sp => await sp.GetRequiredService<ITicketService>().ReplyAsync(ticket.Id,
            new ReplyCommand { BodyHtml = "<p>Fixed it.</p>", StatusId = await StatusIdAsync("Resolved") }));
        Assert.Equal(StatusCategory.Resolved, (await TicketAsync(ticket.Id)).Status!.Category);
    }

    [Fact]
    public async Task Rules_apply_only_to_their_ticket_type_and_can_carry_a_custom_message()
    {
        await AddRuleAsync(ClosureRuleKind.CategorySet, TicketType.ServiceRequest, message: "Pick a catalog category first.");
        var incident = await FileAsync();
        await ResolveAsync(incident.Id);

        var request = await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().CreateAsync(new CreateTicketCommand
        {
            Type = TicketType.ServiceRequest, Subject = "New monitor", RequesterId = Requester.Id,
        }));
        var error = await Assert.ThrowsAsync<ValidationException>(() => ResolveAsync(request.Id));
        Assert.Equal(["Pick a catalog category first."], error.Errors["status"]);
    }

    [Fact]
    public async Task Custom_field_rule_requires_the_field_and_disabled_rules_do_nothing()
    {
        await Db(async db =>
        {
            db.CustomFieldDefinitions.Add(new CustomFieldDefinition { EntityType = "ticket", Key = "root_cause", Label = "Root cause", FieldType = CustomFieldType.Text });
            await db.SaveChangesAsync();
        });
        await AddRuleAsync(ClosureRuleKind.CustomFieldSet, field: "root_cause");
        var ticket = await FileAsync();
        var error = await Assert.ThrowsAsync<ValidationException>(() => ResolveAsync(ticket.Id));
        Assert.Contains("Fill in Root cause before resolving.", error.Errors["status"]);

        var rule = (await Host.RunAsync(SwitchboardTestHost.As(Admin), sp => sp.GetRequiredService<ClosureRuleService>().ListAsync(Host.Workspace.Id))).Single();
        await Host.RunAsync(SwitchboardTestHost.As(Admin), sp => sp.GetRequiredService<ClosureRuleService>().SetEnabledAsync(rule.Id, false));
        await ResolveAsync(ticket.Id);
    }

    [Fact]
    public async Task Only_automation_admins_manage_closure_rules()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Host.RunAsync(SwitchboardTestHost.As(Agent), sp =>
            sp.GetRequiredService<ClosureRuleService>().AddAsync(Host.Workspace.Id, new ClosureRuleInput(ClosureRuleKind.CategorySet, null, null, null))));
        await Assert.ThrowsAsync<ValidationException>(() => AddRuleAsync(ClosureRuleKind.CustomFieldSet, field: "does_not_exist"));
    }
}
