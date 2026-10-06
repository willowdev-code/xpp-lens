using System.Text.RegularExpressions;

namespace XppLens;

public sealed record AttrInfo(string Name, List<Token> Args);

public sealed class HeaderInfo
{
    public List<AttrInfo> Attributes { get; } = [];
    public string AttributeText = "";
    public string Header = "";
    public string? MethodName;
    public string? ReturnType;
    public bool IsStatic;
    public bool IsClassDeclaration;
    public string? ClassName;
    public string? Extends;
    public List<string> Implements { get; } = [];
    /// <summary>Index of the first token after the attribute blocks.</summary>
    public int BodyTokenStart;
}

/// <summary>Context of one code block, used to resolve receiver types.</summary>
public sealed class CodeContext
{
    public string ObjectType = "";
    public string ObjectName = "";
    public string? Extends;
    /// <summary>Target of [ExtensionOf(...)] (class/table/form name) for extension classes.</summary>
    public string? ExtensionTarget;
    public string? ThisType;
    public string? ElementType;
    public string? MethodName;
    public string Owner = "";
    public Dictionary<string, string> ClassVars = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DataSourceTables = new(StringComparer.OrdinalIgnoreCase);
}

public readonly record struct CodeRef(int Line, string Kind, string? Target, string? Member, string? Via);

