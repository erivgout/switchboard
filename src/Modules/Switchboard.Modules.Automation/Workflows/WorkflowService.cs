using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;
using Switchboard.Modules.Automation.Engine;

namespace Switchboard.Modules.Automation.Workflows;

/// <summary>Portable file format for import and export. Secrets are never included.</summary>
public sealed class WorkflowExport
{
    public const string FormatName = "switchboard.workflow";

    public string? Format { get; set; }
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Module { get; set; } = WorkflowModules.Tickets;
    public WorkflowDefinition? Definition { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExportedAt { get; set; }
}

/// <summary>Workflow lifecycle (FR-AUT-5): drafts, publishing with versions, rollback, enable, order, clone, import, export.</summary>
public sealed class WorkflowService(
    SwitchboardDbContext db,
    IPermissionService permissions,
    IActorAccessor actors,
    IAuditLog audit,
    CatalogLoader catalogs,
    SecretProtector secrets,
    Net.OutboundNetworkPolicy network,
    TimeProvider time)
{
    public const string ManagePrivilege = AlertService.ManagePrivilege;
    public const int MaxImportBytes = 256 * 1024;

    public DbSet<AutWorkflow> Workflows => db.Set<AutWorkflow>();

    public async Task<AutWorkflow> GetAsync(Guid id, CancellationToken ct = default)
    {
        var wf = await Workflows.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw new NotFoundException();
        await permissions.DemandAsync(ManagePrivilege, wf.WorkspaceId, ct);
        return wf;
    }

    public async Task<List<AutWorkflow>> ListAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await permissions.DemandAsync(ManagePrivilege, workspaceId, ct);
        return await Workflows.AsNoTracking().Where(w => w.WorkspaceId == workspaceId && w.Module == WorkflowModules.Tickets)
            .OrderBy(w => w.Order).ThenBy(w => w.CreatedAt).ToListAsync(ct);
    }

    /// <summary>The definition as the editor should see it: secrets removed, with a flag where one is stored.</summary>
    public static WorkflowDefinition ForEditor(string json) => SecretProtector.Redact(WorkflowDefinition.Parse(json), keepFlag: true);

    public async Task<AutWorkflow> SaveDraftAsync(Guid? id, Guid workspaceId, string name, string? description, WorkflowDefinition incoming, CancellationToken ct = default)
    {
        await permissions.DemandAsync(ManagePrivilege, workspaceId, ct);
        incoming.Normalize();
        CheckSize(incoming);
        name = (name ?? "").Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (name.Length is 0 or > 200)
        {
            throw new ValidationException("name", name.Length == 0 ? "Give the workflow a name." : "Keep the name under 200 characters.");
        }
        if (description?.Length > 1000)
        {
            throw new ValidationException("description", "Keep the description under 1,000 characters.");
        }

        AutWorkflow wf;
        if (id is { } existing)
        {
            wf = await GetAsync(existing, ct);
            workspaceId = wf.WorkspaceId;
        }
        else
        {
            var order = await Workflows.Where(w => w.WorkspaceId == workspaceId && w.Module == WorkflowModules.Tickets).MaxAsync(w => (int?)w.Order, ct) ?? 0;
            wf = new AutWorkflow { WorkspaceId = workspaceId, Order = order + 1, CreatedById = actors.Current.UserId };
            Workflows.Add(wf);
        }

        if (await Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Name == name && w.Id != wf.Id, ct))
        {
            throw new ValidationException("name", $"Another workflow is already called \"{name}\". Choose a different name.");
        }

