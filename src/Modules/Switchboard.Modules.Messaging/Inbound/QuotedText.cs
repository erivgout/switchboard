using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Switchboard.Modules.Messaging.Notifications;

namespace Switchboard.Modules.Messaging.Inbound;

public sealed record StripResult(string Content, bool Removed);

/// <summary>
/// Removes quoted history and signatures from replies (FR-CH-3). Replies are assumed to be
/// top-posted, so everything from the first sign of quoting onwards goes: our reply marker,
/// client quote containers (Gmail, Outlook, Apple Mail, Thunderbird, Yahoo), "On … wrote:" lines,
/// "Original Message" separators, header blocks, "&gt;" quotes and "-- " signatures. If nothing
/// would be left, the original is kept.
/// </summary>
public static partial class QuotedText
{
    private static readonly HtmlParser Parser = new();

    /// <summary>Start of <see cref="EmailLayout.ReplyMarker"/>.</summary>
    private static readonly string MarkerPrefix = EmailLayout.ReplyMarker[..4];

    private static readonly string[] QuoteContainers =
    [
        "div.gmail_quote", "blockquote.gmail_quote", "div.gmail_extra", "div.gmail_signature",
        "div#appendonsend", "div#divRplyFwdMsg", "hr#stopSpelling", "div#Signature",
        "div.moz-cite-prefix", "div.moz-signature", "div.yahoo_quoted", "div.zmail_extra",
        "blockquote[type=cite]", "div[data-marker=__QUOTED_TEXT__]", "div[data-marker=__SIG_POST__]",
    ];

