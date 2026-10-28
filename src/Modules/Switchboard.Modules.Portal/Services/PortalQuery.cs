using System.Text;
using System.Text.RegularExpressions;

namespace Switchboard.Modules.Portal.Services;

/// <summary>
/// Turns what a requester typed into a filter-language query that only searches text and record
/// numbers. Field terms (<c>group:</c>, <c>agent:</c>, …) and operators are quoted away, so the
/// portal search can't be used to probe internal fields.
/// </summary>
public static partial class PortalQuery
{
    private const int MaxWords = 12;
    private const int MaxWordLength = 100;

    public static string? FromUserText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var terms = new List<string>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Clean(raw);
            if (word.Length == 0)
            {
                continue;
            }
            if (word.Length > MaxWordLength)
            {
                word = word[..MaxWordLength];
            }
            terms.Add(RecordNumber().IsMatch(word) ? word : Quote(word));
            if (terms.Count == MaxWords)
            {
                break;
            }
        }
        return terms.Count == 0 ? null : string.Join(' ', terms);
    }

    public static string? ForState(RequestState state) => state switch
    {
        RequestState.Open => "state:unresolved",
        RequestState.Closed => "(state:resolved OR state:closed)",
        _ => null,
    };

    public static string? Join(params string?[] parts)
    {
        var joined = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return joined.Length == 0 ? null : joined;
    }

    private static string Clean(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string Quote(string word) =>
        "\"" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex(@"^(?:#|(?:INC|SR)-)\d{1,9}$", RegexOptions.IgnoreCase)]
    private static partial Regex RecordNumber();
}
