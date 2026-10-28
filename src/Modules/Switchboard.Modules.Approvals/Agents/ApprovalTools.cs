using System.Text.Json.Nodes;
using Switchboard.Core.Agents;
using Switchboard.Core.Security;
using Switchboard.Modules.Approvals.Domain;
using Switchboard.Modules.Approvals.Services;

namespace Switchboard.Modules.Approvals.Agents;

/// <summary>Maps domain exceptions to MCP error codes (MCP-39). Shared by approval and catalog tools.</summary>
public static class AgentToolErrors
{
    public static async Task<AgentToolResult> RunAsync(Func<Task<AgentToolResult>> action)
    {
        try
        {
            return await action();
        }
        catch (NotFoundException)
        {
            return AgentToolResult.Error("not_found", "That record doesn't exist or you can't see it.");
        }
        catch (ForbiddenException ex)
        {
            return AgentToolResult.Error("permission_denied", ex.Message);
        }
        catch (ConflictException ex)
        {
            return AgentToolResult.Error("conflict", ex.Message);
        }
        catch (ValidationException ex)
        {
            var errors = new JsonObject();
            foreach (var (key, messages) in ex.Errors)
            {
                errors[key] = new JsonArray(messages.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
            }
            return AgentToolResult.Error("validation_failed", "Some values aren't valid. See errors.", new JsonObject { ["errors"] = errors });
        }
    }

    public static bool TryGuid(JsonObject args, string name, out Guid value)
    {
        value = Guid.Empty;
        return args[name] is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out value);
    }

