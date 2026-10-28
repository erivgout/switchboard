using System.Net;
using System.Text.RegularExpressions;

namespace Switchboard.Modules.Messaging.Notifications;

/// <summary>
/// The frame every Switchboard email is sent in. Email clients ignore stylesheets, so styles are
/// inline and colors are the design system's light palette written out.
/// </summary>
public static partial class EmailLayout
{
    /// <summary>Marks where a reply starts; the inbound pipeline cuts quoted history at this line.</summary>
    public const string ReplyMarker = "##- Type your reply above this line -##";

    /// <param name="bodyHtml">Sanitized body.</param>
    /// <param name="footer">Plain text.</param>
    public static string Wrap(string bodyHtml, string organizationName, string? footer, bool replyMarker) => $"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{WebUtility.HtmlEncode(organizationName)}</title></head>
        <body style="margin:0;padding:0;background-color:#e9eeeb">
        {Frame(bodyHtml, organizationName, footer, replyMarker)}
        </body></html>
        """;

    /// <summary>The email without the document shell; used for previews inside the app.</summary>
    public static string Frame(string bodyHtml, string organizationName, string? footer, bool replyMarker)
    {
        var marker = replyMarker
            ? $"""<tr><td style="padding:12px 28px 0;font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:18px;color:#8a9692">{WebUtility.HtmlEncode(ReplyMarker)}</td></tr>"""
            : "";
        var foot = string.IsNullOrWhiteSpace(footer)
            ? ""
            : $"""<tr><td style="padding:14px 28px 18px;border-top:1px solid #d3dbd7;font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:18px;color:#5b6763">{WebUtility.HtmlEncode(footer)}</td></tr>""";
        return $"""
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:#e9eeeb"><tr><td align="center" style="padding:24px 12px">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:600px;background-color:#ffffff;border:1px solid #d3dbd7;border-radius:6px">
            {marker}
            <tr><td style="padding:20px 28px 22px;font-family:'Atkinson Hyperlegible Next','Atkinson Hyperlegible',Arial,Helvetica,sans-serif;font-size:15px;line-height:24px;color:#1b2422">{bodyHtml}</td></tr>
            {foot}
            </table>
            <p style="margin:12px 0 0;font-family:Arial,Helvetica,sans-serif;font-size:12px;color:#5b6763">{WebUtility.HtmlEncode(organizationName)}</p>
            </td></tr></table>
            """;
    }

    /// <summary>Email clients can't follow site-relative links; make them absolute.</summary>
    public static string AbsolutizeLinks(string html, string baseUrl) =>
        RelativeLink().Replace(html, m => $"{m.Groups[1].Value}=\"{baseUrl.TrimEnd('/')}/");

    [GeneratedRegex("""\b(href|src)="/(?!/)""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex RelativeLink();
}