        var previous = WorkflowDefinition.Parse(wf.DraftJson);
        secrets.ApplyIncoming(incoming.Actions, previous.Actions);
        wf.Name = name;
        wf.Description = description;
        wf.DraftJson = incoming.ToJson();
        wf.UpdatedById = actors.Current.UserId;
        audit.Record(id is null ? "automation.workflow_created" : "automation.workflow_saved", "workflow", wf.Id.ToString(), new { wf.Name });
        await db.SaveChangesAsync(ct);
        return wf;
    }

    /// <summary>Validates the draft and makes it the live version. Throws <see cref="ValidationException"/> listing every problem.</summary>
    public async Task<AutWorkflowVersion> PublishAsync(Guid id, CancellationToken ct = default)
    {
        var wf = await GetAsync(id, ct);
        var def = WorkflowDefinition.Parse(wf.DraftJson);
        await DemandValidAsync(wf.WorkspaceId, def, ct);
        var version = await AddVersionAsync(wf, def, wf.PublishedVersion is null ? "First version" : "Published", ct);
        if (wf.PublishedVersion is null || version.Number == 1)
        {
            wf.IsEnabled = true;
        }
        Apply(wf, version);
        audit.Record("automation.workflow_published", "workflow", wf.Id.ToString(), new { wf.Name, version = version.Number });
        await db.SaveChangesAsync(ct);
        return version;
    }

    /// <summary>Publishes a copy of an older version and makes it the draft too.</summary>
    public async Task<AutWorkflowVersion> RollbackAsync(Guid id, int number, CancellationToken ct = default)
    {
        var wf = await GetAsync(id, ct);
        var old = await db.Set<AutWorkflowVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.WorkflowId == id && v.Number == number, ct)
            ?? throw new NotFoundException("That version doesn't exist.");
        var def = WorkflowDefinition.Parse(old.DefinitionJson);
        await DemandValidAsync(wf.WorkspaceId, def, ct);
        var version = await AddVersionAsync(wf, def, $"Rolled back to version {number}", ct);
        Apply(wf, version);
        wf.DraftJson = version.DefinitionJson;
        audit.Record("automation.workflow_rolled_back", "workflow", wf.Id.ToString(), new { wf.Name, to = number, version = version.Number });
        await db.SaveChangesAsync(ct);
        return version;
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var wf = await GetAsync(id, ct);
        if (enabled && wf.PublishedJson is null)
        {
            throw new ValidationException("enabled", "Publish the workflow before turning it on.");
        }
        wf.IsEnabled = enabled;
        if (enabled)
        {
            wf.ConsecutiveFailures = 0;
            wf.AutoDisabledAt = null;
            wf.DisabledReason = null;
        }
        audit.Record(enabled ? "automation.workflow_enabled" : "automation.workflow_disabled", "workflow", wf.Id.ToString(), new { wf.Name });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Moves a workflow one place earlier (-1) or later (+1) in execution order.</summary>
    public async Task MoveAsync(Guid id, int direction, CancellationToken ct = default)
    {
        var wf = await GetAsync(id, ct);
        var all = await Workflows.Where(w => w.WorkspaceId == wf.WorkspaceId && w.Module == wf.Module)
            .OrderBy(w => w.Order).ThenBy(w => w.CreatedAt).ToListAsync(ct);
        var index = all.FindIndex(w => w.Id == id);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= all.Count)
        {
            return;
        }
        (all[index], all[target]) = (all[target], all[index]);
        for (var i = 0; i < all.Count; i++)
        {
            all[i].Order = i + 1;
        }
        audit.Record("automation.workflow_reordered", "workflow", wf.Id.ToString(), new { wf.Name, position = target + 1 });
        await db.SaveChangesAsync(ct);
    }

    public async Task<AutWorkflow> CloneAsync(Guid id, CancellationToken ct = default)
    {
        var source = await GetAsync(id, ct);
        var name = await UniqueNameAsync(source.WorkspaceId, $"Copy of {source.Name}", ct);
        var order = await Workflows.Where(w => w.WorkspaceId == source.WorkspaceId).MaxAsync(w => (int?)w.Order, ct) ?? 0;
        var copy = new AutWorkflow
        {
            WorkspaceId = source.WorkspaceId,
            Module = source.Module,
            Name = name,
            Description = source.Description,
            Order = order + 1,
            DraftJson = source.DraftJson,
            CreatedById = actors.Current.UserId,
            UpdatedById = actors.Current.UserId,
        };
        Workflows.Add(copy);
        audit.Record("automation.workflow_cloned", "workflow", copy.Id.ToString(), new { from = source.Name, copy.Name });
        await db.SaveChangesAsync(ct);
        return copy;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var wf = await GetAsync(id, ct);
        Workflows.Remove(wf);
        audit.Record("automation.workflow_deleted", "workflow", wf.Id.ToString(), new { wf.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> ExportAsync(Guid id, CancellationToken ct = default)
    {
        var wf = await GetAsync(id, ct);
        var export = new WorkflowExport
        {
            Format = WorkflowExport.FormatName,
            Name = wf.Name,
            Description = wf.Description,
            Module = wf.Module,
            Definition = SecretProtector.Redact(WorkflowDefinition.Parse(wf.DraftJson), keepFlag: false),
            ExportedAt = time.GetUtcNow(),
        };
        audit.Record("automation.workflow_exported", "workflow", wf.Id.ToString(), new { wf.Name });
        await db.SaveChangesAsync(ct);
        return JsonSerializer.Serialize(export, new JsonSerializerOptions(WorkflowDefinition.Json) { WriteIndented = true });
    }

    /// <summary>Creates a draft (turned off) from an exported file. Secrets in the file are ignored.</summary>
    public async Task<AutWorkflow> ImportAsync(Guid workspaceId, string json, CancellationToken ct = default)
    {
        await permissions.DemandAsync(ManagePrivilege, workspaceId, ct);
        WorkflowExport? file;
        try
        {
            file = JsonSerializer.Deserialize<WorkflowExport>(json, WorkflowDefinition.Json);
        }
        catch (JsonException)
        {
            file = null;
        }
        if (file is null || file.Format != WorkflowExport.FormatName || file.Definition is null)
        {
            throw new ValidationException("file", "That file isn't a Switchboard workflow export.");
        }
        if (file.Module != WorkflowModules.Tickets)
        {
            throw new ValidationException("file", "That workflow is for records this version can't automate yet.");
        }
        file.Definition.Normalize();
        SecretProtector.RedactActions(file.Definition.Actions, keepFlag: false);
        var name = await UniqueNameAsync(workspaceId, string.IsNullOrWhiteSpace(file.Name) ? "Imported workflow" : file.Name.Trim()[..Math.Min(file.Name.Trim().Length, 180)], ct);
        var wf = await SaveDraftAsync(null, workspaceId, name, file.Description, file.Definition, ct);
        audit.Record("automation.workflow_imported", "workflow", wf.Id.ToString(), new { wf.Name });
        await db.SaveChangesAsync(ct);
        return wf;
    }

    public async Task<List<AutWorkflowVersion>> VersionsAsync(Guid id, CancellationToken ct = default)
    {
        await GetAsync(id, ct);
        return await db.Set<AutWorkflowVersion>().AsNoTracking().Where(v => v.WorkflowId == id).OrderByDescending(v => v.Number).ToListAsync(ct);
    }

    public async Task<List<string>> ValidateAsync(Guid workspaceId, WorkflowDefinition def, CancellationToken ct = default) =>
        DefinitionValidator.ValidateWorkflow(def, await catalogs.LoadAsync(workspaceId, ct), network);

    private async Task DemandValidAsync(Guid workspaceId, WorkflowDefinition def, CancellationToken ct)
    {
        var errors = await ValidateAsync(workspaceId, def, ct);
        if (errors.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["definition"] = errors.ToArray() });
        }
    }

    private async Task<AutWorkflowVersion> AddVersionAsync(AutWorkflow wf, WorkflowDefinition def, string note, CancellationToken ct)
    {
        var number = (await db.Set<AutWorkflowVersion>().Where(v => v.WorkflowId == wf.Id).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
        var actor = actors.Current;
        var version = new AutWorkflowVersion
        {
            WorkflowId = wf.Id,
            Number = number,
            DefinitionJson = def.ToJson(),
            Note = note,
            CreatedById = actor.UserId,
            CreatedByName = actor.DisplayName,
            CreatedAt = time.GetUtcNow(),
        };
        db.Set<AutWorkflowVersion>().Add(version);
        return version;
    }

    private void Apply(AutWorkflow wf, AutWorkflowVersion version)
    {
        var def = WorkflowDefinition.Parse(version.DefinitionJson);
        wf.PublishedVersion = version.Number;
        wf.PublishedJson = version.DefinitionJson;
        wf.PublishedTrigger = def.Trigger.Event;
        wf.PublishedAt = version.CreatedAt;
        // The draft matches the live version after publishing, byte for byte.
        wf.DraftJson = version.DefinitionJson;
        wf.UpdatedById = actors.Current.UserId;
    }

    private async Task<string> UniqueNameAsync(Guid workspaceId, string name, CancellationToken ct)
    {
        var candidate = name;
        for (var i = 2; await Workflows.AnyAsync(w => w.WorkspaceId == workspaceId && w.Name == candidate, ct); i++)
        {
            candidate = $"{name} ({i})";
        }
        return candidate;
    }

    private static void CheckSize(WorkflowDefinition def)
    {
        if (def.Conditions.Groups.Count > DefinitionValidator.MaxGroups
            || def.Conditions.Groups.Any(g => g.Rules.Count > DefinitionValidator.MaxRulesPerGroup)
            || def.Actions.Count > DefinitionValidator.MaxActions
            || def.ToJson().Length > MaxImportBytes)
        {
            throw new ValidationException("definition",
                $"This workflow is too large. Use at most {DefinitionValidator.MaxGroups} condition groups, {DefinitionValidator.MaxRulesPerGroup} conditions per group and {DefinitionValidator.MaxActions} actions.");
        }
    }
}
