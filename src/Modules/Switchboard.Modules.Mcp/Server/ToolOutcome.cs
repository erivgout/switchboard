using System.Text.Json.Nodes;
using Switchboard.Core.Agents;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>The shaped result of one tool call, before it becomes an MCP <c>CallToolResult</c>.</summary>
public sealed record ToolOutcome(string Code, JsonNode? Structured, string Text, Guid? PendingActionId = null, bool ContainedHiddenText = false)
{
    public bool IsError => Code != McpErrorCodes.Ok;

    public static ToolOutcome Ok(JsonNode? structured, string text) => new(McpErrorCodes.Ok, structured, text);

    /// <summary>An MCP-39 error. <paramref name="message"/> must never contain user-generated content (MCP-60).</summary>
    public static ToolOutcome Error(string code, string message, JsonObject? extra = null)
    {
        var structured = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra.ToList())
            {
                extra.Remove(key);
                if (key == "details" && structured["error"] is JsonObject error)
                {
                    error["details"] = value;
                }
                else
                {
                    structured[key] = value;
                }
            }
        }
        return new ToolOutcome(code, structured, message);
    }

    public static ToolOutcome From(AgentToolResult result) =>
        result.IsError
            ? Error(result.ErrorCode!, result.Text, result.Structured is null ? null : new JsonObject { ["details"] = result.Structured.DeepClone() })
            : Ok(result.Structured, result.Text);
}

/// <summary>
/// Implemented by tools whose risk class depends on their arguments (SRS §4.6): the class is the highest
/// one the arguments imply at call time, e.g. a requester replying on their own ticket is W, not X.
/// </summary>
public interface IAgentToolClassifier
{
    Task<AgentActionClass> ClassifyAsync(JsonObject arguments, CancellationToken ct);
}

/// <summary>What the MCP session can do for a call in progress: ask the user, and report progress.</summary>
public sealed record McpCallChannel(
    string? SessionId,
    Func<ModelContextProtocol.Protocol.ElicitRequestParams, CancellationToken, ValueTask<ModelContextProtocol.Protocol.ElicitResult>>? Elicit,
    Func<double, string, CancellationToken, Task>? Progress)
{
    public static McpCallChannel None { get; } = new(null, null, null);
}
