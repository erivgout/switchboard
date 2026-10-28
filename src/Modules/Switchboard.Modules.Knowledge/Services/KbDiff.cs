using System.Text.RegularExpressions;

namespace Switchboard.Modules.Knowledge.Services;

public enum KbDiffOp
{
    Same,
    Added,
    Removed,
}

public sealed record KbDiffSegment(string Text, KbDiffOp Op);

/// <summary>One paragraph of the comparison. Changed paragraphs carry word-level segments.</summary>
public sealed record KbDiffLine(KbDiffOp Op, IReadOnlyList<KbDiffSegment> Segments, bool Changed = false);

/// <summary>Paragraph-then-word diff of two versions' plain text, for the version history (FR-KB-2).</summary>
public static partial class KbDiff
{
    private const int MaxCells = 4_000_000;

    public static IReadOnlyList<KbDiffLine> Compare(string? before, string? after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var ops = Lcs(a, b, StringComparer.Ordinal);
        var result = new List<KbDiffLine>();

        for (var i = 0; i < ops.Count; i++)
        {
            var (op, text) = ops[i];
            if (op == KbDiffOp.Removed && i + 1 < ops.Count && ops[i + 1].Op == KbDiffOp.Added)
            {
                // A paragraph that was edited, not replaced: show the words that changed.
                var words = WordDiff(text, ops[i + 1].Text);
                var same = words.Where(w => w.Op == KbDiffOp.Same).Sum(w => w.Text.Length);
                if (same * 2 >= Math.Max(text.Length, ops[i + 1].Text.Length) / 2)
                {
                    result.Add(new KbDiffLine(KbDiffOp.Same, words, Changed: true));
                    i++;
                    continue;
                }
            }
            result.Add(new KbDiffLine(op, [new KbDiffSegment(text, op)]));
        }
        return result;
    }

    private static List<KbDiffSegment> WordDiff(string before, string after)
    {
        var a = Tokens().Matches(before).Select(m => m.Value).ToList();
        var b = Tokens().Matches(after).Select(m => m.Value).ToList();
        var segments = new List<KbDiffSegment>();
        foreach (var (op, text) in Lcs(a, b, StringComparer.Ordinal))
        {
            if (segments.Count > 0 && segments[^1].Op == op)
            {
                segments[^1] = segments[^1] with { Text = segments[^1].Text + text };
            }
            else
            {
                segments.Add(new KbDiffSegment(text, op));
            }
        }
        return segments;
    }

    private static List<(KbDiffOp Op, string Text)> Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b, IEqualityComparer<string> eq)
    {
        // Trim the common prefix and suffix first; most edits touch a small part of an article.
        var start = 0;
        while (start < a.Count && start < b.Count && eq.Equals(a[start], b[start]))
        {
            start++;
        }
        var endA = a.Count;
        var endB = b.Count;
        while (endA > start && endB > start && eq.Equals(a[endA - 1], b[endB - 1]))
        {
            endA--;
            endB--;
        }

        var result = new List<(KbDiffOp, string)>();
        for (var i = 0; i < start; i++)
        {
            result.Add((KbDiffOp.Same, a[i]));
        }

        var n = endA - start;
        var m = endB - start;
        if ((long)n * m > MaxCells)
        {
            // Too large to align: show the middle as removed then added.
            for (var i = start; i < endA; i++)
            {
                result.Add((KbDiffOp.Removed, a[i]));
            }
            for (var j = start; j < endB; j++)
            {
                result.Add((KbDiffOp.Added, b[j]));
            }
        }
        else
        {
            var table = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    table[i, j] = eq.Equals(a[start + i], b[start + j])
                        ? table[i + 1, j + 1] + 1
                        : Math.Max(table[i + 1, j], table[i, j + 1]);
                }
            }
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (eq.Equals(a[start + x], b[start + y]))
                {
                    result.Add((KbDiffOp.Same, a[start + x]));
                    x++;
                    y++;
                }
                else if (table[x + 1, y] >= table[x, y + 1])
                {
                    result.Add((KbDiffOp.Removed, a[start + x]));
                    x++;
                }
                else
                {
                    result.Add((KbDiffOp.Added, b[start + y]));
                    y++;
                }
            }
            for (; x < n; x++)
            {
                result.Add((KbDiffOp.Removed, a[start + x]));
            }
            for (; y < m; y++)
            {
                result.Add((KbDiffOp.Added, b[start + y]));
            }
        }

        for (var i = endA; i < a.Count; i++)
        {
            result.Add((KbDiffOp.Same, a[i]));
        }
        return result;
    }

    private static List<string> Lines(string? text) =>
        (text ?? "").Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

    [GeneratedRegex(@"\s+|[^\s]+")]
    private static partial Regex Tokens();
}
