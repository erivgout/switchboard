using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Core.Domain;
using Switchboard.Core.Events;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;
using Switchboard.Testing;

namespace Switchboard.Modules.Automation.Tests;

public sealed class EngineTests : AutomationTestBase
{
    [Fact]
    public async Task Matching_workflow_runs_as_automation_and_logs_each_step()
    {
        var wf = await PublishAsync("Urgent printers", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Conditions = When(Rule("ticket.subject", Operators.Contains, "printer")),
            Actions =
            [
                Act(ActionTypes.SetPriority, ("priority", "Urgent")),
                Act(ActionTypes.AddTags, ("tags", "printers, floor-3")),
                Act(ActionTypes.AddNote, ("body", "Auto-flagged {{ticket.id}} for {{requester.name}}")),
            ],
        });
        var match = await FileAsync("Printer on fire");
        var other = await FileAsync("Laptop is slow");
        await ProcessAsync();

        var ticket = await TicketAsync(match.Id);
        Assert.Equal(Priority.Urgent, ticket.Priority);
        Assert.Equal(["floor-3", "printers"], ticket.Tags.Select(t => t.Tag).Order());
        Assert.Equal(Priority.Low, (await TicketAsync(other.Id)).Priority);

        var note = await Db(db => db.TicketConversations.AsNoTracking().SingleAsync(c => c.TicketId == match.Id && c.Kind == ConversationKind.Note));
        Assert.Contains($"Auto-flagged {ticket.DisplayId} for Dana Requester", note.BodyText);
        Assert.Equal(Channel.Automation, note.Channel);

        var activity = await Db(db => db.ActivityEntries.AsNoTracking().Where(a => a.SubjectId == match.Id && a.Action == "updated").ToListAsync());
        Assert.Contains(activity, a => a.ActorKind == ActorKind.Automation && a.ActorName == "Urgent printers");

