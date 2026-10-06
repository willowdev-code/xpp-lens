using System.Collections;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

namespace XppLens;

/// <summary>
/// Compiled-only packages (deployed without Ax* XML) still leave enough behind to be searchable:
///   bin\{Package}_Ax{Type}.md     — BinaryWriter-serialized metadata; the header lists every object name
///   {Package}.xref                — zip; "ElementReferences" = compiler cross references (UTF-16 lines
///                                   "source|target|kind|line|col||tool|sourceModel|targetModel")
///   bin\*.ChainOfCommand.xml      — CoC classes, targets and wrapped methods
///   bin\*.ClassExtends.runtime    — "Class\tBase" lines
///   Resources\{lang}\*.resources.dll — satellite assemblies with the label texts
/// </summary>
public static class BinaryPackage
{
    public const string Marker = "#";

    public static string? XrefPath(string pkgDir, string package)
    {
        var p = Path.Combine(pkgDir, package + ".xref");
        if (File.Exists(p)) return p;
        return Directory.Exists(pkgDir) ? Directory.EnumerateFiles(pkgDir, "*.xref").FirstOrDefault() : null;
    }

    static IEnumerable<string> MdFiles(string pkgDir, string package)
    {
        var bin = Path.Combine(pkgDir, "bin");
        return Directory.Exists(bin) ? Directory.EnumerateFiles(bin, package + "_Ax*.md") : [];
    }

    static IEnumerable<string> ResourceDlls(string pkgDir, IEnumerable<string>? languages)
    {
        var root = Path.Combine(pkgDir, "Resources");
        if (!Directory.Exists(root)) yield break;
        var dirs = languages == null
            ? Directory.EnumerateDirectories(root)
            : languages.Select(l => Path.Combine(root, l)).Where(Directory.Exists);
        foreach (var d in dirs)
            foreach (var f in Directory.EnumerateFiles(d, "*.resources.dll"))
                yield return f;
    }

    public static bool Has(string pkgDir, string package) =>
        XrefPath(pkgDir, package) != null || MdFiles(pkgDir, package).Any() || ResourceDlls(pkgDir, null).Any();

    /// <summary>Pseudo path of an object that has no XML file of its own.</summary>
    public static string ObjectPath(ModelInfo m, string type, string name) =>
        $"{XrefPath(m.Dir, m.Package) ?? m.Dir}{Marker}{type}/{name}";

    public static bool IsPseudoPath(string path) => path.Contains(Marker + "Ax", StringComparison.Ordinal);

    public static string Fingerprint(ModelInfo m, IEnumerable<string> languages)
    {
        long count = 0, bytes = 0, max = 0;
        void Add(string f)
        {
            var fi = new FileInfo(f);
            if (!fi.Exists) return;
            count++;
            bytes += fi.Length;
            max = Math.Max(max, fi.LastWriteTimeUtc.Ticks);
        }
        if (XrefPath(m.Dir, m.Package) is { } x) Add(x);
        foreach (var f in MdFiles(m.Dir, m.Package)) Add(f);
        var bin = Path.Combine(m.Dir, "bin");
        if (Directory.Exists(bin))
            foreach (var f in Directory.EnumerateFiles(bin, "*.runtime").Concat(Directory.EnumerateFiles(bin, "*.ChainOfCommand.xml")))
                Add(f);
        foreach (var f in ResourceDlls(m.Dir, languages)) Add(f);
        return $"{count}:{bytes}:{max}:{string.Join(",", languages)}";
    }

    // ------------------------------------------------------------------ objects

    sealed class Builder
    {
        readonly Dictionary<string, ParsedObject> _objects = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<ParsedObject, Dictionary<string, int>> _methods = [];
        readonly Dictionary<ParsedObject, Dictionary<string, ParsedMember>> _members = [];
        readonly HashSet<(ParsedObject, int, string, string?, string?)> _refs = [];

        public IEnumerable<ParsedObject> Objects => _objects.Values;

        public ParsedObject Get(string type, string name)
        {
            var key = $"{type}|{name}";
            if (_objects.TryGetValue(key, out var o)) return o;
            o = new ParsedObject { Type = type, Name = name, Props = "compiled package (no source)" };
            if (type.EndsWith("Extension", StringComparison.Ordinal) && name.Contains('.'))
            {
                o.Target = name[..name.IndexOf('.')];
                o.Refs.Add(new ParsedRef { Line = 0, Kind = "extension", Target = o.Target, Via = type });
            }
            _objects[key] = o;
            return o;
        }

