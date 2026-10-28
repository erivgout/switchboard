using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Switchboard.Core;
using Switchboard.Core.Agents;
using Switchboard.Core.Data;
using Switchboard.Core.Security;
using Switchboard.Core.Services;
using Switchboard.Modules.Mcp.Data;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>
/// Runs one MCP tool call end to end: kill switches and effective permission (MCP-16), rate limits (MCP-80),
/// argument validation, idempotency (MCP-36), human confirmation (§4.6, INV-MCP-4), read-only enforcement
/// (INV-MCP-3), hidden-content stripping (MCP-63), size caps (MCP-81) and audit (MCP-70, INV-MCP-2).
/// </summary>
public sealed class McpToolDispatcher(
    IServiceProvider services,
    McpToolCatalog catalog,
    McpAccessPolicy policy,
    McpRateLimiter rateLimiter,
    McpKillSwitch killSwitch,
    IActorAccessor actors,
    ISettingsService settings,
    SwitchboardDbContext db,
    ILiveNotifier live,
    IOptions<SwitchboardOptions> options,
    TimeProvider time,
    ILogger<McpToolDispatcher> logger)
{
    public const string IdempotencyKeyArgument = "idempotency_key";
    public const string DryRunArgument = "dry_run";

    private static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxCallDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    /// <summary>The tools this caller may see right now, with annotations and schemas (MCP-32).</summary>
    public async Task<(IReadOnlyList<Tool> Tools, McpDenial? Denial)> ListAsync(McpCaller caller, CancellationToken ct)
    {
        var (access, denial) = await policy.EvaluateAsync(caller, ct);
        if (access is null)
        {
            return ([], denial);
        }
        return (catalog.All.Where(t => access.CanUse(t.Definition)).Select(t => ToProtocolTool(t.Definition)).ToList(), null);
    }

    /// <summary>The tools this caller may use, for <c>whoami</c> and the admin screens.</summary>
    public async Task<IReadOnlyList<AgentToolDefinition>> UsableToolsAsync(McpCaller caller, CancellationToken ct)
    {
        var (access, _) = await policy.EvaluateAsync(caller, ct);
        return access is null ? [] : catalog.All.Select(t => t.Definition).Where(access.CanUse).ToList();
    }

    public static Tool ToProtocolTool(AgentToolDefinition d)
    {
        var input = (JsonObject)d.InputSchema.DeepClone();
        if (d.Class != AgentActionClass.Read && input["properties"] is JsonObject props && !props.ContainsKey(IdempotencyKeyArgument))
        {
            props[IdempotencyKeyArgument] = new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = 200,
                ["description"] = "Optional. Any unique string. Repeating a call with the same key within 24 hours returns the first result instead of acting twice.",
            };
        }
        input["$schema"] ??= "https://json-schema.org/draft/2020-12/schema";

        var tool = new Tool
        {
            Name = d.Name,
            Title = d.Title,
            Description = d.Description,
            InputSchema = JsonSerializer.SerializeToElement(input),
            Annotations = new ToolAnnotations
            {
                Title = d.Title,
                ReadOnlyHint = d.Class == AgentActionClass.Read,
                DestructiveHint = d.Class == AgentActionClass.Destructive,
                IdempotentHint = d.Class == AgentActionClass.Read || d.Idempotent,
                OpenWorldHint = d.Class == AgentActionClass.External,
            },
        };
        if (d.OutputSchema is { } output)
        {
            var schema = (JsonObject)output.DeepClone();
            schema["$schema"] ??= "https://json-schema.org/draft/2020-12/schema";
            tool.OutputSchema = JsonSerializer.SerializeToElement(schema);
        }
        return tool;
    }

    /// <summary>Handles a <c>tools/call</c>. Returns null for a tool that doesn't exist (a protocol error).</summary>
    public async Task<ToolOutcome?> CallAsync(McpCaller caller, McpCallChannel channel, string name, JsonObject arguments, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var tool = catalog.Find(name);
        if (tool is null)
        {
            return null;
        }
        var definition = tool.Definition;
        var context = new CallAudit(caller, definition, arguments, channel.SessionId);

        var (access, denial) = await policy.EvaluateAsync(caller, ct);
        if (access is null)
        {
            return await FinishAsync(context, ToolOutcome.Error(denial!.Code, denial.Message), clock);
        }
        context.Caller = access.Caller;

        if (!access.CanUse(definition))
        {
            var outcome = access.Configuration.DisabledTools.Contains(name)
                ? ToolOutcome.Error(McpErrorCodes.DisabledByAdmin, "Your administrator has turned this tool off.")
                : !access.IsAgent && !definition.RequesterAllowed
                    ? ToolOutcome.Error(McpErrorCodes.PermissionDenied, "This tool is only for agents.")
                    : ToolOutcome.Error(McpErrorCodes.PermissionDenied, $"This tool needs the '{definition.Scope}' scope, which this connection wasn't granted.");
            return await FinishAsync(context, outcome, clock, access);
        }

        if (rateLimiter.TryAcquire(caller.UserId, caller.GrantId, caller.ClientId) is { } retryAfter)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            return await FinishAsync(context, ToolOutcome.Error(McpErrorCodes.RateLimited,
                $"Too many calls. Wait {seconds} seconds and try again.",
                new JsonObject { ["retry_after_seconds"] = seconds }), clock, access);
        }

        // The idempotency key belongs to the MCP layer, not the tool.
        var args = (JsonObject)arguments.DeepClone();
        string? idempotencyKey = null;
        if (definition.Class != AgentActionClass.Read && args[IdempotencyKeyArgument] is JsonValue keyValue
            && (definition.InputSchema["properties"] as JsonObject)?.ContainsKey(IdempotencyKeyArgument) != true)
        {
            idempotencyKey = keyValue.TryGetValue<string>(out var k) ? k : null;
            args.Remove(IdempotencyKeyArgument);
            if (idempotencyKey is null or { Length: 0 or > 200 })
            {
                return await FinishAsync(context, ToolOutcome.Error(McpErrorCodes.ValidationFailed, "idempotency_key must be a string of 1 to 200 characters."), clock, access);
            }
        }

        var errors = SchemaValidator.Validate(definition.InputSchema, args);
        if (errors.Count > 0)
        {
            return await FinishAsync(context, ToolOutcome.Error(McpErrorCodes.ValidationFailed, "Some arguments aren't valid. See details.",
                new JsonObject { ["details"] = JsonSerializer.SerializeToNode(errors) }), clock, access);
        }

        var dryRun = (definition.InputSchema["properties"] as JsonObject)?.ContainsKey(DryRunArgument) == true
            && args[DryRunArgument] is JsonValue dr && dr.TryGetValue<bool>(out var isDry) && isDry;

        using var actorScope = actors.BeginScope(access.Actor);
        using var callScope = Tools.McpCallScope.Enter(access);

        var hash = ParameterHash.Compute(name, args);
        if (idempotencyKey is not null && await FindIdempotentAsync(caller, name, idempotencyKey, ct) is { } previous)
        {
            var replay = previous.ParameterHash == hash
                ? Deserialize(previous.ResultJson)
                : ToolOutcome.Error(McpErrorCodes.ValidationFailed, "That idempotency_key was already used with different arguments. Use a new key.");
            return await FinishAsync(context, replay, clock, access);
        }

        var actionClass = definition.Class;
        try
        {
            if (tool is IAgentToolClassifier classifier)
            {
                actionClass = await classifier.ClassifyAsync(args, ct);
            }
        }
        catch (Exception ex) when (MapException(ex) is { } mapped)
        {
            return await FinishAsync(context, mapped, clock, access);
        }
        context.Class = actionClass;

        if (!dryRun)
        {
            var confirmation = await RequiredConfirmationAsync(access, actionClass, ct);
            if (confirmation is not null)
            {
                var elicit = confirmation == "in_client" ? channel.Elicit : null;
                if (elicit is null)
                {
                    var pendingOutcome = await CreatePendingAsync(access, tool, args, hash, actionClass, channel.SessionId, ct);
                    return await FinishAsync(context, pendingOutcome, clock, access);
                }

                var decision = await ConfirmInClientAsync(access, tool, args, hash, actionClass, channel.SessionId, elicit, ct);
                if (decision.Outcome is { } stop)
                {
                    return await FinishAsync(context, stop, clock, access);
                }
                context.PendingActionId = decision.PendingActionId;
            }
        }

        var result = await InvokeAsync(tool, args, new AgentToolContext(dryRun, idempotencyKey), caller, channel, ct);
        if (idempotencyKey is not null && !result.IsError && !dryRun)
        {
            await StoreIdempotentAsync(caller, name, idempotencyKey, hash, result, ct);
        }
        if (context.PendingActionId is { } confirmed)
        {
            await CompletePendingAsync(confirmed, result, ct);
        }
        return await FinishAsync(context, result, clock, access);
    }

    /// <summary>
    /// Runs an action the user approved out of band (MCP-51), as that user through the MCP channel,
    /// after re-checking every kill switch and the parameter hash. Stores the result on the pending action.
    /// </summary>
    public async Task<ToolOutcome> ExecuteApprovedAsync(McpPendingAction action, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var caller = new McpCaller(action.UserId, await UserNameAsync(action.UserId, ct), action.ClientId, action.ClientName, action.GrantId,
            action.Scopes.ToHashSet(StringComparer.Ordinal), action.ConfigurationSlug, action.GrantId is null);
        var args = JsonNode.Parse(action.ArgumentsJson) as JsonObject ?? [];
        var tool = catalog.Find(action.Tool);
        var context = new CallAudit(caller, tool?.Definition ?? new AgentToolDefinition(action.Tool, action.ToolTitle, "", "", action.ActionClass, []), args, action.SessionId)
        {
            PendingActionId = action.Id,
            Class = action.ActionClass,
        };

        ToolOutcome outcome;
        var (access, denial) = await policy.EvaluateAsync(caller, ct);
        if (access is null)
        {
            outcome = ToolOutcome.Error(denial!.Code, denial.Message);
        }
        else if (tool is null || !access.CanUse(tool.Definition))
        {
            outcome = ToolOutcome.Error(McpErrorCodes.DisabledByAdmin, "This tool is no longer available to this connection.");
        }
        else if (ParameterHash.Compute(action.Tool, args) != action.ParameterHash)
        {
            // INV-MCP-4: never run anything but the exact parameters that were confirmed.
            outcome = ToolOutcome.Error(McpErrorCodes.PermissionDenied, "The action's parameters don't match what was confirmed, so it wasn't run.");
        }
        else
        {
            context.Caller = access.Caller;
            using var _ = actors.BeginScope(access.Actor);
            using var __ = Tools.McpCallScope.Enter(access);
            outcome = await InvokeAsync(tool, args, new AgentToolContext(), access.Caller, McpCallChannel.None, ct);
        }

        await CompletePendingAsync(action.Id, outcome, ct);
        return await FinishAsync(context, outcome, clock, access);
    }

    // ------------------------------------------------------------------ confirmation

    /// <summary>Null when no confirmation is needed; otherwise <c>in_client</c> or <c>out_of_band</c> (§4.6).</summary>
    private async Task<string?> RequiredConfirmationAsync(McpAccess access, AgentActionClass actionClass, CancellationToken ct)
    {
        switch (actionClass)
        {
            case AgentActionClass.Read:
                return null;
            case AgentActionClass.External:
                return "in_client";
            case AgentActionClass.Authority:
                return await settings.GetAsync(McpSettings.AuthorityConfirmation, "out_of_band", ct: ct) == "in_client" ? "in_client" : "out_of_band";
            case AgentActionClass.Destructive:
                return "out_of_band";
            default:
                // MCP-53: past the grant's hourly write budget every write needs out-of-band confirmation.
                var budget = await settings.GetAsync(McpSettings.WriteBudgetPerHour, McpSettings.DefaultWriteBudget, ct: ct);
                var since = time.GetUtcNow().AddHours(-1);
                var writes = await db.Set<McpCallRecord>().AsNoTracking()
                    .CountAsync(r => r.UserId == access.Caller.UserId && r.GrantId == access.Caller.GrantId && r.ClientId == access.Caller.ClientId
                        && r.At >= since && r.ActionClass != AgentActionClass.Read && r.ResultCode == McpErrorCodes.Ok, ct);
                return writes >= budget ? "out_of_band" : null;
        }
    }

    private async Task<ToolOutcome> CreatePendingAsync(McpAccess access, IAgentTool tool, JsonObject args, string hash, AgentActionClass actionClass, string? sessionId, CancellationToken ct)
    {
        string description;
        try
        {
            description = await tool.DescribeAsync((JsonObject)args.DeepClone(), ct);
        }
        catch (Exception ex) when (MapException(ex) is { } mapped)
        {
            return mapped;
        }

        var now = time.GetUtcNow();
        var pending = new McpPendingAction
        {
            UserId = access.Caller.UserId,
            GrantId = access.Caller.GrantId,
            ClientId = access.Caller.ClientId,
            ClientName = access.Caller.ClientName,
            SessionId = sessionId,
            ConfigurationSlug = access.Caller.Slug,
            Tool = tool.Definition.Name,
            ToolTitle = tool.Definition.Title,
            ActionClass = actionClass,
            Scopes = access.EffectiveScopes.Order(StringComparer.Ordinal).ToList(),
            ArgumentsJson = args.ToJsonString(),
            ParameterHash = hash,
            Description = Truncate(description, 8000),
            Method = "out_of_band",
            RequiresReauthentication = actionClass == AgentActionClass.Destructive,
            CreatedAt = now,
            ExpiresAt = now + McpSettings.PendingActionLifetime,
        };
        db.Set<McpPendingAction>().Add(pending);
        await db.SaveChangesAsync(ct);

        var url = $"{options.Value.BaseUrl.TrimEnd('/')}/agent-actions/{pending.Id}";
        try
        {
            await live.ToUserAsync(pending.UserId, "agent_action.pending", new
            {
                id = pending.Id,
                tool = pending.ToolTitle,
                client = pending.ClientName,
                url = $"/agent-actions/{pending.Id}",
                expiresAt = pending.ExpiresAt,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't push the pending agent action {Id} live", pending.Id);
        }

        var outcome = ToolOutcome.Error(McpErrorCodes.ConfirmationRequired,
            $"The user must confirm this action in Switchboard before it runs. Ask them to open {url}, then call get_pending_action with this id to get the result. It expires in {(int)McpSettings.PendingActionLifetime.TotalMinutes} minutes.",
            new JsonObject
            {
                ["pending_action"] = new JsonObject
                {
                    ["id"] = pending.Id.ToString(),
                    ["status"] = "pending",
                    ["confirm_url"] = url,
                    ["expires_at"] = pending.ExpiresAt.ToString("O"),
                },
            });
        return outcome with { PendingActionId = pending.Id };
    }

    private async Task<(ToolOutcome? Outcome, Guid? PendingActionId)> ConfirmInClientAsync(McpAccess access, IAgentTool tool, JsonObject args, string hash,
        AgentActionClass actionClass, string? sessionId, Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>> elicit, CancellationToken ct)
    {
        string description;
        try
        {
            description = await tool.DescribeAsync((JsonObject)args.DeepClone(), ct);
        }
        catch (Exception ex) when (MapException(ex) is { } mapped)
        {
            return (mapped, null);
        }

        ElicitResult answer;
        try
        {
            answer = await elicit(new ElicitRequestParams
            {
                Message = $"{access.Caller.ClientName} wants to do this as you in Switchboard:\n\n{Truncate(description, 6000)}\n\nConfirm only if this is what you want.",
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        ["confirm"] = new ElicitRequestParams.BooleanSchema
                        {
                            Title = "Yes, do this",
                            Description = "Tick to let the agent go ahead. Leave it unticked or decline to stop it.",
                            Default = false,
                        },
                    },
                    Required = ["confirm"],
                },
            }, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ModelContextProtocol.McpException)
        {
            // The client can't be asked after all: fall back to out-of-band confirmation (MCP-50).
            logger.LogInformation(ex, "Elicitation unavailable for session {Session}; confirming out of band", sessionId);
            return (await CreatePendingAsync(access, tool, args, hash, actionClass, sessionId, ct), null);
        }

        var accepted = answer.IsAccepted && answer.Content is { } content
            && content.TryGetValue("confirm", out var confirm) && confirm.ValueKind == JsonValueKind.True;

        var now = time.GetUtcNow();
        var record = new McpPendingAction
        {
            UserId = access.Caller.UserId,
            GrantId = access.Caller.GrantId,
            ClientId = access.Caller.ClientId,
            ClientName = access.Caller.ClientName,
            SessionId = sessionId,
            ConfigurationSlug = access.Caller.Slug,
            Tool = tool.Definition.Name,
            ToolTitle = tool.Definition.Title,
            ActionClass = actionClass,
            Scopes = access.EffectiveScopes.Order(StringComparer.Ordinal).ToList(),
            ArgumentsJson = args.ToJsonString(),
            ParameterHash = hash,
            Description = Truncate(description, 8000),
            Method = "in_client",
            Status = accepted ? McpPendingStatus.Approved : McpPendingStatus.Declined,
            CreatedAt = now,
            ExpiresAt = now,
            DecidedAt = now,
            ResultCode = accepted ? null : McpErrorCodes.ConfirmationDeclined,
        };
        // The confirmation is recorded before the action runs (INV-MCP-4).
        db.Set<McpPendingAction>().Add(record);
        await db.SaveChangesAsync(ct);

        if (!accepted)
        {
            return (ToolOutcome.Error(McpErrorCodes.ConfirmationDeclined, "The user declined this action. Don't retry it unless they ask.") with { PendingActionId = record.Id }, record.Id);
        }
        return (null, record.Id);
    }

    private async Task CompletePendingAsync(Guid id, ToolOutcome outcome, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var isolated = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        var row = await isolated.Set<McpPendingAction>().FirstOrDefaultAsync(p => p.Id == id, CancellationToken.None);
        if (row is null)
        {
            return;
        }
        row.CompletedAt = time.GetUtcNow();
        row.ResultCode = outcome.Code;
        row.ResultText = Truncate(outcome.Text, 2000);
        row.ResultJson = outcome.Structured?.ToJsonString();
        await isolated.SaveChangesAsync(CancellationToken.None);
        _ = ct;
    }

    // ------------------------------------------------------------------ execution

    private async Task<ToolOutcome> InvokeAsync(IAgentTool tool, JsonObject args, AgentToolContext toolContext, McpCaller caller, McpCallChannel channel, CancellationToken ct)
    {
        using var tracked = killSwitch.Track(caller.GrantId, caller.ClientId, caller.UserId, out var killed);
        using var timeout = new CancellationTokenSource(MaxCallDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, killed, timeout.Token);
        using var heartbeat = StartProgress(channel, linked.Token);

        var readOnly = tool.Definition.Class == AgentActionClass.Read;
        IDbContextTransaction? transaction = null;
        EventHandler<SavingChangesEventArgs>? guard = null;
        try
        {
            if (readOnly)
            {
                // INV-MCP-3: a read-only tool can't write. Saving throws, and anything else is rolled back.
                guard = (_, _) => throw new InvalidOperationException($"Read-only tool {tool.Definition.Name} tried to save changes.");
                db.SavingChanges += guard;
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, linked.Token);
            }

            var result = await tool.InvokeAsync((JsonObject)args.DeepClone(), toolContext, linked.Token);
            return Shape(tool.Definition, result);
        }
        catch (OperationCanceledException) when (killed.IsCancellationRequested)
        {
            return ToolOutcome.Error(McpErrorCodes.DisabledByAdmin, "This connection was stopped while the call was running. Some changes may have been made before it stopped.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return ToolOutcome.Error(McpErrorCodes.Cancelled, "The call took longer than 5 minutes and was stopped. Some changes may have been made before it stopped.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ToolOutcome.Error(McpErrorCodes.Cancelled, "The call was cancelled. Some changes may have been made before it stopped.");
        }
        catch (Exception ex) when (MapException(ex) is { } mapped)
        {
            return mapped;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent tool {Tool} failed", tool.Definition.Name);
            return ToolOutcome.Error(McpErrorCodes.Internal, "The tool failed unexpectedly. Try again later.");
        }
        finally
        {
            if (guard is not null)
            {
                db.SavingChanges -= guard;
            }
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await transaction.DisposeAsync();
            }
        }
    }

    private static IDisposable? StartProgress(McpCallChannel channel, CancellationToken ct)
    {
        if (channel.Progress is not { } report)
        {
            return null;
        }
        // MCP-6: long-running calls keep reporting progress until they finish.
        var step = 0;
        return new Timer(_ =>
        {
            if (!ct.IsCancellationRequested)
            {
                _ = report(++step, "Still working", ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }, null, ProgressInterval, ProgressInterval);
    }

    /// <summary>Hidden-content stripping (MCP-63) and the structured-content cap (MCP-81).</summary>
    private static ToolOutcome Shape(AgentToolDefinition definition, AgentToolResult result)
    {
        var outcome = ToolOutcome.From(result);
        var structured = outcome.Structured?.DeepClone();
        if (structured is not null and not JsonObject)
        {
            structured = new JsonObject { ["result"] = structured };
        }
        var hidden = ContentSafety.Sanitize(structured);
        if (hidden && structured is JsonObject root
            && (definition.OutputSchema is null || definition.OutputSchema["additionalProperties"] is not JsonValue { } ap || ap.GetValueKind() != JsonValueKind.False))
        {
            root[ContentSafety.HiddenFlag] = true;
        }

        var size = structured is null ? 0 : Encoding.UTF8.GetByteCount(structured.ToJsonString());
        if (size > McpSettings.MaxStructuredBytes)
        {
            return ToolOutcome.Error(McpErrorCodes.ValidationFailed,
                "The result is too large. Ask for fewer records with page_size, or fewer fields.");
        }
        return outcome with { Structured = structured, ContainedHiddenText = hidden };
    }

    /// <summary>Domain exceptions to MCP-39 codes. Messages come from Core and describe the problem, not user content.</summary>
    public static ToolOutcome? MapException(Exception ex) => ex switch
    {
        NotFoundException => ToolOutcome.Error(McpErrorCodes.NotFound, "That record doesn't exist or you can't see it."),
        ForbiddenException f => ToolOutcome.Error(McpErrorCodes.PermissionDenied, f.Message),
        ConflictException c => ToolOutcome.Error(McpErrorCodes.Conflict, c.Message),
        ValidationException v => ToolOutcome.Error(McpErrorCodes.ValidationFailed, "Some values aren't valid. See details.",
            new JsonObject { ["details"] = JsonSerializer.SerializeToNode(v.Errors) }),
        _ => null,
    };

    // ------------------------------------------------------------------ idempotency

    private async Task<McpIdempotencyRecord?> FindIdempotentAsync(McpCaller caller, string tool, string key, CancellationToken ct)
    {
        var record = await db.Set<McpIdempotencyRecord>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == caller.UserId && r.ClientId == caller.ClientId && r.GrantId == caller.GrantId
                && r.ConfigurationSlug == caller.Slug && r.Tool == tool && r.Key == key, ct);
        if (record is not null && record.CreatedAt < time.GetUtcNow() - IdempotencyWindow)
        {
            await db.Set<McpIdempotencyRecord>().Where(r => r.Id == record.Id).ExecuteDeleteAsync(ct);
            return null;
        }
        return record;
    }

    private async Task StoreIdempotentAsync(McpCaller caller, string tool, string key, string hash, ToolOutcome outcome, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var isolated = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
        isolated.Set<McpIdempotencyRecord>().Add(new McpIdempotencyRecord
        {
            UserId = caller.UserId,
            ClientId = caller.ClientId,
            GrantId = caller.GrantId,
            ConfigurationSlug = caller.Slug,
            Tool = tool,
            Key = key,
            ParameterHash = hash,
            ResultJson = Serialize(outcome),
            CreatedAt = time.GetUtcNow(),
        });
        try
        {
            await isolated.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException)
        {
            // A concurrent call with the same key stored its result first.
        }
        _ = ct;
    }

    private static string Serialize(ToolOutcome outcome) => new JsonObject
    {
        ["code"] = outcome.Code,
        ["text"] = outcome.Text,
        ["structured"] = outcome.Structured?.DeepClone(),
    }.ToJsonString();

    private static ToolOutcome Deserialize(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new ToolOutcome(node["code"]?.GetValue<string>() ?? McpErrorCodes.Ok, node["structured"]?.DeepClone(), node["text"]?.GetValue<string>() ?? "");
    }

    // ------------------------------------------------------------------ audit

    private sealed class CallAudit(McpCaller caller, AgentToolDefinition definition, JsonObject arguments, string? sessionId)
    {
        public McpCaller Caller { get; set; } = caller;
        public AgentToolDefinition Definition { get; } = definition;
        public JsonObject Arguments { get; } = arguments;
        public string? SessionId { get; } = sessionId;
        public Guid? PendingActionId { get; set; }
        public AgentActionClass Class { get; set; } = definition.Class;
    }

    /// <summary>Writes the audit entry and call record before the response goes back (MCP-70, INV-MCP-2).</summary>
    private async Task<ToolOutcome> FinishAsync(CallAudit call, ToolOutcome outcome, Stopwatch clock, McpAccess? access = null)
    {
        var latency = (int)clock.ElapsedMilliseconds;
        var pendingId = outcome.PendingActionId ?? call.PendingActionId;
        var actor = access?.Actor ?? call.Caller.ToActor(new HashSet<string>(StringComparer.Ordinal));

        await using var scope = services.CreateAsyncScope();
        using (actors.BeginScope(actor))
        {
            var isolated = scope.ServiceProvider.GetRequiredService<SwitchboardDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
            audit.Record("mcp.tool.called", "mcp_tool", call.Definition.Name, new
            {
                tool = call.Definition.Name,
                actionClass = call.Class.ToString(),
                result = outcome.Code,
                latencyMs = latency,
                grantId = call.Caller.GrantId,
                sessionId = call.SessionId,
                clientId = call.Caller.ClientId,
                viaApiKey = call.Caller.ViaApiKey ? true : (bool?)null,
                confirmationId = pendingId,
                arguments = Redact(call.Arguments),
                affected = AffectedIds(call.Arguments, outcome.Structured),
                containedHiddenText = outcome.ContainedHiddenText ? true : (bool?)null,
            });
            isolated.Set<McpCallRecord>().Add(new McpCallRecord
            {
                At = time.GetUtcNow(),
                UserId = call.Caller.UserId,
                GrantId = call.Caller.GrantId,
                ClientId = call.Caller.ClientId,
                ClientName = call.Caller.ClientName,
                Tool = call.Definition.Name,
                ActionClass = call.Class,
                ResultCode = outcome.Code,
                LatencyMs = latency,
                PendingActionId = pendingId,
                ViaApiKey = call.Caller.ViaApiKey,
            });
            if (call.Caller.GrantId is { } grantId)
            {
                var now = time.GetUtcNow();
                await isolated.Set<McpGrant>().Where(g => g.Id == grantId && (g.LastUsedAt == null || g.LastUsedAt < now.AddMinutes(-1)))
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedAt, now), CancellationToken.None);
            }
            await isolated.SaveChangesAsync(CancellationToken.None);
        }
        return outcome;
    }

    private static readonly string[] SecretMarkers = ["password", "secret", "token", "apikey", "api_key", "authorization", "credential"];

    internal static JsonNode? Redact(JsonNode? node, string? key = null)
    {
        if (key is not null && SecretMarkers.Any(m => key.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return "[redacted]";
        }
        return node switch
        {
            JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Redact(p.Value, p.Key)))),
            JsonArray array => new JsonArray(array.Take(50).Select(i => Redact(i)).ToArray()),
            JsonValue v when v.TryGetValue<string>(out var s) => Truncate(s, 300),
            _ => node?.DeepClone(),
        };
    }

    private static List<string> AffectedIds(JsonObject args, JsonNode? result)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(JsonNode? node, int depth)
        {
            if (depth > 3 || ids.Count >= 50)
            {
                return;
            }
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if ((key == "id" || key.EndsWith("_id", StringComparison.Ordinal) || key.EndsWith("_ids", StringComparison.Ordinal))
                            && key != "client_id")
                        {
                            foreach (var v in value is JsonArray a ? (IEnumerable<JsonNode?>)a : new[] { value })
                            {
                                if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && (Guid.TryParse(s, out _) || IsDisplayId(s)))
                                {
                                    ids.Add(s);
                                }
                            }
                        }
                        else
                        {
                            Collect(value, depth + 1);
                        }
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array.Take(25))
                    {
                        Collect(item, depth + 1);
                    }
                    break;
            }
        }
        Collect(args, 0);
        Collect(result, 0);
        return ids.ToList();
    }

    private static bool IsDisplayId(string s) => s.Length < 20 && s.Contains('-') && char.IsAsciiLetterUpper(s[0]);

    private async Task<string> UserNameAsync(Guid userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "User";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