public static partial class CodeAnalyzer
{
    static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract","as","asc","at","avg","break","breakpoint","by","case","catch","changecompany","class","client",
        "const","continue","count","crosscompany","default","delegate","delete_from","desc","display","div",
        "do","edit","element","else","event","exists","extends","false","final","finally","firstfast","firstonly",
        "firstonly10","firstonly100","firstonly1000","flush","for","forceliterals","forcenestedloop","forceplaceholders",
        "forceselectorder","forupdate","from","generateonly","group","hint","if","implements","in","index","insert_recordset",
        "interface","internal","is","join","like","maxof","minof","mod","new","next","nofetch","notexists","null","optimisticlock",
        "order","outer","pause","pessimisticlock","print","private","protected","public","readonly","repeatableread","retry",
        "return","reverse","select","server","setting","static","sum","super","switch","this","throw","true","try","ttsabort",
        "ttsbegin","ttscommit","update_recordset","using","validtimestate","virtual","where","while","window","with","unchecked",
        "extern","override","sealed"
    };

    static readonly HashSet<string> Primitives = new(StringComparer.OrdinalIgnoreCase)
    {
        "str","int","int64","real","boolean","date","utcdatetime","timeofday","container","anytype","guid","void","var","char","byte"
    };

    static readonly HashSet<string> IgnoredTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "common","object","FormRun","System"
    };

    public static bool IsPrimitive(string type) => Primitives.Contains(type) || IgnoredTypes.Contains(type);

    public static readonly HashSet<string> Intrinsics = new(StringComparer.OrdinalIgnoreCase)
    {
        "classStr","classNum","tableStr","tableNum","viewStr","formStr","reportStr","queryStr","menuStr",
        "menuItemDisplayStr","menuItemActionStr","menuItemOutputStr","enumStr","enumNum","enumLiteralStr",
        "extendedTypeStr","extendedTypeNum","typeStr","methodStr","staticMethodStr","tableMethodStr","tableStaticMethodStr",
        "formMethodStr","fieldStr","fieldNum","fieldPName","tableFieldGroupStr","indexStr","indexNum","delegateStr",
        "formDataSourceStr","formControlStr","formDataFieldStr","dataEntityDataSourceStr","ssrsReportStr","dutyStr",
        "privilegeStr","roleStr","securityPolicyStr","configurationKeyStr","licenseCodeStr","workflowTypeStr",
        "workflowApprovalStr","workflowTaskStr","workflowCategoryStr","tileStr","resourceStr","mapStr","dataEntityViewStr",
        "aggregateDataEntityStr","kpiStr","measureStr","measurementStr","tableCollectionStr","attributeStr","formPartStr",
        "infoPartStr","cuePartStr","cueGroupStr","webActionItemStr","queryDataSourceStr","queryMethodStr"
    };

    [GeneratedRegex(@"^@([A-Za-z_][A-Za-z0-9_]*:[A-Za-z0-9_.\-]+|[A-Z]{3}\d+)$")]
    public static partial Regex LabelRx();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WsRx();

    public static string Collapse(string s, int max = 400)
    {
        var r = WsRx().Replace(s, " ").Trim();
        return r.Length > max ? r[..max] + "…" : r;
    }

    static bool Is(Token t, string punct) => t.Kind == TokKind.Punct && t.Text == punct;
    static bool IsId(Token t, string text) => t.Kind == TokKind.Ident && t.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads attributes and the header (text up to the first top-level "{").</summary>
    public static HeaderInfo ParseHeader(string src, List<Token> toks)
    {
        var h = new HeaderInfo();
        int i = 0, n = toks.Count;

        while (i < n && Is(toks[i], "["))
        {
            int depth = 0, j = i;
            for (; j < n; j++)
            {
                if (Is(toks[j], "[")) depth++;
                else if (Is(toks[j], "]") && --depth == 0) break;
            }
            ParseAttributeBlock(toks, i + 1, Math.Min(j, n), h.Attributes);
            i = Math.Min(j + 1, n);
        }
        if (i > 0 && i <= n)
            h.AttributeText = Collapse(src.Substring(toks[0].Pos, toks[i - 1].End - toks[0].Pos), 240);
        h.BodyTokenStart = i;

        int brace = -1, paren = 0, firstParen = -1;
        for (int j = i; j < n; j++)
        {
            var t = toks[j];
            if (t.Kind != TokKind.Punct) continue;
            if (t.Text == "(") { if (paren == 0 && firstParen < 0) firstParen = j; paren++; }
            else if (t.Text == ")") paren = Math.Max(0, paren - 1);
            else if (t.Text == "{" && paren == 0) { brace = j; break; }
        }

        if (i < n)
        {
            int endPos = brace >= 0 ? toks[brace].Pos : toks[^1].End;
            if (endPos > toks[i].Pos)
                h.Header = Collapse(src.Substring(toks[i].Pos, endPos - toks[i].Pos));
        }

        int stop = brace >= 0 ? brace : n;
        for (int j = i; j < stop; j++)
        {
            var t = toks[j];
            if (t.Kind != TokKind.Ident) continue;
            if (IsId(t, "static")) h.IsStatic = true;
            else if ((IsId(t, "class") || IsId(t, "interface")) && j + 1 < stop && toks[j + 1].Kind == TokKind.Ident && firstParen < 0)
            {
                h.IsClassDeclaration = true;
                h.ClassName = toks[j + 1].Text;
            }
            else if (IsId(t, "extends") && j + 1 < stop && toks[j + 1].Kind == TokKind.Ident)
                h.Extends = toks[j + 1].Text;
            else if (IsId(t, "implements"))
            {
                for (int k = j + 1; k < stop; k++)
                {
                    if (toks[k].Kind == TokKind.Ident && !IsId(toks[k], "extends")) h.Implements.Add(toks[k].Text);
                    else if (!Is(toks[k], ",")) break;
                }
            }
        }

        if (!h.IsClassDeclaration && firstParen > i && toks[firstParen - 1].Kind == TokKind.Ident)
        {
            h.MethodName = toks[firstParen - 1].Text;
            if (firstParen - 2 >= i && toks[firstParen - 2].Kind == TokKind.Ident)
            {
                var rt = toks[firstParen - 2].Text;
                if (!Keywords.Contains(rt)) h.ReturnType = rt;
            }
        }
        return h;
    }

    static void ParseAttributeBlock(List<Token> toks, int from, int to, List<AttrInfo> into)
    {
        int i = from;
        while (i < to)
        {
            if (toks[i].Kind != TokKind.Ident) { i++; continue; }
            var name = toks[i].Text;
            var args = new List<Token>();
            i++;
            if (i < to && Is(toks[i], "("))
            {
                int depth = 0;
                for (; i < to; i++)
                {
                    if (Is(toks[i], "(")) { if (depth++ == 0) continue; }
                    else if (Is(toks[i], ")") && --depth == 0) { i++; break; }
                    args.Add(toks[i]);
                }
            }
            into.Add(new AttrInfo(name, args));
            while (i < to && !Is(toks[i], ",")) i++;
            i++;
        }
    }

    /// <summary>intrinsicName(a, b, c) calls whose arguments are plain identifiers.</summary>
    public static List<(string Func, List<string> Args, int Line)> Intrinsic(List<Token> toks, int from = 0, int to = -1)
    {
        var res = new List<(string, List<string>, int)>();
        if (to < 0) to = toks.Count;
        for (int i = from; i + 2 < to; i++)
        {
            if (toks[i].Kind != TokKind.Ident || !Is(toks[i + 1], "(") || !Intrinsics.Contains(toks[i].Text)) continue;
            var args = new List<string>();
            int j = i + 2;
            bool ok = true;
            while (j < to)
            {
                if (toks[j].Kind == TokKind.Ident) args.Add(toks[j].Text);
                else if (toks[j].Kind == TokKind.String) args.Add(toks[j].Text);
                else { ok = false; break; }
                j++;
                if (j < to && Is(toks[j], ",")) { j++; continue; }
                if (j < to && Is(toks[j], ")")) break;
                ok = false;
                break;
            }
            if (ok && args.Count > 0) res.Add((toks[i].Text, args, toks[i].Line));
        }
        return res;
    }

    /// <summary>Handler / extension refs declared by attributes.</summary>
    public static void AttributeRefs(HeaderInfo h, int baseLine, List<CodeRef> refs, out (string Func, List<string> Args)? extensionOf)
    {
        extensionOf = null;
        foreach (var a in h.Attributes)
        {
            var name = a.Name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase) ? a.Name[..^9] : a.Name;
            var intr = Intrinsic(a.Args);
            int line = baseLine + (a.Args.Count > 0 ? a.Args[0].Line : 0);

            if (name.Equals("ExtensionOf", StringComparison.OrdinalIgnoreCase))
            {
                if (intr.Count > 0)
                {
                    extensionOf = (intr[0].Func, intr[0].Args);
                    refs.Add(new CodeRef(line, "coc", intr[0].Args[0], intr[0].Args.Count > 1 ? string.Join(".", intr[0].Args.Skip(1)) : null, intr[0].Func));
                }
                continue;
            }

            bool handler = name.Equals("SubscribesTo", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith("HandlerFor", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith("EventHandler", StringComparison.OrdinalIgnoreCase);
            if (!handler || intr.Count == 0) continue;

            string? member;
            if (intr.Count > 1 && intr[1].Args.Count > 1)
                member = intr[1].Args[1];
            else
            {
                var parts = intr[0].Args.Skip(1).ToList();
                for (int i = 0; i + 2 < a.Args.Count; i++)
                    if (a.Args[i].Kind == TokKind.Ident && Is(a.Args[i + 1], "::") && a.Args[i + 2].Kind == TokKind.Ident)
                        parts.Add(a.Args[i + 2].Text);
                member = parts.Count > 0 ? string.Join(".", parts) : null;
            }
            refs.Add(new CodeRef(line, "handler", intr[0].Args[0], member, name));
        }
    }

    static bool TryDeclaration(List<Token> t, int i, out string type, out int varIdx)
    {
        type = "";
        varIdx = -1;
        var first = t[i];
        if (first.Kind != TokKind.Ident || Keywords.Contains(first.Text)) return false;
        if (i > 0)
        {
            var p = t[i - 1];
            if (p.Kind == TokKind.Punct && (p.Text == "." || p.Text == "::")) return false;
            if (p.Kind == TokKind.Ident && !Keywords.Contains(p.Text)) return false;
        }
        int j = i;
        while (j + 2 < t.Count && Is(t[j + 1], ".") && t[j + 2].Kind == TokKind.Ident) j += 2;
        if (j + 2 >= t.Count) return false;
        var v = t[j + 1];
        if (v.Kind != TokKind.Ident || Keywords.Contains(v.Text) || Primitives.Contains(v.Text)) return false;
        var after = t[j + 2];
        if (after.Kind != TokKind.Punct || after.Text is not (";" or "=" or "," or ")" or "[")) return false;

        type = j == i ? first.Text : string.Concat(Enumerable.Range(0, (j - i) / 2 + 1).Select(k => (k > 0 ? "." : "") + t[i + 2 * k].Text));
        varIdx = j + 1;
        return true;
    }

    /// <summary>Collects variable declarations (locals, parameters or class members).</summary>
    public static Dictionary<string, string> Declarations(List<Token> toks, int from, List<CodeRef>? refs, int baseLine)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = from; i < toks.Count; i++)
        {
            if (!TryDeclaration(toks, i, out var type, out var varIdx)) continue;
            bool knownType = !Primitives.Contains(type);
            if (knownType)
            {
                vars[toks[varIdx].Text] = type;
                if (refs != null && !IgnoredTypes.Contains(type) && !type.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                    refs.Add(new CodeRef(baseLine + toks[i].Line, "type", type, null, null));
            }
            int j = varIdx + 1;
            while (j + 1 < toks.Count && Is(toks[j], ",") && toks[j + 1].Kind == TokKind.Ident
                   && j + 2 < toks.Count && toks[j + 2].Kind == TokKind.Punct && toks[j + 2].Text is ";" or "," or "=")
            {
                if (knownType) vars[toks[j + 1].Text] = type;
                j += 2;
            }
            i = varIdx;
        }
        return vars;
    }

    /// <summary>0-based source lines that hold a variable declaration (used to keep them in method fragments).</summary>
    public static HashSet<int> DeclarationLines(List<Token> toks, int from)
    {
        var lines = new HashSet<int>();
        for (int i = from; i < toks.Count; i++)
        {
            if (IsId(toks[i], "var") && i + 2 < toks.Count && toks[i + 1].Kind == TokKind.Ident && Is(toks[i + 2], "="))
            {
                lines.Add(toks[i].Line);
                continue;
            }
            if (!TryDeclaration(toks, i, out _, out var varIdx)) continue;
            lines.Add(toks[i].Line);
            i = varIdx;
        }
        return lines;
    }

    /// <summary>
    /// Chained receivers ("Table::find(x).name()", "a.b().c()") cannot be typed while parsing: the return type
    /// lives in another object. They are recorded with target = null and via = "ret:" + chain, where the chain is
    /// "Type>method>method…" (the type of the first receiver, then the calls applied to it). Queries resolve the
    /// chain from method signatures in the index.
    /// </summary>
    public const string ChainPrefix = "ret:";

    public static bool IsChain(string? type) => type != null && type.Contains('>');

    static int[] MatchParens(List<Token> toks, int from)
    {
        var close = new int[toks.Count];
        Array.Fill(close, -1);
        var open = new Stack<int>();
        for (int i = from; i < toks.Count; i++)
        {
            if (Is(toks[i], "(")) open.Push(i);
            else if (Is(toks[i], ")") && open.Count > 0) close[open.Pop()] = i;
        }
        return close;
    }

    /// <summary>Extracts references from a code block (full tier).</summary>
    public static void BodyRefs(CodeContext ctx, List<Token> toks, int from, int baseLine, List<CodeRef> refs,
        Dictionary<string, string>? locals)
    {
        locals ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int n = toks.Count;
        var close = MatchParens(toks, from);
        // ")" token index → type expression of the call it closes (concrete "Type" or chain "Type>m").
        var resultAt = new Dictionary<int, string>();

        string? Resolve(string name)
        {
            if (name.Equals("this", StringComparison.OrdinalIgnoreCase)) return ctx.ThisType;
            if (name.Equals("element", StringComparison.OrdinalIgnoreCase)) return ctx.ElementType;
            if (name.Equals("super", StringComparison.OrdinalIgnoreCase)) return ctx.Extends;
            if (locals.TryGetValue(name, out var t)) return t;
            if (ctx.ClassVars.TryGetValue(name, out t)) return t;
            if (ctx.DataSourceTables.TryGetValue(name, out t)) return t;
            return null;
        }

        string Next(int i) => i + 1 < n && toks[i + 1].Kind == TokKind.Punct ? toks[i + 1].Text : "";
        string Prev(int i) => i > 0 && toks[i - 1].Kind == TokKind.Punct ? toks[i - 1].Text : "";

        void Result(int openParen, string? type)
        {
            if (type == null || type.Contains('.') || openParen < 0 || openParen >= n || close[openParen] < 0) return;
            resultAt[close[openParen]] = type;
        }

        static string? Then(string? recv, string member) => recv == null || recv.Contains('.') ? null : $"{recv}>{member}";

        void Access(int line, string? recv, string member, bool call)
        {
            if (recv == null)
            {
                if (call) refs.Add(new CodeRef(line, "call", null, member, null));
            }
            else if (IsChain(recv)) refs.Add(new CodeRef(line, call ? "call" : "member", null, member, ChainPrefix + recv));
            else refs.Add(new CodeRef(line, call ? "call" : "member", recv, member, null));
        }

        for (int i = from; i < n; i++)
        {
            var t = toks[i];
            int line = baseLine + t.Line;

            if (t.Kind == TokKind.String)
            {
                if (t.Text.Length > 3 && t.Text[0] == '@' && LabelRx().IsMatch(t.Text))
                    refs.Add(new CodeRef(line, "label", t.Text, null, null));
                continue;
            }

            // "<call>(…).member" — the receiver is whatever that call returned.
            if (Is(t, ")") && Next(i) == "." && i + 2 < n && toks[i + 2].Kind == TokKind.Ident)
            {
                var member = toks[i + 2].Text;
                bool call = Next(i + 2) == "(";
                var recv = resultAt.GetValueOrDefault(i);
                Access(baseLine + toks[i + 2].Line, recv, member, call);
                if (call) Result(i + 3, Then(recv, member));
                i += 2;
                continue;
            }
            if (t.Kind != TokKind.Ident) continue;

            // var x = new T(…) / A::m(…) / y.m(…): remember what x holds.
            if (IsId(t, "var") && i + 3 < n && toks[i + 1].Kind == TokKind.Ident && Is(toks[i + 2], "="))
            {
                var name = toks[i + 1].Text;
                int r = i + 3;
                if (IsId(toks[r], "new") && r + 2 < n && toks[r + 1].Kind == TokKind.Ident && Is(toks[r + 2], "("))
                    locals[name] = toks[r + 1].Text;
                else if (toks[r].Kind == TokKind.Ident && r + 3 < n && Is(toks[r + 1], "::") && toks[r + 2].Kind == TokKind.Ident && Is(toks[r + 3], "("))
                    locals[name] = $"{toks[r].Text}>{toks[r + 2].Text}";
                else if (toks[r].Kind == TokKind.Ident && r + 3 < n && Is(toks[r + 1], ".") && toks[r + 2].Kind == TokKind.Ident && Is(toks[r + 3], "(")
                         && Then(Resolve(toks[r].Text), toks[r + 2].Text) is { } chain)
                    locals[name] = chain;
                continue;
            }

            if (Next(i) == "(" && Intrinsics.Contains(t.Text))
            {
                foreach (var (func, args, _) in Intrinsic(toks, i, Math.Min(n, i + 12)))
                {
                    refs.Add(new CodeRef(line, "intrinsic", args[0], args.Count > 1 ? string.Join(".", args.Skip(1)) : null, func));
                    break;
                }
                continue;
            }

            if (Next(i) == "::" && i + 2 < n && toks[i + 2].Kind == TokKind.Ident)
            {
                bool call = Next(i + 2) == "(";
                refs.Add(new CodeRef(line, call ? "call" : "static", t.Text, toks[i + 2].Text, call ? "::" : null));
                if (call) Result(i + 3, Then(t.Text, toks[i + 2].Text));
                i += 2;
                continue;
            }

            if (IsId(t, "new") && i + 1 < n && toks[i + 1].Kind == TokKind.Ident)
            {
                int j = i + 1;
                while (j + 2 < n && Is(toks[j + 1], ".") && toks[j + 2].Kind == TokKind.Ident) j += 2;
                if (Next(j) == "(")
                {
                    var name = string.Join("", toks.Skip(i + 1).Take(j - i).Select(x => x.Text));
                    refs.Add(new CodeRef(line, "new", name, null, null));
                    Result(j + 1, name);
                }
                i = j;
                continue;
            }

            if (IsId(t, "super") && Next(i) == "(")
            {
                var target = ctx.ExtensionTarget ?? ctx.Extends;
                if (target != null && ctx.MethodName != null)
                {
                    refs.Add(new CodeRef(line, "call", target, ctx.MethodName, "super"));
                    Result(i + 1, Then(target, ctx.MethodName));
                }
                continue;
            }

            if (IsId(t, "next") && i + 1 < n && toks[i + 1].Kind == TokKind.Ident && Next(i + 1) == "(")
            {
                if (ctx.ExtensionTarget != null)
                {
                    refs.Add(new CodeRef(line, "coc", ctx.ExtensionTarget, toks[i + 1].Text, "next"));
                    // Form data source / control extensions wrap element methods, not the form's own.
                    if (string.Equals(ctx.ThisType, ctx.ExtensionTarget, StringComparison.OrdinalIgnoreCase))
                        Result(i + 2, Then(ctx.ExtensionTarget, toks[i + 1].Text));
                }
                i++;
                continue;
            }

            if ((IsId(t, "is") || IsId(t, "as")) && i + 1 < n && toks[i + 1].Kind == TokKind.Ident)
            {
                refs.Add(new CodeRef(line, "type", toks[i + 1].Text, null, t.Text.ToLowerInvariant()));
                i++;
                continue;
            }

            if (Next(i) == "." && i + 2 < n && toks[i + 2].Kind == TokKind.Ident && Prev(i) != ".")
            {
                var recv = Resolve(t.Text);
                var member = toks[i + 2].Text;
                bool call = Next(i + 2) == "(";
                Access(line, recv, member, call);
                if (call) Result(i + 3, Then(recv, member));
                i += 2;
                continue;
            }

            if (Prev(i) == "." && Next(i) == "(")
                refs.Add(new CodeRef(line, "call", null, t.Text, null));
        }
    }
}