        public int Method(ParsedObject o, string owner, string name)
        {
            if (!_methods.TryGetValue(o, out var idx)) _methods[o] = idx = new(StringComparer.OrdinalIgnoreCase);
            var key = owner + "|" + name;
            if (idx.TryGetValue(key, out var i)) return i;
            i = o.Methods.Count;
            o.Methods.Add(new ParsedMethod { Owner = owner, Name = name, Signature = $"{name}(…)  [compiled]" });
            idx[key] = i;
            return i;
        }

        public ParsedMember Member(ParsedObject o, string kind, string name)
        {
            if (!_members.TryGetValue(o, out var idx)) _members[o] = idx = new(StringComparer.OrdinalIgnoreCase);
            var key = kind + "|" + name;
            if (idx.TryGetValue(key, out var mb)) return mb;
            mb = new ParsedMember { Kind = kind, Name = name };
            o.Members.Add(mb);
            idx[key] = mb;
            return mb;
        }

        public void Ref(ParsedObject o, int methodIndex, int line, string kind, string? target, string? member, string? via)
        {
            if (string.IsNullOrEmpty(target)) return;
            if (!_refs.Add((o, methodIndex, kind, target, member))) return;
            o.Refs.Add(new ParsedRef { MethodIndex = methodIndex, Line = line, Kind = kind, Target = target, Member = member, Via = via });
        }
    }