        var run = Assert.Single(await RunsAsync(wf.Id));
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(match.Id, run.TicketId);
        Assert.Equal(["conditions", ActionTypes.SetPriority, ActionTypes.AddTags, ActionTypes.AddNote], run.Steps.OrderBy(s => s.Order).Select(s => s.Kind));
        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Succeeded, s.Status));
    }

    [Fact]
    public async Task Each_event_runs_a_workflow_exactly_once_even_when_redelivered()
    {
        var wf = await PublishAsync("Note on create", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Actions = [Act(ActionTypes.AddNote, ("body", "Hello"))],
        });
        var ticket = await FileAsync();
        var created = new TicketCreated(ticket.Id, ticket.WorkspaceId, ticket.Type) { OccurredAt = DateTimeOffset.UtcNow };

        // The outbox delivers at least once: simulate the same event arriving three times, twice concurrently.
        var dispatcher = Host.Services.GetRequiredService<AutomationDispatcher>();
        await Task.WhenAll(dispatcher.DispatchAsync(created, default), dispatcher.DispatchAsync(created, default));
        await dispatcher.DispatchAsync(created, default);

        Assert.Single(await RunsAsync(wf.Id));
        Assert.Equal(1, await Db(db => db.TicketConversations.CountAsync(c => c.TicketId == ticket.Id && c.Kind == ConversationKind.Note)));
    }

    [Fact]
    public async Task Workflows_never_retrigger_themselves_within_one_cascade()
    {
        // A and B would flip priority forever: High -> Low -> High -> ...
        var a = await PublishAsync("High to low", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketUpdated, Changes = [new FieldChangeFilter { Field = "ticket.priority" }] },
            Conditions = When(Rule("ticket.priority", Operators.Is, "High")),
            Actions = [Act(ActionTypes.SetPriority, ("priority", "Low"))],
        });
        var b = await PublishAsync("Low to high", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketUpdated, Changes = [new FieldChangeFilter { Field = "ticket.priority", To = "Low" }] },
            Actions = [Act(ActionTypes.SetPriority, ("priority", "High"))],
        });
        var ticket = await FileAsync();
        await ProcessAsync();

        await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.High }));
        await ProcessAsync();

        var runsA = await RunsAsync(a.Id);
        var runsB = await RunsAsync(b.Id);
        Assert.Single(runsA);
        Assert.Single(runsB);
        Assert.Equal(runsA[0].CascadeRootId, runsB[0].CascadeRootId);
        Assert.Equal(0, runsA[0].CascadeDepth);
        Assert.Equal(1, runsB[0].CascadeDepth);
        Assert.Equal(Priority.High, (await TicketAsync(ticket.Id)).Priority);

        // A new change by a person starts a new cascade, so A may run again.
        await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.Medium }));
        await Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { Priority = Priority.High }));
        await ProcessAsync();
        Assert.Equal(2, (await RunsAsync(a.Id)).Count);
    }

    [Fact]
    public async Task Cascades_stop_at_the_depth_limit_and_alert_admins()
    {
        WorkflowDefinition Chain(string has, string add) => new()
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketUpdated, Changes = [new FieldChangeFilter { Field = "ticket.tags", To = has }] },
            Actions = [Act(ActionTypes.AddTags, ("tags", add))],
        };
        await PublishAsync("Step 1", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Actions = [Act(ActionTypes.AddTags, ("tags", "t1"))],
        });
        await PublishAsync("Step 2", Chain("t1", "t2"));
        await PublishAsync("Step 3", Chain("t2", "t3"));
        var step4 = await PublishAsync("Step 4", Chain("t3", "t4"));

        var ticket = await FileAsync();
        await ProcessAsync();

        Assert.Equal(["t1", "t2", "t3"], (await TicketAsync(ticket.Id)).Tags.Select(t => t.Tag).Order());
        var blocked = Assert.Single(await RunsAsync(step4.Id));
        Assert.Equal(RunStatus.Blocked, blocked.Status);
        Assert.Equal(3, blocked.CascadeDepth);
        Assert.Contains("limit of 3", blocked.Message);
        Assert.Contains(Email.Sent, e => e.Subject == "Automation stopped: Step 4" && e.To.Contains(Admin.Email!));
    }

    [Fact]
    public async Task A_failed_run_changes_nothing_and_repeated_failures_turn_the_workflow_off()
    {
        await Host.RunAsync(SwitchboardTestHost.As(Admin), async sp =>
        {
            await sp.GetRequiredService<ISettingsService>().SetAsync(AutomationSettingKeys.FailureThreshold, 2);
            await sp.GetRequiredService<Core.Data.SwitchboardDbContext>().SaveChangesAsync();
        });
        var wf = await PublishAsync("Broken", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Actions = [Act(ActionTypes.AddTags, ("tags", "first")), Act(ActionTypes.SetStatus, ("statusId", (await StatusIdAsync("Pending")).ToString()))],
        });
        // Break the published definition after validation: the status disappears.
        var pending = await StatusIdAsync("Pending");
        await Db(async db =>
        {
            var row = await db.Set<AutWorkflow>().FirstAsync(w => w.Id == wf.Id);
            row.PublishedJson = row.PublishedJson!.Replace(pending.ToString(), Guid.NewGuid().ToString());
            await db.SaveChangesAsync();
        });

        var first = await FileAsync("One");
        await ProcessAsync();
        var run = Assert.Single(await RunsAsync(wf.Id));
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.StartsWith("Nothing was changed.", run.Message);
        Assert.Empty((await TicketAsync(first.Id)).Tags);
        Assert.Contains(run.Steps, s => s.Kind == ActionTypes.AddTags && s.Status == StepStatus.RolledBack);
        Assert.Contains(run.Steps, s => s.Kind == ActionTypes.SetStatus && s.Status == StepStatus.Failed);

        await FileAsync("Two");
        await ProcessAsync();
        var row = await Db(db => db.Set<AutWorkflow>().AsNoTracking().FirstAsync(w => w.Id == wf.Id));
        Assert.False(row.IsEnabled);
        Assert.NotNull(row.AutoDisabledAt);
        Assert.Equal(2, row.ConsecutiveFailures);
        Assert.Contains(Email.Sent, e => e.Subject == "Workflow turned off: Broken");
        Assert.True(await Db(db => db.AuditEntries.AnyAsync(a => a.Action == "automation.workflow_auto_disabled")));

        await FileAsync("Three");
        await ProcessAsync();
        Assert.Equal(2, (await RunsAsync(wf.Id)).Count);
    }

    [Fact]
    public async Task Pausing_queues_events_and_resuming_replays_them()
    {
        var wf = await PublishAsync("Tag new", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Actions = [Act(ActionTypes.AddTags, ("tags", "seen"))],
        });
        await Host.RunAsync(SwitchboardTestHost.As(Admin), async sp =>
        {
            await sp.GetRequiredService<ISettingsService>().SetAsync(AutomationSettingKeys.Paused, true, Host.Workspace.Id);
            await sp.GetRequiredService<Core.Data.SwitchboardDbContext>().SaveChangesAsync();
        });

        var ticket = await FileAsync();
        await ProcessAsync();
        Assert.Empty(await RunsAsync(wf.Id));
        Assert.Equal(1, await Db(db => db.Set<AutQueuedEvent>().CountAsync()));

        await Host.RunAsync(SwitchboardTestHost.As(Admin), async sp =>
        {
            await sp.GetRequiredService<ISettingsService>().SetAsync(AutomationSettingKeys.Paused, false, Host.Workspace.Id);
            await sp.GetRequiredService<Core.Data.SwitchboardDbContext>().SaveChangesAsync();
        });
        await Host.Services.GetRequiredService<AutomationDispatcher>().DrainQueueAsync(100, default);
        await ProcessAsync();

        Assert.Single(await RunsAsync(wf.Id));
        Assert.Contains("seen", (await TicketAsync(ticket.Id)).Tags.Select(t => t.Tag));
        Assert.Equal(0, await Db(db => db.Set<AutQueuedEvent>().CountAsync()));
    }

    [Fact]
    public async Task A_paused_workspace_backlog_does_not_hold_up_replay_for_others()
    {
        var wf = await PublishAsync("Tag new", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Actions = [Act(ActionTypes.AddTags, ("tags", "seen"))],
        });
        var hr = new Workspace { Key = "hr", Name = "HR", CreatedAt = DateTimeOffset.UtcNow };
        await Db(async db =>
        {
            db.Workspaces.Add(hr);
            await db.SaveChangesAsync();
        });
        var ticket = await FileAsync();
        await Host.RunAsync(SwitchboardTestHost.As(Admin), async sp =>
        {
            await sp.GetRequiredService<ISettingsService>().SetAsync(AutomationSettingKeys.Paused, true, hr.Id);
            var db = sp.GetRequiredService<Core.Data.SwitchboardDbContext>();
            AutQueuedEvent Queue(TicketCreated e) => new()
            {
                EventId = e.EventId,
                WorkspaceId = e.WorkspaceId,
                EventType = $"{typeof(TicketCreated).FullName}, {typeof(TicketCreated).Assembly.GetName().Name}",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(e, Core.Data.JsonDefaults.Options),
                OccurredAt = e.OccurredAt,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            for (var i = 0; i < 3; i++)
            {
                db.Set<AutQueuedEvent>().Add(Queue(new TicketCreated(Guid.NewGuid(), hr.Id, TicketType.Incident) { OccurredAt = DateTimeOffset.UtcNow.AddHours(-1 - i) }));
            }
            db.Set<AutQueuedEvent>().Add(Queue(new TicketCreated(ticket.Id, ticket.WorkspaceId, ticket.Type) { OccurredAt = DateTimeOffset.UtcNow }));
            await db.SaveChangesAsync();
        });

        await Host.Services.GetRequiredService<AutomationDispatcher>().DrainQueueAsync(2, default);

        Assert.Single(await RunsAsync(wf.Id));
        Assert.Contains("seen", (await TicketAsync(ticket.Id)).Tags.Select(t => t.Tag));
        Assert.Equal(3, await Db(db => db.Set<AutQueuedEvent>().CountAsync(q => q.WorkspaceId == hr.Id)));
    }

    [Fact]
    public async Task Pausing_in_drop_mode_discards_events()
    {
        var wf = await PublishAsync("Tag new", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Actions = [Act(ActionTypes.AddTags, ("tags", "seen"))],
        });
        await Host.RunAsync(SwitchboardTestHost.As(Admin), async sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            await settings.SetAsync(AutomationSettingKeys.Paused, true);
            await settings.SetAsync(AutomationSettingKeys.PauseMode, "drop");
            await sp.GetRequiredService<Core.Data.SwitchboardDbContext>().SaveChangesAsync();
        });
        await FileAsync();
        await ProcessAsync();
        Assert.Empty(await RunsAsync(wf.Id));
        Assert.Equal(0, await Db(db => db.Set<AutQueuedEvent>().CountAsync()));
    }

    [Fact]
    public async Task Emails_are_sent_once_after_commit_with_encoded_placeholders()
    {
        var wf = await PublishAsync("Acknowledge", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Actions = [Act(ActionTypes.SendEmail, ("recipient", "requester"), ("subject", "Got it: {{ticket.subject}}"), ("body", "We received \"{{ticket.subject}}\"."))],
        });
        var ticket = await FileAsync("<script>alert(1)</script>\r\nBcc: evil@example.com");
        await ProcessAsync();

        var mail = Assert.Single(Email.Sent);
        Assert.Equal([Requester.Email!], mail.To);
        Assert.DoesNotContain("\n", mail.Subject);
        Assert.DoesNotContain("<script>", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;", mail.HtmlBody);
        Assert.Equal("auto-generated", mail.Headers!["Auto-Submitted"]);

        var run = Assert.Single(await RunsAsync(wf.Id));
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var step = run.Steps.Single(s => s.Kind == ActionTypes.SendEmail);
        Assert.Equal(StepStatus.Succeeded, step.Status);

        // Re-sending the run (e.g. a crash-recovery sweep) never sends twice.
        var sender = Host.Services.GetRequiredService<ExternalStepSender>();
        await sender.SendRunAsync(run.Id, default);
        await sender.SendStepAsync(step.Id, default);
        Assert.Single(Email.Sent);
        _ = ticket;
    }

    [Fact]
    public async Task Web_requests_send_the_rendered_body_and_auth_header_and_log_the_response()
    {
        var wf = await PublishAsync("Notify chat", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Actions = [Act(ActionTypes.WebRequest, ("method", "POST"), ("url", "https://hooks.example.com/in?id={{ticket.id}}"),
                ("authHeader", "X-Api-Key"), ("authValue", "s3cret"), ("body", "{\"text\": \"{{ticket.subject}}\"}"))],
        });
        Assert.DoesNotContain("s3cret", wf.PublishedJson);
        var ticket = await FileAsync("Say \"hi\"");
        await ProcessAsync();

        var request = Assert.Single(Http.Requests);
        Assert.Equal($"https://hooks.example.com/in?id={ticket.DisplayId}", request.Uri.ToString());
        Assert.Equal("s3cret", request.Auth);
        Assert.Equal("Say \"hi\"", System.Text.Json.JsonDocument.Parse(request.Body!).RootElement.GetProperty("text").GetString());
        var step = (await RunsAsync(wf.Id)).Single().Steps.Single(s => s.Kind == ActionTypes.WebRequest);
        Assert.Contains("\"status\":200", step.OutputJson);
        Assert.DoesNotContain("s3cret", step.InputJson);
    }

    [Fact]
    public async Task Supervisor_rules_run_once_per_clock_crossing()
    {
        var wf = await PublishAsync("Stale tickets", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.Time, Clock = TimeClocks.Created, Hours = 24 },
            Conditions = When(Rule("ticket.status_category", Operators.Is, "Open")),
            Actions = [Act(ActionTypes.AddTags, ("tags", "stale"))],
        });
        var old = await FileAsync("Old");
        var fresh = await FileAsync("Fresh");
        await ProcessAsync();
        await Db(db => db.Tickets.Where(t => t.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2))));

        var runner = Host.Services.GetRequiredService<TimeRuleRunner>();
        await runner.RunAsync(default);
        await runner.RunAsync(default);
        await ProcessAsync();

        Assert.Contains("stale", (await TicketAsync(old.Id)).Tags.Select(t => t.Tag));
        Assert.DoesNotContain("stale", (await TicketAsync(fresh.Id)).Tags.Select(t => t.Tag));
        var run = Assert.Single(await RunsAsync(wf.Id));
        Assert.NotNull(run.Anchor);
    }

    [Fact]
    public async Task Hours_in_status_uses_when_the_ticket_entered_its_status()
    {
        var wf = await PublishAsync("Pending too long", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.Time, Clock = TimeClocks.InStatus, Hours = 48 },
            Conditions = When(Rule("ticket.status_category", Operators.Is, "Pending")),
            Actions = [Act(ActionTypes.AddNote, ("body", "Still pending"))],
        });
        var ticket = await FileAsync();
        await Host.RunAsync(SwitchboardTestHost.As(Agent), async sp =>
            await sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, new TicketPatch { StatusId = await StatusIdAsync("Pending") }));
        var clock = await Db(db => db.Set<AutTicketClock>().AsNoTracking().FirstAsync(c => c.TicketId == ticket.Id));
        Assert.Equal(await StatusIdAsync("Pending"), clock.StatusId);

        var runner = Host.Services.GetRequiredService<TimeRuleRunner>();
        await runner.RunAsync(default);
        Assert.Empty(await RunsAsync(wf.Id));

        await Db(db => db.Set<AutTicketClock>().Where(c => c.TicketId == ticket.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.StatusSince, DateTimeOffset.UtcNow.AddDays(-3))));
        await runner.RunAsync(default);
        Assert.Single(await RunsAsync(wf.Id));
    }

    [Fact]
    public async Task Field_change_filters_only_fire_for_that_change()
    {
        var wf = await PublishAsync("Urgent escalation", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketUpdated, Changes = [new FieldChangeFilter { Field = "ticket.priority", To = "Urgent" }] },
            Actions = [Act(ActionTypes.AddTags, ("tags", "escalated"))],
        });
        var ticket = await FileAsync();
        await ProcessAsync();
        var tickets = (Func<TicketPatch, Task>)(patch => Host.RunAsync(SwitchboardTestHost.As(Agent), sp => sp.GetRequiredService<ITicketService>().UpdateAsync(ticket.Id, patch)));
        await tickets(new TicketPatch { Subject = "Renamed" });
        await tickets(new TicketPatch { Priority = Priority.High });
        await ProcessAsync();
        Assert.Empty(await RunsAsync(wf.Id));

        await tickets(new TicketPatch { Priority = Priority.Urgent });
        await ProcessAsync();
        Assert.Single(await RunsAsync(wf.Id));
    }

    [Fact]
    public async Task Publishing_rejects_incomplete_definitions_with_plain_messages()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() => PublishAsync("Incomplete", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.Time, Clock = TimeClocks.Created },
            Conditions = When(Rule("ticket.priority", Operators.Contains, "x")),
            Actions = [Act(ActionTypes.SendEmail, ("recipient", "requester"), ("subject", "Hi {{ticket.nope}}")), Act(ActionTypes.WebRequest, ("url", "http://10.0.0.5/hook"))],
        }));
        var messages = error.Errors["definition"];
        Assert.Contains("When: enter a number of hours between 1 and 8760.", messages);
        Assert.Contains("If: choose how to compare \"Priority\".", messages);
        Assert.Contains("Then, step 1 (Send an email): fill in message.", messages);
        Assert.Contains("Then, step 1 (Send an email): {{ticket.nope}} isn't a placeholder. Check the spelling against the list.", messages);
        Assert.Contains(messages, m => m.StartsWith("Then, step 2 (Send a web request): that address points inside your network", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rollback_publishes_an_older_version_and_requires_manage_privilege()
    {
        var def = new WorkflowDefinition { Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated }, Actions = [Act(ActionTypes.AddTags, ("tags", "v1"))] };
        var wf = await PublishAsync("Versioned", def);
        await Host.RunAsync(SwitchboardTestHost.As(Admin), async sp =>
        {
            var service = sp.GetRequiredService<Workflows.WorkflowService>();
            def.Actions[0].Params["tags"] = "v2";
            await service.SaveDraftAsync(wf.Id, Host.Workspace.Id, wf.Name, null, def);
            await service.PublishAsync(wf.Id);
            var back = await service.RollbackAsync(wf.Id, 1);
            Assert.Equal(3, back.Number);
            Assert.Equal("Rolled back to version 1", back.Note);
            var row = await service.GetAsync(wf.Id);
            Assert.Contains("v1", row.PublishedJson);
            Assert.Equal(row.PublishedJson, row.DraftJson);
        });

        await Assert.ThrowsAsync<ForbiddenException>(() => Host.RunAsync(SwitchboardTestHost.As(Agent), sp =>
            sp.GetRequiredService<Workflows.WorkflowService>().PublishAsync(wf.Id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Host.RunAsync(SwitchboardTestHost.As(Agent), sp =>
            sp.GetRequiredService<Workflows.WorkflowService>().ListAsync(Host.Workspace.Id)));
    }

    [Fact]
    public async Task Export_leaves_out_secrets_and_import_creates_a_disabled_draft()
    {
        var wf = await PublishAsync("Hook", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Actions = [Act(ActionTypes.WebRequest, ("method", "POST"), ("url", "https://hooks.example.com/x"), ("authHeader", "X-Api-Key"), ("authValue", "s3cret"))],
        });
        await Host.RunAsync(SwitchboardTestHost.As(Admin), async sp =>
        {
            var service = sp.GetRequiredService<Workflows.WorkflowService>();
            var json = await service.ExportAsync(wf.Id);
            Assert.DoesNotContain("s3cret", json);
            Assert.DoesNotContain("enc:", json);
            var imported = await service.ImportAsync(Host.Workspace.Id, json);
            Assert.Equal("Hook (2)", imported.Name);
            Assert.False(imported.IsEnabled);
            Assert.Null(imported.PublishedVersion);
            await Assert.ThrowsAsync<ValidationException>(() => service.ImportAsync(Host.Workspace.Id, "{\"hello\":1}"));
        });
    }
}
