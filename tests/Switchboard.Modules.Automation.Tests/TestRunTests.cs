using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;
using Switchboard.Testing;

namespace Switchboard.Modules.Automation.Tests;

/// <summary>SR-AUT-3: a test run writes nothing, calls nothing and sends nothing.</summary>
public sealed class TestRunTests : AutomationTestBase
{
    [Fact]
    public async Task Test_run_reports_every_step_without_any_side_effect()
    {
        await SetAvailabilityAsync(Agent, AgentAvailability.Available);
        var ticket = await FileAsync("VPN keeps dropping", group: "Service Desk");
        await Db(db => db.Tickets.Where(t => t.Id == ticket.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.AgentId, (Guid?)null)));
        var pending = await StatusIdAsync("Pending");
        var network = await GroupIdAsync("Network");
        var def = new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Conditions = When(Rule("ticket.subject", Operators.Contains, "vpn")),
            Actions =
            [
                Act(ActionTypes.SetStatus, ("statusId", pending.ToString())),
                Act(ActionTypes.AddTags, ("tags", "vpn")),
                Act(ActionTypes.AssignRoundRobin),
                Act(ActionTypes.AddNote, ("body", "Checked by {{workflow.name}}")),
                Act(ActionTypes.CreateTask, ("title", "Call {{requester.name}}")),
                Act(ActionTypes.SendEmail, ("recipient", "group"), ("groupId", network.ToString()), ("subject", "VPN: {{ticket.id}}"), ("body", "Status is {{ticket.status}}")),
                Act(ActionTypes.WebRequest, ("method", "POST"), ("url", "https://hooks.example.com/vpn"), ("authHeader", "X-Api-Key"), ("authValue", "enc:whatever"),
                    ("body", "{\"id\": \"{{ticket.id}}\"}")),
            ],
        };
        var before = await CountsAsync();
        var resolverCalls = Resolver.Calls;

        Writes.Recording = true;
        var report = await Host.RunAsync(SwitchboardTestHost.As(Admin), sp =>
            sp.GetRequiredService<WorkflowTestRunner>().RunAsync(def, Host.Workspace.Id, ticket.Id, "VPN triage", default));
        Writes.Recording = false;

        Assert.NotNull(report);
        Assert.True(report.Evaluation.Matched);
        Assert.Equal(7, report.Steps.Count);
        Assert.All(report.Steps, s => Assert.Null(s.Error));
        Assert.Equal("Set status to Pending", report.Steps[0].Title);
        Assert.Equal("Sam Agent", report.Steps[2].Input["choice"]);
        Assert.Equal("Status is Pending", report.Steps[5].Input["body"]);
        Assert.Equal("{\"id\": \"" + ticket.DisplayId + "\"}", report.Steps[6].Input["body"]);
        Assert.DoesNotContain("enc:", System.Text.Json.JsonSerializer.Serialize(report.Steps[6].Input));

        Assert.Empty(Writes.Writes);
        Assert.Empty(Email.Sent);
        Assert.Empty(Http.Requests);
        Assert.Equal(resolverCalls, Resolver.Calls);
        Assert.Equal(before, await CountsAsync());
        var after = await TicketAsync(ticket.Id);
        Assert.Null(after.AgentId);
        Assert.NotEqual(pending, after.StatusId);
    }

    [Fact]
    public async Task Test_run_flags_blocked_addresses_and_unmet_conditions()
    {
        var ticket = await FileAsync("Mouse");
        var def = new WorkflowDefinition
        {
            Conditions = When(Rule("ticket.subject", Operators.Contains, "vpn")),
            Actions = [Act(ActionTypes.WebRequest, ("method", "GET"), ("url", "http://169.254.169.254/latest/meta-data"))],
        };
        var report = await Host.RunAsync(SwitchboardTestHost.As(Admin), sp =>
            sp.GetRequiredService<WorkflowTestRunner>().RunAsync(def, Host.Workspace.Id, ticket.Id, "Probe", default));
        Assert.False(report!.Evaluation.Matched);
        Assert.False(report.Evaluation.Groups[0].Rules[0].Matched);
        Assert.Equal("Mouse", report.Evaluation.Groups[0].Rules[0].Actual);
        Assert.StartsWith("Would be blocked", report.Steps[0].NoChange);
        Assert.Empty(Http.Requests);
    }

    private Task<(int Runs, int Conversations, int Tasks, int Activity, int Outbox)> CountsAsync() =>
        Db(async db => (
            await db.Set<AutRun>().CountAsync(),
            await db.TicketConversations.CountAsync(),
            await db.TicketTasks.CountAsync(),
            await db.ActivityEntries.CountAsync(),
            await db.OutboxMessages.CountAsync()));
}