    static readonly Dictionary<string, string> CodeKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Classes"] = "AxClass", ["Tables"] = "AxTable", ["Forms"] = "AxForm", ["Views"] = "AxView", ["Maps"] = "AxMap",
        ["Queries"] = "AxQuery", ["DataEntityViews"] = "AxDataEntityView", ["DataEntities"] = "AxDataEntityView",
        ["TableExtensions"] = "AxTableExtension", ["FormExtensions"] = "AxFormExtension", ["Enums"] = "AxEnum",
        ["Reports"] = "AxReport", ["Interfaces"] = "AxClass",
    };

    static string MetaType(string kind) =>
        kind.StartsWith("Edt", StringComparison.Ordinal) && !kind.StartsWith("EdtExtension", StringComparison.Ordinal) ? "AxEdt"
        : kind.StartsWith("EdtExtension", StringComparison.Ordinal) ? "AxEdtExtension"
        : "Ax" + kind;

    /// <summary>"/Forms/F/DataSources/DS/Methods/m" → (AxForm, F, "DataSource:DS", m, null).</summary>
    static (string? Type, string? Name, string Owner, string? Method, string? Field) CodePath(string path)
    {
        var segs = path.TrimStart('/').Split('/');
        if (segs.Length < 2 || segs[0] == "ClrType") return (null, null, "", null, null);
        var type = CodeKinds.TryGetValue(segs[0], out var t) ? t : "Ax" + segs[0].TrimEnd('s');
        string? method = null, field = null;
        var owner = new List<string>();
        for (int i = 2; i + 1 < segs.Length; i += 2)
        {
            var k = segs[i];
            var v = segs[i + 1];
            if (k == "Methods") method = v;
            else if (k == "Fields") field = v;
            else owner.Add($"{(k.EndsWith('s') ? k[..^1] : k)}:{v}");
        }
        return (type, segs[1], string.Join("/", owner), method, field);
    }

    static string BaseName(string name) => name.Contains('.') ? name[..name.IndexOf('.')] : name;

    public static List<ParsedObject> ReadObjects(ModelInfo m)
    {
        var b = new Builder();

        // 1) complete object lists from the metadata headers
        foreach (var md in MdFiles(m.Dir, m.Package))
        {
            var fileName = Path.GetFileNameWithoutExtension(md);
            var type = fileName[(m.Package.Length + 1)..];
            if (!type.StartsWith("Ax", StringComparison.Ordinal)) continue;
            try
            {
                using var br = new BinaryReader(File.OpenRead(md), Encoding.UTF8);
                if (br.BaseStream.Length < 4) continue;
                int count = br.ReadInt32();
                if (count is < 0 or > 200_000) continue;
                for (int i = 0; i < count; i++)
                {
                    var name = br.ReadString();
                    br.ReadInt32();
                    br.ReadInt32();
                    br.ReadInt32();
                    if (name.Length > 0) b.Get(type, name);
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"md header {md}: {ex.Message}");
            }
        }

        // 2) compiler cross references
        if (XrefPath(m.Dir, m.Package) is { } xref)
        {
            try
            {
                using var zip = ZipFile.OpenRead(xref);
                if (zip.GetEntry("ElementReferences") is { } entry)
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.Unicode);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        XrefLine(b, line);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"xref {xref}: {ex.Message}");
            }
        }

        var bin = Path.Combine(m.Dir, "bin");
        if (Directory.Exists(bin))
        {
            // 3) Chain of Command
            foreach (var coc in Directory.EnumerateFiles(bin, "*.ChainOfCommand.xml"))
            {
                try
                {
                    foreach (var ext in XDocument.Load(coc).Descendants("ExtensionClass"))
                    {
                        var name = (string?)ext.Attribute("Name");
                        var target = (string?)ext.Attribute("ExtensionTarget");
                        if (name == null || target == null) continue;
                        var o = b.Get("AxClass", name);
                        o.Target ??= target;
                        b.Ref(o, -1, 0, "coc", target, null, (string?)ext.Attribute("ExtensionTargetType"));
                        foreach (var me in ext.Elements("Method"))
                        {
                            var mi = b.Method(o, "", me.Value);
                            b.Ref(o, mi, 0, "coc", (string?)me.Attribute("ActualExtensionTarget") ?? target, me.Value, "next");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"coc {coc}: {ex.Message}");
                }
            }

            // 4) class hierarchy
            foreach (var rt in Directory.EnumerateFiles(bin, "*.ClassExtends.runtime"))
            {
                foreach (var l in File.ReadLines(rt))
                {
                    var p = l.Split('\t');
                    if (p.Length < 2 || p[0].Length == 0 || p[1].Trim().Length == 0) continue;
                    var o = b.Get("AxClass", p[0].Trim());
                    o.Extends ??= p[1].Trim();
                    b.Ref(o, -1, 0, "extends", o.Extends, null, null);
                }
            }
        }

        foreach (var o in b.Objects)
            if (o.Extends == null && o.Refs.FirstOrDefault(r => r.Kind == "extends") is { } e)
                o.Extends = e.Target;
        return b.Objects.ToList();
    }

    static void XrefLine(Builder b, string line)
    {
        var p = line.Split('|');
        if (p.Length < 9) return;
        string src = p[0], tgt = p[1], kind = p[2], targetModule = p[8];
        int.TryParse(p[3], out var lineNo);
        if (targetModule is "KernelTypeModule" or "ClrModule") return;

        if (src.StartsWith('/'))
        {
            var s = CodePath(src);
            if (s.Type == null || s.Name == null) return;
            var o = b.Get(s.Type, s.Name);
            int mi = s.Method != null ? b.Method(o, s.Owner, s.Method) : -1;
            if (s.Method == null && s.Field != null) return; // class member variable declaration

            var t = CodePath(tgt);
            if (t.Name == null) return;
            var tName = t.Type == "AxTableExtension" ? BaseName(t.Name) : t.Name;
            switch (kind)
            {
                case "MethodCall":
                    b.Ref(o, mi, lineNo, "call", tName, t.Method, null);
                    break;
                case "TypeReference":
                    if (t.Field != null) b.Ref(o, mi, lineNo, "member", tName, t.Field, null);
                    else if (t.Method == null) b.Ref(o, mi, lineNo, "type", tName, null, null);
                    break;
                case "ClassExtended":
                    o.Extends ??= tName;
                    b.Ref(o, -1, 0, "extends", tName, null, null);
                    break;
                case "InterfaceImplementation":
                    b.Ref(o, -1, 0, "implements", tName, null, null);
                    break;
            }
            return;
        }

        // Metadata reference: "Table/T/FieldString/F?ExtendedDataType|EdtString/X|TypeReference|…|Metadata|…"
        int q = src.LastIndexOf('?');
        if (q < 0) return;
        var prop = src[(q + 1)..];
        var segs = src[..q].Split('/');
        if (segs.Length < 2) return;
        var obj = b.Get(MetaType(segs[0]), segs[1]);

        var via = $"{string.Join("/", segs.Skip(2))}.{prop}".TrimStart('/', '.');

        // Label references point at "/Labels/@File:Key".
        string? label = tgt.StartsWith("/Labels/", StringComparison.Ordinal) ? tgt[8..] : null;

        var tSegs = tgt.Split('?')[0].TrimStart('/').Split('/');
        string? target = label == null && tSegs.Length > 1 ? tSegs[1] : null;
        if (target != null && tSegs[0].EndsWith("Extension", StringComparison.Ordinal)) target = BaseName(target);
        string? targetMember = target != null && tSegs.Length > 3 && SubKind(tSegs[2]) == "field" ? tSegs[3] : null;

        ParsedMember? member = null;
        if (segs.Length >= 4)
        {
            var subName = segs[3];
            switch (SubKind(segs[2]))
            {
                case "fieldgroup":
                    member = b.Member(obj, "fieldgroup", subName);
                    break;
                case "field":
                    member = b.Member(obj, "field", subName);
                    if (member.Info.Length == 0) member.Info = FieldType(segs[2]);
                    if (prop == "ExtendedDataType" && target != null && !member.Info.Contains("edt=")) member.Info += $" edt={target}";
                    if (prop == "EnumType" && target != null && !member.Info.Contains("enum=")) member.Info += $" enum={target}";
                    break;
                case "relation":
                    member = b.Member(obj, "relation", subName);
                    if (prop == "RelatedTable" && target != null) member.Info = target;
                    else if (prop == "RelatedField" && segs.Length >= 6 && targetMember != null && !member.Info.Contains(" on "))
                        member.Info += $" on {segs[5]}={targetMember}";
                    break;
                case "index":
                    member = b.Member(obj, "index", subName);
                    break;
                case "enumvalue":
                    member = b.Member(obj, "enumvalue", subName);
                    break;
            }
        }
        if (member != null && label != null && prop == "Label" && !member.Info.Contains("label="))
            member.Info += $" label={label}";
        if (member != null) member.Info = member.Info.Trim();

        if (label != null)
        {
            b.Ref(obj, -1, 0, "label", label, null, via);
            return;
        }
        if (obj.Type == "AxEdt" && prop == "Extends" && target != null) obj.Extends ??= target;
        if (target != null && target != obj.Name)
            b.Ref(obj, -1, 0, "meta", target, targetMember, via);
    }

    /// <summary>"TableFieldString" / "DataEntityViewMappedField" / "TableFieldGroup" / "TableRelation" → member kind.</summary>
    static string? SubKind(string k)
    {
        if (k.StartsWith("EnumValue", StringComparison.Ordinal) || k.EndsWith("EnumValue", StringComparison.Ordinal)) return "enumvalue";
        if (k.Contains("FieldGroup", StringComparison.Ordinal)) return "fieldgroup";
        if (k.Contains("Field", StringComparison.Ordinal) && !k.Contains("Constraint", StringComparison.Ordinal)) return "field";
        if (k.Contains("Relation", StringComparison.Ordinal)) return "relation";
        if (k.Contains("Index", StringComparison.Ordinal)) return "index";
        return null;
    }

    /// <summary>"TableFieldString" → "String", "DataEntityViewMappedField" → "MappedField".</summary>
    static string FieldType(string k)
    {
        int i = k.IndexOf("Field", StringComparison.Ordinal);
        var after = i >= 0 ? k[(i + 5)..] : "";
        return after.Length > 0 ? after : k.Replace("DataEntityView", "").Replace("Table", "");
    }

    // ------------------------------------------------------------------ labels

    public static IEnumerable<(string Path, string LabelFile, string Lang, List<KeyValuePair<string, string>> Labels)> ReadLabels(
        ModelInfo m, IEnumerable<string> languages)
    {
        foreach (var dll in ResourceDlls(m.Dir, languages))
        {
            var lang = Path.GetFileName(Path.GetDirectoryName(dll)) ?? "";
            var fileName = Path.GetFileName(dll);
            var labelFile = fileName[..fileName.IndexOf('.')];
            List<KeyValuePair<string, string>> labels;
            try
            {
                labels = ReadResources(dll).ToList();
            }
            catch (Exception ex)
            {
                Log.Debug($"resources {dll}: {ex.Message}");
                continue;
            }
            yield return (dll, labelFile, lang, labels);
        }
    }

    /// <summary>String resources embedded in a (satellite) assembly, read without loading it.</summary>
    static IEnumerable<KeyValuePair<string, string>> ReadResources(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader == null) yield break;
        var md = pe.GetMetadataReader();
        var dir = pe.PEHeaders.CorHeader.ResourcesDirectory;
        if (dir.Size == 0) yield break;
        var section = pe.GetSectionData(dir.RelativeVirtualAddress);

        foreach (var h in md.ManifestResources)
        {
            var res = md.GetManifestResource(h);
            if (!res.Implementation.IsNil) continue;
            var r = section.GetReader((int)res.Offset, section.Length - (int)res.Offset);
            var len = r.ReadInt32();
            var bytes = r.ReadBytes(len);
            var entries = new List<KeyValuePair<string, string>>();
            using (var rr = new System.Resources.ResourceReader(new MemoryStream(bytes)))
            {
                var e = rr.GetEnumerator();
                while (e.MoveNext())
                {
                    if (e.Key is string key && e.Value is string value)
                        entries.Add(new(key, value));
                }
            }
            foreach (var kv in entries) yield return kv;
        }
    }
}
