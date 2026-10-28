using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Switchboard.Modules.Mcp.Server;

/// <summary>
/// Hidden-content stripping for everything handed to an agent (MCP-63): HTML comments, invisible
/// Unicode, and text styled so a person reading the ticket wouldn't see it.
/// </summary>
public static partial class ContentSafety
{
    public const string HiddenFlag = "contained_hidden_text";

    private static readonly HtmlParser Parser = new();

    /// <summary>Text a person would actually see in <paramref name="html"/>, and whether anything was hidden.</summary>
    public static (string Text, bool HadHidden) VisibleText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return ("", false);
        }

        var hidden = false;
        var doc = Parser.ParseDocument($"<body>{html}</body>");
        var body = doc.Body!;

        foreach (var comment in body.Descendants<IComment>().ToList())
        {
            hidden |= comment.TextContent.Trim().Length > 0;
            comment.Remove();
        }
        foreach (var element in body.QuerySelectorAll("script,style,template,noscript,head,title,meta").ToList())
        {
            hidden |= element.TextContent.Trim().Length > 0;
            element.Remove();
        }
        foreach (var element in body.QuerySelectorAll("*").ToList())
        {
            if (element.Parent is null || !IsHidden(element))
            {
                continue;
            }
            hidden |= element.TextContent.Trim().Length > 0;
            element.Remove();
        }

        foreach (var br in body.QuerySelectorAll("br").ToList())
        {
            br.Replace(doc.CreateTextNode("\n"));
        }
        foreach (var block in body.QuerySelectorAll("p,div,li,tr,h1,h2,h3,h4,h5,h6,blockquote,pre"))
        {
            block.Append(doc.CreateTextNode("\n"));
        }

        var text = string.Join("\n", (body.TextContent ?? "").Replace("\r", "").Split('\n').Select(l => l.TrimEnd())).Trim();
        var (clean, invisible) = StripInvisible(text);
        return (CollapseBlankLines(clean), hidden || invisible);
    }

    /// <summary>Removes invisible Unicode and comment markup from plain text.</summary>
    public static (string Text, bool HadHidden) CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ("", false);
        }
        var withoutComments = CommentPattern().Replace(text, "");
        var (clean, invisible) = StripInvisible(withoutComments);
        return (clean, invisible || withoutComments.Length != text.Length);
    }

    /// <summary>
    /// Cleans every string in a tool result. Untrusted wrappers (<c>trust: "untrusted"</c>) that contained
    /// hidden text get <c>contained_hidden_text: true</c>. Returns true when anything was removed.
    /// </summary>
    public static bool Sanitize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var any = false;
                var isUntrusted = obj["trust"] is JsonValue t && t.TryGetValue<string>(out var trust) && trust == "untrusted";
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var (clean, hidden) = key == "value" && isUntrusted ? CleanText(s) : StripInvisible(s);
                        if (hidden)
                        {
                            obj[key] = clean;
                            any = true;
                        }
                    }
                    else
                    {
                        any |= Sanitize(child);
                    }
                }
                if (isUntrusted && obj[HiddenFlag] is JsonValue flag && flag.TryGetValue<bool>(out var already) && already)
                {
                    any = true;
                }
                else if (isUntrusted && any)
                {
                    obj[HiddenFlag] = true;
                }
                return any;
            }
            case JsonArray array:
            {
                var any = false;
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var (clean, hidden) = StripInvisible(s);
                        if (hidden)
                        {
                            array[i] = clean;
                            any = true;
                        }
                    }
                    else
                    {
                        any |= Sanitize(array[i]);
                    }
                }
                return any;
            }
            default:
                return false;
        }
    }

    /// <summary>Removes zero-width, bidi-control, tag, variation-selector and filler characters.</summary>
    public static (string Text, bool Removed) StripInvisible(string text)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            int codePoint;
            int width;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                width = 2;
            }
            else
            {
                codePoint = text[i];
                width = 1;
            }

            if (IsInvisible(codePoint))
            {
                sb ??= new StringBuilder(text, 0, i, text.Length);
            }
            else
            {
                sb?.Append(text, i, width);
            }
            i += width - 1;
        }
        return sb is null ? (text, false) : (sb.ToString(), true);
    }

    private static bool IsInvisible(int cp)
    {
        if (cp is '\n' or '\r' or '\t')
        {
            return false;
        }
        if (cp is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF) // variation selectors
            or 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800 // filler characters that render blank
            or (>= 0xE0000 and <= 0xE007F)) // tag characters
        {
            return true;
        }
        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        // Cf covers zero-width spaces/joiners, bidi controls, soft hyphen, word joiner and BOM.
        return category is UnicodeCategory.Format or UnicodeCategory.Control;
    }

    private static bool IsHidden(IElement element)
    {
        if (element.HasAttribute("hidden"))
        {
            return true;
        }
        var style = (element.GetAttribute("style") ?? "").ToLowerInvariant().Replace(" ", "");
        if (style.Contains("display:none") || style.Contains("visibility:hidden") || style.Contains("opacity:0;") || style.EndsWith("opacity:0")
            || FontSizeZero().IsMatch(style) || style.Contains("color:transparent"))
        {
            return true;
        }
        var color = CssValue(style, "color") ?? element.GetAttribute("color");
        if (color is null)
        {
            return false;
        }
        var background = CssValue(style, "background-color") ?? CssValue(style, "background") ?? InheritedBackground(element);
        var fg = ParseColor(color);
        if (fg is null)
        {
            return false;
        }
        if (fg.Value.A == 0)
        {
            return true;
        }
        var bg = background is null ? (R: 255, G: 255, B: 255, A: 255) : ParseColor(background) ?? (R: 255, G: 255, B: 255, A: 255);
        // Text whose color is (almost) the background color is invisible to a reader.
        return Math.Abs(Luminance(fg.Value) - Luminance(bg)) < 0.08;
    }

    private static string? InheritedBackground(IElement element)
    {
        for (var e = element.ParentElement; e is not null; e = e.ParentElement)
        {
            var style = (e.GetAttribute("style") ?? "").ToLowerInvariant().Replace(" ", "");
            if ((CssValue(style, "background-color") ?? CssValue(style, "background")) is { } bg)
            {
                return bg;
            }
        }
        return null;
    }

    private static string? CssValue(string style, string property)
    {
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon > 0 && declaration[..colon] == property)
            {
                return declaration[(colon + 1)..].Replace("!important", "");
            }
        }
        return null;
    }

    private static (int R, int G, int B, int A)? ParseColor(string value)
    {
        value = value.Trim().ToLowerInvariant();
        switch (value)
        {
            case "white": return (255, 255, 255, 255);
            case "black": return (0, 0, 0, 255);
            case "transparent": return (0, 0, 0, 0);
        }
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => $"{c}{c}"));
            }
            if (hex.Length is 6 or 8 && int.TryParse(hex[..6], NumberStyles.HexNumber, null, out var rgb))
            {
                var a = hex.Length == 8 && int.TryParse(hex[6..], NumberStyles.HexNumber, null, out var alpha) ? alpha : 255;
                return ((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255, a);
            }
            return null;
        }
        var m = RgbPattern().Match(value);
        if (m.Success)
        {
            var a = m.Groups[4].Success && double.TryParse(m.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
                ? (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255)
                : 255;
            return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), a);
        }
        return null;
    }

    private static double Luminance((int R, int G, int B, int A) c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static string CollapseBlankLines(string text) => BlankLines().Replace(text, "\n\n");

    [GeneratedRegex(@"<!--.*?(-->|$)", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"font-size:0(px|pt|em|rem|%)?(;|$)")]
    private static partial Regex FontSizeZero();

    [GeneratedRegex(@"rgba?\((\d{1,3}),(\d{1,3}),(\d{1,3})(?:,([\d.]+))?\)")]
    private static partial Regex RgbPattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}

/// <summary>Stable hashing of tool arguments, so a confirmation binds to exactly one set of parameters (MCP-52).</summary>
public static class ParameterHash
{
    public static string Compute(string tool, JsonObject arguments)
    {
        var canonical = $"{tool}\n{Canonicalize(arguments)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>JSON with object keys sorted at every level and no insignificant whitespace.</summary>
    public static string Canonicalize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, node);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    Write(writer, value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    Write(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
