using System.Xml;

namespace XppLens;

/// <summary>
/// Standard: dictionary tier (signatures, members without controls, extension/handler attributes).
/// StandardCode: Standard + call references from method bodies (who calls what), no sources.
/// Full: + method sources, all code references, metadata references, labels.
/// Render: Full + everything needed to print an object skeleton.
/// </summary>
public enum ParseMode { Standard, StandardCode, Full, Render }

public sealed class ParsedMethod
{
    public string Owner = "";
    public string Name = "";
    public string Signature = "";
    public bool IsStatic;
    public bool IsDeclaration;
    public int StartLine;
    public int EndLine;
    public string? Source;
}

public sealed class ParsedMember
{
    public string Kind = "";
    public string Owner = "";
    public string Name = "";
    public string Info = "";
    public int Depth;
    /// <summary>Full path inside the object's tree (controls, menu elements), e.g. "Tab/TabPageGeneral/Grid".</summary>
    public string Path = "";
    /// <summary>Document order of the element's start tag (members are created when the tag closes).</summary>
    public int Order;
}

public sealed class ParsedRef
{
    public int MethodIndex = -1;
    public int Line;
    public string Kind = "";
    public string? Target;
    public string? Member;
    public string? Via;
}

public sealed class ParsedObject
{
    public string Type = "";
    public string Name = "";
    public string? Target;
    public string? Extends;
    public string? Header;
    public string Props = "";
    public List<KeyValuePair<string, string>> RootProps = [];
    public List<ParsedMethod> Methods = [];
    public List<ParsedMember> Members = [];
    public List<ParsedRef> Refs = [];
}

public static class XmlObjectParser
{
    const string XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    sealed class Frame
    {
        public string Local = "";
        public string? Name;
        public string? IType;
        public int Line;
        public bool HasChildren;
        public string? Text;
        public int TextLine;
        public Dictionary<string, string>? Leafs;
        public List<string>? ChildNames;
        public int MemberIndex = -1;
        public int Seq;
        public List<string>? RelationPairs;

        public string? Leaf(string k) => Leafs != null && Leafs.TryGetValue(k, out var v) ? v : null;
    }

    sealed record CodeBlock(string Owner, string Name, string Text, int Line, bool IsDeclaration);

    static readonly HashSet<string> RefProps = new(StringComparer.Ordinal)
    {
        "ExtendedDataType", "EnumType", "Extends", "RelatedTable", "Table", "ReferenceTable", "Object", "ObjectName",
        "MenuItemName", "Query", "ConfigurationKey", "Form", "FormName", "Class", "ClassName", "DataEntity",
        "StagingTable", "Menu", "MenuName", "TableName", "RelatedDataEntity", "Report", "ReportName", "LookupFormName",
        "View", "Map", "Duty", "Privilege", "Role", "SecurityPolicy", "Workflow", "WorkflowType", "Tile", "Kpi",
    };

    static readonly HashSet<string> DataSourceElements = new(StringComparer.Ordinal)
    {
        "AxFormDataSource", "AxQuerySimpleRootDataSource", "AxQuerySimpleEmbeddedDataSource",
        "AxQueryCompositeRootDataSource", "AxQueryCompositeEmbeddedDataSource", "AxFormReferenceDataSource",
    };

    static readonly HashSet<string> FieldElements = new(StringComparer.Ordinal)
    {
        "AxTableField", "AxViewField", "AxDataEntityViewField", "AxMapBaseField", "AxAggregateDataEntityField",
    };

    static readonly HashSet<string> ControlElements = new(StringComparer.Ordinal) { "AxFormControl", "FormControl" };

