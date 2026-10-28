using System.Text;

namespace Switchboard.Modules.Admin.Services;

/// <summary>RFC 4180 CSV reading and writing. Written cells are neutralized so spreadsheets never run them as formulas.</summary>
public static class Csv
{
    /// <summary>Parses CSV text. Detects comma, semicolon or tab from the header line and strips a byte order mark.</summary>
    public static List<string[]> Parse(string text, int maxRows = int.MaxValue)
    {
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();

        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }
                continue;
            }
            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Count > 1 || row[0].Length > 0)
                {
                    rows.Add([.. row]);
                    if (rows.Count >= maxRows)
                    {
                        return rows;
                    }
                }
                row.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }
        row.Add(cell.ToString());
        if (row.Count > 1 || row[0].Length > 0)
        {
            rows.Add([.. row]);
        }
        return rows;
    }

    public static string Write(IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Quotes a cell and prefixes values a spreadsheet would treat as a formula (CSV injection).</summary>
    public static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '|' or '%')
        {
            v = "'" + v;
        }
        return v.IndexOfAny([',', '"', '\n', '\r', ';']) >= 0 || v != v.Trim() ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