    public static int Limit(JsonObject args, int fallback = 20, int max = 100) =>
        args["limit"] is JsonValue v && v.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, max) : fallback;

    public static string? String(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

internal static class ApprovalJson
{
    public static JsonObject Item(ApprovalListItem a) => new()
    {
        ["id"] = a.Id.ToString(),
        ["title"] = AgentContent.Untrusted(a.Title, "approval.title"),
        ["status"] = a.Status.ToString().ToLowerInvariant(),
        ["current_level"] = a.CurrentLevel,
        ["levels"] = a.LevelCount,
        ["subject"] = new JsonObject { ["type"] = a.SubjectType, ["id"] = a.SubjectId.ToString() },
        ["requester"] = AgentContent.Untrusted(a.RequesterName, "user.name"),
        ["on_behalf_of"] = a.OnBehalfOfName is null ? null : AgentContent.Untrusted(a.OnBehalfOfName, "user.name"),
        ["waiting_since"] = a.WaitingSince?.ToString("O"),
        ["updated_at"] = a.UpdatedAt.ToString("O"),
    };

    public static JsonObject Details(ApprovalDetails d)
    {
        var r = d.Request;
        var levels = new JsonArray();
        foreach (var level in r.Levels.OrderBy(l => l.Order))
        {
            var outcome = d.Evaluation.Levels.First(e => e.Order == level.Order);
            var approvers = new JsonArray();
            foreach (var a in level.Approvers)
            {
                var decision = r.Decisions.FirstOrDefault(x => x.LevelId == level.Id && x.ApproverId == a.UserId);
                approvers.Add(new JsonObject
                {
                    ["user_id"] = a.UserId.ToString(),
                    ["name"] = AgentContent.Untrusted(d.NameOf(a.UserId), "user.name"),
                    ["source"] = a.Source,
                    ["decision"] = decision?.Verdict.ToString().ToLowerInvariant(),
                });
            }
            levels.Add(new JsonObject
            {
                ["order"] = level.Order,
                ["rule"] = level.Rule.ToString().ToLowerInvariant(),
                ["outcome"] = outcome.Outcome.ToString().ToLowerInvariant(),
                ["approvals_needed"] = outcome.Needed,
                ["approvers"] = approvers,
            });
        }
        var decisions = new JsonArray();
        foreach (var x in r.Decisions.OrderBy(x => x.CreatedAt))
        {
            decisions.Add(new JsonObject
            {
                ["id"] = x.Id.ToString(),
                ["decision"] = x.Verdict.ToString().ToLowerInvariant(),
                ["decided_by"] = AgentContent.Untrusted(d.NameOf(x.DecidedById), "user.name"),
                ["on_behalf_of"] = x.OnBehalfOfId is { } o ? AgentContent.Untrusted(d.NameOf(o), "user.name") : null,
                ["comment"] = x.Comment is null ? null : AgentContent.Untrusted(x.Comment, "approval.comment", d.NameOf(x.DecidedById)),
                ["channel"] = x.Channel.ToString().ToLowerInvariant(),
                ["decided_at"] = x.CreatedAt.ToString("O"),
            });
        }
        return new JsonObject
        {
            ["id"] = r.Id.ToString(),
            ["title"] = AgentContent.Untrusted(r.Title, "approval.title"),
            ["status"] = r.Status.ToString().ToLowerInvariant(),
            ["current_level"] = r.CurrentLevel,
            ["subject"] = new JsonObject { ["type"] = r.SubjectType, ["id"] = r.SubjectId.ToString(), ["display_id"] = d.Subject.DisplayId },
            ["requester"] = AgentContent.Untrusted(d.NameOf(r.RequesterId), "user.name"),
            ["requested_at"] = r.CreatedAt.ToString("O"),
            ["levels"] = levels,
            ["decisions"] = decisions,
            ["you_can_decide_for"] = new JsonArray(d.CanDecideFor.Select(id => (JsonNode)new JsonObject
            {
                ["user_id"] = id.ToString(),
                ["name"] = AgentContent.Untrusted(d.NameOf(id), "user.name"),
            }).ToArray()),
            ["why_you_cannot_decide"] = d.DecideBlockedReason,
        };
    }
}

internal sealed class ListMyApprovalsTool(IApprovalService approvals) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new(
        "list_my_approvals",
        "List my approvals",
        "Lists approval requests for the signed-in user. view=pending (default) returns requests waiting on the user's decision, " +
        "including those they cover as a delegate; view=decided returns ones they decided; view=requested returns ones made by or for them. " +
        "Use get_approval for details and decide_approval to approve or reject.",
        "approvals:read",
        AgentActionClass.Read,
        AgentContent.ObjectSchema([],
            ("view", AgentContent.EnumSchema("Which list to return.", "pending", "decided", "requested")),
            ("limit", AgentContent.IntegerSchema("Maximum items to return (1-100, default 20).", 1, 100))),
        RequesterAllowed: true);

    public Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct) =>
        AgentToolErrors.RunAsync(async () =>
        {
            var view = AgentToolErrors.String(arguments, "view") ?? "pending";
            var limit = AgentToolErrors.Limit(arguments);
            var items = view switch
            {
                "decided" => await approvals.ListDecidedAsync(limit, ct),
                "requested" => await approvals.ListRequestedAsync(limit, ct),
                "pending" => await approvals.ListPendingAsync(limit, ct),
                _ => throw new ValidationException("view", "Use pending, decided or requested."),
            };
            var array = new JsonArray(items.Select(i => (JsonNode)ApprovalJson.Item(i)).ToArray());
            return AgentToolResult.Ok(new JsonObject { ["view"] = view, ["items"] = array }, $"{items.Count} {view} approval request(s).");
        });
}

internal sealed class GetApprovalTool(IApprovalService approvals) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new(
        "get_approval",
        "Get an approval request",
        "Returns one approval request: status, the chain of levels with each approver and their decision, decision history with " +
        "comments, and whether the signed-in user can decide now (and for whom). Requires approval_id from list_my_approvals.",
        "approvals:read",
        AgentActionClass.Read,
        AgentContent.ObjectSchema(["approval_id"], ("approval_id", AgentContent.StringSchema("Approval request id (GUID)."))),
        RequesterAllowed: true);

    public Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct) =>
        AgentToolErrors.RunAsync(async () =>
        {
            if (!AgentToolErrors.TryGuid(arguments, "approval_id", out var id))
            {
                throw new ValidationException("approval_id", "Give a valid approval id.");
            }
            var details = await approvals.GetAsync(id, ct);
            return AgentToolResult.Ok(ApprovalJson.Details(details), $"Approval is {details.Request.Status.ToString().ToLowerInvariant()}.");
        });
}

