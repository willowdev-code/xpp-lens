using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

namespace XppLens;

/// <summary>
/// The X++ kernel (system) types — QueryBuildDataSource, FormDataObject, Args, Map, xRecord methods, system enums,
/// EDTs and tables — have no XML in PackagesLocalDirectory. The compiler takes them from resources embedded in
/// bin\Microsoft.Dynamics.AX.Metadata.dll:
///   Static.Models.{AxClass|AxTable|AxView|AxEnum|AxEdt}.{Type}_{Name}.xml  — AOT format (fields, enum values…)
///   Static.Models.XppMetadata.{AxClass|AxTable|AxView}.{Type}_{Name}.xml   — full method signatures
/// They are read without loading the assembly and indexed as the compiled-only model "Kernel".
/// </summary>
public static class KernelMetadata
{
    public const string Package = "Kernel";
    public const string Assembly = "Microsoft.Dynamics.AX.Metadata.dll";
    const string Prefix = "Microsoft.Dynamics.AX.Metadata.Static.Models.";
    const string XppPrefix = Prefix + "XppMetadata.";

    public static string? DllPath(string packagesDir)
    {
        var p = Path.Combine(packagesDir, "bin", Assembly);
        return File.Exists(p) ? p : null;
    }

    public static bool IsKernel(ModelInfo m) =>
        m.Binary && m.Package.Equals(Package, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(m.Dir, Assembly));

    public static string Fingerprint(ModelInfo m)
    {
        var fi = new FileInfo(Path.Combine(m.Dir, Assembly));
        return fi.Exists ? $"kernel:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : "kernel:none";
    }

