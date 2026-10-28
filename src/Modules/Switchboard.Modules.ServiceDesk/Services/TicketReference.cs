using System.Globalization;
using System.Text.RegularExpressions;

namespace Switchboard.Modules.ServiceDesk.Services;

/// <summary>Recognizes a typed ticket reference such as <c>INC-1042</c>, <c>SR-77</c> or <c>#1042</c>.</summary>
public static partial class TicketReference
{
    public static bool TryParse(string? text, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var match = Pattern().Match(text.Trim());
        return match.Success
            && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    /// <summary>Turns free text into a filter-language phrase that matches subject and description.</summary>
    public static string ToTextQuery(string text) =>
        "\"" + text.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [GeneratedRegex(@"^(?:(?:INC|SR)\s*-\s*|#)(?<n>\d{1,9})$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
