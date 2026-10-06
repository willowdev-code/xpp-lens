using System.Text;
using System.Text.RegularExpressions;

namespace XppLens;

/// <summary>What part of a method to return: lines matching a pattern and/or a file line range.</summary>
public sealed record FragmentSpec(Regex? Match, string? MatchText, int From, int To, int Context)
{
    public static FragmentSpec? Create(string? match, string? lines, int context)
    {
        Regex? rx = null;
        if (!string.IsNullOrWhiteSpace(match))
        {
            try
            {
                rx = new Regex(match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException)
            {
                rx = new Regex(Regex.Escape(match), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
        int from = 0, to = 0;
        if (!string.IsNullOrWhiteSpace(lines))
        {
            var p = lines.Split(['-', ':', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (p.Length >= 1 && int.TryParse(p[0].TrimStart('L', 'l'), out var a)) from = to = a;
            if (p.Length >= 2 && int.TryParse(p[1].TrimStart('L', 'l'), out var b)) to = b;
            if (to < from) (from, to) = (to, from);
        }
        if (rx == null && from == 0) return null;
        return new FragmentSpec(rx, match, from, to, Math.Clamp(context, 0, 30));
    }
}

/// <summary>Source lines of one method with their file line numbers.</summary>
public static class CodeFragment
{
    public const int LongMethodHint = 150;

    /// <summary>Lines start..end of an AOT XML file, without the CDATA wrapper and surrounding blank lines.</summary>
    public static List<(int No, string Text)> FromFile(string path, int start, int end)
    {
        var lines = File.ReadLines(path).Skip(Math.Max(0, start - 1)).Take(Math.Max(1, end - start + 1))
            .Select((t, i) => (No: start + i, Text: t)).ToList();
        return Clean(lines);
    }

    /// <summary>Method source as stored by the parser; its first line sits on <paramref name="startLine"/> of the file.</summary>
    public static List<(int No, string Text)> FromSource(string source, int startLine) =>
        Clean(source.Split('\n').Select((t, i) => (No: startLine + i, Text: t.TrimEnd('\r'))).ToList());

    static List<(int No, string Text)> Clean(List<(int No, string Text)> lines)
    {
        if (lines.Count == 0) return lines;
        var first = lines[0].Text;
        var cd = first.IndexOf("<![CDATA[", StringComparison.Ordinal);
        if (cd >= 0) lines[0] = (lines[0].No, first[(cd + 9)..]);
        var last = lines[^1].Text;
        var ce = last.IndexOf("]]>", StringComparison.Ordinal);
        if (ce >= 0) lines[^1] = (lines[^1].No, last[..ce]);
        while (lines.Count > 0 && lines[0].Text.Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Text.Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    public static string Plain(List<(int No, string Text)> lines) => string.Join("\n", lines.Select(l => l.Text));

    /// <summary>
    /// The signature, variable declarations and the requested lines (matches ± context and/or a line range),
    /// numbered with file lines; gaps are marked so nothing is dropped silently.
    /// </summary>
    public static string Render(List<(int No, string Text)> lines, FragmentSpec spec)
    {
        if (lines.Count == 0) return "(no source lines)";
        var keep = new bool[lines.Count];
        var reason = new StringBuilder();

        // Signature: everything up to the first "{".
        for (int i = 0; i < lines.Count && i < 8; i++)
        {
            keep[i] = true;
            if (lines[i].Text.Contains('{')) break;
        }

        var text = string.Join("\n", lines.Select(l => l.Text));
        var toks = XppLexer.Tokenize(text);
        var h = CodeAnalyzer.ParseHeader(text, toks);
        int decl = 0;
        foreach (var l in CodeAnalyzer.DeclarationLines(toks, h.BodyTokenStart).Order())
            if (l < lines.Count && decl++ < 40) keep[l] = true;

        int matches = 0;
        if (spec.Match != null)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (!spec.Match.IsMatch(lines[i].Text)) continue;
                matches++;
                for (int k = Math.Max(0, i - spec.Context); k <= Math.Min(lines.Count - 1, i + spec.Context); k++) keep[k] = true;
            }
            reason.Append($"{matches} line(s) matching '{spec.MatchText}' (±{spec.Context})");
        }
        if (spec.From > 0)
        {
            int inRange = 0;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].No >= spec.From && lines[i].No <= spec.To) { keep[i] = true; inRange++; }
            if (reason.Length > 0) reason.Append(" + ");
            reason.Append(inRange > 0 ? $"lines {spec.From}-{spec.To}" : $"lines {spec.From}-{spec.To} (outside this method: L{lines[0].No}-{lines[^1].No})");
        }
        keep[^1] = true;

        int shown = keep.Count(k => k);
        var sb = new StringBuilder();
        sb.AppendLine($"fragment: {reason}; showing {shown} of {lines.Count} lines (signature and declarations always included)");
        int gap = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!keep[i]) { gap++; continue; }
            if (gap > 0) { sb.AppendLine($"      … {gap} line(s)"); gap = 0; }
            sb.AppendLine($"{lines[i].No,5}: {lines[i].Text}");
        }
        return sb.ToString().TrimEnd();
    }
}
