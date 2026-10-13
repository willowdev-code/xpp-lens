using System.Text;
using System.Text.RegularExpressions;

namespace XppLens;

/// <summary>
/// The distinct names of objects, methods and members, held in memory. A wildcard or substring search ("*Invoice*")
/// would otherwise read the whole objects / methods / members tables — seconds from the OS cache, minutes from a
/// slow disk after the cache was dropped. Here it is matched in memory and the index answers only exact names.
/// Each list is one string of "\n"-separated names, which keeps the memory small (2 bytes per character).
/// </summary>
public sealed class NameDirectory
{
    public enum Kind { Object, Method, Member }

    sealed record Snapshot(string Objects, string Methods, string Members, int Count);

    volatile Snapshot? _snapshot;
    int _generation;

    /// <summary>A search with more matching names than this goes to the index as before.</summary>
    public const int MaxNames = 2000;

    public bool Ready => _snapshot != null;

    public int Count => _snapshot?.Count ?? 0;

    /// <summary>The index changed: searches use the index until <see cref="Build"/> has read the new names.</summary>
    public int Invalidate()
    {
        _snapshot = null;
        return Interlocked.Increment(ref _generation);
    }

    /// <summary>Reads the names (index-only scans of the name indexes). Dropped when the index changed meanwhile.</summary>
    public void Build(Store s)
    {
        var generation = Volatile.Read(ref _generation);
        string Read(string table, out int count)
        {
            var sb = new StringBuilder("\n");
            count = 0;
            foreach (var r in s.Query($"SELECT DISTINCT name FROM {table}"))
            {
                sb.Append(r.GetString(0)).Append('\n');
                count++;
            }
            return sb.ToString();
        }
        var snapshot = new Snapshot(Read("objects", out var o), Read("methods", out var m), Read("members", out var mb), o + m + mb);
        if (Volatile.Read(ref _generation) == generation) _snapshot = snapshot;
    }

    /// <summary>
    /// Names matching a user pattern: * and ? are wildcards, plain text matches anywhere in the name (as LIKE '%text%').
    /// Null when the directory is not ready or more than <see cref="MaxNames"/> names match.
    /// </summary>
    public List<string>? Match(Kind kind, string pattern)
    {
        var snap = _snapshot;
        if (snap == null) return null;
        var text = kind switch { Kind.Object => snap.Objects, Kind.Method => snap.Methods, _ => snap.Members };
        var result = new List<string>();
        var whole = WholeName(pattern);
        // The longest fixed part of the pattern is found with a vectorized IndexOf; only the names (lines) holding it
        // are checked against the full pattern. A pattern of wildcards only ("*", "?x?"…) scans every name.
        var literal = pattern.Trim().Split('*', '?').MaxBy(p => p.Length) ?? "";
        if (literal.Length == 0)
        {
            foreach (Match m in PatternRegex(pattern).Matches(text))
            {
                if (result.Count == MaxNames) return null;
                result.Add(m.Value);
            }
            return result;
        }
        int pos = 0;
        while (pos < text.Length && text.IndexOf(literal, pos, StringComparison.OrdinalIgnoreCase) is >= 0 and var hit)
        {
            int start = text.LastIndexOf('\n', hit) + 1;
            int end = text.IndexOf('\n', hit + literal.Length);
            if (end < 0) end = text.Length;
            var name = text[start..end];
            if (whole.IsMatch(name))
            {
                if (result.Count == MaxNames) return null;
                result.Add(name);
            }
            pos = end + 1;
        }
        return result;
    }

    static string Body(string pattern, out bool wildcard)
    {
        var p = pattern.Trim();
        wildcard = p.Contains('*') || p.Contains('?');
        return Regex.Escape(p).Replace(@"\*", @"[^\n]*").Replace(@"\?", @"[^\n]");
    }

    /// <summary>
    /// A user pattern against one whole name: * and ? are wildcards anchored at both ends, plain text matches
    /// anywhere (LIKE '%text%'); case-insensitive, '_' and '.' are literals.
    /// </summary>
    public static Regex WholeName(string pattern)
    {
        var body = Body(pattern, out var wildcard);
        return new Regex(wildcard ? $"^{body}$" : body, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The same pattern over a "\n"-separated list of names (one match per whole line).</summary>
    internal static Regex PatternRegex(string pattern)
    {
        var body = Body(pattern, out var wildcard);
        if (!wildcard) body = @"[^\n]*" + body + @"[^\n]*";
        return new Regex($"(?<=\n){body}(?=\n)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