    public static StripResult StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new StripResult(html ?? "", false);
        }
        var doc = Parser.ParseDocument($"<body>{html}</body>");
        var body = doc.Body!;
        var originalText = Normalize(body.TextContent);

        var cut = FindCut(body);
        if (cut is null)
        {
            RemoveMobileSignatures(body);
            return new StripResult(body.InnerHtml.Trim(), Normalize(body.TextContent) != originalText);
        }

        CutFrom(cut, body);
        RemoveMobileSignatures(body);
        TrimTrailingEmpty(body);
        var remaining = Normalize(body.TextContent);
        if (remaining.Length == 0 && body.QuerySelector("img") is null)
        {
            return new StripResult(html, false);
        }
        return new StripResult(body.InnerHtml.Trim(), true);
    }

    public static StripResult StripText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new StripResult(text ?? "", false);
        }
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cut = lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Contains(MarkerPrefix, StringComparison.Ordinal)
                || WroteLine().IsMatch(line)
                || (line.StartsWith("On ", StringComparison.Ordinal) && i + 1 < lines.Length && WroteEnd().IsMatch(lines[i + 1].Trim()) && line.Length + lines[i + 1].Length < 300)
                || OriginalMessage().IsMatch(line)
                || (Underscores().IsMatch(line) && NextIsHeader(lines, i + 1))
                || (HeaderLine().IsMatch(line) && NextIsHeader(lines, i + 1))
                || line == "--" || lines[i] == "-- "
                || (line.StartsWith('>') && RestIsQuoted(lines, i)))
            {
                cut = i;
                break;
            }
        }

        var kept = lines.Take(cut)
            .Where(l => !MobileSignature().IsMatch(l.Trim()))
            .ToList();
        while (kept.Count > 0 && kept[^1].Trim().Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }
        var result = string.Join("\n", kept).Trim('\n');
        if (result.Trim().Length == 0)
        {
            return new StripResult(text, false);
        }
        return new StripResult(result, cut < lines.Length || kept.Count < Math.Min(cut, lines.Length));
    }

    private static IElement? FindCut(IElement body)
    {
        IElement? first = null;
        void Consider(IElement? candidate)
        {
            if (candidate is not null && (first is null || IsBefore(candidate, first)))
            {
                first = candidate;
            }
        }

        // Our own marker.
        foreach (var el in body.QuerySelectorAll("*"))
        {
            if (el.Children.Length == 0 && el.TextContent.Contains(MarkerPrefix, StringComparison.Ordinal))
            {
                Consider(el);
                break;
            }
        }
        foreach (var selector in QuoteContainers)
        {
            var match = body.QuerySelector(selector);
            if (match is not null && selector.StartsWith("blockquote[type=cite]", StringComparison.Ordinal))
            {
                // Include the "On … wrote:" line that introduces the quote.
                var previous = match.PreviousElementSibling;
                Consider(previous is not null && WroteLine().IsMatch(Normalize(previous.TextContent)) ? previous : match);
            }
            else
            {
                Consider(match);
            }
        }
        foreach (var block in body.QuerySelectorAll("p, div, span, font, b"))
        {
            if (block.Children.Any(c => c.LocalName is "p" or "div"))
            {
                continue;
            }
            var text = Normalize(block.TextContent);
            if (text.Length > 400)
            {
                continue;
            }
            var inner = block.InnerHtml.TrimStart();
            if (WroteLine().IsMatch(text) || OriginalMessage().IsMatch(text) || text is "--"
                || inner.StartsWith("-- <br", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("--<br", StringComparison.OrdinalIgnoreCase))
            {
                Consider(block);
            }
            else if (OutlookHeader().IsMatch(text))
            {
                // Outlook's "From: … Sent: … To: … Subject:" block, often inside a bordered div.
                Consider(block.ParentElement is { } parent && parent != body && parent.GetAttribute("style")?.Contains("border-top", StringComparison.OrdinalIgnoreCase) == true ? parent : block);
            }
        }
        foreach (var hr in body.QuerySelectorAll("hr"))
        {
            var next = hr.NextElementSibling;
            if (next is not null && OutlookHeader().IsMatch(Normalize(next.TextContent)))
            {
                Consider(hr);
            }
        }
        return first;
    }

    /// <summary>Removes <paramref name="node"/> and everything after it in document order.</summary>
    private static void CutFrom(IElement node, IElement body)
    {
        INode current = node;
        while (current != body && current.Parent is not null)
        {
            var parent = current.Parent;
            var sibling = current.NextSibling;
            while (sibling is not null)
            {
                var next = sibling.NextSibling;
                parent.RemoveChild(sibling);
                sibling = next;
            }
            current = parent;
        }
        node.Parent?.RemoveChild(node);
    }

    private static void RemoveMobileSignatures(IElement body)
    {
        foreach (var el in body.QuerySelectorAll("p, div, span").ToList())
        {
            if (el.Children.Length == 0 && MobileSignature().IsMatch(Normalize(el.TextContent)))
            {
                el.Remove();
            }
        }
    }

    private static void TrimTrailingEmpty(IElement body)
    {
        while (body.LastChild is { } last)
        {
            var empty = last is IText t ? string.IsNullOrWhiteSpace(t.Data)
                : last is IElement e && e.LocalName is "br" or "p" or "div" && Normalize(e.TextContent).Length == 0 && e.QuerySelector("img") is null;
            if (!empty)
            {
                break;
            }
            body.RemoveChild(last);
        }
    }

    private static bool IsBefore(IElement a, IElement b) =>
        (a.CompareDocumentPosition(b) & DocumentPositions.Following) != 0 || b.IsDescendantOf(a);

    private static bool NextIsHeader(string[] lines, int start)
    {
        for (var i = start; i < Math.Min(lines.Length, start + 4); i++)
        {
            if (HeaderLine().IsMatch(lines[i].Trim()) || SentLine().IsMatch(lines[i].Trim()))
            {
                return true;
            }
        }
        return false;
    }

    private static bool RestIsQuoted(string[] lines, int start)
    {
        var rest = lines.Skip(start).Where(l => l.Trim().Length > 0).ToList();
        return rest.Count > 0 && rest.Count(l => l.TrimStart().StartsWith('>')) >= rest.Count * 0.8;
    }

    private static string Normalize(string text) => Whitespace().Replace(text.Replace(' ', ' '), " ").Trim();

    [GeneratedRegex(@"^(On|Am|Le|El|Il|Op)\s.{3,250}(wrote|schrieb|a écrit|escribió|ha scritto|schreef)\s*:$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex WroteLine();

    [GeneratedRegex(@"(wrote|schrieb|a écrit|escribió)\s*:$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex WroteEnd();

    [GeneratedRegex(@"^-{2,}\s*(Original Message|Forwarded message|Ursprüngliche Nachricht|Message d'origine)\s*-{2,}$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex OriginalMessage();

    [GeneratedRegex(@"^_{10,}$", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex Underscores();

    [GeneratedRegex(@"^\*?(From|Von|De)\s*:\*?\s", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"^\*?(Sent|Date|Gesendet|Envoyé|To|Subject)\s*:", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex SentLine();

    [GeneratedRegex(@"^\*?(From|Von|De)\s*:.{1,300}(Sent|Date|Gesendet|Envoyé)\s*:", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 250)]
    private static partial Regex OutlookHeader();

    [GeneratedRegex(@"^(Sent from my (iPhone|iPad|Android|mobile|Galaxy|phone)|Get Outlook for (iOS|Android)|Sent from (Outlook|Mail) for)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex MobileSignature();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex Whitespace();
}