    /// <summary>Embedded resources whose name starts with <paramref name="prefix"/>, as (name, UTF-8 text).</summary>
    internal static IEnumerable<(string Name, string Text)> Resources(string dll, string prefix)
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
            var name = md.GetString(res.Name);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var r = section.GetReader((int)res.Offset, section.Length - (int)res.Offset);
            var len = r.ReadInt32();
            var bytes = r.ReadBytes(len);
            yield return (name, Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
        }
    }

    /// <summary>"…Static.Models.AxClass.AxClass_Args.xml" → (AxClass, Args).</summary>
    static (string Type, string Name)? Split(string resource, string prefix)
    {
        var rest = resource[prefix.Length..];
        if (!rest.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return null;
        rest = rest[..^4];
        int dot = rest.IndexOf('.');
        if (dot < 0) return null;
        var type = rest[..dot];
        var file = rest[(dot + 1)..];
        return file.StartsWith(type + "_", StringComparison.Ordinal) ? (type, file[(type.Length + 1)..]) : null;
    }

    public static List<ParsedObject> ReadObjects(string dll)
    {
        List<(string Name, string Text)> all;
        try
        {
            all = Resources(dll, Prefix).ToList();
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
        {
            Log.Warn($"kernel types: cannot read {dll}: {ex.Message}");
            return [];
        }
        var objects = new Dictionary<string, ParsedObject>(StringComparer.OrdinalIgnoreCase);
        // 1) AOT-format definitions: enum values, EDT properties, table fields, class method names.
        foreach (var (name, text) in all)
        {
            if (name.StartsWith(XppPrefix, StringComparison.Ordinal) || Split(name, Prefix) is not { } key) continue;
            try
            {
                var po = XmlObjectParser.ParseText(text, key.Name, ParseMode.Standard);
                if (po.Name.Length == 0) po.Name = key.Name;
                if (po.Type.Length == 0) po.Type = key.Type;
                po.Props = string.IsNullOrEmpty(po.Props) ? "kernel type (no source)" : "kernel type (no source); " + po.Props;
                objects[$"{key.Type}|{key.Name}"] = po;
            }
            catch (Exception ex)
            {
                Log.Debug($"kernel {name}: {ex.Message}");
            }
        }
        // 2) Compiler metadata: complete method signatures (replace the bare method names from step 1).
        foreach (var (name, text) in all.Where(r => r.Name.StartsWith(XppPrefix, StringComparison.Ordinal)))
        {
            if (Split(name, XppPrefix) is not { } key) continue;
            try
            {
                var k = $"{key.Type}|{key.Name}";
                objects.TryGetValue(k, out var existing);
                if (ApplyCompilerMetadata(key.Type, key.Name, text, existing) is { } po) objects[k] = po;
            }
            catch (Exception ex)
            {
                Log.Debug($"kernel {name}: {ex.Message}");
            }
        }
        return objects.Values.ToList();
    }

    /// <summary>
    /// Adds the methods (with signatures), base class and interfaces of an AxCompilerMetadata document to
    /// <paramref name="existing"/> (or a new object). Null when the document holds nothing of that.
    /// </summary>
    internal static ParsedObject? ApplyCompilerMetadata(string type, string name, string xml, ParsedObject? existing)
    {
        var root = XDocument.Parse(xml).Root;
        if (root == null) return existing;
        var methods = root.Descendants().Where(e => e.Name.LocalName == "Method" && e.Parent?.Name.LocalName == "Methods")
            .Select(ReadMethod).Where(m => m != null).Cast<ParsedMethod>().ToList();
        var extends = Value(root, "Extends");
        var interfaces = root.Elements().Where(e => e.Name.LocalName == "Implements").SelectMany(e => e.Elements())
            .Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
        if (methods.Count == 0 && extends == null && interfaces.Count == 0) return existing;

        var po = existing ?? new ParsedObject { Type = type, Name = name, Props = "kernel type (no source)" };
        if (methods.Count > 0) po.Methods = methods;
        if (extends != null && po.Extends == null)
        {
            po.Extends = extends;
            po.Refs.Add(new ParsedRef { Line = 0, Kind = "extends", Target = extends });
        }
        foreach (var iface in interfaces) po.Refs.Add(new ParsedRef { Line = 0, Kind = "implements", Target = iface });
        return po;
    }

    static string? Value(XElement e, string name)
    {
        var v = e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    static bool Flag(XElement e, string name) => Value(e, name) is { } v && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Kernel type of a parameter or return value: the named type, else the primitive.</summary>
    internal static string TypeText(XElement? t)
    {
        if (t == null) return "void";
        if (Value(t, "TypeName") is { } named) return named;
        return Value(t, "Type") switch
        {
            null or "Void" => "void",
            "String" => "str",
            "Int32" => "int",
            "Int64" => "int64",
            "Real" => "real",
            "Date" => "date",
            "UtcDateTime" => "utcdatetime",
            "Time" => "timeOfDay",
            "Container" => "container",
            "AnyType" => "anytype",
            "Guid" => "guid",
            "Boolean" => "boolean",
            "Record" => "Common",
            "Class" => "Object",
            var other => other,
        };
    }

    static ParsedMethod? ReadMethod(XElement m)
    {
        var name = Value(m, "Name");
        if (name == null) return null;
        var parameters = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Parameters")?.Elements()
            .Where(e => e.Name.LocalName == "AxMethodParameter")
            .Select(p =>
            {
                var text = $"{TypeText(p)} {Value(p, "Name") ?? "_"}";
                return Value(p, "DefaultValue") is { } d ? $"{text} = {d}" : text;
            }) ?? [];
        var mods = new List<string> { (Value(m, "Visibility") ?? "public").ToLowerInvariant() };
        bool isStatic = Flag(m, "IsStatic");
        if (isStatic) mods.Add("static");
        if (Flag(m, "IsFinal")) mods.Add("final");
        if (Flag(m, "IsDelegate")) mods.Add("delegate");
        var ret = TypeText(m.Elements().FirstOrDefault(e => e.Name.LocalName == "ReturnType"));
        return new ParsedMethod
        {
            Name = name,
            IsStatic = isStatic,
            Signature = $"{string.Join(" ", mods)} {ret} {name}({string.Join(", ", parameters)})",
        };
    }
}
