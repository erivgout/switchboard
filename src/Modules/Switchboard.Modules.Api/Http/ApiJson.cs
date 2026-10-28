using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Switchboard.Modules.Api.Http;

/// <summary>JSON conventions of the REST API: camelCase names, enums as camelCase strings, nulls omitted.</summary>
public static class ApiJson
{
    public const int MaxDepth = 32;

    /// <summary>Default body limit for JSON requests (SR-INP-7). Ticket bodies are capped at 200k characters by Core.</summary>
    public const long MaxJsonBytes = 2 * 1024 * 1024;

    public static JsonStringEnumConverter EnumConverter(bool allowIntegers) => new(JsonNamingPolicy.CamelCase, allowIntegers);

    /// <summary>Output, and strict input: unknown fields and numeric enums are rejected.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = MaxDepth,
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(EnumConverter(allowIntegers: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static IResult Ok<T>(T value, int status = StatusCodes.Status200OK) =>
        Results.Json(value, Options, "application/json", status);

    /// <summary>Reads the body as JSON with size and depth limits. Unknown fields and type errors become 400s.</summary>
    public static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken ct, long maxBytes = MaxJsonBytes)
    {
        var bytes = await ReadBodyAsync(request, maxBytes, ct);
        if (bytes.Length == 0)
        {
            throw new Core.Security.ValidationException("body", "Send a JSON body.");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new Core.Security.ValidationException("body", "Send a JSON object.");
        }
        catch (JsonException ex)
        {
            throw BodyError(ex);
        }
    }

    /// <summary>Parses the body as a JSON object for field-by-field handling (PATCH).</summary>
    public static async Task<JsonDocument> ReadDocumentAsync(HttpRequest request, CancellationToken ct, long maxBytes = MaxJsonBytes)
    {
        var bytes = await ReadBodyAsync(request, maxBytes, ct);
        try
        {
            var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                throw new Core.Security.ValidationException("body", "Send a JSON object.");
            }
            return doc;
        }
        catch (JsonException ex)
        {
            throw BodyError(ex);
        }
    }

    internal static async Task<byte[]> ReadBodyAsync(HttpRequest request, long maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes)
        {
            throw new PayloadTooLargeException(maxBytes);
        }
        if (request.ContentLength > 0 && request.ContentType is { } type
            && !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("+json", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedMediaTypeException();
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new PayloadTooLargeException(maxBytes);
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static Core.Security.ValidationException BodyError(JsonException ex)
    {
        var path = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? "" : ex.Path.TrimStart('$', '.');
        if (ex.Message.Contains("could not be mapped", StringComparison.Ordinal) && ExtractProperty(ex.Message) is { } name)
        {
            var field = path.Length == 0 ? name : path == name || path.EndsWith("." + name, StringComparison.Ordinal) ? path : $"{path}.{name}";
            return new Core.Security.ValidationException(field, "This field doesn't exist.");
        }
        return new Core.Security.ValidationException(path.Length == 0 ? "body" : path, "That value has the wrong type or the JSON is malformed.");
    }

    private static string? ExtractProperty(string message)
    {
        // "The JSON property 'foo' could not be mapped to any .NET member contained in type ..."
        var start = message.IndexOf('\'');
        var end = start >= 0 ? message.IndexOf('\'', start + 1) : -1;
        return start >= 0 && end > start ? message[(start + 1)..end] : null;
    }
}

public sealed class PayloadTooLargeException(long limit) : Exception($"The request body is larger than {limit / 1024} KB.");

public sealed class UnsupportedMediaTypeException() : Exception("Send the body as application/json.");
