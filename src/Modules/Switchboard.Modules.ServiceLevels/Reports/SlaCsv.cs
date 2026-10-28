using System.Globalization;
using System.Text;

namespace Switchboard.Modules.ServiceLevels.Reports;

/// <summary>RFC 4180 CSV with spreadsheet formula injection neutralized (SR-INP-6).</summary>
public static class SlaCsv
{
    public static byte[] Write(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(Cell(row[i]));
            }
            sb.Append("\r\n");
        }
        // BOM so spreadsheet apps read UTF-8.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static string Cell(string? value)
    {
        var v = value ?? "";
        // A cell a spreadsheet would evaluate starts with one of these; a leading quote makes it text.
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string? Time(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
