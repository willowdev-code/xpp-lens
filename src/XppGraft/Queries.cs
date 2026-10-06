using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace XppGraft;

public sealed partial class Queries(IndexService svc)
{
    const string ObjSelect = """
        SELECT o.id, o.file_id, o.type, o.name, o.target, o.extends, o.header, o.props, f.path, md.package, md.name, md.tier
        FROM objects o JOIN files f ON f.id = o.file_id JOIN models md ON md.id = f.model_id
        """;

    const string TypePriority = """
        CASE o.type WHEN 'AxClass' THEN 0 WHEN 'AxTable' THEN 1 WHEN 'AxDataEntityView' THEN 2 WHEN 'AxView' THEN 3
        WHEN 'AxForm' THEN 4 WHEN 'AxEdt' THEN 5 WHEN 'AxEnum' THEN 6 WHEN 'AxQuery' THEN 7 WHEN 'AxMap' THEN 8 ELSE 20 END
        """;

    static readonly Dictionary<string, string> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["class"] = "AxClass", ["table"] = "AxTable", ["form"] = "AxForm", ["edt"] = "AxEdt", ["enum"] = "AxEnum",
        ["view"] = "AxView", ["entity"] = "AxDataEntityView", ["dataentity"] = "AxDataEntityView", ["query"] = "AxQuery",
        ["map"] = "AxMap", ["menuitem"] = "AxMenuItem%", ["display"] = "AxMenuItemDisplay", ["action"] = "AxMenuItemAction",
        ["output"] = "AxMenuItemOutput", ["menu"] = "AxMenu", ["report"] = "AxReport", ["privilege"] = "AxSecurityPrivilege",
        ["duty"] = "AxSecurityDuty", ["role"] = "AxSecurityRole", ["policy"] = "AxSecurityPolicy",
        ["tableext"] = "AxTableExtension", ["formext"] = "AxFormExtension", ["enumext"] = "AxEnumExtension",
        ["edtext"] = "AxEdtExtension", ["extension"] = "Ax%Extension", ["service"] = "AxService",
        ["servicegroup"] = "AxServiceGroup", ["workflow"] = "AxWorkflow%", ["security"] = "AxSecurity%",
    };

    sealed record ObjRow(long Id, long FileId, string Type, string Name, string? Target, string? Extends, string? Header,
        string? Props, string Path, string Package, string Model, int Tier)
    {
        public string Tag => Queries.Tag(Package, Model, Tier);
        public bool Compiled => Tier == -1 || BinaryPackage.IsPseudoPath(Path);
    }

    static ObjRow ReadObj(SqliteDataReader r, int o = 0) => new(
        r.GetInt64(o), r.GetInt64(o + 1), r.GetString(o + 2), r.GetString(o + 3), N(r, o + 4), N(r, o + 5), N(r, o + 6),
        N(r, o + 7), r.GetString(o + 8), r.GetString(o + 9), r.GetString(o + 10), r.GetInt32(o + 11));

    static string? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary>Tier 1 = custom (no suffix), 0 = standard, -1 = compiled-only package.</summary>
    static string Tag(string package, string model, int tier)
    {
        var suffix = tier switch { 1 => "", 0 => ", std", _ => ", compiled" };
        return package.Equals(model, StringComparison.OrdinalIgnoreCase) ? $"[{model}{suffix}]" : $"[{package}/{model}{suffix}]";
    }

    static string? TypePattern(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        type = type.Trim();
        if (TypeAliases.TryGetValue(type, out var a)) return a;
        return type.StartsWith("Ax", StringComparison.OrdinalIgnoreCase) ? type : "Ax" + type;
    }

    /// <summary>
    /// LIKE patterns use '!' as the escape character, so a literal "_" in X++ names ("Address_Ext",
    /// "_Extension") is not a single-character wildcard. Users write * and ? as wildcards.
    /// </summary>
    static string Esc(string s) => s.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
    static string Pat(string q) => Esc(q).Replace('*', '%').Replace('?', '_');
    static string Like(string q) => HasWildcard(q) ? Pat(q) : $"%{Esc(q)}%";
    static string? PatOrNull(string? q) => string.IsNullOrWhiteSpace(q) ? null : Pat(q.Trim());
    static bool HasWildcard(string q) => q.Contains('*') || q.Contains('?');

    static string Trunc(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max] + "…";

    string Finish(StringBuilder sb)
    {
        var note = svc.LastRefreshNote;
        if (note.Length > 0) sb.Insert(0, note + "\n");
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- batches

    public const int MaxBatch = 20;

    /// <summary>"a; b" or one item per line → distinct items (several lookups in one tool call).</summary>
    internal static List<string> SplitList(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : s.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static string Batch(IReadOnlyList<(string Title, Func<string> Run)> items)
    {
        if (items.Count == 1) return items[0].Run();
        var sb = new StringBuilder();
        if (items.Count > MaxBatch) sb.AppendLine($"(batch limited to the first {MaxBatch} of {items.Count} items)");
        foreach (var (title, run) in items.Take(MaxBatch))
        {
            sb.AppendLine($"### {title}");
            sb.AppendLine(run().TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- labels

    [GeneratedRegex(@"@[A-Za-z_][A-Za-z0-9_]*:[A-Za-z0-9_.\-]+|@[A-Z]{3}\d+")]
    private static partial Regex InlineLabelRx();

    string? LabelText(Store s, string id, string? lang = null)
    {
        lang ??= svc.Cfg.DisplayLanguage;
        var t = s.Scalar("SELECT text FROM labels WHERE label_id=$id AND lang=$l COLLATE NOCASE LIMIT 1", ("$id", id), ("$l", lang)) as string;
        return t ?? s.Scalar("SELECT text FROM labels WHERE label_id=$id LIMIT 1", ("$id", id)) as string;
    }

    string WithLabels(Store s, string text, Dictionary<string, string?> cache)
    {
        return InlineLabelRx().Replace(text, m =>
        {
            if (!cache.TryGetValue(m.Value, out var t)) cache[m.Value] = t = LabelText(s, m.Value);
            return t == null ? m.Value : $"{m.Value} \"{Trunc(t, 70)}\"";
        });
    }

    // ---------------------------------------------------------------- lookup helpers

    List<ObjRow> ObjectRows(Store s, string name, string? type)
    {
        var tp = TypePattern(type);
        var sql = $"{ObjSelect} WHERE o.name = $n {(tp != null ? "AND o.type LIKE $tp" : "")} ORDER BY md.tier DESC, {TypePriority}, o.type";
        return s.Query(sql, ("$n", name), ("$tp", tp)).Select(r => ReadObj(r)).ToList();
    }

    string NotFound(Store s, string name, string? type)
    {
        var tp = TypePattern(type);
        var similar = s.Query($"{ObjSelect} WHERE o.name LIKE $l ESCAPE '!' {(tp != null ? "AND o.type LIKE $tp" : "")} ORDER BY md.tier DESC, length(o.name) LIMIT 12",
            ("$l", $"%{Esc(name)}%"), ("$tp", tp)).Select(r => ReadObj(r)).ToList();
        var sb = new StringBuilder($"No object named '{name}'{(tp != null ? $" of type {tp}" : "")}.");
        if (similar.Count > 0)
        {
            sb.AppendLine(" Similar:");
            foreach (var o in similar) sb.AppendLine($"  {o.Type} {o.Name} {o.Tag}");
        }
        return sb.ToString();
    }

    static List<string> SelfAndDescendants(Store s, string name) =>
        s.Query("""
            WITH RECURSIVE d(name) AS (SELECT $n UNION SELECT o.name FROM objects o JOIN d ON o.extends = d.name)
            SELECT name FROM d LIMIT 300
            """, ("$n", name)).Select(r => r.GetString(0)).ToList();

    static List<string> Ancestors(Store s, string name) =>
        s.Query("""
            WITH RECURSIVE a(name) AS (
              SELECT extends FROM objects WHERE name = $n AND extends IS NOT NULL
              UNION SELECT o.extends FROM objects o JOIN a ON o.name = a.name WHERE o.extends IS NOT NULL)
            SELECT name FROM a LIMIT 30
            """, ("$n", name)).Select(r => r.GetString(0)).ToList();

    static string InList(string prefix, IReadOnlyList<object?> values, List<(string, object?)> args)
    {
        var names = new List<string>();
        for (int i = 0; i < values.Count; i++)
        {
            names.Add($"{prefix}{i}");
            args.Add(($"{prefix}{i}", values[i]));
        }
        return string.Join(",", names);
    }

    // ---------------------------------------------------------------- find

    public string Find(string query, string? kind, string? type, string? model, int limit) =>
        Batch(SplitList(query).Select(q => (q, (Func<string>)(() => FindOne(q, kind, type, model, limit)))).ToList() is { Count: > 0 } items
            ? items
            : [(query, () => "nothing found (empty query)")]);

    string FindOne(string query, string? kind, string? type, string? model, int limit) => svc.Read(s =>
    {
        limit = Math.Clamp(limit, 1, 200);
        query = query.Trim();
        var sb = new StringBuilder();
        string? objPart = query, memPart = null;
        int sep = query.IndexOf("::", StringComparison.Ordinal);
        int sepLen = 2;
        if (sep < 0) { sep = query.IndexOf('.'); sepLen = 1; }
        if (sep >= 0)
        {
            objPart = query[..sep].Trim();
            memPart = query[(sep + sepLen)..].Trim();
            if (objPart.Length == 0) objPart = null;
            if (memPart.Length == 0) memPart = null;
        }
        var tp = TypePattern(type);
        var modelLike = PatOrNull(model);
        const string modelFilter = "AND ($model IS NULL OR md.name LIKE $model ESCAPE '!' OR md.package LIKE $model ESCAPE '!')";

        // Extension objects are named "Base.Model", so a dot is not always "Object.member".
        bool dotted = sep >= 0 && sepLen == 1;
        bool kindGiven = !string.IsNullOrWhiteSpace(kind);
        bool extType = tp != null && tp.Contains("Extension", StringComparison.OrdinalIgnoreCase);
        if (dotted && (extType || (kindGiven && kind!.Trim().Equals("object", StringComparison.OrdinalIgnoreCase))))
        {
            objPart = query;
            memPart = null;
        }
        kind = !kindGiven ? (memPart != null ? "member" : "any") : kind!.Trim().ToLowerInvariant();
        string? objectQuery = memPart == null && kind is "any" or "object" ? objPart
            : dotted && !kindGiven ? query
            : null;

        if (objectQuery != null)
        {
            var rows = s.Query($"""
                {ObjSelect}
                WHERE o.name LIKE $like ESCAPE '!' {(tp != null ? "AND o.type LIKE $tp" : "")} {modelFilter}
                ORDER BY CASE WHEN o.name = $q THEN 0 WHEN o.name LIKE $qprefix ESCAPE '!' THEN 1 ELSE 2 END, md.tier DESC, {TypePriority}, length(o.name), o.name
                LIMIT $limit
                """, ("$like", Like(objectQuery)), ("$tp", tp), ("$q", objectQuery), ("$qprefix", Esc(objectQuery) + "%"), ("$model", modelLike), ("$limit", limit))
                .Select(r => ReadObj(r)).ToList();
            if (rows.Count > 0 || memPart == null)
            {
                sb.AppendLine($"objects ({rows.Count}{(rows.Count == limit ? "+" : "")}):");
                foreach (var o in rows)
                    sb.AppendLine($"  {o.Type} {o.Name}{(o.Target != null ? $" → {o.Target}" : "")}{(o.Extends != null ? $" : {o.Extends}" : "")} {o.Tag}");
            }
        }

        string? methodName = memPart ?? (kind is "method" or "field" or "member" ? objPart : null);
        string? ownerName = memPart != null ? objPart : null;
        bool exactOnly = kind == "any" && memPart == null && objPart != null && !HasWildcard(objPart);
        if (kind == "any" && memPart == null) methodName = objPart;

        if (methodName != null && kind is "any" or "method" or "member")
        {
            var mCond = exactOnly ? "m.name = $mq" : "m.name LIKE $mlike ESCAPE '!'";
            var oCond = ownerName == null ? "" : HasWildcard(ownerName) ? "AND o.name LIKE $olike ESCAPE '!'" : "AND o.name = $oq";
            var rows = s.Query($"""
                SELECT o.type, o.name, m.owner, m.name, m.sig, m.start_line, md.package, md.name, md.tier
                FROM methods m JOIN objects o ON o.id = m.object_id JOIN files f ON f.id = m.file_id JOIN models md ON md.id = f.model_id
                WHERE {mCond} {oCond} {(tp != null ? "AND o.type LIKE $tp" : "")} {modelFilter} AND m.name <> 'classDeclaration'
                ORDER BY CASE WHEN m.name = $mq THEN 0 ELSE 1 END, md.tier DESC, {TypePriority}, o.name
                LIMIT $limit
                """, ("$mq", methodName), ("$mlike", Like(methodName)), ("$olike", PatOrNull(ownerName)),
                ("$oq", ownerName), ("$tp", tp), ("$model", modelLike), ("$limit", limit))
                .Select(r => (Type: r.GetString(0), Obj: r.GetString(1), Owner: N(r, 2), Name: r.GetString(3), Sig: N(r, 4),
                    Line: r.GetInt32(5), Tag: Tag(r.GetString(6), r.GetString(7), r.GetInt32(8)))).ToList();
            if (rows.Count > 0 || (kind != "any" && !(dotted && !kindGiven)))
            {
                sb.AppendLine($"methods ({rows.Count}{(rows.Count == limit ? "+" : "")}):");
                foreach (var m in rows)
                    sb.AppendLine($"  {m.Type} {m.Obj}.{(string.IsNullOrEmpty(m.Owner) ? "" : m.Owner + "/")}{m.Name} {m.Tag}{(m.Line > 0 ? $" L{m.Line}" : "")}  {Trunc(m.Sig, 160)}");
            }
        }

        if (methodName != null && kind is "any" or "field" or "member")
        {
            var mCond = exactOnly ? "mb.name = $mq" : "mb.name LIKE $mlike ESCAPE '!'";
            var oCond = ownerName == null ? "" : HasWildcard(ownerName) ? "AND o.name LIKE $olike ESCAPE '!'" : "AND o.name = $oq";
            var rows = s.Query($"""
                SELECT o.type, o.name, mb.kind, mb.name, mb.info, md.package, md.name, md.tier
                FROM members mb JOIN objects o ON o.id = mb.object_id JOIN files f ON f.id = mb.file_id JOIN models md ON md.id = f.model_id
                WHERE {mCond} {oCond} AND mb.kind IN ('field','enumvalue','datasource','index','relation','fieldgroup','control')
                {(tp != null ? "AND o.type LIKE $tp" : "")} {modelFilter}
                ORDER BY CASE WHEN mb.name = $mq THEN 0 ELSE 1 END, md.tier DESC, {TypePriority}, o.name
                LIMIT $limit
                """, ("$mq", methodName), ("$mlike", Like(methodName)), ("$olike", PatOrNull(ownerName)),
                ("$oq", ownerName), ("$tp", tp), ("$model", modelLike), ("$limit", limit))
                .Select(r => $"  {r.GetString(0)} {r.GetString(1)}.{r.GetString(3)} ({r.GetString(2)}) {Tag(r.GetString(5), r.GetString(6), r.GetInt32(7))}  {Trunc(N(r, 4), 120)}")
                .ToList();
            if (rows.Count > 0 || (kind != "any" && !(dotted && !kindGiven)))
            {
                sb.AppendLine($"fields/members ({rows.Count}{(rows.Count == limit ? "+" : "")}):");
                foreach (var l in rows) sb.AppendLine(l);
            }
        }

        if (sb.Length == 0) sb.Append("nothing found");
        return Finish(sb);
    });

    // ---------------------------------------------------------------- object

    static readonly (string Kind, string Title)[] MemberSections =
    [
        ("field", "fields"), ("index", "indexes"), ("relation", "relations"), ("fieldgroup", "field groups"),
        ("enumvalue", "values"), ("datasource", "data sources"), ("entrypoint", "entry points"), ("menuitem", "menu items"),
        ("reference", "references"), ("control", "controls"),
    ];

    /// <summary>Compiled-only objects have no XML: rebuild the skeleton from what the index holds.</summary>
    static ParsedObject LoadCompiled(Store s, ObjRow o)
    {
        var po = new ParsedObject { Type = o.Type, Name = o.Name, Target = o.Target, Extends = o.Extends };
        foreach (var r in s.Query("SELECT kind, owner, name, info FROM members WHERE object_id = $id ORDER BY kind, name", ("$id", o.Id)))
            po.Members.Add(new ParsedMember { Kind = r.GetString(0), Owner = N(r, 1) ?? "", Name = r.GetString(2), Info = N(r, 3) ?? "" });
        foreach (var r in s.Query("SELECT owner, name, sig FROM methods WHERE object_id = $id ORDER BY owner, name", ("$id", o.Id)))
            po.Methods.Add(new ParsedMethod { Owner = N(r, 0) ?? "", Name = r.GetString(1), Signature = N(r, 2) ?? r.GetString(1) });
        return po;
    }

    static readonly HashSet<string> TreeKinds = ["control", "menuitem"];

    static Regex Wildcard(string pattern) =>
        new("^" + Regex.Escape(pattern.Contains('*') || pattern.Contains('?') ? pattern : $"*{pattern}*")
            .Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Object(string name, string? type, string? sections, string? parent = null, int depth = 0, string? filter = null) =>
        Batch(SplitList(name).Select(n => (n, (Func<string>)(() => ObjectOne(n, type, sections, parent, depth, filter)))).ToList() is { Count: > 0 } items
            ? items
            : [(name, () => "No object named '' (empty name).")]);

    string ObjectOne(string name, string? type, string? sections, string? parent, int depth, string? filter) => svc.Read(s =>
    {
        var rows = ObjectRows(s, name, type);
        if (rows.Count == 0) return NotFound(s, name, type);
        var o = rows[0];
        if (!o.Compiled && svc.EnsureFileFresh(s, o.Path))
        {
            rows = ObjectRows(s, name, type);
            if (rows.Count == 0) return $"'{name}' was deleted from disk.";
            o = rows[0];
        }
        if (!o.Compiled && !File.Exists(o.Path)) return $"File missing: {o.Path}";

        parent = string.IsNullOrWhiteSpace(parent) ? null : parent.Trim();
        filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        if (string.IsNullOrWhiteSpace(sections) && (parent != null || filter != null)) sections = "controls,menuitems";
        var want = string.IsNullOrWhiteSpace(sections)
            ? null
            : new HashSet<string>(sections.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        bool Show(string sec) => want == null || want.Contains(sec) || want.Contains(sec.TrimEnd('s')) || want.Contains(sec.Replace(" ", ""));

        var po = o.Compiled ? LoadCompiled(s, o) : XmlObjectParser.Parse(o.Path, ParseMode.Render);
        var cache = new Dictionary<string, string?>();
        var sb = new StringBuilder();
        sb.AppendLine($"{o.Type} {o.Name} {o.Tag}");
        if (o.Compiled)
        {
            sb.AppendLine($"source: none — compiled package; skeleton rebuilt from {o.Path[..o.Path.IndexOf(BinaryPackage.Marker, StringComparison.Ordinal)]}");
            if (po.Extends != null) sb.AppendLine($"extends: {po.Extends}");
            if (po.Target != null) sb.AppendLine($"extends object: {po.Target}");
        }
        else sb.AppendLine($"file: {o.Path}");
        if (rows.Count > 1)
            sb.AppendLine("same name: " + string.Join(", ", rows.Skip(1).Select(r => $"{r.Type} {r.Tag}")));

        if (Show("props") && po.RootProps.Count > 0)
        {
            var props = string.Join("; ", po.RootProps.Where(kv => kv.Value.Length is > 0 and < 200).Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendLine("props: " + WithLabels(s, Trunc(props, 900), cache));
        }

        var decl = po.Methods.FirstOrDefault(m => m.IsDeclaration);
        if (decl?.Source != null && Show("declaration"))
        {
            var lines = decl.Source.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count <= 40 || want != null)
            {
                sb.AppendLine($"declaration (L{decl.StartLine}-{decl.EndLine}):");
                foreach (var l in lines) sb.AppendLine("  " + l);
            }
            else
            {
                sb.AppendLine($"declaration L{decl.StartLine}-{decl.EndLine} ({lines.Count} lines, use xpp_method {o.Name} classDeclaration): {Trunc(po.Header, 200)}");
            }
        }

        foreach (var (kind, title) in MemberSections)
        {
            var list = po.Members.Where(m => m.Kind == kind).ToList();
            if (list.Count == 0) continue;
            if (!Show(title) && !Show(kind)) continue;
            if (TreeKinds.Contains(kind) && list.Any(m => m.Path.Length > 0))
            {
                AppendTree(s, sb, list, title, parent, depth, filter, autoCollapse: true, cache);
                continue;
            }
            sb.AppendLine($"{title} ({list.Count}):");
            foreach (var m in list)
            {
                var owner = m.Owner.Length > 0 ? $" (under {m.Owner})" : "";
                sb.AppendLine($"  {m.Name}{owner}{(m.Info.Length > 0 ? ": " + WithLabels(s, m.Info, cache) : "")}");
            }
        }

        var methods = po.Methods.Where(m => !m.IsDeclaration).ToList();
        if (methods.Count > 0 && Show("methods"))
        {
            sb.AppendLine($"methods ({methods.Count}):");
            foreach (var g in methods.GroupBy(m => m.Owner))
            {
                var ind = "  ";
                if (g.Key.Length > 0)
                {
                    sb.AppendLine($"  [{g.Key}]");
                    ind = "    ";
                }
                foreach (var m in g)
                    sb.AppendLine(m.StartLine > 0 ? $"{ind}L{m.StartLine}-{m.EndLine} {Trunc(m.Signature, 220)}" : $"{ind}{Trunc(m.Signature, 220)}");
            }
        }

        if (Show("extensions") && !o.Type.EndsWith("Extension", StringComparison.Ordinal))
            AppendExtensionSummary(s, sb, o.Name);

        return Finish(sb);
    });

    /// <summary>
    /// Control / menu trees. filter = wildcard on name or path (prints full paths); parent = subtree of one element;
    /// depth = levels to show. Without either, large trees are collapsed to two levels with "[+N]" child counts.
    /// </summary>
    void AppendTree(Store s, StringBuilder sb, List<ParsedMember> list, string title, string? parent, int depth, string? filter,
        bool autoCollapse, Dictionary<string, string?> cache)
    {
        const int maxLines = 400;
        list = list.OrderBy(m => m.Order).ToList();
        string Line(ParsedMember m, string label) =>
            $"{label}{(m.Owner.Length > 0 ? $" (under {m.Owner})" : "")}{(m.Info.Length > 0 ? ": " + WithLabels(s, m.Info, cache) : "")}";
        int Below(ParsedMember m) => list.Count(x => x.Path.StartsWith(m.Path + "/", StringComparison.OrdinalIgnoreCase));

        if (filter != null)
        {
            var rx = Wildcard(filter);
            var hits = list.Where(m => rx.IsMatch(m.Name) || rx.IsMatch(m.Path)).ToList();
            sb.AppendLine($"{title} matching '{filter}': {hits.Count} of {list.Count}");
            foreach (var m in hits.Take(maxLines)) sb.AppendLine("  " + Line(m, m.Path));
            if (hits.Count > maxLines) sb.AppendLine($"  … {hits.Count - maxLines} more");
            return;
        }

        IEnumerable<ParsedMember> sel = list;
        int baseDepth = 0;
        var header = $"{title} ({list.Count})";
        if (parent != null)
        {
            var roots = list.Where(m => m.Name.Equals(parent, StringComparison.OrdinalIgnoreCase)
                                        || m.Path.Equals(parent, StringComparison.OrdinalIgnoreCase)
                                        || m.Path.EndsWith("/" + parent, StringComparison.OrdinalIgnoreCase)).ToList();
            if (roots.Count == 0)
            {
                var rx = Wildcard(parent);
                var similar = list.Where(m => rx.IsMatch(m.Name)).Take(15).Select(m => m.Path).ToList();
                sb.AppendLine($"{header}: no element named '{parent}'.{(similar.Count > 0 ? " Similar: " + string.Join(", ", similar) : "")}");
                return;
            }
            var root = roots[0];
            sel = list.Where(m => m == root || m.Path.StartsWith(root.Path + "/", StringComparison.OrdinalIgnoreCase));
            baseDepth = root.Depth - 1;
            header += $", subtree of {root.Path}";
        }

        int maxDepth = depth > 0 ? depth : autoCollapse && parent == null && list.Count > 60 ? 2 : int.MaxValue;
        var shown = sel.Where(m => m.Depth - baseDepth <= maxDepth).ToList();
        if (shown.Count < sel.Count())
            header += $" — showing {shown.Count} up to depth {maxDepth}; drill down with parent=<name>, depth=<n> or filter=<*text*>";
        sb.AppendLine(header + ":");
        foreach (var m in shown.Take(maxLines))
        {
            int rel = Math.Max(1, m.Depth - baseDepth);
            var more = rel == maxDepth && Below(m) is > 0 and var n ? $" [+{n}]" : "";
            sb.AppendLine(new string(' ', 2 * rel) + Line(m, m.Name + more));
        }
        if (shown.Count > maxLines) sb.AppendLine($"  … {shown.Count - maxLines} more (narrow with parent/depth/filter)");
    }

    void AppendExtensionSummary(Store s, StringBuilder sb, string name)
    {
        var ext = s.Query($"{ObjSelect} WHERE o.target = $n ORDER BY md.tier DESC, o.type, o.name LIMIT 40", ("$n", name))
            .Select(r => ReadObj(r)).ToList();
        var handlers = Convert.ToInt64(s.Scalar("SELECT COUNT(*) FROM refs WHERE target = $n AND kind = 'handler'", ("$n", name)));
        var derived = Convert.ToInt64(s.Scalar("SELECT COUNT(*) FROM objects WHERE extends = $n", ("$n", name)));
        if (ext.Count == 0 && handlers == 0 && derived == 0) return;
        sb.AppendLine($"extensions ({ext.Count}{(ext.Count == 40 ? "+" : "")}), event handlers: {handlers}, derived types: {derived}:");
        foreach (var e in ext) sb.AppendLine($"  {e.Type} {e.Name} {e.Tag}{ExtensionDetail(s, e)}");
    }

    string ExtensionDetail(Store s, ObjRow e)
    {
        if (e.Type == "AxClass")
        {
            var wraps = s.Query("SELECT DISTINCT member FROM refs WHERE object_id = $id AND kind = 'coc' AND via = 'next' AND member IS NOT NULL ORDER BY member",
                ("$id", e.Id)).Select(r => r.GetString(0)).ToList();
            var of = s.Query("SELECT via, member FROM refs WHERE object_id = $id AND kind = 'coc' AND method_id = (SELECT MIN(id) FROM methods WHERE object_id = $id AND name = 'classDeclaration') LIMIT 1",
                ("$id", e.Id)).Select(r => $"{r.GetString(0)}{(r.IsDBNull(1) ? "" : ":" + r.GetString(1))}").FirstOrDefault();
            var parts = new List<string>();
            if (of != null && !of.StartsWith("classStr", StringComparison.OrdinalIgnoreCase) && !of.StartsWith("tableStr", StringComparison.OrdinalIgnoreCase)) parts.Add(of);
            if (wraps.Count > 0) parts.Add("wraps " + string.Join(", ", wraps));
            return parts.Count > 0 ? " — " + string.Join("; ", parts) : "";
        }
        var counts = s.Query("SELECT kind, COUNT(*), GROUP_CONCAT(name, ', ') FROM members WHERE object_id = $id GROUP BY kind", ("$id", e.Id))
            .Select(r => r.GetString(0) == "field" ? $"+fields: {Trunc(r.GetString(2), 200)}" : $"+{r.GetInt64(1)} {r.GetString(0)}")
            .ToList();
        return counts.Count > 0 ? " — " + string.Join("; ", counts) : "";
    }

    // ---------------------------------------------------------------- method

    /// <summary>
    /// One or more methods. method may list several names ("a;b") of the same object; without method,
    /// objectName may list "Object.method" / "Object::method" items. match/lines return a numbered fragment.
    /// </summary>
    public string Method(string objectName, string? method, string? type, string? match = null, string? lines = null, int context = 3)
    {
        var frag = FragmentSpec.Create(match, lines, context);
        var pairs = new List<(string Obj, string Method)>();
        var methods = SplitList(method);
        if (methods.Count > 0)
        {
            foreach (var o in SplitList(objectName))
                foreach (var m in methods) pairs.Add((o, m));
        }
        else
        {
            foreach (var item in SplitList(objectName))
            {
                int sep = item.IndexOf("::", StringComparison.Ordinal);
                int len = 2;
                if (sep < 0) { sep = item.LastIndexOf('.'); len = 1; }
                if (sep <= 0 || sep + len >= item.Length)
                    return $"'{item}': pass the method name (method=...), or write it as Object.method.";
                pairs.Add((item[..sep], item[(sep + len)..]));
            }
        }
        if (pairs.Count == 0) return "pass objectName and method.";
        return Batch(pairs.Select(p => ($"{p.Obj}.{p.Method}", (Func<string>)(() => MethodOne(p.Obj, p.Method, type, frag)))).ToList());
    }

    static string Body(List<(int No, string Text)> lines, FragmentSpec? frag)
    {
        if (frag != null) return CodeFragment.Render(lines, frag);
        if (lines.Count == 0) return "(file changed; range not available)";
        var code = CodeFragment.Plain(lines);
        return lines.Count > CodeFragment.LongMethodHint
            ? $"{code}\n({lines.Count} lines — next time pass match=<regex> or lines=<from-to> to get only the part you need)"
            : code;
    }

    string MethodOne(string objectName, string method, string? type, FragmentSpec? frag) => svc.Read(s =>
    {
        var tp = TypePattern(type);
        List<(long Id, string? Owner, string Name, int Start, int End, string Type, string Obj, string Path, string Tag)> Load() =>
            s.Query($"""
                SELECT m.id, m.owner, m.name, m.start_line, m.end_line, o.type, o.name, f.path, md.package, md.name, md.tier
                FROM methods m JOIN objects o ON o.id = m.object_id JOIN files f ON f.id = m.file_id JOIN models md ON md.id = f.model_id
                WHERE o.name = $o AND m.name = $m {(tp != null ? "AND o.type LIKE $tp" : "")}
                ORDER BY md.tier DESC, {TypePriority}, m.owner LIMIT 6
                """, ("$o", objectName), ("$m", method), ("$tp", tp))
                .Select(r => (r.GetInt64(0), N(r, 1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetString(6),
                    r.GetString(7), Tag(r.GetString(8), r.GetString(9), r.GetInt32(10))))
                .ToList();

        var rows = Load();
        if (rows.Count > 0 && rows.Select(r => r.Path).Distinct().Aggregate(false, (acc, p) => svc.EnsureFileFresh(s, p) | acc))
            rows = Load();

        // Files that still differ from the index (typically a read-only index) are parsed live,
        // so the returned code is always what is on disk.
        var sb = new StringBuilder();
        var live = new List<string>();
        foreach (var o in ObjectRows(s, objectName, type))
        {
            if (!IndexService.IsFileStale(s, o.Path)) continue;
            rows = rows.Where(r => !r.Path.Equals(o.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            try
            {
                var po = XmlObjectParser.Parse(o.Path, ParseMode.Render);
                foreach (var m in po.Methods.Where(m => m.Name.Equals(method, StringComparison.OrdinalIgnoreCase)).Take(6))
                {
                    var owner = m.Owner.Length > 0 ? m.Owner + "/" : "";
                    live.Add($"{po.Type} {po.Name}.{owner}{m.Name} {o.Tag}  [file changed since indexing — parsed live]\n" +
                             $"{o.Path}:{m.StartLine}-{m.EndLine}\n{Body(CodeFragment.FromSource(m.Source ?? "", m.StartLine), frag)}\n");
                }
            }
            catch (Exception ex)
            {
                live.Add($"{o.Path}: cannot parse ({ex.Message})");
            }
        }

        if (rows.Count == 0 && live.Count == 0)
        {
            if (ObjectRows(s, objectName, type).Count == 0) return NotFound(s, objectName, type);

            List<string> Names(string like, int take) => s.Query($"""
                SELECT DISTINCT m.name FROM methods m JOIN objects o ON o.id = m.object_id
                WHERE o.name = $o AND m.name <> 'classDeclaration' {(like.Length > 0 ? "AND m.name LIKE $l ESCAPE '!'" : "")}
                ORDER BY m.name LIMIT {take}
                """, ("$o", objectName), ("$l", like)).Select(r => r.GetString(0)).ToList();

            // "validateStatus" should still point at "validateWMSStatus": try contains, then the longest
            // prefix/suffix fragments of the name, then simply list what the object has.
            var similar = Names($"%{Esc(method)}%", 20);
            for (int cut = method.Length - 1; similar.Count == 0 && cut >= 4; cut--)
            {
                similar = Names($"%{Esc(method[..cut])}%", 10);
                if (similar.Count == 0) similar = Names($"%{Esc(method[^cut..])}%", 10);
            }
            sb.Append($"'{objectName}' has no method '{method}'.");
            if (similar.Count > 0) sb.Append(" Similar: " + string.Join(", ", similar));
            else
            {
                var all = Names("", 40);
                if (all.Count > 0) sb.Append($" Methods of {objectName}: " + string.Join(", ", all));
            }
            var wrappers = CocWrappers(s, objectName, method);
            if (wrappers.Length > 0) sb.Append("\n" + wrappers);
            return Finish(sb);
        }

        foreach (var block in live) sb.AppendLine(block);
        foreach (var r in rows)
        {
            var owner = string.IsNullOrEmpty(r.Owner) ? "" : r.Owner + "/";
            sb.AppendLine($"{r.Type} {r.Obj}.{owner}{r.Name} {r.Tag}");
            if (BinaryPackage.IsPseudoPath(r.Path))
            {
                sb.AppendLine("no source: compiled package. What the compiler recorded for this method:");
                sb.AppendLine(CalleesCore(s, r.Obj, r.Name, type).TrimEnd());
                sb.AppendLine();
                continue;
            }
            sb.AppendLine($"{r.Path}:{r.Start}-{r.End}");
            sb.AppendLine(Body(CodeFragment.FromFile(r.Path, r.Start, r.End), frag));
            sb.AppendLine();
        }
        var w = CocWrappers(s, objectName, method);
        if (w.Length > 0) sb.AppendLine(w);
        return Finish(sb);
    });

    static string CocWrappers(Store s, string objectName, string method)
    {
        var rows = s.Query("""
            SELECT DISTINCT r.kind, o.type, o.name, m.name, md.package, md.name, md.tier, r.via
            FROM refs r JOIN objects o ON o.id = r.object_id JOIN files f ON f.id = r.file_id JOIN models md ON md.id = f.model_id
            LEFT JOIN methods m ON m.id = r.method_id
            WHERE r.target = $o AND r.member = $m AND ((r.kind = 'coc' AND r.via = 'next') OR r.kind = 'handler')
            ORDER BY r.kind, md.tier DESC, o.name LIMIT 40
            """, ("$o", objectName), ("$m", method))
            .Select(r => $"  {(r.GetString(0) == "coc" ? "CoC" : "handler " + N(r, 7))}: {r.GetString(2)}.{N(r, 3)} {Tag(r.GetString(4), r.GetString(5), r.GetInt32(6))}")
            .ToList();
        return rows.Count == 0 ? "" : "wrappers/handlers:\n" + string.Join("\n", rows);
    }

    // ---------------------------------------------------------------- callers / callees

    /// <summary>
    /// Callers from the index: custom models first, then compiled packages and standard (Microsoft) code
    /// (calls only — kept when standardCodeRefs is on). standard=false leaves Microsoft callers out.
    /// </summary>
    public string Callers(string objectName, string method, int depth, int limit, bool standard = true) => svc.Read(s =>
    {
        depth = Math.Clamp(depth, 1, 4);
        limit = Math.Clamp(limit, 1, 500);
        var sb = new StringBuilder();
        var shownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int printed = 0, unresolvedChains = 0;

        void Level(string obj, string m, int d)
        {
            if (d > depth || printed >= limit || !visited.Add($"{obj}.{m}")) return;
            var targets = SelfAndDescendants(s, obj);
            var args = new List<(string, object?)> { ("$m", m) };
            var inList = InList("$t", targets.Cast<object?>().ToList(), args);
            var rows = s.Query($"""
                SELECT r.kind, r.line, r.via, so.type, so.name, sm.name, sm.owner, f.path, md.package, md.name, md.tier, r.target
                FROM refs r JOIN objects so ON so.id = r.object_id JOIN files f ON f.id = r.file_id JOIN models md ON md.id = f.model_id
                LEFT JOIN methods sm ON sm.id = r.method_id
                WHERE r.member = $m AND r.target IN ({inList}) AND r.kind IN ('call','coc','handler','intrinsic') {(standard ? "" : "AND md.tier <> 0")}
                ORDER BY md.tier DESC, so.name, sm.name, r.line
                """, args.ToArray())
                .Select(r => (Kind: r.GetString(0), Line: r.GetInt32(1), Via: N(r, 2), Type: r.GetString(3), Obj: r.GetString(4),
                    Method: N(r, 5), Owner: N(r, 6), Path: r.GetString(7), Tag: Tag(r.GetString(8), r.GetString(9), r.GetInt32(10)),
                    Target: r.GetString(11)))
                .ToList();

            // Calls on a chained receiver ("CustTable::find(x).m()") whose return type resolves to obj or a derived type.
            var targetSet = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            foreach (var c in ChainedRefs(s, m, "'call'"))
            {
                if (!standard && c.Tag.EndsWith(", std]", StringComparison.Ordinal)) continue;
                if (c.Resolved != null && targetSet.Contains(c.Resolved))
                    rows.Add((c.Kind, c.Line, "chain", c.Type, c.Obj, c.Method, c.Owner, c.Path, c.Tag, c.Resolved));
                else if (c.Resolved == null && d == 1) unresolvedChains++;
            }

            if (d == 1)
            {
                var std = rows.Count(r => r.Tag.EndsWith(", std]", StringComparison.Ordinal));
                sb.AppendLine($"callers of {obj}.{m}{(targets.Count > 1 ? $" (incl. {targets.Count - 1} derived types)" : "")}: {rows.Count} reference(s)" +
                              (std > 0 ? $" — {rows.Count - std} in custom/compiled code, {std} in standard code" : ""));
                rows = rows.OrderByDescending(r => !r.Tag.EndsWith(", std]", StringComparison.Ordinal)).ToList();
            }

            foreach (var g in rows.GroupBy(r => (r.Type, r.Obj, r.Owner, r.Method)))
            {
                if (printed++ >= limit) { sb.AppendLine($"{new string(' ', 2 * d)}… limit reached"); return; }
                var first = g.First();
                var kinds = g.Select(x => x.Kind == "call" ? (x.Via == "super" ? "super" : x.Via == "chain" ? "chained" : null) : x.Kind + (x.Via != null ? ":" + x.Via : ""))
                    .Where(x => x != null).Distinct().ToList();
                var viaTarget = g.Select(x => x.Target).Distinct(StringComparer.OrdinalIgnoreCase).Where(t => !t.Equals(obj, StringComparison.OrdinalIgnoreCase)).ToList();
                var path = shownPaths.Add(first.Path) ? "  " + first.Path : "";
                var owner = string.IsNullOrEmpty(first.Owner) ? "" : first.Owner + "/";
                sb.AppendLine($"{new string(' ', 2 * d)}{(d > 1 ? "← " : "")}{first.Type} {first.Obj}.{owner}{first.Method ?? "(metadata)"} {first.Tag} " +
                              $"L{string.Join(",", g.Select(x => x.Line).Distinct())}" +
                              $"{(kinds.Count > 0 ? " (" + string.Join(", ", kinds) + ")" : "")}" +
                              $"{(viaTarget.Count > 0 ? " on " + string.Join(",", viaTarget) : "")}{path}");
                if (first.Method != null && g.Any(x => x.Kind == "call"))
                    Level(first.Obj, first.Method, d + 1);
            }
        }

        Level(objectName, method, 1);

        var anc = Ancestors(s, objectName);
        if (anc.Count > 0)
        {
            var args = new List<(string, object?)> { ("$m", method) };
            var inList = InList("$a", anc.Cast<object?>().ToList(), args);
            var baseRows = s.Query($"""
                SELECT r.target, so.name, sm.name, r.line, md.package, md.name, md.tier
                FROM refs r JOIN objects so ON so.id = r.object_id JOIN files f ON f.id = r.file_id JOIN models md ON md.id = f.model_id
                LEFT JOIN methods sm ON sm.id = r.method_id
                WHERE r.member = $m AND r.target IN ({inList}) AND r.kind = 'call'
                ORDER BY md.tier DESC, so.name LIMIT 16
                """, args.ToArray()).Select(r => $"  {r.GetString(1)}.{N(r, 2)} {Tag(r.GetString(4), r.GetString(5), r.GetInt32(6))} L{r.GetInt32(3)} on {r.GetString(0)}").ToList();
            if (baseRows.Count > 0)
            {
                sb.AppendLine($"calls through base types ({string.Join(", ", anc)}) — may reach other overrides{(baseRows.Count == 16 ? ", first 15" : "")}:");
                foreach (var l in baseRows.Take(15)) sb.AppendLine(l);
            }
        }

        // Receivers we could not type: plain unknown variables, plus chains whose return type is not in the index.
        var unresolved = Convert.ToInt64(s.Scalar("SELECT COUNT(*) FROM refs WHERE target IS NULL AND member = $m AND kind = 'call' AND via IS NULL", ("$m", method)))
                         + unresolvedChains;
        if (unresolved > 0)
        {
            var sample = s.Query("""
                SELECT so.name, sm.name, r.line, md.package, md.name, md.tier
                FROM refs r JOIN objects so ON so.id = r.object_id JOIN files f ON f.id = r.file_id JOIN models md ON md.id = f.model_id
                LEFT JOIN methods sm ON sm.id = r.method_id
                WHERE r.target IS NULL AND r.member = $m AND r.kind = 'call' AND r.via IS NULL ORDER BY so.name LIMIT 10
                """, ("$m", method)).Select(r => $"  {r.GetString(0)}.{N(r, 1)} {Tag(r.GetString(3), r.GetString(4), r.GetInt32(5))} L{r.GetInt32(2)}").ToList();
            sb.AppendLine($"possible callers with unknown receiver type (any '.{method}(' call in custom code): {unresolved}{(sample.Count == 10 ? ", first 10" : "")}:");
            foreach (var l in sample) sb.AppendLine(l);
        }
        if (standard && !svc.Cfg.StandardCodeRefs)
            sb.AppendLine("(standard code calls are not indexed: xppgraft config --standard-code true, then xppgraft build --std-only)");
        return Finish(sb);
    });

    public string Callees(string objectName, string method, string? type) =>
        svc.Read(s => Finish(new StringBuilder(CalleesCore(s, objectName, method, type))));

    string CalleesCore(Store s, string objectName, string method, string? type)
    {
        var tp = TypePattern(type);
        var rows = s.Query($"""
            SELECT r.kind, r.target, r.member, r.line, r.via, m.owner
            FROM refs r JOIN methods m ON m.id = r.method_id JOIN objects o ON o.id = m.object_id
            WHERE o.name = $o AND m.name = $m {(tp != null ? "AND o.type LIKE $tp" : "")}
            ORDER BY m.owner, r.line
            """, ("$o", objectName), ("$m", method), ("$tp", tp))
            .Select(r => (Kind: r.GetString(0), Target: N(r, 1), Member: N(r, 2), Line: r.GetInt32(3), Via: N(r, 4), Owner: N(r, 5)))
            .ToList();
        if (rows.Count == 0)
            return $"No references recorded for {objectName}.{method} (method missing or empty; Microsoft code keeps calls only).";

        var cache = new Dictionary<string, string?>();
        var types = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string Chain(string via, string? member)
        {
            var t = ResolveType(s, via[CodeAnalyzer.ChainPrefix.Length..], types);
            return $"{ChainText(via)}.{member}{(t != null ? $" (= {t}.{member})" : "")}";
        }
        var sb = new StringBuilder($"references from {objectName}.{method}:\n");
        string Fmt((string Kind, string? Target, string? Member, int Line, string? Via, string? Owner) r) => r.Kind switch
        {
            "call" or "member" when r.Target == null && r.Via?.StartsWith(CodeAnalyzer.ChainPrefix, StringComparison.Ordinal) == true => Chain(r.Via, r.Member),
            "call" when r.Via == "super" => $"super {r.Target}.{r.Member}",
            "call" when r.Via == "::" => $"{r.Target}::{r.Member}",
            "call" => $"{r.Target ?? "?"}.{r.Member}",
            "static" => $"{r.Target}::{r.Member}",
            "member" => $"{r.Target}.{r.Member}",
            "intrinsic" => $"{r.Via}({r.Target}{(r.Member != null ? ", " + r.Member : "")})",
            "label" => WithLabels(s, r.Target ?? "", cache),
            _ => $"{r.Target}{(r.Member != null ? "." + r.Member : "")}{(r.Via != null ? " (" + r.Via + ")" : "")}",
        };
        var titles = new[] { ("call", "calls"), ("new", "new"), ("static", "enum values / constants"), ("member", "fields / members"),
            ("intrinsic", "intrinsics"), ("type", "types used"), ("coc", "next / extension"), ("handler", "handles"), ("label", "labels"),
            ("extends", "extends"), ("implements", "implements") };
        foreach (var (kind, title) in titles)
        {
            var items = rows.Where(r => r.Kind == kind)
                .GroupBy(r => Fmt(r), StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key} L{string.Join(",", g.Select(x => x.Line).Distinct().Take(5))}")
                .ToList();
            if (items.Count == 0) continue;
            sb.AppendLine($"{title} ({items.Count}): {string.Join("; ", items)}");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- refs / extensions

    public string Refs(string name, string? member, string? kind, string? model, int limit) => svc.Read(s =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        var modelLike = PatOrNull(model);
        var where = """
            r.target = $n AND ($mem IS NULL OR r.member = $mem) AND ($kind IS NULL OR r.kind = $kind)
            AND ($model IS NULL OR md.name LIKE $model ESCAPE '!' OR md.package LIKE $model ESCAPE '!')
            """;
        var args = new (string, object?)[] { ("$n", name), ("$mem", string.IsNullOrWhiteSpace(member) ? null : member),
            ("$kind", string.IsNullOrWhiteSpace(kind) ? null : kind), ("$model", modelLike), ("$limit", limit) };
        const string from = "FROM refs r JOIN objects so ON so.id = r.object_id JOIN files f ON f.id = r.file_id JOIN models md ON md.id = f.model_id";

        var sb = new StringBuilder();
        if (name.StartsWith('@'))
        {
            var text = LabelText(s, name);
            sb.AppendLine($"{name} = {(text == null ? "(label not found)" : $"\"{text}\"")}");
        }

        var summary = s.Query($"SELECT r.kind, COUNT(*) {from} WHERE {where} GROUP BY r.kind ORDER BY COUNT(*) DESC", args)
            .Select(r => $"{r.GetString(0)}={r.GetInt64(1)}").ToList();

        // Member used on a chained receiver ("CustTable::find(x).AccountNum") — typed from method return types.
        var chained = new List<ChainRow>();
        if (!string.IsNullOrWhiteSpace(member) && (string.IsNullOrWhiteSpace(kind) || kind is "call" or "member"))
        {
            var types = new HashSet<string>(SelfAndDescendants(s, name), StringComparer.OrdinalIgnoreCase);
            chained = ChainedRefs(s, member, string.IsNullOrWhiteSpace(kind) ? "'call','member'" : $"'{kind}'", modelLike)
                .Where(c => c.Resolved != null && types.Contains(c.Resolved)).ToList();
            if (chained.Count > 0) summary.Add($"chained={chained.Count}");
        }
        if (summary.Count == 0)
        {
            sb.AppendLine($"no references to {name}{(member != null ? "." + member : "")} in the index.");
            sb.AppendLine("(Microsoft code keeps calls, new, intrinsics, extensions and handlers only — no field reads, types or labels)");
            return Finish(sb);
        }
        sb.AppendLine($"references to {name}{(string.IsNullOrWhiteSpace(member) ? "" : "." + member)}: {string.Join(", ", summary)}");

        var rows = s.Query($"""
            SELECT r.kind, r.member, r.line, r.via, so.type, so.name, sm.name, sm.owner, f.path, md.package, md.name, md.tier
            {from} LEFT JOIN methods sm ON sm.id = r.method_id
            WHERE {where}
            ORDER BY md.tier DESC, so.name, r.line LIMIT $limit
            """, args)
            .Select(r => (Kind: r.GetString(0), Member: N(r, 1), Line: r.GetInt32(2), Via: N(r, 3), Type: r.GetString(4), Obj: r.GetString(5),
                Method: N(r, 6), Owner: N(r, 7), Path: r.GetString(8), Tag: Tag(r.GetString(9), r.GetString(10), r.GetInt32(11))))
            .ToList();

        foreach (var og in rows.GroupBy(r => (r.Type, r.Obj, r.Path)))
        {
            var first = og.First();
            sb.AppendLine($"{og.Key.Type} {og.Key.Obj} {first.Tag}  {og.Key.Path}");
            foreach (var mg in og.GroupBy(r => (r.Owner, r.Method)))
            {
                var label = mg.Key.Method == null ? "(metadata)" : (string.IsNullOrEmpty(mg.Key.Owner) ? "" : mg.Key.Owner + "/") + mg.Key.Method;
                var items = mg.Select(r => $"L{r.Line} {r.Kind}{(r.Member != null ? " ." + r.Member : "")}{(r.Via != null ? " " + r.Via : "")}").Distinct();
                sb.AppendLine($"  {label}: {string.Join("; ", items)}");
            }
        }
        if (rows.Count == limit) sb.AppendLine($"… limit {limit} reached (narrow with member/kind/model)");
        if (chained.Count > 0)
        {
            sb.AppendLine($"on chained receivers ({chained.Count}):");
            foreach (var og in chained.Take(limit).GroupBy(c => (c.Type, c.Obj, c.Path)))
            {
                sb.AppendLine($"{og.Key.Type} {og.Key.Obj} {og.First().Tag}  {og.Key.Path}");
                foreach (var mg in og.GroupBy(c => (c.Owner, c.Method)))
                {
                    var label = (string.IsNullOrEmpty(mg.Key.Owner) ? "" : mg.Key.Owner + "/") + mg.Key.Method;
                    sb.AppendLine($"  {label}: {string.Join("; ", mg.Select(c => $"L{c.Line} {c.Kind} {ChainText(c.Via)}.{member}").Distinct())}");
                }
            }
        }
        return Finish(sb);
    });

    public string Extensions(string name) => svc.Read(s =>
    {
        var sb = new StringBuilder();
        var ext = s.Query($"{ObjSelect} WHERE o.target = $n ORDER BY md.tier DESC, o.type, o.name LIMIT 200", ("$n", name))
            .Select(r => ReadObj(r)).ToList();
        sb.AppendLine($"extensions of {name} ({ext.Count}{(ext.Count == 200 ? "+" : "")}):");
        foreach (var e in ext) sb.AppendLine($"  {e.Type} {e.Name} {e.Tag}{ExtensionDetail(s, e)}  {e.Path}");

        var handlers = s.Query("""
            SELECT r.member, r.via, so.name, sm.name, md.package, md.name, md.tier, r.line
            FROM refs r JOIN objects so ON so.id = r.object_id JOIN files f ON f.id = r.file_id JOIN models md ON md.id = f.model_id
            LEFT JOIN methods sm ON sm.id = r.method_id
            WHERE r.target = $n AND r.kind = 'handler' ORDER BY md.tier DESC, r.member, so.name LIMIT 200
            """, ("$n", name))
            .Select(r => $"  {N(r, 0) ?? "*"} ← {r.GetString(2)}.{N(r, 3)} {Tag(r.GetString(4), r.GetString(5), r.GetInt32(6))} ({N(r, 1)}) L{r.GetInt32(7)}")
            .ToList();
        if (handlers.Count > 0)
        {
            sb.AppendLine($"event handlers ({handlers.Count}):");
            foreach (var h in handlers) sb.AppendLine(h);
        }

        var derivedCount = Convert.ToInt64(s.Scalar("SELECT COUNT(*) FROM objects WHERE extends = $n", ("$n", name)));
        var derived = s.Query($"{ObjSelect} WHERE o.extends = $n ORDER BY md.tier DESC, o.name LIMIT 40", ("$n", name))
            .Select(r => ReadObj(r)).ToList();
        if (derived.Count > 0)
        {
            var byModel = derived.GroupBy(d => d.Tag).Select(g => $"{string.Join(", ", g.Select(d => d.Name))} {g.Key}");
            sb.AppendLine($"derived ({derivedCount}{(derivedCount > derived.Count ? $", first {derived.Count}; use xpp_find with type/model to list more" : "")}): " +
                          string.Join("; ", byModel));
        }

        var impl = s.Query("""
            SELECT DISTINCT so.name, md.package, md.name, md.tier FROM refs r JOIN objects so ON so.id = r.object_id
            JOIN files f ON f.id = r.file_id JOIN models md ON md.id = f.model_id
            WHERE r.target = $n AND r.kind = 'implements' ORDER BY md.tier DESC, so.name LIMIT 100
            """, ("$n", name)).Select(r => $"{r.GetString(0)} {Tag(r.GetString(1), r.GetString(2), r.GetInt32(3))}").ToList();
        if (impl.Count > 0) sb.AppendLine($"implemented by ({impl.Count}): {string.Join(", ", impl)}");

        if (ext.Count == 0 && handlers.Count == 0 && derived.Count == 0 && impl.Count == 0)
            sb.AppendLine("  none");
        return Finish(sb);
    });

    // ---------------------------------------------------------------- grep

    public string Grep(string pattern, string? model, string? type, string? objectName, bool standard, int limit) => svc.Read(s =>
    {
        limit = Math.Clamp(limit, 1, 500);
        Regex rx;
        try
        {
            rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException ex)
        {
            return $"invalid regex: {ex.Message}";
        }
        var tp = TypePattern(type);
        var modelLike = PatOrNull(model);
        var objLike = PatOrNull(objectName);
        var hits = new List<(string Type, string Obj, string Tag, string Path, string Method, int Line, string Text)>();
        int total = 0;

        if (!standard)
        {
            foreach (var r in s.Query($"""
                SELECT s.text, m.start_line, m.name, m.owner, o.type, o.name, f.path, md.package, md.name, md.tier
                FROM sources s JOIN methods m ON m.id = s.method_id JOIN objects o ON o.id = m.object_id
                JOIN files f ON f.id = s.file_id JOIN models md ON md.id = f.model_id
                WHERE ($model IS NULL OR md.name LIKE $model ESCAPE '!' OR md.package LIKE $model ESCAPE '!')
                  AND ($obj IS NULL OR o.name LIKE $obj ESCAPE '!') {(tp != null ? "AND o.type LIKE $tp" : "")}
                ORDER BY md.name, o.name, m.start_line
                """, ("$model", modelLike), ("$obj", objLike), ("$tp", tp)))
            {
                var text = r.GetString(0);
                int lastLine = -1;
                foreach (Match mt in rx.Matches(text))
                {
                    int lineStart = text.LastIndexOf('\n', Math.Max(0, mt.Index - 1)) + 1;
                    if (mt.Index == 0) lineStart = 0;
                    int offset = 0;
                    for (int k = 0; k < lineStart; k++) if (text[k] == '\n') offset++;
                    if (offset == lastLine) continue;
                    lastLine = offset;
                    total++;
                    if (hits.Count >= limit) continue;
                    int lineEnd = text.IndexOf('\n', mt.Index);
                    var lineText = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
                    var owner = N(r, 3);
                    hits.Add((r.GetString(4), r.GetString(5), Tag(r.GetString(7), r.GetString(8), r.GetInt32(9)), r.GetString(6),
                        (string.IsNullOrEmpty(owner) ? "" : owner + "/") + r.GetString(2), r.GetInt32(1) + offset, Trunc(lineText, 180)));
                }
            }
        }
        else
        {
            if (modelLike == null && objLike == null)
                return "standard grep reads XML files from disk: pass model (package or model name) or object to limit the scope.";
            var files = s.Query($"""
                SELECT DISTINCT f.id, f.path, md.package, md.name, md.tier, o.type, o.name FROM files f JOIN models md ON md.id = f.model_id
                JOIN objects o ON o.file_id = f.id
                WHERE ($model IS NULL OR md.name LIKE $model ESCAPE '!' OR md.package LIKE $model ESCAPE '!') AND ($obj IS NULL OR o.name LIKE $obj ESCAPE '!')
                  {(tp != null ? "AND o.type LIKE $tp" : "")}
                ORDER BY md.name, o.name
                """, ("$model", modelLike), ("$obj", objLike), ("$tp", tp))
                .Select(r => (Id: r.GetInt64(0), Path: r.GetString(1), Tag: Tag(r.GetString(2), r.GetString(3), r.GetInt32(4)),
                    Type: r.GetString(5), Obj: r.GetString(6))).ToList();
            if (files.Count > 60000)
                return $"scope too large ({files.Count} XML files to read); narrow with model/object/type, or index that package fully " +
                       "(xppgraft config --add-full-model <package> + xppgraft build), which makes grep and the call graph instant there.";

            // Scan the XML files in parallel, then resolve line -> method for the few files that matched.
            files = files.Where(f => !BinaryPackage.IsPseudoPath(f.Path)).ToList();
            if (files.Count == 0)
                return "no XML in that scope (compiled packages have no source; use xpp_find / xpp_refs / xpp_callees there).";
            var perFile = files.AsParallel().WithDegreeOfParallelism(Math.Max(2, Environment.ProcessorCount))
                .Select(f =>
                {
                    List<(int Line, string Text)>? found = null;
                    try
                    {
                        var text = File.ReadAllText(f.Path);
                        if (!rx.IsMatch(text)) return (File: f, Hits: found);
                        found = [];
                        int line = 1, pos = 0;
                        foreach (Match mt in rx.Matches(text))
                        {
                            while (pos < mt.Index)
                                if (text[pos++] == '\n') line++;
                            int start = text.LastIndexOf('\n', Math.Max(0, mt.Index - 1)) + 1;
                            int end = text.IndexOf('\n', mt.Index);
                            if (found.Count == 0 || found[^1].Line != line)
                                found.Add((line, text[start..(end < 0 ? text.Length : end)].Trim()));
                            if (found.Count >= 200) break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"grep {f.Path}: {ex.Message}");
                    }
                    return (File: f, Hits: found);
                })
                .Where(x => x.Hits is { Count: > 0 })
                .ToList();

            if (perFile.Count > 0)
            {
                var args = new List<(string, object?)>();
                var ids = InList("$f", perFile.Select(p => (object?)p.File.Id).ToList(), args);
                var ranges = new Dictionary<long, List<(string Name, string? Owner, int Start, int End)>>();
                foreach (var r in s.Query($"SELECT file_id, name, owner, start_line, end_line FROM methods WHERE file_id IN ({ids}) ORDER BY start_line", args.ToArray()))
                {
                    var fid = r.GetInt64(0);
                    if (!ranges.TryGetValue(fid, out var list)) ranges[fid] = list = [];
                    list.Add((r.GetString(1), N(r, 2), r.GetInt32(3), r.GetInt32(4)));
                }

                foreach (var (f, fileHits) in perFile.OrderBy(p => p.File.Obj, StringComparer.OrdinalIgnoreCase))
                {
                    if (!ranges.TryGetValue(f.Id, out var list)) continue;
                    foreach (var (line, text) in fileHits!)
                    {
                        var m = list.FirstOrDefault(x => line >= x.Start && line <= x.End);
                        if (m.Name == null) continue;
                        total++;
                        if (hits.Count < limit)
                            hits.Add((f.Type, f.Obj, f.Tag, f.Path, (string.IsNullOrEmpty(m.Owner) ? "" : m.Owner + "/") + m.Name, line, Trunc(text, 180)));
                    }
                }
            }
        }

        var sb = new StringBuilder($"{total} match(es){(total > hits.Count ? $", showing {hits.Count}" : "")}:\n");
        if (total == 0 && !standard)
            sb.AppendLine("(searched indexed method sources = custom models; for Microsoft code pass standard=true with a model filter)");
        foreach (var og in hits.GroupBy(h => (h.Type, h.Obj, h.Path, h.Tag)))
        {
            sb.AppendLine($"{og.Key.Type} {og.Key.Obj} {og.Key.Tag}  {og.Key.Path}");
            foreach (var h in og) sb.AppendLine($"  {h.Method} L{h.Line}: {h.Text}");
        }
        return Finish(sb);
    });

    // ---------------------------------------------------------------- labels

    public string Label(string query, string? lang, int limit) => svc.Read(s =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var sb = new StringBuilder();
        query = query.Trim();
        if (query.StartsWith('@'))
        {
            var rows = s.Query("SELECT lang, text FROM labels WHERE label_id = $id ORDER BY lang", ("$id", query))
                .Select(r => $"  {r.GetString(0)}: {r.GetString(1)}").Distinct().ToList();
            sb.AppendLine(rows.Count == 0 ? $"{query}: not found" : $"{query}:");
            foreach (var l in rows) sb.AppendLine(l);
            var used = Convert.ToInt64(s.Scalar("SELECT COUNT(*) FROM refs WHERE target = $id AND kind = 'label'", ("$id", query)));
            if (used > 0) sb.AppendLine($"used {used}x in full-tier models (xpp_refs {query})");
            return Finish(sb);
        }
        lang ??= svc.Cfg.DisplayLanguage;
        var hits = s.Query("""
            SELECT l.label_id, l.text, md.tier FROM labels l JOIN label_files lf ON lf.id = l.lf_id JOIN models md ON md.id = lf.model_id
            WHERE l.lang = $lang COLLATE NOCASE AND (l.text LIKE $like ESCAPE '!' OR l.label_id LIKE $like ESCAPE '!')
            ORDER BY CASE WHEN l.text = $q THEN 0 WHEN l.text LIKE $qprefix ESCAPE '!' THEN 1 ELSE 2 END, md.tier DESC, length(l.text)
            LIMIT $limit
            """, ("$lang", lang), ("$like", Like(query)), ("$q", query), ("$qprefix", Esc(query) + "%"), ("$limit", limit))
            .Select(r => $"  {r.GetString(0)} = {Trunc(r.GetString(1), 160)}{r.GetInt32(2) switch { 1 => "", 0 => " (std)", _ => " (compiled)" }}").ToList();
        sb.AppendLine($"labels ({lang}) matching '{query}': {hits.Count}{(hits.Count == limit ? "+" : "")}");
        foreach (var h in hits) sb.AppendLine(h);
        return Finish(sb);
    });
}
