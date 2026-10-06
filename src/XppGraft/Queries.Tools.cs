using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace XppGraft;

/// <summary>Build results, recent changes, security chains, relation paths and data entities.</summary>
public sealed partial class Queries
{
    // ---------------------------------------------------------------- build errors

    internal sealed record Diagnostic(string Severity, string Path, string Message, int Line, int Column, string? Moniker);

    /// <summary>Diagnostics from a package's BuildModelResult.xml (written by the X++ build in Visual Studio).</summary>
    internal static (DateTime? Generated, List<Diagnostic> Items) ReadBuildResult(string file)
    {
        var doc = XDocument.Load(file);
        DateTime? generated = DateTime.TryParse(doc.Root?.Attribute("GenerationTime")?.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var g) ? g.ToUniversalTime() : null;
        var items = new List<Diagnostic>();
        foreach (var d in doc.Descendants().Where(e => e.Name.LocalName == "Diagnostic"))
        {
            string V(string n) => d.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value ?? "";
            int.TryParse(V("Line"), out var line);
            int.TryParse(V("Column"), out var col);
            items.Add(new Diagnostic(V("Severity"), V("Path"), V("Message").Trim(), line, col, V("Moniker") is { Length: > 0 } m ? m : null));
        }
        return (generated, items);
    }

