using System.Text;
using System.Text.RegularExpressions;

namespace XppGraft;

/// <summary>
/// Ready-to-paste X++ skeletons with exact signatures taken from the index: Chain of Command wrappers,
/// data / form event handlers, delegate subscribers and pre/post handlers.
/// </summary>
public sealed partial class Queries
{
    /// <summary>Header pieces of a method signature.</summary>
    internal sealed record SigParts(string Attributes, List<string> Modifiers, string ReturnType, string Name, List<(string Type, string Name)> Params);

    static readonly HashSet<string> ModifierWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "protected", "private", "internal", "static", "final", "abstract", "display", "edit", "client", "server",
        "delegate", "virtual", "override", "extern",
    };

    /// <summary>Splits "[Attr] public static CustTable find(CustAccount _a, boolean _f = false)" into its parts.</summary>
    internal static SigParts? ParseSignature(string sig)
    {
        var toks = XppLexer.Tokenize(sig, stopAtFirstBrace: true);
        var h = CodeAnalyzer.ParseHeader(sig, toks);
        if (h.MethodName == null) return null;
        int i = h.BodyTokenStart, open = -1;
        for (int k = i; k < toks.Count; k++)
            if (toks[k].Kind == TokKind.Punct && toks[k].Text == "(") { open = k; break; }
        if (open < 1) return null;

        var mods = new List<string>();
        string ret = "void";
        for (int k = i; k < open - 1; k++)
        {
            var t = toks[k].Text;
            if (ModifierWords.Contains(t)) mods.Add(t.ToLowerInvariant());
            else ret = t;
        }

        var ps = new List<(string, string)>();
        var cur = new List<Token>();
        int depth = 0;
        bool inDefault = false;
        void Flush()
        {
            var ids = cur.Where(t => t.Kind == TokKind.Ident).ToList();
            if (ids.Count >= 2) ps.Add((string.Join(" ", ids.Take(ids.Count - 1).Select(t => t.Text)), ids[^1].Text));
            cur.Clear();
            inDefault = false;
        }
        for (int k = open + 1; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.Kind == TokKind.Punct && t.Text is "(" or "[") depth++;
            else if (t.Kind == TokKind.Punct && t.Text is ")" or "]")
            {
                if (depth == 0) { Flush(); break; }
                depth--;
            }
            if (depth == 0 && t.Kind == TokKind.Punct && t.Text == ",") { Flush(); continue; }
            if (depth == 0 && t.Kind == TokKind.Punct && t.Text == "=") { inDefault = true; continue; }
            if (!inDefault) cur.Add(t);
        }
        return new SigParts(h.AttributeText, mods, ret, h.MethodName, ps);
    }

    /// <summary>The middle part your team puts in extension class names ("CustTable_Contoso_Extension" → "_Contoso").</summary>
    string ExtensionInfix(Store s)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in s.Query("""
            SELECT o.name, o.target FROM objects o JOIN files f ON f.id = o.file_id JOIN models md ON md.id = f.model_id
            WHERE md.tier = 1 AND o.type = 'AxClass' AND o.target IS NOT NULL AND o.name LIKE '%!_Extension' ESCAPE '!' LIMIT 3000
            """))
        {
            var name = r.GetString(0);
            var target = r.GetString(1);
            if (!name.StartsWith(target, StringComparison.OrdinalIgnoreCase)) continue;
            var mid = name[target.Length..^"_Extension".Length];
            if (mid.Length is > 0 and < 30) counts[mid] = counts.GetValueOrDefault(mid) + 1;
        }
        var best = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        return best.Value >= 3 ? best.Key : "";
    }

    /// <summary>Event names and the parameter list most often used with them, learned from handlers in the index.</summary>
    Dictionary<string, string> EventSignatures(Store s, string enumType)
    {
        var rx = new Regex(Regex.Escape(enumType) + @"::(\w+)\s*\)\s*\]\s*(?:public\s+|static\s+)*void\s+\w+\s*\(([^)]*)\)", RegexOptions.IgnoreCase);
        var seen = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in s.Query("SELECT sig FROM methods WHERE sig LIKE $p LIMIT 20000", ("$p", $"%{enumType}::%")))
        {
            var m = rx.Match(r.GetString(0));
            if (!m.Success) continue;
            var ev = m.Groups[1].Value;
            var ps = Regex.Replace(m.Groups[2].Value.Trim(), @"\s+", " ");
            if (!seen.TryGetValue(ev, out var d)) seen[ev] = d = new Dictionary<string, int>(StringComparer.Ordinal);
            d[ps] = d.GetValueOrDefault(ps) + 1;
        }
        return seen.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).First().Key, StringComparer.OrdinalIgnoreCase);
    }

    static readonly Dictionary<string, (string Attr, string Enum, string Str, string DefaultParams)> EventKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["table"] = ("DataEventHandler", "DataEventType", "tableStr", "Common sender, DataEventArgs e"),
        ["form"] = ("FormEventHandler", "FormEventType", "formStr", "xFormRun sender, FormEventArgs e"),
        ["datasource"] = ("FormDataSourceEventHandler", "FormDataSourceEventType", "formDataSourceStr", "FormDataSource sender, FormDataSourceEventArgs e"),
        ["control"] = ("FormControlEventHandler", "FormControlEventType", "formControlStr", "FormControl sender, FormControlEventArgs e"),
        ["field"] = ("FormDataFieldEventHandler", "FormDataFieldEventType", "formDataFieldStr", "FormDataObject sender, FormDataFieldEventArgs e"),
    };

    public string Scaffold(string kind, string objectName, string member, string? element, string? className, string? type = null) => svc.Read(s =>
    {
        kind = kind.Trim().ToLowerInvariant();
        objectName = objectName.Trim();
        member = member.Trim();
        element = string.IsNullOrWhiteSpace(element) ? null : element.Trim();
        var rows = ObjectRows(s, objectName, type).Where(r => !r.Type.EndsWith("Extension", StringComparison.Ordinal)).ToList();
        if (rows.Count == 0) return NotFound(s, objectName, type);
        // A form often shares its name with a table: an element (data source / control) means the form.
        var o = element != null && type == null ? rows.FirstOrDefault(r => r.Type == "AxForm") ?? rows[0] : rows[0];
        var infix = ExtensionInfix(s);
        var sb = new StringBuilder();

        return kind switch
        {
            "coc" => ScaffoldCoc(s, sb, o, member, element, className, infix),
            "event" => ScaffoldEvent(s, sb, o, member, element, className, infix),
            "delegate" => ScaffoldDelegate(s, sb, o, member, className, infix),
            "pre" or "post" => ScaffoldPrePost(s, sb, o, member, kind, className, infix),
            _ => "kind must be one of: coc, event, delegate, pre, post",
        };
    });

    static string StrFunc(string type) => type switch
    {
        "AxTable" or "AxView" or "AxDataEntityView" => "tableStr",
        "AxForm" => "formStr",
        "AxMap" => "mapStr",
        _ => "classStr",
    };

    string ScaffoldCoc(Store s, StringBuilder sb, ObjRow o, string method, string? element, string? className, string infix)
    {
        // Form elements: element = data source, "DataSource.Field" or a control name.
        string? owner = null;
        if (o.Type == "AxForm" && element != null)
        {
            var dot = element.IndexOf('.');
            owner = dot > 0 ? $"DataSource:{element[..dot]}/Field:{element[(dot + 1)..]}" : null;
        }
        var cands = s.Query("""
            SELECT m.sig, IFNULL(m.owner, '') FROM methods m WHERE m.object_id = $id AND m.name = $m
            """, ("$id", o.Id), ("$m", method)).Select(r => (Sig: r.GetString(0), Owner: r.GetString(1))).ToList();
        var pick = element == null
            ? cands.FirstOrDefault(c => c.Owner.Length == 0)
            : cands.FirstOrDefault(c => owner != null ? c.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase)
                : c.Owner.Equals($"DataSource:{element}", StringComparison.OrdinalIgnoreCase) || c.Owner.EndsWith($"Control:{element}", StringComparison.OrdinalIgnoreCase));
        if (pick.Sig == null)
        {
            var where = element == null ? "" : $" on element {element}";
            var hint = cands.Count > 0 ? $" It exists on: {string.Join(", ", cands.Select(c => c.Owner.Length == 0 ? "(the form itself)" : c.Owner))} — pass element." : "";
            return $"'{o.Name}' has no method '{method}'{where}.{hint}\nKernel methods (e.g. FormDataSource.init/validateWrite) can be wrapped too: pass the element; the signature is then not in the index — copy it from the base class.";
        }
        var sp = ParseSignature(pick.Sig);
        if (sp == null) return $"cannot read the signature: {pick.Sig}";

        var warnings = new List<string>();
        if (sp.Modifiers.Contains("private")) warnings.Add("the method is private — Chain of Command cannot wrap it.");
        if (sp.Modifiers.Contains("final") && !sp.Attributes.Contains("Wrappable(true)", StringComparison.OrdinalIgnoreCase))
            warnings.Add("the method is final — it can be wrapped only if it has [Wrappable(true)].");
        if (sp.Attributes.Contains("Wrappable(false)", StringComparison.OrdinalIgnoreCase) || sp.Attributes.Contains("Hookable(false)", StringComparison.OrdinalIgnoreCase))
            warnings.Add("the method is marked [Wrappable(false)] / [Hookable(false)] — it cannot be wrapped.");
        if (sp.Modifiers.Contains("delegate")) warnings.Add("this is a delegate — subscribe to it (kind=delegate) instead.");

        string extOf, defaultClass;
        var dsOwner = pick.Owner;
        if (o.Type == "AxForm" && dsOwner.StartsWith("DataSource:", StringComparison.Ordinal))
        {
            var ds = dsOwner["DataSource:".Length..].Split('/')[0];
            var field = dsOwner.Contains("/Field:") ? dsOwner[(dsOwner.IndexOf("/Field:", StringComparison.Ordinal) + 7)..] : null;
            extOf = field != null ? $"formDataFieldStr({o.Name}, {ds}, {field})" : $"formDataSourceStr({o.Name}, {ds})";
            defaultClass = $"{o.Name}_{ds}{(field != null ? "_" + field : "")}{infix}_Extension";
        }
        else if (o.Type == "AxForm" && dsOwner.Length > 0)
        {
            var control = dsOwner[(dsOwner.LastIndexOf(':') + 1)..];
            extOf = $"formControlStr({o.Name}, {control})";
            defaultClass = $"{o.Name}_{control}{infix}_Extension";
        }
        else
        {
            extOf = $"{StrFunc(o.Type)}({o.Name})";
            defaultClass = o.Type == "AxForm" ? $"{o.Name}Form{infix}_Extension" : $"{o.Name}{infix}_Extension";
        }

        var mods = sp.Modifiers.Where(m => m is not ("final" or "abstract" or "virtual" or "override")).ToList();
        var paramDecl = string.Join(", ", sp.Params.Select(p => $"{p.Type} {p.Name}"));
        var paramUse = string.Join(", ", sp.Params.Select(p => p.Name));
        bool isVoid = sp.ReturnType.Equals("void", StringComparison.OrdinalIgnoreCase);

        sb.AppendLine($"Chain of Command for {o.Type} {o.Name}.{(dsOwner.Length > 0 ? dsOwner + "/" : "")}{method} {o.Tag}");
        sb.AppendLine($"base: {pick.Sig}");
        foreach (var w in warnings) sb.AppendLine("WARNING: " + w);
        sb.AppendLine();
        sb.AppendLine($"[ExtensionOf({extOf})]");
        sb.AppendLine($"final class {className ?? defaultClass}");
        sb.AppendLine("{");
        sb.AppendLine($"    {string.Join(" ", mods.Append(sp.ReturnType).Append($"{sp.Name}({paramDecl})"))}");
        sb.AppendLine("    {");
        if (isVoid) sb.AppendLine($"        next {sp.Name}({paramUse});");
        else
        {
            sb.AppendLine($"        {sp.ReturnType} ret = next {sp.Name}({paramUse});");
            sb.AppendLine();
            sb.AppendLine("        return ret;");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("notes: parameter default values are left out on purpose (a wrapper must not repeat them); `next` must be called unconditionally.");
        if (infix.Length == 0 && className == null) sb.AppendLine("class name: no naming pattern found in your models — rename to your convention.");

        var existing = s.Query("""
            SELECT DISTINCT o.name, md.package, md.name, md.tier FROM refs r JOIN objects o ON o.id = r.object_id
            JOIN files f ON f.id = r.file_id JOIN models md ON md.id = f.model_id
            WHERE r.kind = 'coc' AND r.target = $t AND r.via <> 'next' AND md.tier = 1 ORDER BY o.name LIMIT 10
            """, ("$t", o.Name)).Select(r => $"{r.GetString(0)} {Tag(r.GetString(1), r.GetString(2), r.GetInt32(3))}").ToList();
        if (existing.Count > 0) sb.AppendLine($"existing extension classes of {o.Name} in your models (you can add the method to one of them): {string.Join(", ", existing)}");
        var wrapped = CocWrappers(s, o.Name, method);
        if (wrapped.Length > 0) sb.AppendLine("this method is already wrapped / handled by:" + wrapped[wrapped.IndexOf('\n')..]);
        return Finish(sb);
    }

    string ScaffoldEvent(Store s, StringBuilder sb, ObjRow o, string ev, string? element, string? className, string infix)
    {
        string k;
        string strArgs, prefix;
        if (o.Type is "AxTable" or "AxView" or "AxDataEntityView") { k = "table"; strArgs = o.Name; prefix = o.Name; }
        else if (o.Type == "AxForm" && element == null) { k = "form"; strArgs = o.Name; prefix = o.Name; }
        else if (o.Type == "AxForm" && element!.Contains('.'))
        {
            k = "field";
            var ds = element[..element.IndexOf('.')];
            var field = element[(element.IndexOf('.') + 1)..];
            strArgs = $"{o.Name}, {ds}, {field}";
            prefix = $"{ds}_{field}";
        }
        else if (o.Type == "AxForm")
        {
            bool isDs = s.Query("SELECT 1 FROM members WHERE object_id = $id AND kind = 'datasource' AND name = $n LIMIT 1", ("$id", o.Id), ("$n", element))
                .Any() || (!o.Compiled && File.Exists(o.Path) && XmlObjectParser.Parse(o.Path, ParseMode.Render).Members
                    .Any(m => m.Kind == "datasource" && m.Name.Equals(element, StringComparison.OrdinalIgnoreCase)));
            k = isDs ? "datasource" : "control";
            strArgs = $"{o.Name}, {element}";
            prefix = element!;
        }
        else return $"events: pass a table, view, data entity or form (with element = data source, DataSource.Field or control). {o.Type} {o.Name} has delegates instead — use kind=delegate.";

        var spec = EventKinds[k];
        var known = EventSignatures(s, spec.Enum);
        var evName = ev.StartsWith("on", StringComparison.OrdinalIgnoreCase) && ev.Length > 2 && char.IsUpper(ev[2]) ? ev[2..] : ev;
        var match = known.Keys.FirstOrDefault(x => x.Equals(evName, StringComparison.OrdinalIgnoreCase));
        if (match == null && known.Count > 0)
            return $"'{ev}' is not a {spec.Enum} value seen in the index. Known: {string.Join(", ", known.Keys.Order())}";
        evName = match ?? evName;
        var ps = known.TryGetValue(evName, out var p) ? p : spec.DefaultParams;
        var methodName = k == "table" ? $"{prefix}_on{evName}" : $"{prefix}_On{evName}";
        // Names as in the learned parameter list ("Common _sender, DataEventArgs _e").
        var names = ps.Split(',').Select(x => x.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "").ToList();
        var senderName = names.ElementAtOrDefault(0) is { Length: > 0 } sn ? sn : "sender";
        var argsName = names.ElementAtOrDefault(1) is { Length: > 0 } an ? an : "e";

        sb.AppendLine($"{spec.Attr} for {o.Type} {o.Name}{(element != null ? " / " + element : "")} {o.Tag}, event {evName}");
        sb.AppendLine();
        sb.AppendLine($"public final class {className ?? $"{o.Name}{infix}_EventHandler"}");
        sb.AppendLine("{");
        sb.AppendLine($"    [{spec.Attr}({spec.Str}({strArgs}), {spec.Enum}::{evName})]");
        sb.AppendLine($"    public static void {methodName}({ps})");
        sb.AppendLine("    {");
        if (k == "table") sb.AppendLine($"        {o.Name} {char.ToLowerInvariant(o.Name[0])}{o.Name[1..]} = {senderName} as {o.Name};");
        if (k == "table" && evName.StartsWith("Validat", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"        ValidateEventArgs validateArgs = {argsName} as ValidateEventArgs;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        if (known.Count == 0) sb.AppendLine($"\n(no {spec.Enum} handlers in the index to learn from — parameters are the Visual Studio defaults)");
        else sb.AppendLine($"\nparameters as most often used with {spec.Enum}::{evName} in the index. Prefer Chain of Command when you need the method's arguments or return value.");
        return Finish(sb);
    }

    string ScaffoldDelegate(Store s, StringBuilder sb, ObjRow o, string name, string? className, string infix)
    {
        var sig = s.Scalar("SELECT sig FROM methods WHERE object_id = $id AND name = $n LIMIT 1", ("$id", o.Id), ("$n", name)) as string;
        if (sig == null) return $"'{o.Name}' has no delegate '{name}'.";
        var sp = ParseSignature(sig);
        if (sp == null || !sp.Modifiers.Contains("delegate"))
            return $"{o.Name}.{name} is not a delegate ({sig}). For a method use kind=coc.";
        var str = StrFunc(o.Type);
        sb.AppendLine($"subscriber of delegate {o.Type} {o.Name}.{name} {o.Tag}");
        sb.AppendLine($"delegate: {sig}");
        sb.AppendLine();
        sb.AppendLine($"public final class {className ?? $"{o.Name}{infix}_EventHandler"}");
        sb.AppendLine("{");
        sb.AppendLine($"    [SubscribesTo({str}({o.Name}), delegateStr({o.Name}, {name}))]");
        sb.AppendLine($"    public static void {o.Name}_{name}({string.Join(", ", sp.Params.Select(p => $"{p.Type} {p.Name}"))})");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return Finish(sb);
    }

    string ScaffoldPrePost(Store s, StringBuilder sb, ObjRow o, string method, string kind, string? className, string infix)
    {
        var sig = s.Scalar("SELECT sig FROM methods WHERE object_id = $id AND name = $n AND owner IS NULL LIMIT 1", ("$id", o.Id), ("$n", method)) as string;
        if (sig == null) return $"'{o.Name}' has no method '{method}'.";
        var sp = ParseSignature(sig);
        bool isStatic = sp?.Modifiers.Contains("static") == true;
        var methodStr = o.Type switch
        {
            "AxTable" or "AxView" or "AxDataEntityView" => isStatic ? "tableStaticMethodStr" : "tableMethodStr",
            "AxForm" => "formMethodStr",
            _ => isStatic ? "staticMethodStr" : "methodStr",
        };
        var attr = kind == "pre" ? "PreHandlerFor" : "PostHandlerFor";
        sb.AppendLine($"{attr} for {o.Type} {o.Name}.{method} {o.Tag}");
        sb.AppendLine($"base: {sig}");
        sb.AppendLine();
        sb.AppendLine($"public final class {className ?? $"{o.Name}{infix}_EventHandler"}");
        sb.AppendLine("{");
        sb.AppendLine($"    [{attr}({StrFunc(o.Type)}({o.Name}), {methodStr}({o.Name}, {method}))]");
        sb.AppendLine($"    public static void {o.Name}_{(kind == "pre" ? "Pre" : "Post")}_{method}(XppPrePostArgs args)");
        sb.AppendLine("    {");
        if (kind == "post" && sp != null && !sp.ReturnType.Equals("void", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"        {sp.ReturnType} ret = args.getReturnValue();");
        foreach (var (type, name) in sp?.Params ?? [])
            sb.AppendLine($"        {type} {name} = args.getArg('{name}');");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("\nnote: pre/post handlers are legacy — prefer Chain of Command (kind=coc).");
        return Finish(sb);
    }
}
