using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace Switchboard.Core.Services;

/// <summary>Allowlist HTML sanitizing for every rich-text input and output (SR-INP-1).</summary>
public interface IHtmlContent
{
    /// <summary>Removes scripts, event handlers, forms, unsafe URLs and unsafe CSS.</summary>
    string Sanitize(string? html);

    /// <summary>Plain text for search, previews and text email bodies.</summary>
    string ToPlainText(string? html);

    /// <summary>Escapes plain text and keeps line breaks, for text that becomes HTML.</summary>
    string FromPlainText(string? text);
}

public sealed class HtmlContent : IHtmlContent
{
    private static readonly HtmlParser Parser = new();
    private readonly HtmlSanitizer _sanitizer;

    public HtmlContent()
    {
        _sanitizer = new HtmlSanitizer();
        foreach (var tag in new[] { "form", "input", "button", "select", "option", "textarea", "iframe", "object", "embed", "style", "link", "meta", "base" })
        {
            _sanitizer.AllowedTags.Remove(tag);
        }
        _sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "tel" })
        {
            _sanitizer.AllowedSchemes.Add(scheme);
        }
        _sanitizer.AllowedCssProperties.Clear();
        foreach (var css in new[] { "color", "background-color", "font-weight", "font-style", "text-align", "text-decoration", "width", "height" })
        {
            _sanitizer.AllowedCssProperties.Add(css);
        }
        _sanitizer.AllowedAttributes.Remove("id");
        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement a)
            {
                a.SetAttribute("rel", "noopener noreferrer nofollow");
                a.SetAttribute("target", "_blank");
            }
        };
    }

    public string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? "" : _sanitizer.Sanitize(html).Trim();

    public string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }
        var doc = Parser.ParseDocument($"<body>{html}</body>");
        foreach (var br in doc.QuerySelectorAll("br"))
        {
            br.Replace(doc.CreateTextNode("\n"));
        }
        foreach (var block in doc.QuerySelectorAll("p,div,li,tr,h1,h2,h3,h4,h5,h6,blockquote,pre"))
        {
            block.Append(doc.CreateTextNode("\n"));
        }
        var text = doc.Body?.TextContent ?? "";
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd());
        return string.Join("\n", lines).Trim();
    }

    public string FromPlainText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var encoded = System.Net.WebUtility.HtmlEncode(text.Replace("\r", ""));
        var paragraphs = encoded.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => $"<p>{p.Replace("\n", "<br>")}</p>");
        return string.Concat(paragraphs);
    }
}