    static readonly Dictionary<string, string> DynamicsTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Class"] = "AxClass", ["Table"] = "AxTable", ["Form"] = "AxForm", ["View"] = "AxView", ["Query"] = "AxQuery",
        ["Map"] = "AxMap", ["DataEntityView"] = "AxDataEntityView", ["Enum"] = "AxEnum", ["Edt"] = "AxEdt",
        ["TableExtension"] = "AxTableExtension", ["FormExtension"] = "AxFormExtension", ["Report"] = "AxReport",
    };

    /// <summary>"dynamics://Class/Foo/Method/bar" → (AxClass, Foo, owner, bar).</summary>
    internal static (string? Type, string? Name, string? Owner, string? Method) ParseDynamicsPath(string path)
    {
        const string prefix = "dynamics://";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (null, null, null, null);
        var p = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return (null, null, null, null);
        var type = DynamicsTypes.TryGetValue(p[0], out var t) ? t : "Ax" + p[0];
        string? method = null;
        var owner = new List<string>();
        for (int i = 2; i + 1 < p.Length; i += 2)
        {
            if (p[i].Equals("Method", StringComparison.OrdinalIgnoreCase)) { method = p[i + 1]; break; }
            owner.Add($"{p[i]}:{p[i + 1]}");
        }
        return (type, p[1], owner.Count > 0 ? string.Join("/", owner) : null, method);
    }

    public string BuildErrors(string? model, string? severity, int limit) => svc.Read(s =>
    {
        limit = Math.Clamp(limit, 1, 500);
        var sev = (severity ?? "error").Trim().ToLowerInvariant();
        bool Want(string sv) => sev switch
        {
            "all" => true,
            "warning" or "warnings" => sv is "Error" or "Warning",
            _ => sv == "Error",
        };

        var rx = string.IsNullOrWhiteSpace(model) ? null : Wildcard(model.Trim());
        var packages = svc.Models
            .Where(m => rx == null ? m.Full : rx.IsMatch(m.Name) || rx.IsMatch(m.Package))
            .Select(m => m.Package).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (packages.Count == 0) return $"no build results: no indexed package matches '{model}'.";

        var sb = new StringBuilder();
        var missing = new List<string>();
        int shown = 0;
        foreach (var pkg in packages)
        {
            var file = Path.Combine(svc.Cfg.PackagesDir, pkg, "BuildModelResult.xml");
            if (!File.Exists(file)) { missing.Add(pkg); continue; }
            (DateTime? Generated, List<Diagnostic> Items) res;
            try
            {
                res = ReadBuildResult(file);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{pkg}: cannot read {file}: {ex.Message}");
                continue;
            }
            var built = res.Generated ?? File.GetLastWriteTimeUtc(file);
            var counts = res.Items.GroupBy(d => d.Severity).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}").ToList();
            sb.AppendLine($"{pkg} — built {built.ToLocalTime():yyyy-MM-dd HH:mm}: {(counts.Count == 0 ? "clean" : string.Join(", ", counts))}");

            foreach (var d in res.Items.Where(d => Want(d.Severity)))
            {
                if (shown++ >= limit) continue;
                var (type, name, owner, method) = ParseDynamicsPath(d.Path);
                string where = d.Path;
                if (name != null)
                {
                    var hit = s.Query($"""
                        SELECT f.path, m.start_line, m.owner FROM methods m JOIN objects o ON o.id = m.object_id JOIN files f ON f.id = m.file_id
                        WHERE o.name = $n AND o.type = $t AND ($m IS NULL OR m.name = $m)
                        ORDER BY CASE WHEN IFNULL(m.owner, '') = IFNULL($o, '') THEN 0 ELSE 1 END LIMIT 1
                        """, ("$n", name), ("$t", type), ("$m", method), ("$o", owner))
                        .Select(r => (Path: r.GetString(0), Start: r.GetInt32(1))).FirstOrDefault();
                    var label = $"{type} {name}{(method != null ? "." + (owner != null ? owner + "/" : "") + method : "")}";
                    if (hit.Path != null)
                    {
                        var abs = method != null && d.Line > 0 ? hit.Start + d.Line - 1 : hit.Start;
                        var changed = File.Exists(hit.Path) && File.GetLastWriteTimeUtc(hit.Path) > built ? "  [file changed after this build]" : "";
                        where = $"{label}{(d.Line > 0 ? $" L{d.Line}:{d.Column}" : "")} → {hit.Path}:{abs}{changed}";
                    }
                    else where = $"{label}{(d.Line > 0 ? $" L{d.Line}:{d.Column}" : "")}";
                }
                sb.AppendLine($"  {d.Severity} {where}");
                sb.AppendLine($"      {CodeAnalyzer.Collapse(d.Message, 400)}{(d.Moniker != null ? $" ({d.Moniker})" : "")}");
            }
        }
        if (shown > limit) sb.AppendLine($"… {shown - limit} more (raise limit or filter by model)");
        if (sev == "error" && shown == 0 && sb.Length > 0) sb.AppendLine("no errors (pass severity=warning or all to list warnings)");
        if (missing.Count > 0) sb.AppendLine($"no BuildModelResult.xml (never built here): {string.Join(", ", missing)}");
        if (sb.Length == 0) sb.Append("no build results found.");
        sb.AppendLine("(line numbers in build results count from the first line of the method; → shows the line in the XML file)");
        return Finish(sb);
    });

    // ---------------------------------------------------------------- changed objects

    /// <summary>"24h", "3d", "90m", "2026-10-01", "2026-10-01 14:00" → UTC instant.</summary>
    internal static DateTime? ParseSince(string? since, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(since)) return nowUtc.AddHours(-24);
        since = since.Trim();
        if (since.Length >= 2 && double.TryParse(since[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            switch (char.ToLowerInvariant(since[^1]))
            {
                case 'm': return nowUtc.AddMinutes(-n);
                case 'h': return nowUtc.AddHours(-n);
                case 'd': return nowUtc.AddDays(-n);
                case 'w': return nowUtc.AddDays(-7 * n);
            }
        }
        return DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
               || DateTime.TryParse(since, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out d)
            ? d.ToUniversalTime()
            : null;
    }

    public string Changed(string? since, string? model, string? type, int limit) => svc.Read(s =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        var from = ParseSince(since, DateTime.UtcNow);
        if (from == null) return $"cannot read since='{since}' — use e.g. 24h, 3d, 2026-10-01 or '2026-10-01 14:00'.";
        var tp = TypePattern(type);
        var modelLike = PatOrNull(model);
        var where = $"""
            f.mtime > $t AND md.tier >= 0
            AND ($model IS NULL OR md.name LIKE $model ESCAPE '!' OR md.package LIKE $model ESCAPE '!') {(tp != null ? "AND o.type LIKE $tp" : "")}
            """;
        var args = new (string, object?)[] { ("$t", from.Value.Ticks), ("$model", modelLike), ("$tp", tp), ("$limit", limit) };
        const string from_ = "FROM files f JOIN objects o ON o.file_id = f.id JOIN models md ON md.id = f.model_id";

        var byModel = s.Query($"SELECT md.package, md.name, md.tier, COUNT(*) {from_} WHERE {where} GROUP BY md.id ORDER BY COUNT(*) DESC", args)
            .Select(r => $"{Tag(r.GetString(0), r.GetString(1), r.GetInt32(2))} {r.GetInt64(3)}").ToList();
        var labels = s.Query("""
            SELECT lf.label_file, lf.lang, lf.mtime, md.package, md.name, md.tier FROM label_files lf JOIN models md ON md.id = lf.model_id
            WHERE lf.mtime > $t AND md.tier >= 0 AND ($model IS NULL OR md.name LIKE $model ESCAPE '!' OR md.package LIKE $model ESCAPE '!')
            ORDER BY lf.mtime DESC LIMIT 50
            """, args).Select(r => $"  {new DateTime(r.GetInt64(2), DateTimeKind.Utc).ToLocalTime():MM-dd HH:mm} labels {r.GetString(0)} ({r.GetString(1)}) {Tag(r.GetString(3), r.GetString(4), r.GetInt32(5))}").ToList();

        var sb = new StringBuilder();
        var sinceText = from.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        if (byModel.Count == 0 && labels.Count == 0)
            return Finish(sb.Append($"nothing changed since {sinceText} (files on disk vs index; deleted objects are not listed)."));

        sb.AppendLine($"changed since {sinceText}: {string.Join(", ", byModel)}");
        foreach (var r in s.Query($"""
            SELECT f.mtime, o.type, o.name, md.package, md.name, md.tier {from_} WHERE {where} ORDER BY f.mtime DESC LIMIT $limit
            """, args))
            sb.AppendLine($"  {new DateTime(r.GetInt64(0), DateTimeKind.Utc).ToLocalTime():MM-dd HH:mm} {r.GetString(1)} {r.GetString(2)} {Tag(r.GetString(3), r.GetString(4), r.GetInt32(5))}");
        foreach (var l in labels) sb.AppendLine(l);
        sb.AppendLine("(by file time on disk — a Get Latest shows up here too; deleted objects are not listed)");
        return Finish(sb);
    });

    // ---------------------------------------------------------------- security

    sealed record SecRow(string Name, string Type, string Tag, string? Path, string Info);

    /// <summary>Containers (duties / roles…) whose reference list holds <paramref name="child"/>.</summary>
    List<SecRow> ContainersOf(Store s, string child, string refKind, string containerType) =>
        s.Query("""
            SELECT DISTINCT IFNULL(o.target, o.name), o.type, md.package, md.name, md.tier, f.path
            FROM members mb JOIN objects o ON o.id = mb.object_id JOIN files f ON f.id = mb.file_id JOIN models md ON md.id = f.model_id
            WHERE mb.kind = 'reference' AND mb.name = $n AND mb.info = $k AND o.type LIKE $t
            ORDER BY md.tier DESC, 1
            """, ("$n", child), ("$k", refKind), ("$t", containerType + "%"))
            .Select(r => new SecRow(r.GetString(0), r.GetString(1), Tag(r.GetString(2), r.GetString(3), r.GetInt32(4)), r.GetString(5), ""))
            .ToList();

    /// <summary>References held by a container (and its extensions), e.g. the duties of a role.</summary>
    List<SecRow> ContentsOf(Store s, string container, string refKind, string containerType) =>
        s.Query("""
            SELECT DISTINCT mb.name, o.type, md.package, md.name, md.tier, f.path
            FROM members mb JOIN objects o ON o.id = mb.object_id JOIN files f ON f.id = mb.file_id JOIN models md ON md.id = f.model_id
            WHERE mb.kind = 'reference' AND mb.info = $k AND o.type LIKE $t AND (o.name = $c OR o.target = $c)
            ORDER BY mb.name
            """, ("$c", container), ("$k", refKind), ("$t", containerType + "%"))
            .Select(r => new SecRow(r.GetString(0), r.GetString(1), Tag(r.GetString(2), r.GetString(3), r.GetInt32(4)), r.GetString(5), ""))
            .ToList();

    /// <summary>Privileges with an entry point on <paramref name="entry"/> (menu item, service operation…).</summary>
    List<SecRow> PrivilegesFor(Store s, string entry) =>
        s.Query("""
            SELECT IFNULL(o.target, o.name), o.type, md.package, md.name, md.tier, f.path, mb.info
            FROM members mb JOIN objects o ON o.id = mb.object_id JOIN files f ON f.id = mb.file_id JOIN models md ON md.id = f.model_id
            WHERE mb.kind = 'entrypoint' AND o.type LIKE 'AxSecurityPrivilege%' AND (' ' || mb.info || ' ') LIKE $p ESCAPE '!'
            ORDER BY md.tier DESC, 1
            """, ("$p", $"% {Esc(entry)} %"))
            .Select(r => new SecRow(r.GetString(0), r.GetString(1), Tag(r.GetString(2), r.GetString(3), r.GetInt32(4)), r.GetString(5), r.GetString(6)))
            .ToList();

    /// <summary>Access granted to <paramref name="entry"/> by a privilege (read live: older indexes lack it).</summary>
    static string Grant(SecRow privilege, string entry)
    {
        if (privilege.Info.Contains("grant=", StringComparison.Ordinal))
            return privilege.Info[privilege.Info.IndexOf("grant=", StringComparison.Ordinal)..].Split(' ')[0];
        if (privilege.Path == null || BinaryPackage.IsPseudoPath(privilege.Path) || !File.Exists(privilege.Path)) return "";
        try
        {
            var po = XmlObjectParser.Parse(privilege.Path, ParseMode.Render);
            var ep = po.Members.FirstOrDefault(m => m.Kind == "entrypoint" && (" " + m.Info + " ").Contains($" {entry} ", StringComparison.OrdinalIgnoreCase));
            return ep != null && ep.Info.Contains("grant=", StringComparison.Ordinal) ? ep.Info[ep.Info.IndexOf("grant=", StringComparison.Ordinal)..].Split(' ')[0] : "";
        }
        catch
        {
            return "";
        }
    }

    public string Security(string name, string? type, int limit) => svc.Read(s =>
    {
        limit = Math.Clamp(limit, 1, 200);
        name = name.Trim();
        var rows = ObjectRows(s, name, type);
        var o = rows.FirstOrDefault();
        var sb = new StringBuilder();
        var allRoles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        string Names(IEnumerable<SecRow> list, int max) =>
            list.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is var n && n.Count > max
                ? string.Join(", ", n.Take(max)) + $" … (+{n.Count - max})"
                : string.Join(", ", n);

        void Upward(string privilege, string indent)
        {
            var duties = ContainersOf(s, privilege, "Privilege", "AxSecurityDuty");
            var direct = ContainersOf(s, privilege, "Privilege", "AxSecurityRole");
            foreach (var r in direct) allRoles.Add(r.Name);
            foreach (var d in duties.Take(limit))
            {
                var roles = ContainersOf(s, d.Name, "Duty", "AxSecurityRole");
                foreach (var r in roles) allRoles.Add(r.Name);
                sb.AppendLine($"{indent}duty {d.Name} {d.Tag} → roles: {(roles.Count == 0 ? "(none)" : Names(roles, 15))}");
            }
            if (duties.Count > limit) sb.AppendLine($"{indent}… {duties.Count - limit} more duties");
            if (direct.Count > 0) sb.AppendLine($"{indent}roles directly: {Names(direct, 15)}");
            if (duties.Count == 0 && direct.Count == 0) sb.AppendLine($"{indent}(not in any duty or role)");
        }

        void FromEntryPoint(string entry, string title)
        {
            var privs = PrivilegesFor(s, entry);
            sb.AppendLine($"{title} → privileges ({privs.Count}):");
            foreach (var p in privs.Take(limit))
            {
                var grant = Grant(p, entry);
                sb.AppendLine($"  privilege {p.Name} {p.Tag}{(grant.Length > 0 ? " " + grant : "")}");
                Upward(p.Name, "    ");
            }
            if (privs.Count > limit) sb.AppendLine($"  … {privs.Count - limit} more privileges");
        }

        var t = o?.Type ?? "";
        if (t.StartsWith("AxSecurityRole", StringComparison.Ordinal))
        {
            sb.AppendLine($"{t} {name} {o!.Tag}");
            var duties = ContentsOf(s, name, "Duty", "AxSecurityRole");
            var privs = ContentsOf(s, name, "Privilege", "AxSecurityRole");
            var subRoles = ContentsOf(s, name, "Role", "AxSecurityRole");
            sb.AppendLine($"duties ({duties.Count}):");
            foreach (var d in duties.Take(limit))
                sb.AppendLine($"  {d.Name}: {ContentsOf(s, d.Name, "Privilege", "AxSecurityDuty").Count} privilege(s)");
            if (duties.Count > limit) sb.AppendLine($"  … {duties.Count - limit} more");
            if (privs.Count > 0) sb.AppendLine($"privileges directly ({privs.Count}): {Names(privs, limit)}");
            if (subRoles.Count > 0) sb.AppendLine($"sub-roles: {Names(subRoles, limit)}");
            var parents = ContainersOf(s, name, "Role", "AxSecurityRole");
            if (parents.Count > 0) sb.AppendLine($"contained in roles: {Names(parents, limit)}");
            return Finish(sb);
        }
        if (t.StartsWith("AxSecurityDuty", StringComparison.Ordinal))
        {
            sb.AppendLine($"{t} {name} {o!.Tag}");
            var privs = ContentsOf(s, name, "Privilege", "AxSecurityDuty");
            sb.AppendLine($"privileges ({privs.Count}): {Names(privs, limit)}");
            var roles = ContainersOf(s, name, "Duty", "AxSecurityRole");
            sb.AppendLine($"in roles ({roles.Count}): {Names(roles, limit)}");
            return Finish(sb);
        }
        if (t.StartsWith("AxSecurityPrivilege", StringComparison.Ordinal))
        {
            sb.AppendLine($"{t} {name} {o!.Tag}");
            if (!o.Compiled && File.Exists(o.Path))
            {
                var po = XmlObjectParser.Parse(o.Path, ParseMode.Render);
                var eps = po.Members.Where(m => m.Kind == "entrypoint").ToList();
                sb.AppendLine($"entry points ({eps.Count}):");
                foreach (var ep in eps.Take(limit)) sb.AppendLine($"  {ep.Name}: {ep.Info}");
            }
            Upward(name, "  ");
        }
        else if (t == "AxForm")
        {
            var items = s.Query($"{ObjSelect} WHERE o.type LIKE 'AxMenuItem%' AND (o.props LIKE $a ESCAPE '!' OR o.props LIKE $b ESCAPE '!') ORDER BY md.tier DESC, o.name LIMIT 30",
                ("$a", $"%Object={Esc(name)};%"), ("$b", $"%Object={Esc(name)}")).Select(r => ReadObj(r)).ToList();
            sb.AppendLine($"AxForm {name} {o!.Tag} is opened by {items.Count} menu item(s):");
            foreach (var mi in items) FromEntryPoint(mi.Name, $"{mi.Type} {mi.Name} {mi.Tag}");
            if (items.Count == 0) sb.AppendLine("  (no menu item points to this form — security comes from the menu item that opens it)");
        }
        else
        {
            FromEntryPoint(name, o != null ? $"{o.Type} {name} {o.Tag}" : $"{name} (no such object; searched as an entry point name)");
        }
        if (allRoles.Count > 0) sb.AppendLine($"roles in total ({allRoles.Count}): {string.Join(", ", allRoles.Take(60))}{(allRoles.Count > 60 ? " …" : "")}");
        return Finish(sb);
    });

    // ---------------------------------------------------------------- relation paths

    internal sealed record RelEdge(string From, string To, string Relation, string DefinedOn, List<(string A, string B)> Pairs, List<string> Fixed, bool Reverse);

    Dictionary<string, List<RelEdge>>? _graph;
    string _graphKey = "";

    /// <summary>"CustTable card=… on AccountNum=CustAccount, Fixed" → related table + field pairs (+ fixed constraints).</summary>
    internal static (string? Related, List<(string A, string B)> Pairs, List<string> Fixed) ParseRelationInfo(string info)
    {
        var pairs = new List<(string, string)>();
        var fixedC = new List<string>();
        int on = info.StartsWith("on ", StringComparison.Ordinal) ? 0 : info.IndexOf(" on ", StringComparison.Ordinal);
        var head = on < 0 ? info : info[..on];
        var first = head.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var related = first != null && !first.Contains('=') ? first : null;
        if (on >= 0)
        {
            foreach (var c in info[(on == 0 ? 3 : on + 4)..].Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = c.IndexOf('=');
                if (eq > 0) pairs.Add((c[..eq], c[(eq + 1)..]));
                else fixedC.Add(c);
            }
        }
        return (related, pairs, fixedC);
    }

    Dictionary<string, List<RelEdge>> RelationGraph(Store s)
    {
        var key = $"{s.Scalar("SELECT MAX(rowid) FROM members")}|{s.Scalar("SELECT COUNT(*) FROM files")}";
        if (_graph != null && key == _graphKey) return _graph;
        var g = new Dictionary<string, List<RelEdge>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(RelEdge e)
        {
            if (!seen.Add($"{e.From}|{e.To}|{string.Join(",", e.Pairs)}|{e.Reverse}")) return;
            if (!g.TryGetValue(e.From, out var list)) g[e.From] = list = [];
            list.Add(e);
        }
        foreach (var r in s.Query("""
            SELECT o.name, o.type, o.target, mb.name, mb.info FROM members mb JOIN objects o ON o.id = mb.object_id
            WHERE mb.kind = 'relation' AND o.type IN ('AxTable', 'AxTableExtension', 'AxView')
            """))
        {
            var table = r.GetString(1) == "AxTableExtension" ? N(r, 2) : r.GetString(0);
            var info = N(r, 4);
            if (table == null || info == null) continue;
            var (related, pairs, fixedC) = ParseRelationInfo(info);
            if (related == null || related.Equals(table, StringComparison.OrdinalIgnoreCase) && pairs.Count == 0) continue;
            Add(new RelEdge(table, related, r.GetString(3), table, pairs, fixedC, false));
            Add(new RelEdge(related, table, r.GetString(3), table, pairs.Select(p => (p.B, p.A)).ToList(), fixedC, true));
        }
        _graph = g;
        _graphKey = key;
        return g;
    }

    internal static List<List<RelEdge>> ShortestPaths(Dictionary<string, List<RelEdge>> g, string from, string to, int maxHops, int maxPaths)
    {
        var dist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [from] = 0 };
        var parents = new Dictionary<string, List<RelEdge>>(StringComparer.OrdinalIgnoreCase);
        var frontier = new List<string> { from };
        for (int depth = 1; depth <= maxHops && frontier.Count > 0 && !dist.ContainsKey(to); depth++)
        {
            var next = new List<string>();
            foreach (var node in frontier)
            {
                if (!g.TryGetValue(node, out var edges)) continue;
                foreach (var e in edges)
                {
                    if (dist.TryGetValue(e.To, out var d) && d < depth) continue;
                    if (!dist.ContainsKey(e.To)) { dist[e.To] = depth; next.Add(e.To); }
                    if (!parents.TryGetValue(e.To, out var ps)) parents[e.To] = ps = [];
                    ps.Add(e);
                }
            }
            frontier = next;
        }
        var result = new List<List<RelEdge>>();
        if (!dist.ContainsKey(to) || from.Equals(to, StringComparison.OrdinalIgnoreCase)) return result;

        void Back(string node, List<RelEdge> acc)
        {
            if (result.Count >= 500) return;
            if (node.Equals(from, StringComparison.OrdinalIgnoreCase)) { var p = acc.ToList(); p.Reverse(); result.Add(p); return; }
            if (!parents.TryGetValue(node, out var ps)) return;
            foreach (var e in ps.Where(e => dist.TryGetValue(e.From, out var d) && d == dist[node] - 1))
            {
                acc.Add(e);
                Back(e.From, acc);
                acc.RemoveAt(acc.Count - 1);
            }
        }
        Back(to, []);
        return result.OrderBy(p => p.Sum(EdgeCost)).Take(maxPaths).ToList();
    }

    static readonly System.Text.RegularExpressions.Regex CountrySuffix = new(@"_[A-Z]{2}(\d*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Lower is more natural: joins on real field pairs, relations named after the table they lead to, defined in the
    /// forward direction, and not country-specific (relation or field names ending in _RU, _FR…).
    /// </summary>
    internal static double EdgeCost(RelEdge e)
    {
        double c = 0;
        if (e.Pairs.Count == 0) c += 3;
        if (CountrySuffix.IsMatch(e.Relation) || e.Pairs.Any(p => CountrySuffix.IsMatch(p.A) || CountrySuffix.IsMatch(p.B))) c += 2;
        if (e.Reverse) c += 0.5;
        var target = e.Reverse ? e.From : e.To;
        if (!e.Relation.StartsWith(target, StringComparison.OrdinalIgnoreCase)) c += 0.5;
        if (e.Pairs.Any(p => p.A.Equals("RecId", StringComparison.OrdinalIgnoreCase) || p.B.Equals("RecId", StringComparison.OrdinalIgnoreCase))) c += 0.25;
        return c;
    }

    internal static string JoinCode(List<RelEdge> path)
    {
        var vars = new List<string>();
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string Var(string table)
        {
            var v = char.ToLowerInvariant(table[0]) + table[1..];
            used[v] = used.TryGetValue(v, out var n) ? n + 1 : 1;
            return used[v] == 1 ? v : v + used[v];
        }
        vars.Add(Var(path[0].From));
        foreach (var e in path) vars.Add(Var(e.To));

        var sb = new StringBuilder();
        sb.AppendLine($"    select {vars[0]}");
        for (int i = 0; i < path.Count; i++)
        {
            var e = path[i];
            var ind = new string(' ', 8 + 4 * i);
            sb.AppendLine($"{ind}join {vars[i + 1]}");
            var conds = e.Pairs.Select(p => $"{vars[i + 1]}.{p.B} == {vars[i]}.{p.A}").ToList();
            if (conds.Count > 0) sb.AppendLine($"{ind}    where {string.Join($"\n{ind}       && ", conds)}");
            if (e.Fixed.Count > 0) sb.AppendLine($"{ind}    // + fixed constraint(s) of relation {e.Relation}: {string.Join(", ", e.Fixed)}");
        }
        return sb.ToString().TrimEnd() + ";";
    }

    public string Join(string from, string to, int maxHops, int limit) => svc.Read(s =>
    {
        maxHops = Math.Clamp(maxHops, 1, 5);
        limit = Math.Clamp(limit, 1, 10);
        from = from.Trim();
        to = to.Trim();
        var g = RelationGraph(s);
        foreach (var t in new[] { from, to })
            if (ObjectRows(s, t, null).Count == 0) return NotFound(s, t, "table");
        var paths = ShortestPaths(g, from, to, maxHops, limit);
        var sb = new StringBuilder();
        if (paths.Count == 0)
            return Finish(sb.Append($"no relation path from {from} to {to} within {maxHops} hop(s) (table relations only; EDT relations are not followed)."));
        sb.AppendLine($"{from} → {to}: {paths[0].Count} hop(s), {paths.Count} path(s) shown");
        int k = 0;
        foreach (var p in paths)
        {
            sb.AppendLine($"{++k}) {string.Join("  →  ", p.Select(e => $"{e.To} via {e.Relation} (on {e.DefinedOn}{(e.Reverse ? ", reverse" : "")})"))}");
            sb.AppendLine(JoinCode(p));
        }
        return Finish(sb);
    });

    // ---------------------------------------------------------------- data entities

    static readonly string[] EntityProps =
    [
        "PublicEntityName", "PublicCollectionName", "IsPublic", "IsReadOnly", "DataManagementEnabled", "DataManagementStagingTable",
        "EntityCategory", "PrimaryKey", "Label", "PrimaryCompanyContext", "SupportsSetBasedSqlOperations", "ConfigurationKey",
    ];

    public string Entity(string name, string? sections, int limit) => svc.Read(s =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        name = name.Trim();
        var want = string.IsNullOrWhiteSpace(sections)
            ? null
            : new HashSet<string>(sections.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        bool Show(string sec) => want == null || want.Contains(sec);
        var sb = new StringBuilder();
        var cache = new Dictionary<string, string?>();

        var rows = ObjectRows(s, name, null);
        var e = rows.FirstOrDefault(r => r.Type == "AxDataEntityView")
                ?? s.Query($"""
                    {ObjSelect} WHERE o.type = 'AxDataEntityView' AND (o.props LIKE $a ESCAPE '!' OR o.props LIKE $b ESCAPE '!'
                      OR o.props LIKE $c ESCAPE '!' OR o.props LIKE $d ESCAPE '!') ORDER BY md.tier DESC LIMIT 1
                    """, ("$a", $"%PublicEntityName={Esc(name)};%"), ("$b", $"%PublicEntityName={Esc(name)}"),
                    ("$c", $"%PublicCollectionName={Esc(name)};%"), ("$d", $"%PublicCollectionName={Esc(name)}"))
                    .Select(r => ReadObj(r)).FirstOrDefault();

        if (e == null)
        {
            // A table or view: which entities expose it?
            var users = s.Query("""
                SELECT DISTINCT IFNULL(o.target, o.name), o.type, md.package, md.name, md.tier, mb.name, mb.info, o.props
                FROM members mb JOIN objects o ON o.id = mb.object_id JOIN files f ON f.id = mb.file_id JOIN models md ON md.id = f.model_id
                WHERE mb.kind = 'datasource' AND o.type IN ('AxDataEntityView', 'AxDataEntityViewExtension') AND (mb.info = $t OR mb.info LIKE $p ESCAPE '!')
                ORDER BY md.tier DESC, 1 LIMIT 200
                """, ("$t", name), ("$p", Esc(name) + " %"))
                .Select(r => (Entity: r.GetString(0), Type: r.GetString(1), Tag: Tag(r.GetString(2), r.GetString(3), r.GetInt32(4)), Ds: r.GetString(5),
                    Public: N(r, 7) is { } pr && pr.Contains("PublicEntityName=") ? pr[(pr.IndexOf("PublicEntityName=", StringComparison.Ordinal) + 17)..].Split(';')[0] : null))
                .ToList();
            if (users.Count == 0)
                return Finish(sb.Append(rows.Count == 0
                    ? $"no data entity named '{name}' (also searched public entity / collection names)."
                    : $"no data entity uses {rows[0].Type} {name} as a data source."));
            sb.AppendLine($"data entities using {name} ({users.Count}{(users.Count == 200 ? "+" : "")}):");
            foreach (var u in users.Take(limit))
                sb.AppendLine($"  {u.Entity} {u.Tag} — data source {u.Ds}{(u.Public != null ? $", public name {u.Public}" : "")}{(u.Type.EndsWith("Extension", StringComparison.Ordinal) ? " (added by extension)" : "")}");
            return Finish(sb);
        }

        var po = e.Compiled ? LoadCompiled(s, e) : XmlObjectParser.Parse(e.Path, ParseMode.Render);
        sb.AppendLine($"AxDataEntityView {e.Name} {e.Tag}");
        sb.AppendLine(e.Compiled ? "source: none — compiled package" : $"file: {e.Path}");
        if (Show("props"))
        {
            var props = EntityProps.Select(k => po.RootProps.FirstOrDefault(kv => kv.Key == k)).Where(kv => kv.Key != null && kv.Value.Length > 0)
                .Select(kv => $"{kv.Key}={kv.Value}");
            sb.AppendLine("props: " + WithLabels(s, string.Join("; ", props), cache));
        }
        if (Show("datasources"))
        {
            var ds = po.Members.Where(m => m.Kind == "datasource").OrderBy(m => m.Order).ToList();
            sb.AppendLine($"data sources ({ds.Count}):");
            foreach (var d in ds) sb.AppendLine($"{new string(' ', 2 * Math.Max(1, d.Depth))}{d.Name}: {d.Info}");
        }
        if (Show("keys"))
        {
            var keys = po.Members.Where(m => m.Kind == "index").ToList();
            if (keys.Count > 0) sb.AppendLine("keys: " + string.Join("; ", keys.Select(k => $"{k.Name} {k.Info}")));
        }
        if (Show("fields"))
        {
            var fields = po.Members.Where(m => m.Kind == "field").ToList();
            var mapped = new List<string>();
            var other = new List<string>();
            foreach (var f in fields)
            {
                var src = f.Info.Split(' ').FirstOrDefault(p => p.Contains('.') && !p.Contains('='));
                if (src != null) mapped.Add($"{f.Name} ← {src}");
                else other.Add($"{f.Name} ({f.Info})");
            }
            sb.AppendLine($"fields ({fields.Count}): {mapped.Count} mapped, {other.Count} computed/virtual");
            foreach (var l in mapped.Take(limit)) sb.AppendLine("  " + l);
            foreach (var l in other.Take(Math.Max(0, limit - mapped.Count))) sb.AppendLine("  " + WithLabels(s, l, cache));
            if (fields.Count > limit) sb.AppendLine($"  … {fields.Count - limit} more (raise limit)");
        }
        if (Show("methods"))
        {
            var methods = po.Methods.Where(m => !m.IsDeclaration).Select(m => m.Name).ToList();
            if (methods.Count > 0) sb.AppendLine($"methods ({methods.Count}): {string.Join(", ", methods)}");
        }
        if (Show("extensions"))
        {
            var ext = s.Query($"{ObjSelect} WHERE o.target = $n ORDER BY md.tier DESC, o.name LIMIT 40", ("$n", e.Name)).Select(r => ReadObj(r)).ToList();
            if (ext.Count > 0)
            {
                sb.AppendLine($"extensions ({ext.Count}):");
                foreach (var x in ext) sb.AppendLine($"  {x.Type} {x.Name} {x.Tag}{ExtensionDetail(s, x)}");
            }
        }
        var staging = po.RootProps.FirstOrDefault(kv => kv.Key == "DataManagementStagingTable").Value;
        if (!string.IsNullOrEmpty(staging)) sb.AppendLine($"staging table: {staging} (xpp_object {staging})");
        return Finish(sb);
    });
}
