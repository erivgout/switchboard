using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Switchboard.Core;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Scheduling;

namespace Switchboard.Modules.Automation;

internal static class AutomationSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<SwitchboardDbContext>();
        if (!await db.TicketSources.AnyAsync(s => s.Key == AutomationSources.Scheduler, ct))
        {
            var order = await db.TicketSources.MaxAsync(s => (int?)s.Order, ct) ?? 0;
            db.TicketSources.Add(new TicketSource { Key = AutomationSources.Scheduler, Name = "Scheduled", Icon = "bi-calendar-event", IsSystem = true, Order = order + 1 });
            await db.SaveChangesAsync(ct);
        }

        var demo = services.GetService<IOptions<SwitchboardOptions>>()?.Value.DemoData == true
            && services.GetService<IHostEnvironment>()?.IsDevelopment() == true;
        if (demo)
        {
            await SeedExamplesAsync(db, services.GetRequiredService<TimeProvider>(), ct);
        }
    }

    /// <summary>Example workflows and scenarios for development instances, built only from Core's default data.</summary>
    private static async Task SeedExamplesAsync(SwitchboardDbContext db, TimeProvider time, CancellationToken ct)
    {
        var workspace = await db.Workspaces.FirstAsync(w => w.IsPrimary, ct);
        if (await db.Set<AutWorkflow>().AnyAsync(w => w.WorkspaceId == workspace.Id, ct))
        {
            return;
        }
        var groups = await db.AgentGroups.Where(g => g.WorkspaceId == workspace.Id).ToDictionaryAsync(g => g.Name, g => g.Id.ToString(), ct);
        var statuses = await db.TicketStatuses.Where(s => s.WorkspaceId == workspace.Id).ToDictionaryAsync(s => s.Name, s => s.Id.ToString(), ct);
        var network = (await db.TicketCategories.Where(c => c.WorkspaceId == workspace.Id && c.Name == "Network" && c.ParentId == null).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct))?.ToString();
        if (!groups.ContainsKey("Network") || !groups.ContainsKey("Hardware") || !statuses.ContainsKey("Pending") || network is null)
        {
            return;
        }
        var now = time.GetUtcNow();
        var order = 0;

        AutWorkflow Workflow(string name, string description, WorkflowDefinition def, bool publish)
        {
            var json = def.ToJson();
            var wf = new AutWorkflow
            {
                WorkspaceId = workspace.Id,
                Name = name,
                Description = description,
                Order = ++order,
                DraftJson = json,
                IsEnabled = publish,
                PublishedVersion = publish ? 1 : null,
                PublishedJson = publish ? json : null,
                PublishedTrigger = publish ? def.Trigger.Event : null,
                PublishedAt = publish ? now : null,
            };
            db.Set<AutWorkflow>().Add(wf);
            if (publish)
            {
                db.Set<AutWorkflowVersion>().Add(new AutWorkflowVersion
                {
                    WorkflowId = wf.Id, Number = 1, DefinitionJson = json, Note = "First version", CreatedByName = "Setup", CreatedAt = now,
                });
            }
            return wf;
        }

        static ActionDefinition A(string type, params (string Key, string Value)[] ps) =>
            new() { Type = type, Params = ps.ToDictionary(p => p.Key, p => (string?)p.Value) };

        Workflow("Route network problems to the Network team", "New tickets filed under Network go straight to the people who fix them.", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Conditions = new ConditionSet
            {
                Groups =
                [
                    new ConditionGroup
                    {
                        Rules =
                        [
                            new Condition { Field = "ticket.category", Op = Operators.Is, Value = network },
                            new Condition { Field = "ticket.group", Op = Operators.IsNot, Value = groups["Network"] },
                        ],
                    },
                ],
            },
            Actions =
            [
                A(ActionTypes.SetGroup, ("groupId", groups["Network"])),
                A(ActionTypes.AddNote, ("body", "Routed to Network because the category is {{ticket.category}}.")),
            ],
        }, publish: true);

        Workflow("Flag requests from VIPs", "Raises priority and tags tickets from people marked as VIPs.", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketCreated },
            Conditions = new ConditionSet
            {
                Groups =
                [
                    new ConditionGroup
                    {
                        Rules =
                        [
                            new Condition { Field = "requester.vip", Op = Operators.Is, Value = "true" },
                            new Condition { Field = "ticket.priority", Op = Operators.AtMost, Value = "Medium" },
                        ],
                    },
                ],
            },
            Actions = [A(ActionTypes.SetPriority, ("priority", "High")), A(ActionTypes.AddTags, ("tags", "vip"))],
        }, publish: true);

        Workflow("Remind requesters about pending tickets", "After three days waiting on the requester, send a friendly nudge.", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.Time, Clock = TimeClocks.InStatus, Hours = 72 },
            Conditions = new ConditionSet
            {
                Groups = [new ConditionGroup { Rules = [new Condition { Field = "ticket.status", Op = Operators.Is, Value = statuses["Pending"] }] }],
            },
            Actions =
            [
                A(ActionTypes.SendEmail, ("recipient", "requester"), ("subject", "Still need help with {{ticket.id}}?"),
                    ("body", "Hi {{requester.name}},\n\nWe're waiting to hear back from you about \"{{ticket.subject}}\". Reply to this email and we'll pick it up again.\n\n{{ticket.url}}")),
                A(ActionTypes.AddNote, ("body", "Sent the requester a reminder after 72 hours in Pending.")),
            ],
        }, publish: true);

        Workflow("Post major incidents to the team chat", "Draft: add the chat webhook address and an auth header, then publish.", new WorkflowDefinition
        {
            Trigger = new TriggerDefinition { Event = TriggerKeys.TicketUpdated, Changes = [new FieldChangeFilter { Field = "ticket.major_incident", To = "true" }] },
            Actions =
            [
                A(ActionTypes.WebRequest, ("method", "POST"), ("url", "https://chat.example.com/hooks/incidents"), ("timeoutSeconds", "10"),
                    ("body", "{\"text\": \"Major incident {{ticket.id}}: {{ticket.subject}}\", \"link\": \"{{ticket.url}}\"}")),
            ],
        }, publish: false);

        db.Set<AutScenario>().AddRange(
            new AutScenario
            {
                WorkspaceId = workspace.Id,
                Name = "Send to Hardware for repair",
                Description = "Moves the ticket to Hardware, tags it and logs a note.",
                Visibility = ScenarioVisibility.Global,
                ActionsJson = JsonSerializer.Serialize(new List<ActionDefinition>
                {
                    A(ActionTypes.SetGroup, ("groupId", groups["Hardware"])),
                    A(ActionTypes.AddTags, ("tags", "repair")),
                    A(ActionTypes.AddNote, ("body", "Handed to Hardware for a repair or replacement.")),
                }, WorkflowDefinition.Json),
            },
            new AutScenario
            {
                WorkspaceId = workspace.Id,
                Name = "Waiting on the requester",
                Description = "Sets the status to Pending and adds a follow-up task.",
                Visibility = ScenarioVisibility.Global,
                ActionsJson = JsonSerializer.Serialize(new List<ActionDefinition>
                {
                    A(ActionTypes.SetStatus, ("statusId", statuses["Pending"])),
                    A(ActionTypes.CreateTask, ("title", "Follow up with {{requester.name}}"), ("dueInHours", "48")),
                }, WorkflowDefinition.Json),
            });
        await db.SaveChangesAsync(ct);
    }
}
