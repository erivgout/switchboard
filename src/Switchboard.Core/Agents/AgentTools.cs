using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Switchboard.Core.Agents;

/// <summary>Risk class of an agent action; drives confirmation (SRS §4.6).</summary>
public enum AgentActionClass
{
    /// <summary>R: reads. No confirmation.</summary>
    Read,
    /// <summary>W: internal writes (fields, assignment, private notes). No confirmation by default.</summary>
    InternalWrite,
    /// <summary>X: anything that leaves the organization (replies, forwards, status pages).</summary>
    External,
    /// <summary>A: authority (approve/reject, publish, override).</summary>
    Authority,
    /// <summary>D: destructive or bulk (trash, merge, bulk above threshold, orchestration).</summary>
    Destructive,
}

/// <summary>Static description of a tool. Descriptions are written for models and never contain user content (MCP-31).</summary>
public sealed record AgentToolDefinition(
    string Name,
    string Title,
    string Description,
    /// <summary>OAuth scope required, e.g. <c>tickets:read</c> (SRS MCP-18).</summary>
    string Scope,
    AgentActionClass Class,
    JsonObject InputSchema,
    JsonObject? OutputSchema = null,
    /// <summary>Requesters (not just agents) may call it, on their own data.</summary>
    bool RequesterAllowed = false,
    /// <summary>Idempotent writes may be retried safely.</summary>
    bool Idempotent = false);

public sealed record AgentToolContext(
    /// <summary>Return the planned change set without applying it (MCP-38).</summary>
    bool DryRun = false,
    string? IdempotencyKey = null);

/// <summary>
/// Result of a tool call. <see cref="Structured"/> is the machine-readable payload; <see cref="Text"/> a one-line summary.
/// Errors use codes from SRS MCP-39: permission_denied, not_found, validation_failed, conflict, ...
/// </summary>
public sealed record AgentToolResult(JsonNode? Structured, string Text, string? ErrorCode = null)
{
    public bool IsError => ErrorCode is not null;

    public static AgentToolResult Ok(JsonNode? structured, string text) => new(structured, text);

    public static AgentToolResult Error(string code, string message, JsonNode? details = null) => new(details, message, code);
}

/// <summary>
/// An action an AI agent may take on a user's behalf. Modules implement tools for their own data
/// and register them with <see cref="AgentToolServiceCollectionExtensions.AddAgentTool{T}"/>; the MCP
/// module exposes them, enforcing scopes, confirmation, rate limits and audit. Tools run as the
/// current actor (the delegating user) through the same services as the UI, so permissions apply.
/// </summary>
public interface IAgentTool
{
    AgentToolDefinition Definition { get; }

    Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct);

    /// <summary>
    /// Human-readable description of exactly what a call will do (targets, field diffs, recipients,
    /// message preview), shown when a person confirms a class X/A/D action. Must not change anything.
    /// </summary>
    Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct) =>
        Task.FromResult($"{Definition.Title}: {arguments.ToJsonString()}");
}

/// <summary>Helpers for returning user-generated content to agents as clearly labelled data (MCP-60).</summary>
public static class AgentContent
{
    /// <summary>Wraps text that came from requesters, email, attachments or other untrusted sources.</summary>
    public static JsonObject Untrusted(string? value, string source, string? author = null) => new()
    {
        ["value"] = value ?? "",
        ["source"] = source,
        ["author"] = author,
        ["trust"] = "untrusted",
    };

    /// <summary>Minimal JSON Schema object builder: <c>Schema.Object(("query", Schema.String("…")))</c>.</summary>
    public static JsonObject ObjectSchema(IEnumerable<string> required, params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["additionalProperties"] = false,
        };
    }

    public static JsonObject StringSchema(string description, int? maxLength = null)
    {
        var s = new JsonObject { ["type"] = "string", ["description"] = description };
        if (maxLength is { } max)
        {
            s["maxLength"] = max;
        }
        return s;
    }

    public static JsonObject IntegerSchema(string description, int? minimum = null, int? maximum = null)
    {
        var s = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (minimum is { } min)
        {
            s["minimum"] = min;
        }
        if (maximum is { } max)
        {
            s["maximum"] = max;
        }
        return s;
    }

    public static JsonObject EnumSchema(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
    };
}

public static class AgentToolServiceCollectionExtensions
{
    public static IServiceCollection AddAgentTool<TTool>(this IServiceCollection services) where TTool : class, IAgentTool =>
        services.AddScoped<IAgentTool, TTool>();
}