    static readonly HashSet<string> SystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "RecId", "RecVersion", "DataAreaId", "Partition", "TableId", "ModifiedBy", "ModifiedDateTime", "CreatedBy",
        "CreatedDateTime", "ModifiedTransactionId", "CreatedTransactionId", "SysRowVersion", "InstanceRelationType",
    };

    static readonly HashSet<string> IgnoredBaseTypes = new(StringComparer.OrdinalIgnoreCase) { "common", "FormRun", "Object" };

    static string? ShortType(string? itype, string element)
    {
        if (itype == null) return null;
        var s = itype;
        foreach (var prefix in new[] { element, "AxForm", "AxTableField", "AxViewField", "AxDataEntityView", "AxMapBaseField", "Ax" })
            if (s.Length > prefix.Length && s.StartsWith(prefix, StringComparison.Ordinal)) { s = s[prefix.Length..]; break; }
        if (s.EndsWith("Control", StringComparison.Ordinal) && s.Length > 7) s = s[..^7];
        return s;
    }

    static bool IsIdentifier(string v)
    {
        if (v.Length == 0 || !(char.IsLetter(v[0]) || v[0] == '_')) return false;
        foreach (var ch in v) if (!(char.IsLetterOrDigit(ch) || ch == '_')) return false;
        return true;
    }

    static int CountLines(string s)
    {
        int c = 0;
        foreach (var ch in s) if (ch == '\n') c++;
        return c;
    }

    public static ParsedObject Parse(string path, ParseMode mode)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Parse(fs, Path.GetFileNameWithoutExtension(path), mode);
    }

    static readonly XmlReaderSettings Settings = new()
    {
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        DtdProcessing = DtdProcessing.Ignore,
        CloseInput = false,
    };

    public static ParsedObject Parse(Stream stream, string fallbackName, ParseMode mode)
    {
        using var r = XmlReader.Create(stream, Settings);
        return Parse(r, fallbackName, mode, null);
    }

    /// <summary>
    /// Parses XML already in memory. <paramref name="analyze"/> limits code analysis (full mode) to the method
    /// bodies it accepts — the declaration is always analysed so member variables still resolve.
    /// </summary>
    public static ParsedObject ParseText(string xml, string fallbackName, ParseMode mode, Func<string, bool>? analyze = null)
    {
        using var r = XmlReader.Create(new StringReader(xml), Settings);
        return Parse(r, fallbackName, mode, analyze);
    }

    static ParsedObject Parse(XmlReader r, string fallbackName, ParseMode mode, Func<string, bool>? analyze)
    {
        bool full = mode is ParseMode.Full or ParseMode.Render;
        var obj = new ParsedObject();
        var blocks = new List<CodeBlock>();
        var stack = new List<Frame>(32);
        var dsTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var controlVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pendingFieldRefs = new List<(string Ds, string Field, string Via, int Line)>();
        int seq = 0;
        var li = (IXmlLineInfo)r;

        string Describe(Frame f) => f.Name != null ? $"{f.Local}:{f.Name}" : f.Local;

        void AddMeta(Frame f, string? target, string? member, string via)
        {
            if (!full || string.IsNullOrEmpty(target)) return;
            obj.Refs.Add(new ParsedRef { Line = f.Line, Kind = "meta", Target = target, Member = member, Via = via });
        }

        void LeafRefs(Frame f)
        {
            if (!full || f.Leafs == null) return;
            foreach (var (k, v) in f.Leafs)
            {
                if (v.Length > 3 && v[0] == '@' && CodeAnalyzer.LabelRx().IsMatch(v))
                    obj.Refs.Add(new ParsedRef { Line = f.Line, Kind = "label", Target = v, Via = $"{Describe(f)}.{k}" });
                else if (RefProps.Contains(k) && IsIdentifier(v))
                    AddMeta(f, v, null, $"{Describe(f)}.{k}");
            }
        }

        string OwnerPath(int upTo)
        {
            var parts = new List<string>();
            for (int k = 1; k < upTo; k++)
            {
                var fr = stack[k];
                if (fr.Name != null && fr.Local is not ("Method" or "Methods" or "SourceCode"))
                    parts.Add($"{fr.Local}:{fr.Name}");
            }
            return string.Join("/", parts);
        }

        string? EnclosingDataSourceTable()
        {
            for (int k = stack.Count - 1; k >= 0; k--)
                if (DataSourceElements.Contains(stack[k].Local))
                    return stack[k].Leaf("Table") ?? stack[k].Name;
            return null;
        }

        int ControlDepth()
        {
            int d = 0;
            foreach (var fr in stack) if (ControlElements.Contains(fr.Local)) d++;
            return d;
        }

        static bool IsMenuElement(Frame fr) => fr.IType != null && fr.IType.StartsWith("AxMenuElement", StringComparison.Ordinal);

        // Path of the frame being closed: its tree ancestors (still on the stack) plus itself.
        string TreePath(Frame f, Func<Frame, bool> inTree) =>
            string.Join("/", stack.Where(fr => inTree(fr) && fr.Name != null).Select(fr => fr.Name).Append(f.Name));

        void AddMember(Frame f, string kind, string info, Frame? parent)
        {
            if (f.Name == null) return;
            int idx = obj.Members.Count;
            obj.Members.Add(new ParsedMember { Kind = kind, Name = f.Name, Info = info.Trim() });
            if (parent != null && parent.Local == "AxFormExtensionControl") parent.MemberIndex = idx;
        }

        void Complex(Frame f, Frame parent)
        {
            var parts = new List<string>();
            void P(string key, string? label = null)
            {
                var v = f.Leaf(key);
                if (!string.IsNullOrEmpty(v)) parts.Add(label == null ? v : $"{label}={v}");
            }

            if (FieldElements.Contains(f.Local))
            {
                var t = ShortType(f.IType, f.Local);
                if (t != null) parts.Add(t);
                P("ExtendedDataType", "edt");
                P("EnumType", "enum");
                if (f.Leaf("DataSource") is { } ds && f.Leaf("DataField") is { } df) parts.Add($"{ds}.{df}");
                P("DataMethod", "method");
                P("StringSize", "size");
                P("Label", "label");
                if (f.Leaf("Mandatory") == "Yes") parts.Add("mandatory");
                if (f.Leaf("AllowEdit") == "No") parts.Add("noedit");
                if (f.Leaf("IsComputedField") == "Yes") parts.Add("computed");
                AddMember(f, "field", string.Join(" ", parts), parent);
            }
            else if (f.Local is "AxTableIndex" or "AxViewIndex" or "AxDataEntityViewKey")
            {
                if (f.Leaf("AllowDuplicates") != "Yes") parts.Add("unique");
                if (f.Leaf("AlternateKey") == "Yes") parts.Add("AK");
                AddMember(f, "index", $"({string.Join(", ", f.ChildNames ?? [])}) {string.Join(" ", parts)}", parent);
            }
            else if (f.Local is "AxTableRelation" or "AxDataEntityViewRelation")
            {
                P("RelatedTable");
                P("RelatedDataEntity");
                P("Cardinality", "card");
                P("RelatedTableCardinality", "relCard");
                if (f.ChildNames is { Count: > 0 }) parts.Add($"on {string.Join(", ", f.ChildNames)}");
                AddMember(f, "relation", string.Join(" ", parts), parent);
            }
            else if (f.Local == "AxTableFieldGroup")
            {
                if (f.ChildNames is { Count: > 0 }) AddMember(f, "fieldgroup", string.Join(", ", f.ChildNames), parent);
            }
            else if (f.Local == "AxEnumValue")
            {
                P("Value");
                P("Label", "label");
                AddMember(f, "enumvalue", string.Join(" ", parts), parent);
            }
            else if (DataSourceElements.Contains(f.Local))
            {
                var table = f.Leaf("Table");
                if (f.Name != null && table != null) dsTables[f.Name] = table;
                if (table != null) parts.Add(table);
                P("JoinSource", "join");
                P("LinkType", "link");
                P("JoinMode", "joinMode");
                if (f.Leaf("UseRelations") == "Yes") parts.Add("useRelations");
                if (f.RelationPairs is { Count: > 0 }) parts.Add($"on {string.Join(", ", f.RelationPairs)}");
                if (f.Name != null)
                    obj.Members.Add(new ParsedMember
                    {
                        Kind = "datasource", Name = f.Name, Info = string.Join(" ", parts).Trim(),
                        Depth = stack.Count(fr => DataSourceElements.Contains(fr.Local)) + 1,
                        Path = TreePath(f, fr => DataSourceElements.Contains(fr.Local)),
                        Order = f.Seq,
                    });
            }
            else if (f.Local.EndsWith("DataSourceRelation", StringComparison.Ordinal))
            {
                // Query / entity data source join: Field = RelatedField of the enclosing data source.
                if (f.Leaf("Field") is { } a1 && f.Leaf("RelatedField") is { } b1)
                {
                    for (int k = stack.Count - 1; k >= 0; k--)
                        if (DataSourceElements.Contains(stack[k].Local))
                        {
                            var src = f.Leaf("JoinDataSource") is { } jds ? $"{jds}." : "";
                            (stack[k].RelationPairs ??= []).Add($"{a1}={src}{b1}");
                            break;
                        }
                }
            }
            else if (f.Local == "Grant" && parent.Local == "AxSecurityEntryPointReference" && f.Leafs != null)
            {
                var allowed = f.Leafs.Where(kv => kv.Value == "Allow").Select(kv => kv.Key).ToList();
                if (allowed.Count > 0) (parent.Leafs ??= new Dictionary<string, string>(StringComparer.Ordinal)).TryAdd("Grant", string.Join(",", allowed));
            }
            else if (ControlElements.Contains(f.Local))
            {
                var t = ShortType(f.IType, "AxForm") ?? f.Leaf("Type") ?? "";
                if (f.Leaf("AutoDeclaration") == "Yes" && f.Name != null && f.IType != null)
                    controlVars[f.Name] = "Form" + t + "Control";
                if (full && f.Name != null)
                {
                    parts.Add(t);
                    if (f.Leaf("DataSource") is { } ds)
                    {
                        parts.Add(f.Leaf("DataField") is { } df ? $"{ds}.{df}" : ds);
                        if (f.Leaf("DataField") is { } df2) pendingFieldRefs.Add((ds, df2, $"{Describe(f)}.DataField", f.Line));
                    }
                    if (f.Leaf("ReferenceField") is { } rf)
                    {
                        var rds = f.Leaf("DataSource");
                        parts.Add(rds != null ? $"ref={rds}.{rf}" : $"ref={rf}");
                        if (rds != null) pendingFieldRefs.Add((rds, rf, $"{Describe(f)}.ReferenceField", f.Line));
                    }
                    P("ReplacementFieldGroup", "replGroup");
                    P("DataRelationPath", "relPath");
                    P("DataMethod", "method");
                    P("MenuItemName", "menuitem");
                    P("Text", "text");
                    P("Label", "label");
                    P("Caption", "caption");
                    if (f.Leaf("AutoDeclaration") == "Yes") parts.Add("autodeclare");
                    if (f.Leaf("Visible") == "No") parts.Add("hidden");
                    int idx = obj.Members.Count;
                    obj.Members.Add(new ParsedMember
                    {
                        Kind = "control", Name = f.Name, Info = string.Join(" ", parts.Where(x => x.Length > 0)),
                        Depth = ControlDepth(),
                        Path = TreePath(f, fr => ControlElements.Contains(fr.Local)),
                        Order = f.Seq,
                    });
                    if (parent.Local == "AxFormExtensionControl") parent.MemberIndex = idx;
                }
            }
            else if (f.Local == "AxFormExtensionControl")
            {
                if (f.MemberIndex >= 0 && f.Leaf("Parent") is { } p) obj.Members[f.MemberIndex].Owner = p;
            }
            else if (f.Local == "AxFormDataSourceField" || f.Local.EndsWith("MappedField", StringComparison.Ordinal))
            {
                // handled by the DataField/DataSource pending refs below
            }
            else if (f.Local == "AxSecurityEntryPointReference")
            {
                P("ObjectType");
                P("ObjectName");
                P("Forms");
                P("Grant", "grant");
                AddMember(f, "entrypoint", string.Join(" ", parts), parent);
            }
            else if (IsMenuElement(f) || f.Local.StartsWith("AxMenuElement", StringComparison.Ordinal))
            {
                // <MenuElement i:type="AxMenuElementMenuItem"> in menu extensions, <AxMenuElement… i:type=…> in menus
                var t = ShortType(f.IType ?? f.Local, "AxMenuElement");
                if (t != null) parts.Add(t);
                P("MenuItemType", "type");
                P("MenuItemName", "menuitem");
                P("MenuName", "menu");
                P("Label", "label");
                if (f.Name != null)
                {
                    int idx = obj.Members.Count;
                    obj.Members.Add(new ParsedMember
                    {
                        Kind = "menuitem", Name = f.Name, Info = string.Join(" ", parts),
                        Depth = stack.Count(IsMenuElement) + 1,
                        Path = TreePath(f, IsMenuElement),
                        Order = f.Seq,
                    });
                    if (parent.Local == "AxMenuExtensionElement") parent.MemberIndex = idx;
                }
            }
            else if (f.Local == "AxMenuExtensionElement")
            {
                if (f.MemberIndex >= 0)
                {
                    var mb = obj.Members[f.MemberIndex];
                    if (f.Leaf("Parent") is { } mp) mb.Owner = mp;
                    var pos = new List<string>();
                    if (f.Leaf("PositionType") is { } pt) pos.Add($"position={pt}");
                    if (f.Leaf("PreviousSibling") is { } ps) pos.Add($"after={ps}");
                    if (pos.Count > 0) mb.Info = (mb.Info + " " + string.Join(" ", pos)).Trim();
                }
            }
            else if (f.Local.EndsWith("Reference", StringComparison.Ordinal) && f.Name != null)
            {
                AddMember(f, "reference", f.Local.Replace("AxSecurity", "").Replace("Reference", ""), parent);
                AddMeta(f, f.Name, null, f.Local);
            }

            if (full && f.Leafs != null && f.Leaf("DataField") is { } field)
            {
                var ds = f.Leaf("DataSource");
                if (ds != null && !ControlElements.Contains(f.Local))
                    pendingFieldRefs.Add((ds, field, $"{Describe(f)}.DataField", f.Line));
                else if (ds == null && f.Local == "AxFormDataSourceField" && !SystemFields.Contains(field) && EnclosingDataSourceTable() is { } t)
                    AddMeta(f, t, field, $"{Describe(f)}.DataField");
            }

            LeafRefs(f);

            var childLabel = f.Leaf("Field") is { } a && f.Leaf("RelatedField") is { } b ? $"{a}={b}" : f.Name ?? f.Leaf("DataField");
            if (childLabel != null) (parent.ChildNames ??= []).Add(childLabel);
            else if (f.ChildNames != null && f.Local is "Fields" or "Constraints") (parent.ChildNames ??= []).AddRange(f.ChildNames);
        }

        while (r.Read())
        {
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var f = new Frame { Local = r.LocalName, Line = li.LineNumber, Seq = ++seq };
                    if (r.HasAttributes) f.IType = r.GetAttribute("type", XsiNs);
                    if (stack.Count > 0) stack[^1].HasChildren = true;
                    stack.Add(f);
                    if (r.IsEmptyElement) Close();
                    break;
                }
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                {
                    if (stack.Count == 0) break;
                    var top = stack[^1];
                    if (top.TextLine == 0) top.TextLine = li.LineNumber;
                    top.Text = top.Text == null ? r.Value : top.Text + r.Value;
                    break;
                }
                case XmlNodeType.EndElement:
                    Close();
                    break;
            }
        }

        void Close()
        {
            var f = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var parent = stack.Count > 0 ? stack[^1] : null;

            if (parent == null)
            {
                obj.Type = f.Local;
                obj.Name = f.Name ?? fallbackName;
                if (f.IType != null && f.IType != f.Local)
                    obj.RootProps.Add(new("Type", ShortType(f.IType, f.Local) ?? f.IType));
                if (f.Leafs != null)
                    foreach (var kv in f.Leafs)
                        obj.RootProps.Add(kv);
                LeafRefs(f);
                return;
            }

            if (!f.HasChildren)
            {
                var val = f.Text ?? "";
                if (f.Local == "Name") { parent.Name ??= val; return; }
                if (f.Local == "DataField" && parent.Name == null && parent.Local == "Field") parent.Name = val;

                if (f.Local is "Source" or "Declaration" && parent.Local is "Method" or "SourceCode")
                {
                    var isDecl = f.Local == "Declaration" || string.Equals(parent.Name, "classDeclaration", StringComparison.OrdinalIgnoreCase);
                    var owner = f.Local == "Declaration" ? "" : OwnerPath(stack.Count - 1);
                    var name = f.Local == "Declaration" ? "classDeclaration" : parent.Name ?? "?";
                    blocks.Add(new CodeBlock(owner, name, val, f.TextLine > 0 ? f.TextLine : f.Line, isDecl));
                    return;
                }
                if (val.Length > 0) (parent.Leafs ??= new Dictionary<string, string>(StringComparer.Ordinal)).TryAdd(f.Local, val);
                return;
            }

            Complex(f, parent);
        }

        // ---- object-level facts
        if (obj.Type.EndsWith("Extension", StringComparison.Ordinal) && obj.Name.Contains('.'))
        {
            obj.Target = obj.Name[..obj.Name.IndexOf('.')];
            obj.Refs.Add(new ParsedRef { Line = 1, Kind = "extension", Target = obj.Target, Via = obj.Type });
        }
        foreach (var kv in obj.RootProps)
            if (kv.Key == "Extends" && IsIdentifier(kv.Value)) obj.Extends = kv.Value;

        obj.Props = string.Join("; ", obj.RootProps
            .Where(kv => kv.Value.Length > 0 && kv.Value.Length < 200)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        if (obj.Props.Length > 1000) obj.Props = obj.Props[..1000];

        foreach (var (ds, field, via, line) in pendingFieldRefs)
            if (full)
                obj.Refs.Add(new ParsedRef
                {
                    Line = line, Kind = "meta", Target = dsTables.TryGetValue(ds, out var t) ? t : ds, Member = field, Via = via,
                });

        AnalyzeCode(obj, blocks, mode, dsTables, controlVars, analyze);
        return obj;
    }

    /// <summary>
    /// Standard tier keeps only what answers "who calls / creates / wraps / handles X": calls with a known or
    /// chained receiver, new, intrinsics, CoC, handlers, inheritance. Field reads, types and labels of Microsoft
    /// code would multiply the index size for little use.
    /// </summary>
    static bool KeepInStandard(CodeRef r) => r.Kind switch
    {
        "call" => r.Target != null || r.Via != null,
        "new" or "intrinsic" or "coc" or "handler" or "extends" or "implements" => true,
        _ => false,
    };

    static void AnalyzeCode(ParsedObject obj, List<CodeBlock> blocks, ParseMode mode, Dictionary<string, string> dsTables,
        Dictionary<string, string> controlVars, Func<string, bool>? analyze)
    {
        bool full = mode is ParseMode.Full or ParseMode.Render;
        bool code = full || mode == ParseMode.StandardCode;
        bool isForm = obj.Type == "AxForm";
        var ctx = new CodeContext
        {
            ObjectType = obj.Type,
            ObjectName = obj.Name,
            ThisType = obj.Target ?? obj.Name,
            ElementType = isForm ? obj.Name : null,
            DataSourceTables = dsTables,
        };
        foreach (var kv in controlVars) ctx.ClassVars[kv.Key] = kv.Value;

        var seen = new HashSet<(int, int, string, string?, string?)>();

        foreach (var b in blocks.Where(x => x.IsDeclaration).Concat(blocks.Where(x => !x.IsDeclaration)))
        {
            bool deep = code && (analyze == null || b.IsDeclaration || analyze(b.Text));
            var toks = XppLexer.Tokenize(b.Text, stopAtFirstBrace: !deep);
            var h = CodeAnalyzer.ParseHeader(b.Text, toks);
            var endLine = b.Line + CountLines(b.Text) - (b.Text.EndsWith('\n') ? 1 : 0);
            var m = new ParsedMethod
            {
                Owner = b.Owner,
                Name = b.Name,
                Signature = h.AttributeText.Length > 0 ? $"{h.AttributeText} {h.Header}" : h.Header,
                IsStatic = h.IsStatic,
                IsDeclaration = b.IsDeclaration,
                StartLine = b.Line,
                EndLine = Math.Max(b.Line, endLine),
                Source = full ? b.Text : null,
            };
            int mi = obj.Methods.Count;
            obj.Methods.Add(m);

            var crefs = new List<CodeRef>();
            CodeAnalyzer.AttributeRefs(h, b.Line, crefs, out var extOf);

            if (b.IsDeclaration)
            {
                obj.Header = m.Signature;
                if (h.Extends != null && !IgnoredBaseTypes.Contains(h.Extends))
                {
                    obj.Extends ??= h.Extends;
                    crefs.Add(new CodeRef(b.Line, "extends", h.Extends, null, null));
                }
                foreach (var impl in h.Implements)
                    crefs.Add(new CodeRef(b.Line, "implements", impl, null, null));
                if (extOf is { } e)
                {
                    obj.Target ??= e.Args[0];
                    ctx.ExtensionTarget = e.Args[0];
                    ctx.ThisType = e.Func.ToLowerInvariant() switch
                    {
                        "formdatasourcestr" => "FormDataSource",
                        "formcontrolstr" => "FormControl",
                        "formdatafieldstr" => "FormDataObject",
                        _ => e.Args[0],
                    };
                    if (e.Func.StartsWith("form", StringComparison.OrdinalIgnoreCase)) ctx.ElementType = e.Args[0];
                }
                ctx.Extends = obj.Extends;
                if (code)
                {
                    foreach (var kv in CodeAnalyzer.Declarations(toks, h.BodyTokenStart, crefs, b.Line))
                        ctx.ClassVars[kv.Key] = kv.Value;
                    ctx.MethodName = null;
                    CodeAnalyzer.BodyRefs(ctx, toks, h.BodyTokenStart, b.Line, crefs, null);
                }
            }
            else if (deep)
            {
                ctx.MethodName = b.Name;
                ctx.Owner = b.Owner;
                var savedThis = ctx.ThisType;
                if (isForm && b.Owner.StartsWith("DataSource:", StringComparison.Ordinal))
                    ctx.ThisType = b.Owner.Contains("/Field:") ? "FormDataObject" : "FormDataSource";
                else if (isForm && b.Owner.Length > 0)
                    ctx.ThisType = "FormControl";

                var locals = CodeAnalyzer.Declarations(toks, h.BodyTokenStart, crefs, b.Line);
                if (h.ReturnType != null && !CodeAnalyzer.IsPrimitive(h.ReturnType))
                    crefs.Add(new CodeRef(b.Line + (toks.Count > 0 ? toks[Math.Min(h.BodyTokenStart, toks.Count - 1)].Line : 0), "type", h.ReturnType, null, "return"));
                CodeAnalyzer.BodyRefs(ctx, toks, h.BodyTokenStart, b.Line, crefs, locals);
                ctx.ThisType = savedThis;
            }

            foreach (var cr in crefs)
            {
                if (mode == ParseMode.StandardCode && !KeepInStandard(cr)) continue;
                if (!seen.Add((mi, cr.Line, cr.Kind, cr.Target, cr.Member))) continue;
                obj.Refs.Add(new ParsedRef
                {
                    MethodIndex = mi, Line = cr.Line, Kind = cr.Kind, Target = cr.Target, Member = cr.Member, Via = cr.Via,
                });
            }
        }
    }
}
