using System.Text;

namespace Switchboard.Modules.ServiceDesk.Services;

/// <summary>CSV writing that neutralizes spreadsheet formula injection (SR-INP-6).</summary>
public static class CsvExport
{
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>
    /// Prefixes a value that a spreadsheet would treat as a formula (starting with <c>=</c>, <c>+</c>,
    /// <c>-</c>, <c>@</c>, tab or carriage return) with an apostrophe so it stays text.
    /// </summary>
    public static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        return Array.IndexOf(FormulaTriggers, value[0]) >= 0 ? "'" + value : value;
    }

    /// <summary>A neutralized value, quoted for CSV.</summary>
    public static string Cell(string? value) => "\"" + Neutralize(value).Replace("\"", "\"\"") + "\"";

    public static string Row(IEnumerable<string?> values) => string.Join(",", values.Select(Cell));

    /// <summary>Writes UTF-8 with a byte-order mark so spreadsheet apps detect the encoding.</summary>
    public static async Task WriteAsync(Stream output, IReadOnlyList<string> header, IAsyncEnumerable<IReadOnlyList<string?>> rows, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        await writer.WriteAsync(Row(header) + "\r\n");
        await foreach (var row in rows.WithCancellation(ct))
        {
            await writer.WriteAsync(Row(row) + "\r\n");
        }
        await writer.FlushAsync(ct);
    }
}
