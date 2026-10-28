using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace Switchboard.Modules.ServiceDesk.Services;

/// <summary>
/// Renders a canned-response preview with its placeholders shown as readable tokens
/// ("Requester's first name") instead of raw <c>{{requester.first_name}}</c>, which looks like a bug
/// outside a ticket. The tokens are filled in for real when the response is inserted on a ticket.
/// </summary>
public static partial class CannedPreview
{
    private static readonly Dictionary<string, string> Labels =
        // Short form for inline display: "Ticket ID, like INC-1042" becomes "Ticket ID".
        CannedResponseService.Placeholders.ToDictionary(p => p.Token, p => p.Meaning.Split(',')[0], StringComparer.Ordinal);

    public static IHtmlContent Render(string preview)
    {
        var enc = HtmlEncoder.Default;
        var html = new StringBuilder();
        var last = 0;
        foreach (Match m in Token().Matches(preview))
        {
            html.Append(enc.Encode(preview[last..m.Index]));
            if (Labels.TryGetValue(m.Value, out var label))
            {
                html.Append("<span class=\"sd-ph\" title=\"")
                    .Append(enc.Encode($"Filled in when you insert it: {m.Value}"))
                    .Append("\">")
                    .Append(enc.Encode(label))
                    .Append("</span>");
            }
            else
            {
                html.Append(enc.Encode(m.Value));
            }
            last = m.Index + m.Length;
        }
        html.Append(enc.Encode(preview[last..]));
        return new HtmlString(html.ToString());
    }

    [GeneratedRegex(@"\{\{[a-z_.]+\}\}")]
    private static partial Regex Token();
}
