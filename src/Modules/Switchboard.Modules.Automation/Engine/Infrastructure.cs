using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Automation.Definitions;
using Switchboard.Modules.Automation.Domain;

namespace Switchboard.Modules.Automation.Engine;

/// <summary>Setting keys (per instance, optionally overridden per workspace).</summary>
public static class AutomationSettingKeys
{
    /// <summary>bool. Instance and workspace values both pause (SR-AUT-2).</summary>
    public const string Paused = "automation.paused";
    /// <summary><c>queue</c> (default) or <c>drop</c>: what happens to events while paused.</summary>
    public const string PauseMode = "automation.pause_mode";
    public const string MaxCascadeDepth = "automation.max_cascade_depth";
    public const string FailureThreshold = "automation.failure_threshold";
    /// <summary>Runs per workflow per 10 minutes.</summary>
    public const string WorkflowRateLimit = "automation.workflow_rate_limit";
    /// <summary>Automation runs per ticket per 10 minutes, across all workflows.</summary>
    public const string TicketRateLimit = "automation.ticket_rate_limit";
}

public sealed record AutomationLimits(int MaxCascadeDepth, int FailureThreshold, int WorkflowRateLimit, int TicketRateLimit)
{
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);
}

public sealed class AutomationSettings(ISettingsService settings)
{
    public const int DefaultMaxCascadeDepth = 3;
    public const int DefaultFailureThreshold = 5;
    public const int DefaultWorkflowRateLimit = 300;
    public const int DefaultTicketRateLimit = 20;

    public async Task<AutomationLimits> LimitsAsync(Guid? workspaceId, CancellationToken ct = default) => new(
        Math.Clamp(await settings.GetAsync(AutomationSettingKeys.MaxCascadeDepth, DefaultMaxCascadeDepth, workspaceId, ct), 1, 10),
        Math.Clamp(await settings.GetAsync(AutomationSettingKeys.FailureThreshold, DefaultFailureThreshold, workspaceId, ct), 1, 100),
        Math.Clamp(await settings.GetAsync(AutomationSettingKeys.WorkflowRateLimit, DefaultWorkflowRateLimit, workspaceId, ct), 1, 100_000),
        Math.Clamp(await settings.GetAsync(AutomationSettingKeys.TicketRateLimit, DefaultTicketRateLimit, workspaceId, ct), 1, 10_000));

    /// <summary>Paused when the instance or the workspace is paused.</summary>
    public async Task<bool> IsPausedAsync(Guid? workspaceId, CancellationToken ct = default)
    {
        if (await settings.GetAsync(AutomationSettingKeys.Paused, false, null, ct))
        {
            return true;
        }
        return workspaceId is not null && await settings.GetAsync(AutomationSettingKeys.Paused, false, workspaceId, ct);
    }

    public async Task<bool> QueueWhilePausedAsync(Guid? workspaceId, CancellationToken ct = default) =>
        await settings.GetAsync(AutomationSettingKeys.PauseMode, "queue", workspaceId, ct) != "drop";
}

/// <summary>
/// The automation run executing on this async flow. The lineage recorder reads it inside
/// <c>SaveChanges</c> to tag the events the run raises with the run's cascade.
/// </summary>
public static class AutomationRunContext
{
    private static readonly AsyncLocal<RunFrame?> Frame = new();

    public sealed record RunFrame(Guid? RunId, Guid CascadeRootId);

    public static RunFrame? Current => Frame.Value;

    public static IDisposable Begin(Guid? runId, Guid cascadeRootId)
    {
        var previous = Frame.Value;
        Frame.Value = new RunFrame(runId, cascadeRootId);
        return new Restore(previous);
    }

    private sealed class Restore(RunFrame? previous) : IDisposable
    {
        public void Dispose() => Frame.Value = previous;
    }
}

/// <summary>Encrypts secrets stored in definitions (web request auth headers).</summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private const string Prefix = "enc:";
    private readonly IDataProtector _protector = provider.CreateProtector("Switchboard.Automation.Secrets.v1");

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public string Protect(string plain) => Prefix + _protector.Protect(plain);

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }
        if (!IsProtected(stored))
        {
            return stored;
        }
        try
        {
            return _protector.Unprotect(stored[Prefix.Length..]);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public const string SetFlag = "authValueSet";

    /// <summary>Copy for the editor or an export: secrets removed, with a flag saying one is stored.</summary>
    public static WorkflowDefinition Redact(WorkflowDefinition def, bool keepFlag)
    {
        var copy = def.Clone();
        RedactActions(copy.Actions, keepFlag);
        return copy;
    }

    public static void RedactActions(List<ActionDefinition> actions, bool keepFlag)
    {
        foreach (var action in actions.Where(a => a.Type == ActionTypes.WebRequest))
        {
            var has = !string.IsNullOrEmpty(action.Get("authValue"));
            action.Params["authValue"] = "";
            action.Params.Remove(SetFlag);
            if (keepFlag && has)
            {
                action.Params[SetFlag] = "true";
            }
        }
    }

    /// <summary>
    /// Prepares an edited definition for storage: new secrets are encrypted, and an empty secret with the
    /// "stored" flag keeps the previously stored value of the same action.
    /// </summary>
    public void ApplyIncoming(List<ActionDefinition> incoming, IReadOnlyList<ActionDefinition> previous)
    {
        foreach (var action in incoming)
        {
            var keep = action.Params.Remove(SetFlag, out var flag) && flag == "true";
            if (action.Type != ActionTypes.WebRequest)
            {
                action.Params.Remove("authValue");
                continue;
            }
            var value = action.Get("authValue");
            if (value is not null)
            {
                action.Params["authValue"] = IsProtected(value)
                    ? previous.FirstOrDefault(p => p.Id == action.Id)?.Get("authValue")
                    : Protect(value);
            }
            else if (keep)
            {
                action.Params["authValue"] = previous.FirstOrDefault(p => p.Id == action.Id && p.Type == ActionTypes.WebRequest)?.Get("authValue");
            }
            else
            {
                action.Params.Remove("authValue");
            }
        }
    }
}

