using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Switchboard.Modules.Knowledge.Services;

public sealed record KbHeading(string Id, string Text, int Level);

/// <summary>Article HTML ready to render: heading anchors added, plus an "On this page" outline.</summary>
public sealed record KbRenderedBody(string Html, IReadOnlyList<KbHeading> Outline);

/// <summary>Helpers for already-sanitized article HTML.</summary>
public static partial class KbHtml
{
    private static readonly HtmlParser Parser = new();

    /// <summary>Attachment ids referenced as <c>/files/{id}/…</c> in the HTML.</summary>
    public static IReadOnlyList<Guid> ReferencedFileIds(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }
        return FileUrl().Matches(html)
            .Select(m => Guid.TryParse(m.Groups[1].Value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Adds stable ids to h2/h3 headings so the outline can link to them. Runs on sanitized HTML;
    /// the ids are generated here, never taken from user input.
    /// </summary>
    public static KbRenderedBody Render(string sanitizedHtml)
    {
        if (string.IsNullOrWhiteSpace(sanitizedHtml))
        {
            return new KbRenderedBody("", []);
        }
        var doc = Parser.ParseDocument($"<body>{sanitizedHtml}</body>");
        var body = doc.Body!;
        var outline = new List<KbHeading>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in body.QuerySelectorAll("h2, h3"))
        {
            var text = heading.TextContent.Trim();
            if (text.Length == 0)
            {
                continue;
            }
            var slug = "s-" + Slug(text);
            var id = slug;
            for (var n = 2; !used.Add(id); n++)
            {
                id = $"{slug}-{n}";
            }
            heading.SetAttribute("id", id);
            outline.Add(new KbHeading(id, text, heading.LocalName == "h2" ? 2 : 3));
        }
        foreach (var table in body.QuerySelectorAll("table").ToList())
        {
            // Wide tables scroll inside the reading column instead of stretching it.
            if (table.ParentElement?.ClassList.Contains("kb-table") == true)
            {
                continue;
            }
            var wrap = doc.CreateElement("div");
            wrap.ClassList.Add("kb-table");
            table.Parent!.ReplaceChild(wrap, table);
            wrap.AppendChild(table);
        }
        return new KbRenderedBody(body.InnerHtml, outline);
    }

    public static string Excerpt(string text, int max = 280)
    {
        var flat = Flatten(text);
        if (flat.Length <= max)
        {
            return flat;
        }
        var cut = flat.LastIndexOf(' ', max - 1);
        return flat[..(cut > max / 2 ? cut : max - 1)].TrimEnd(',', '.', ';', ':') + "…";
    }

    /// <summary>A short excerpt around the first matching term, for search results.</summary>
    public static string Snippet(string text, IReadOnlyList<string> terms, int max = 220)
    {
        var flat = Flatten(text);
        var at = terms
            .Select(t => flat.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (at < 0 || flat.Length <= max)
        {
            return Excerpt(flat, max);
        }
        var start = Math.Max(0, at - max / 3);
        if (start > 0)
        {
            var space = flat.IndexOf(' ', start);
            start = space > 0 && space < at ? space + 1 : start;
        }
        var slice = flat[start..];
        return (start > 0 ? "…" : "") + Excerpt(slice, max);
    }

    /// <summary>
    /// One line of reading text: paragraphs, headings and list items joined with spaces, with a full
    /// stop after lines that had none so "Workaround" and "Print single-sided" don't run together.
    /// </summary>
    private static string Flatten(string? text)
    {
        var lines = (text ?? "").Replace("\r", "").Split('\n')
            .Select(l => Whitespace().Replace(l, " ").Trim())
            .Where(l => l.Length > 0)
            .ToList();
        for (var i = 0; i < lines.Count - 1; i++)
        {
            if (!".!?:;,…".Contains(lines[i][^1]))
            {
                lines[i] += ".";
            }
        }
        return string.Join(' ', lines);
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
            if (sb.Length >= 48)
            {
                break;
            }
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "section" : slug;
    }

    [GeneratedRegex(@"/files/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex FileUrl();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
