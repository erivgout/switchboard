using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Switchboard.Modules.Messaging.Notifications;

/// <summary>A placeholder value. Text is HTML-encoded on output; <see cref="IsSafeHtml"/> values were sanitized before they got here.</summary>
public readonly record struct TemplateValue(string Value, bool IsSafeHtml)
{
    public static TemplateValue Text(string? value) => new(value ?? "", false);
    public static TemplateValue Html(string? sanitizedHtml) => new(sanitizedHtml ?? "", true);
}

/// <summary>
/// Logic-free template language (SR-INP-4): <c>{{name}}</c> substitution only. No expressions, loops,
/// file or network access; one pass over bounded input; output HTML-encoded by default.
/// </summary>
public static partial class TemplateRenderer
{
    public const int MaxTemplateLength = 50_000;
    public const int MaxSubjectLength = 300;

    [GeneratedRegex(@"\{\{\s*([a-z0-9_.]{1,64})\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex PlaceholderPattern();

    /// <summary>Renders HTML. Unknown placeholders render as nothing.</summary>
    public static string RenderHtml(string template, IReadOnlyDictionary<string, TemplateValue> values) =>
        Render(template, values, (value) => value.IsSafeHtml ? value.Value : WebUtility.HtmlEncode(value.Value));

    /// <summary>Renders a one-line plain-text subject. HTML values contribute their text only; line breaks are removed.</summary>
    public static string RenderSubject(string template, IReadOnlyDictionary<string, TemplateValue> values)
    {
        var rendered = Render(template, values, value => value.IsSafeHtml ? StripTags(value.Value) : value.Value);
        var single = Regex.Replace(rendered, @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(250)).Trim();
        return single.Length > MaxSubjectLength ? single[..MaxSubjectLength] : single;
    }

    /// <summary>Placeholder names the template uses that the catalog doesn't define.</summary>
    public static IReadOnlyList<string> UnknownPlaceholders(string template) =>
        PlaceholderPattern().Matches(template ?? "")
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Where(name => NotificationCatalog.Placeholders.All(p => p.Key != name))
            .Distinct()
            .ToList();

    private static string Render(string template, IReadOnlyDictionary<string, TemplateValue> values, Func<TemplateValue, string> format)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }
        var bounded = template.Length > MaxTemplateLength ? template[..MaxTemplateLength] : template;
        var output = new StringBuilder(bounded.Length + 256);
        var last = 0;
        foreach (Match match in PlaceholderPattern().Matches(bounded))
        {
            output.Append(bounded, last, match.Index - last);
            if (values.TryGetValue(match.Groups[1].Value.ToLowerInvariant(), out var value))
            {
                output.Append(format(value));
            }
            last = match.Index + match.Length;
        }
        output.Append(bounded, last, bounded.Length - last);
        return output.ToString();
    }

    private static string StripTags(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromMilliseconds(250)));
}
