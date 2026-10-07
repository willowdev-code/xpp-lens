using System.Globalization;
using System.Text;

namespace XppLens;

/// <summary>How a <see cref="TextTable"/> is drawn.</summary>
public enum TableStyle
{
    /// <summary>Markdown pipe table (MCP answers).</summary>
    Markdown,

    /// <summary>Box-drawing frame ┌─┬─┐ (console with UTF-8 output).</summary>
    Box,

    /// <summary>Frame from + - | (redirected output, --ascii).</summary>
    Ascii,
}

/// <summary>A small text table with left or right aligned columns.</summary>
public sealed class TextTable(params string[] headers)
{
    readonly List<string[]> _rows = [];
    readonly HashSet<int> _right = [];

    static readonly NumberFormatInfo Groups = new() { NumberGroupSeparator = " ", NumberGroupSizes = [3] };

    /// <summary>12345 → "12 345".</summary>
    public static string Num(long n) => n.ToString("#,0", Groups);

    /// <summary>Aligns the given columns (0-based) to the right — for numbers.</summary>
    public TextTable Right(params int[] columns)
    {
        foreach (var c in columns) _right.Add(c);
        return this;
    }

    public TextTable Row(params string[] cells)
    {
        _rows.Add(cells);
        return this;
    }

    public string Render(TableStyle style)
    {
        var cols = headers.Length;
        string Cell(string[] row, int c) => c < row.Length ? (row[c] ?? "") : "";
        var width = Enumerable.Range(0, cols)
            .Select(c => Math.Max(headers[c].Length, _rows.Count == 0 ? 0 : _rows.Max(r => Cell(r, c).Length)))
            .ToArray();
        string Pad(string text, int c) => _right.Contains(c) ? text.PadLeft(width[c]) : text.PadRight(width[c]);

        var sb = new StringBuilder();
        if (style == TableStyle.Markdown)
        {
            string Md(string s) => s.Replace("|", "\\|");
            sb.AppendLine("| " + string.Join(" | ", headers.Select(Md)) + " |");
            sb.AppendLine("|" + string.Join("|", Enumerable.Range(0, cols).Select(c => _right.Contains(c) ? "---:" : "---")) + "|");
            foreach (var r in _rows)
                sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(0, cols).Select(c => Md(Cell(r, c)))) + " |");
            return sb.ToString();
        }

        var box = style == TableStyle.Box;
        string Line(char left, char mid, char right) =>
            left + string.Join(mid, width.Select(w => new string(box ? '─' : '-', w + 2))) + right;
        string Cells(string[] row)
        {
            var bar = box ? '│' : '|';
            return bar + string.Join(bar, Enumerable.Range(0, cols).Select(c => " " + Pad(Cell(row, c), c) + " ")) + bar;
        }

        sb.AppendLine(box ? Line('┌', '┬', '┐') : Line('+', '+', '+'));
        sb.AppendLine(Cells(headers));
        sb.AppendLine(box ? Line('├', '┼', '┤') : Line('+', '+', '+'));
        foreach (var r in _rows) sb.AppendLine(Cells(r));
        sb.AppendLine(box ? Line('└', '┴', '┘') : Line('+', '+', '+'));
        return sb.ToString();
    }
}