internal sealed class DecideApprovalTool(IApprovalService approvals) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new(
        "decide_approval",
        "Approve or reject",
        "Records the signed-in user's decision on an approval request: decision=approve or reject. A comment is required to reject, " +
        "and is shown to the requester. If the user covers for several approvers on the same level, pass on_behalf_of with that " +
        "approver's user id. Decisions are final and can't be changed. Requires human confirmation.",
        "approvals:decide",
        AgentActionClass.Authority,
        AgentContent.ObjectSchema(["approval_id", "decision"],
            ("approval_id", AgentContent.StringSchema("Approval request id (GUID).")),
            ("decision", AgentContent.EnumSchema("The decision.", "approve", "reject")),
            ("comment", AgentContent.StringSchema("Reason or note for the requester. Required when rejecting.", 2000)),
            ("on_behalf_of", AgentContent.StringSchema("User id of the approver you cover for, when you cover more than one."))),
        RequesterAllowed: true);

    public Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct) =>
        AgentToolErrors.RunAsync(async () =>
        {
            var command = Parse(arguments);
            var id = Guid.Parse(AgentToolErrors.String(arguments, "approval_id")!);
            if (context.DryRun)
            {
                var preview = await approvals.GetAsync(id, ct);
                return AgentToolResult.Ok(new JsonObject
                {
                    ["dry_run"] = true,
                    ["approval_id"] = id.ToString(),
                    ["decision"] = command.Verdict == ApprovalVerdict.Approved ? "approve" : "reject",
                    ["can_decide"] = preview.CanDecideFor.Count > 0,
                }, "Dry run: nothing was recorded.");
            }
            var decision = await approvals.DecideAsync(id, command, ct);
            var after = await approvals.GetAsync(id, ct);
            return AgentToolResult.Ok(new JsonObject
            {
                ["decision_id"] = decision.Id.ToString(),
                ["approval_id"] = id.ToString(),
                ["decision"] = decision.Verdict.ToString().ToLowerInvariant(),
                ["approval_status"] = after.Request.Status.ToString().ToLowerInvariant(),
            }, $"Recorded: {decision.Verdict.ToString().ToLowerInvariant()}. The request is now {after.Request.Status.ToString().ToLowerInvariant()}.");
        });

    public async Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var command = Parse(arguments);
            var details = await approvals.GetAsync(Guid.Parse(AgentToolErrors.String(arguments, "approval_id")!), ct);
            var who = command.OnBehalfOfId is { } o ? $" on behalf of {details.NameOf(o)}" : "";
            var verb = command.Verdict == ApprovalVerdict.Approved ? "Approve" : "Reject";
            var comment = command.Comment is null ? "" : $" with the comment \"{command.Comment}\"";
            return $"{verb}{who} \"{details.Request.Title}\", requested by {details.NameOf(details.Request.RequesterId)}{comment}. This can't be undone.";
        }
        catch (Exception ex) when (ex is ValidationException or NotFoundException or FormatException or ArgumentNullException)
        {
            return "Decide an approval request (the request couldn't be loaded).";
        }
    }

    private static DecideApprovalCommand Parse(JsonObject args)
    {
        if (!AgentToolErrors.TryGuid(args, "approval_id", out _))
        {
            throw new ValidationException("approval_id", "Give a valid approval id.");
        }
        var verdict = AgentToolErrors.String(args, "decision") switch
        {
            "approve" => ApprovalVerdict.Approved,
            "reject" => ApprovalVerdict.Rejected,
            _ => throw new ValidationException("decision", "Use approve or reject."),
        };
        Guid? onBehalf = null;
        if (args["on_behalf_of"] is not null)
        {
            if (!AgentToolErrors.TryGuid(args, "on_behalf_of", out var o))
            {
                throw new ValidationException("on_behalf_of", "Give a valid user id.");
            }
            onBehalf = o;
        }
        return new DecideApprovalCommand { Verdict = verdict, Comment = AgentToolErrors.String(args, "comment"), OnBehalfOfId = onBehalf };
    }
}
