using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Switchboard.Modules.Automation.Engine;

public enum PlaceholderEncoding
{
    /// <summary>Raw text (note and email bodies are HTML-encoded as a whole afterwards).</summary>
    Text,
    /// <summary>Values are HTML-encoded.</summary>
    Html,
    /// <summary>Values are escaped for use inside a JSON string.</summary>
    Json,
    /// <summary>Values are percent-encoded for use in a URL.</summary>
    Url,
    /// <summary>Line breaks removed, for email subjects and headers.</summary>
    SingleLine,
}

/// <summary>Fills <c>{{placeholder}}</c> tokens. Unknown tokens stay as written so mistakes are visible.</summary>
public static partial class PlaceholderRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}")]
    private static partial Regex Token();

    public static string Render(string? template, IReadOnlyDictionary<string, string> values, PlaceholderEncoding encoding)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }
        var rendered = Token().Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var value) ? Encode(value, encoding) : m.Value);
        return encoding == PlaceholderEncoding.SingleLine ? rendered.Replace('\r', ' ').Replace('\n', ' ') : rendered;
    }

    public static IEnumerable<string> Tokens(string? template) =>
        string.IsNullOrEmpty(template) ? [] : Token().Matches(template).Select(m => m.Groups[1].Value);

    private static string Encode(string value, PlaceholderEncoding encoding) => encoding switch
    {
        PlaceholderEncoding.Html => WebUtility.HtmlEncode(value),
        PlaceholderEncoding.Json => JsonEncodedText.Encode(value).ToString(),
        PlaceholderEncoding.Url => Uri.EscapeDataString(value),
        PlaceholderEncoding.SingleLine => value.Replace('\r', ' ').Replace('\n', ' '),
        _ => value,
    };
}