/// <summary>Sends each admin alert at most once (deduplication key), to org and workspace admins.</summary>
public sealed class AlertService(IServiceScopeFactory scopes, TimeProvider time, ILogger<AlertService> logger)
{
    public const string ManagePrivilege = "automation.manage";

    public async Task SendOnceAsync(string key, Guid? workspaceId, string subject, string text, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<SwitchboardDbContext>();
        using var _ = sp.GetRequiredService<IActorAccessor>().BeginScope(Actor.System("Automation alerts"));
        db.Set<AutAlert>().Add(new AutAlert { Key = key.Length > 200 ? key[..200] : key, Message = Truncate(text, 2000), CreatedAt = time.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Sql.IsUniqueViolation(ex))
        {
            return;
        }

        var recipients = await RecipientsAsync(db, workspaceId, ct);
        var html = sp.GetRequiredService<IHtmlContent>();
        var sender = sp.GetRequiredService<ISystemEmailSender>();
        foreach (var to in recipients)
        {
            try
            {
                await sender.SendAsync(new OutboundEmail([to], subject, html.FromPlainText(text), text,
                    Headers: new Dictionary<string, string> { ["Auto-Submitted"] = "auto-generated" }), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Couldn't send automation alert {Key} to {To}", key, to);
            }
        }
        logger.LogWarning("Automation alert: {Subject}", subject);
    }

    public static async Task<List<string>> RecipientsAsync(SwitchboardDbContext db, Guid? workspaceId, CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().Select(r => new { r.Id, r.Privileges }).ToListAsync(ct);
        var roleIds = roles.Where(r => r.Privileges.Contains(ManagePrivilege) || r.Privileges.Contains(Privileges.WorkspaceAdmin)).Select(r => r.Id).ToList();
        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Email != null
                && (u.IsOrgAdmin || u.Memberships.Any(m => roleIds.Contains(m.RoleId) && (workspaceId == null || m.WorkspaceId == workspaceId))))
            .Select(u => u.Email!)
            .Distinct()
            .OrderBy(e => e)
            .Take(50)
            .ToListAsync(ct);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>An event (or clock tick) that may start workflows for one ticket.</summary>
public sealed record TriggerEvent
{
    public required string TriggerKey { get; init; }
    public required Guid TicketId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required Guid EventId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public int CascadeDepth { get; init; }
    public ActorSnapshot? Actor { get; init; }
    public IReadOnlyList<FieldChange> Changes { get; init; } = [];
    public Guid? ConversationId { get; init; }
    public ConversationKind? ConversationKind { get; init; }
    /// <summary>For assignment events: the group changed and no agent was chosen.</summary>
    public bool LandedInGroupWithoutAgent { get; init; }
    /// <summary>For time-based triggers: the clock value that crossed the threshold.</summary>
    public DateTimeOffset? Anchor { get; init; }
    public string Summary { get; init; } = "";

    public string RunKey(Guid workflowId) => $"evt:{EventId:N}:{workflowId:N}";
}

/// <summary>Triggers offered in the builder. Other modules' events join through <see cref="ExternalTrigger"/>.</summary>
public sealed class AutomationTriggerRegistry(IEnumerable<ExternalTrigger> external)
{
    public IReadOnlyList<CatalogTrigger> Available { get; } =
    [
        new(TriggerKeys.TicketCreated, "A ticket is created", "Runs once for every new ticket, from any channel."),
        new(TriggerKeys.TicketUpdated, "A ticket is updated", "Runs when properties change. Narrow it to specific fields below."),
        new(TriggerKeys.Conversation, "A reply or note is added", "Runs when the requester replies, an agent replies, or someone adds a note."),
        new(TriggerKeys.StatusChanged, "The status changes", "Runs whenever the ticket moves to another status."),
        new(TriggerKeys.Assigned, "The ticket is assigned", "Runs when the group or agent changes."),
        new(TriggerKeys.Time, "Time passes", "A supervisor rule: checked every few minutes against open tickets."),
        .. external.DistinctBy(e => e.Key).Select(e => new CatalogTrigger(e.Key, e.Label, e.Hint)),
    ];
}

/// <summary>A domain event from another module that can start workflows. Registered by convention at startup.</summary>
public sealed record ExternalTrigger(string Key, string Label, string Hint, Type EventType);

internal static class Sql
{
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    /// <summary>Stable GUID from parts, for deterministic event and cascade ids.</summary>
    public static Guid DeterministicGuid(params object?[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
