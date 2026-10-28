using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switchboard.Core.Data;

public static class JsonDefaults
{
    /// <summary>Shared options for persisted JSON (events, activity, settings, custom fields).</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
