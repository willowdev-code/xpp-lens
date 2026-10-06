using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace XppGraft;

/// <summary>
/// Local log of MCP tool calls (one JSON line per call) and its summary for 'xppgraft stats'.
/// Stays on this machine; shows which tools cost the most tokens, which are slow and which return nothing.
/// </summary>
public static class Usage
{
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("XPPGRAFT_USAGE") != "0";

    public static string Dir =>
        Environment.GetEnvironmentVariable("XPPGRAFT_USAGE_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xpp-graft", "usage");

    static readonly object Gate = new();

    static readonly JsonSerializerOptions ArgOpts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    /// <summary>Openings of answers that mean "nothing useful found" — counted as empty results.</summary>
    static readonly string[] EmptyStarts =
    [
        "nothing found", "No object named", "no references", "No references recorded", "0 match", "no element named",
        "no relation path", "no data entity", "no build results", "nothing changed",
    ];

    static readonly string[] EmptyContains = [" has no method ", ": not found"];

    public static bool LooksEmpty(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return true;
        var head = result.TrimStart();
        // Skip a leading "[index refreshed…]" note.
        if (head.StartsWith('[') && head.IndexOf("]\n", StringComparison.Ordinal) is > 0 and var e) head = head[(e + 2)..];
        if (head.StartsWith("labels (", StringComparison.Ordinal))
            return head.Contains("': 0\n", StringComparison.Ordinal) || head.EndsWith("': 0", StringComparison.Ordinal);
        if (EmptyStarts.Any(m => head.StartsWith(m, StringComparison.OrdinalIgnoreCase))) return true;
        var firstLine = head.Split('\n')[0];
        if (EmptyContains.Any(m => firstLine.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
        return head.StartsWith("objects (0)", StringComparison.Ordinal) && !head.Contains("methods (") && !head.Contains("fields/members (");
    }

    public static void Record(string tool, object args, string? result, long ms, bool error)
    {
        if (!Enabled) return;
        try
        {
            var line = new JsonObject
            {
                ["t"] = DateTime.UtcNow.ToString("o"),
                ["tool"] = tool,
                ["args"] = JsonSerializer.SerializeToNode(args, ArgOpts),
                ["chars"] = result?.Length ?? 0,
                ["lines"] = result == null ? 0 : result.Count(c => c == '\n') + 1,
                ["ms"] = ms,
                ["empty"] = !error && LooksEmpty(result),
                ["error"] = error,
            }.ToJsonString();
            var path = Path.Combine(Dir, $"usage-{DateTime.UtcNow:yyyyMM}.jsonl");
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(path, line + "\n");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"usage log: {ex.Message}");
        }
    }

    public sealed record Entry(DateTime Utc, string Tool, string Args, int Chars, long Ms, bool Empty, bool Error)
    {
        public int Tokens => (Chars + 3) / 4;
    }

    public static List<Entry> Load(DateTime sinceUtc)
    {
        var list = new List<Entry>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var file in Directory.EnumerateFiles(Dir, "usage-*.jsonl").Order())
        {
            foreach (var raw in File.ReadLines(file))
            {
                if (raw.Length == 0) continue;
                try
                {
                    var n = JsonNode.Parse(raw)!;
                    var t = DateTime.Parse(n["t"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                    if (t < sinceUtc) continue;
                    list.Add(new Entry(t, n["tool"]!.GetValue<string>(), n["args"]?.ToJsonString() ?? "{}",
                        n["chars"]!.GetValue<int>(), n["ms"]!.GetValue<long>(), n["empty"]?.GetValue<bool>() ?? false,
                        n["error"]?.GetValue<bool>() ?? false));
                }
                catch (Exception)
                {
                    // a torn line from a crash — skip it
                }
            }
        }
        return list;
    }

    static double Percentile(List<int> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    public static string Report(int days, int top)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var all = Load(since);
        var sb = new StringBuilder();
        if (all.Count == 0)
            return $"no MCP calls recorded in the last {days} day(s) ({Dir}).";

        long totalTok = all.Sum(e => (long)e.Tokens);
        sb.AppendLine($"MCP calls in the last {days} day(s): {all.Count}, ~{totalTok:N0} tokens returned (chars/4)  [{Dir}]");
        sb.AppendLine();
        sb.AppendLine($"{"tool",-18}{"calls",6}{"avg tok",9}{"p95 tok",9}{"max tok",9}{"avg ms",8}{"p95 ms",8}{"empty",7}{"err",5}");
        foreach (var g in all.GroupBy(e => e.Tool).OrderByDescending(g => g.Sum(e => (long)e.Tokens)))
        {
            var tok = g.Select(e => e.Tokens).Order().ToList();
            var ms = g.Select(e => (int)Math.Min(int.MaxValue, e.Ms)).Order().ToList();
            sb.AppendLine($"{g.Key,-18}{g.Count(),6}{(int)tok.Average(),9}{Percentile(tok, 0.95),9}{tok[^1],9}" +
                          $"{(int)ms.Average(),8}{Percentile(ms, 0.95),8}{100.0 * g.Count(e => e.Empty) / g.Count(),6:0}%{g.Count(e => e.Error),5}");
        }

        void Section(string title, IEnumerable<Entry> rows, Func<Entry, string> metric)
        {
            var list = rows.Take(top).ToList();
            if (list.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine(title);
            foreach (var e in list) sb.AppendLine($"  {metric(e),9}  {e.Tool} {CodeAnalyzer.Collapse(e.Args, 140)}");
        }

        Section("largest answers:", all.OrderByDescending(e => e.Chars), e => $"{e.Tokens:N0} tok");
        Section("slowest calls:", all.OrderByDescending(e => e.Ms), e => $"{e.Ms:N0} ms");
        Section("empty answers (latest first) — possible gaps:", all.Where(e => e.Empty).OrderByDescending(e => e.Utc), e => e.Utc.ToLocalTime().ToString("MM-dd HH:mm"));
        Section("errors (latest first):", all.Where(e => e.Error).OrderByDescending(e => e.Utc), e => e.Utc.ToLocalTime().ToString("MM-dd HH:mm"));
        return sb.ToString().TrimEnd();
    }
}
