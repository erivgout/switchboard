using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Core.Tickets;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;

namespace Switchboard.Modules.Automation.Scenarios;

public sealed record ScenarioInput(Guid WorkspaceId, string Name, string? Description, ScenarioVisibility Visibility, Guid? GroupId, List<ActionDefinition> Actions);

public sealed record ScenarioRunResult(string Name, IReadOnlyList<string> Done, IReadOnlyList<string> Skipped);

/// <summary>
/// Scenario automations (FR-AUT-6). Agents keep personal scenarios; group and global ones need
/// <c>automation.manage</c>. Running one applies its actions as the agent, through the ticket service,
/// all or nothing.
/// </summary>
public sealed class ScenarioService(
    SwitchboardDbContext db,
    IPermissionService permissions,
    IAuditLog audit,
    ITicketService tickets,
    CatalogLoader catalogs,
    TicketFactsLoader facts,
    ActionRunner runner)
{
    public const string ManagePrivilege = AlertService.ManagePrivilege;

    private async Task<UserPermissions> AgentAsync(CancellationToken ct)
    {
        var p = await permissions.CurrentAsync(ct);
        if (p is not { IsAgent: true })
        {
            throw new ForbiddenException("Only agents can use scenarios.");
        }
        return p;
    }

    /// <summary>Scenarios the current agent can see and run, optionally in one workspace.</summary>
    public async Task<List<AutScenario>> VisibleAsync(Guid? workspaceId, CancellationToken ct = default)
    {
        var p = await AgentAsync(ct);
        var workspaces = p.WorkspacesWith(Privileges.TicketsView).Where(w => workspaceId is null || w == workspaceId).ToList();
        if (p.IsOrgAdmin && workspaceId is { } only && !workspaces.Contains(only))
        {
            workspaces.Add(only);
        }
        var groups = p.GroupIds.Concat(p.ObserverGroupIds).ToList();
        var uid = p.UserId;
        return await db.Set<AutScenario>().AsNoTracking()
            .Where(s => (workspaceId == null || s.WorkspaceId == workspaceId)
                && ((s.Visibility == ScenarioVisibility.Personal && s.OwnerUserId == uid)
                    || (s.Visibility == ScenarioVisibility.Global && workspaces.Contains(s.WorkspaceId))
                    || (s.Visibility == ScenarioVisibility.Group && s.GroupId != null && groups.Contains(s.GroupId.Value) && workspaces.Contains(s.WorkspaceId))))
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<bool> CanEditAsync(AutScenario scenario, CancellationToken ct = default)
    {
        var p = await AgentAsync(ct);
        return scenario.Visibility == ScenarioVisibility.Personal
            ? scenario.OwnerUserId == p.UserId
            : p.Has(ManagePrivilege, scenario.WorkspaceId);
    }

    public async Task<bool> CanManageSharedAsync(Guid workspaceId, CancellationToken ct = default) =>
        (await AgentAsync(ct)).Has(ManagePrivilege, workspaceId);

    public async Task<AutScenario> GetForEditAsync(Guid id, CancellationToken ct = default)
    {
        var scenario = await db.Set<AutScenario>().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException();
        if (!await CanEditAsync(scenario, ct))
        {
            // Someone else's personal scenario is invisible; a shared one is visible but read-only.
            throw scenario.Visibility == ScenarioVisibility.Personal ? new NotFoundException() : new ForbiddenException("Only automation admins can change shared scenarios.");
        }
        return scenario;
    }

    public async Task<AutScenario> SaveAsync(Guid? id, ScenarioInput input, CancellationToken ct = default)
    {
        var p = await AgentAsync(ct);
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
        {
            throw new ValidationException("name", name.Length == 0 ? "Give the scenario a name." : "Keep the name under 200 characters.");
        }
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (description?.Length > 1000)
        {
            throw new ValidationException("description", "Keep the description under 1,000 characters.");
        }
        if (!p.Has(Privileges.TicketsView, input.WorkspaceId))
        {
            throw new ForbiddenException();
        }
        if (input.Visibility != ScenarioVisibility.Personal && !p.Has(ManagePrivilege, input.WorkspaceId))
        {
            throw new ForbiddenException("Only automation admins can share scenarios with a group or everyone.");
        }
        if (input.Visibility == ScenarioVisibility.Group
            && (input.GroupId is not { } g || !await db.AgentGroups.AnyAsync(x => x.Id == g && x.WorkspaceId == input.WorkspaceId, ct)))
        {
            throw new ValidationException("groupId", "Choose the group that can use this scenario.");
        }

        var actions = input.Actions ?? [];
        foreach (var a in actions)
        {
            a.Params ??= [];
            a.Params.Remove("authValue");
            a.Params.Remove(SecretProtector.SetFlag);
        }
        var errors = DefinitionValidator.ValidateScenario(actions, await catalogs.LoadAsync(input.WorkspaceId, ct));
        if (errors.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["actions"] = errors.ToArray() });
        }

        AutScenario scenario;
        if (id is { } existing)
        {
            scenario = await GetForEditAsync(existing, ct);
            if (scenario.WorkspaceId != input.WorkspaceId)
            {
                throw new ValidationException("workspace", "A scenario can't move to another workspace.");
            }
        }
        else
        {
            scenario = new AutScenario { WorkspaceId = input.WorkspaceId, OwnerUserId = p.UserId };
            db.Set<AutScenario>().Add(scenario);
        }

        scenario.Name = name;
        scenario.Description = description;
        scenario.Visibility = input.Visibility;
        scenario.GroupId = input.Visibility == ScenarioVisibility.Group ? input.GroupId : null;
        scenario.OwnerUserId ??= p.UserId;
        scenario.ActionsJson = JsonSerializer.Serialize(actions, WorkflowDefinition.Json);
        if (scenario.Visibility != ScenarioVisibility.Personal)
        {
            audit.Record(id is null ? "automation.scenario_created" : "automation.scenario_saved", "scenario", scenario.Id.ToString(), new { scenario.Name, scenario.Visibility });
        }
        await db.SaveChangesAsync(ct);
        return scenario;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var scenario = await GetForEditAsync(id, ct);
        db.Set<AutScenario>().Remove(scenario);
        if (scenario.Visibility != ScenarioVisibility.Personal)
        {
            audit.Record("automation.scenario_deleted", "scenario", scenario.Id.ToString(), new { scenario.Name });
        }
        await db.SaveChangesAsync(ct);
    }

    public static List<ActionDefinition> Actions(AutScenario scenario) =>
        JsonSerializer.Deserialize<List<ActionDefinition>>(scenario.ActionsJson, WorkflowDefinition.Json) ?? [];

    /// <summary>Runs a visible scenario on a ticket as the current agent. Every change or none.</summary>
    public async Task<ScenarioRunResult> RunAsync(Guid scenarioId, Guid ticketId, CancellationToken ct = default)
    {
        var ticket = await tickets.GetForAgentAsync(ticketId, ct);
        var scenario = (await VisibleAsync(ticket.WorkspaceId, ct)).FirstOrDefault(s => s.Id == scenarioId) ?? throw new NotFoundException();
        var catalog = await catalogs.LoadAsync(ticket.WorkspaceId, ct);
        var actions = Actions(scenario).Where(a => ActionTypes.ScenarioAllowed.Contains(a.Type)).ToList();
        var done = new List<string>();
        var skipped = new List<string>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await facts.LoadAsync(ticket.Id, null, ct) ?? throw new NotFoundException();
        foreach (var action in actions)
        {
            PlannedStep plan;
            try
            {
                plan = await runner.PlanAsync(action, current, catalog, scenario.Name, ct);
            }
            catch (AutomationActionException ex)
            {
                throw new ValidationException("scenario", $"{scenario.Name} couldn't run: {ex.Message} Nothing was changed.");
            }
            if (plan.NoChange is not null)
            {
                skipped.Add($"{plan.Title}: {plan.NoChange}");
                continue;
            }
            try
            {
                await runner.ApplyAsync(plan, current, ct);
            }
            catch (ValidationException ex)
            {
                throw new ValidationException("scenario", $"{scenario.Name} couldn't run: {ex.Message} Nothing was changed.");
            }
            done.Add(plan.Title);
            current = await facts.LoadAsync(ticket.Id, null, ct) ?? current;
        }
        await tx.CommitAsync(ct);
        return new ScenarioRunResult(scenario.Name, done, skipped);
    }
}
