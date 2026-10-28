using System.Text.Json;
using System.Text.Json.Nodes;
using Switchboard.Core.Agents;
using Switchboard.Core.Security;
using Switchboard.Modules.Mcp.Server;

namespace Switchboard.Modules.Mcp.Tools;

/// <summary>Shared argument parsing and schema helpers for the module's own tools.</summary>
public abstract class AgentToolBase : IAgentTool
{
    public abstract AgentToolDefinition Definition { get; }

    public abstract Task<AgentToolResult> InvokeAsync(JsonObject arguments, AgentToolContext context, CancellationToken ct);

    public virtual Task<string> DescribeAsync(JsonObject arguments, CancellationToken ct) =>
        Task.FromResult($"{Definition.Title}.");

    protected static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    protected static string RequiredStr(JsonObject args, string name) =>
        Str(args, name) is { Length: > 0 } s ? s : throw new ValidationException(name, "Required.");

    protected static int? Int(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : args[name] is JsonValue d && d.TryGetValue<double>(out var dd) ? (int)dd : null;

    protected static bool? Bool(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    protected static bool Has(JsonObject args, string name) => args.ContainsKey(name);

    protected static List<string>? StrList(JsonObject args, string name) =>
        args[name] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : null;

    protected static Guid? Uuid(JsonObject args, string name)
    {
        var s = Str(args, name);
        if (s is null)
        {
            return null;
        }
        return Guid.TryParse(s, out var g) ? g : throw new ValidationException(name, "Expected an id (UUID).");
    }

    protected static AgentToolResult Error(string code, string message, JsonNode? details = null) => AgentToolResult.Error(code, message, details);

    protected static JsonObject Schema(string[] required, params (string Name, JsonObject Schema)[] properties) =>
        AgentContent.ObjectSchema(required, properties);

    protected static JsonObject S(string description, int? maxLength = null) => AgentContent.StringSchema(description, maxLength);

    protected static JsonObject Id(string description) => new() { ["type"] = "string", ["format"] = "uuid", ["description"] = description };

    protected static JsonObject I(string description, int? min = null, int? max = null) => AgentContent.IntegerSchema(description, min, max);

    protected static JsonObject B(string description) => new() { ["type"] = "boolean", ["description"] = description };

    protected static JsonObject E(string description, params string[] values) => AgentContent.EnumSchema(description, values);

    protected static JsonObject Nullable(JsonObject schema)
    {
        var type = schema["type"]?.GetValue<string>() ?? "string";
        schema["type"] = new JsonArray(type, "null");
        if (schema["enum"] is JsonArray values)
        {
            values.Add(null);
        }
        return schema;
    }

    protected static JsonObject Arr(string description, JsonObject items, int? maxItems = null)
    {
        var s = new JsonObject { ["type"] = "array", ["description"] = description, ["items"] = items };
        if (maxItems is { } max)
        {
            s["maxItems"] = max;
        }
        return s;
    }

    protected static JsonObject Obj(string description) => new() { ["type"] = "object", ["description"] = description };

    /// <summary>A permissive output schema: documents the main properties without forbidding extras.</summary>
    protected static JsonObject Output(params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    protected static JsonObject T(string type, string? description = null)
    {
        var s = new JsonObject { ["type"] = type };
        if (description is not null)
        {
            s["description"] = description;
        }
        return s;
    }

    protected static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value)!;

    /// <summary>The MCP call in progress, when the tool runs through the MCP server.</summary>
    protected static McpAccess? Call => McpCallScope.Current;
}

/// <summary>The access of the MCP call running on this async flow, for tools that report on the connection itself.</summary>
public static class McpCallScope
{
    private static readonly AsyncLocal<McpAccess?> CurrentAccess = new();

    public static McpAccess? Current => CurrentAccess.Value;

    public static IDisposable Enter(McpAccess access)
    {
        var previous = CurrentAccess.Value;
        CurrentAccess.Value = access;
        return new Restore(previous);
    }

    private sealed class Restore(McpAccess? previous) : IDisposable
    {
        public void Dispose() => CurrentAccess.Value = previous;
    }
}
