using System.Xml.Linq;

namespace XppGraft;

/// <param name="Binary">Compiled-only package (no Ax* XML): indexed from .xref / bin\*.md / Resources.</param>
public sealed record ModelInfo(string Package, string Name, string Publisher, int Layer, string Dir, bool Full, bool Binary = false)
{
    public long Id { get; set; }

    /// <summary>1 = full (custom source), 0 = standard (source, dictionary only), -1 = compiled-only.</summary>
    public int Tier => Full ? 1 : Binary ? -1 : 0;
}

public static class Catalog
{
    public static List<ModelInfo> Discover(Config cfg)
    {
        var result = new List<ModelInfo>();
        if (string.IsNullOrWhiteSpace(cfg.PackagesDir) || !Directory.Exists(cfg.PackagesDir))
            throw new InvalidOperationException(
                $"packagesDir is not set or does not exist ({(cfg.PackagesDir.Length == 0 ? "(empty)" : cfg.PackagesDir)}). " +
                "Run 'xppgraft detect' or 'xppgraft config --packages-dir <path>'.");

        foreach (var pkgDir in Directory.EnumerateDirectories(cfg.PackagesDir))
        {
            var descDir = Path.Combine(pkgDir, "Descriptor");
            var package = Path.GetFileName(pkgDir);
            if (!Directory.Exists(descDir))
            {
                if (BinaryPackage.Has(pkgDir, package))
                    result.Add(new ModelInfo(package, package, "(compiled)", 0, pkgDir, Full: false, Binary: true));
                continue;
            }
            foreach (var desc in Directory.EnumerateFiles(descDir, "*.xml"))
            {
                try
                {
                    var root = XDocument.Load(desc).Root;
                    if (root == null) continue;
                    string Get(string n) => root.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value ?? "";
                    var name = Get("Name");
                    var publisher = Get("Publisher");
                    int.TryParse(Get("Layer"), out var layer);

                    var dir = Path.Combine(pkgDir, Path.GetFileNameWithoutExtension(desc));
                    if (!Directory.Exists(dir)) dir = Path.Combine(pkgDir, name);
                    bool hasSource = Directory.Exists(dir) && Directory.EnumerateDirectories(dir, "Ax*").Any();
                    if (!hasSource)
                    {
                        // Descriptor present but shipped without XML: index what the compiler left behind.
                        if (BinaryPackage.Has(pkgDir, package) && !result.Any(m => m.Binary && m.Package == package))
                            result.Add(new ModelInfo(package, package, publisher, layer, pkgDir, Full: false, Binary: true));
                        continue;
                    }

                    bool full = cfg.ExtraFullModels.Contains(name, StringComparer.OrdinalIgnoreCase)
                                || cfg.ExtraFullModels.Contains(package, StringComparer.OrdinalIgnoreCase)
                                || (!cfg.ExtraStandardModels.Contains(name, StringComparer.OrdinalIgnoreCase)
                                    && !cfg.ExtraStandardModels.Contains(package, StringComparer.OrdinalIgnoreCase)
                                    && !cfg.StandardPublisherPatterns.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase)));
                    result.Add(new ModelInfo(package, name, publisher, layer, dir, full));
                }
                catch (Exception ex)
                {
                    Log.Warn($"descriptor {desc}: {ex.Message}");
                }
            }
        }
        return result;
    }

    static readonly HashSet<string> InfrastructureDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", ".vs", "Plugins", "StaticMetadata", "InstallationRecords", "GeneratedXppSource", "DataStack", "FormAdaptor",
    };

    /// <summary>Folders in PackagesLocalDirectory that are not represented in the index, with the reason.</summary>
    public static List<(string Package, string Reason)> Unindexed(Config cfg, IReadOnlyCollection<ModelInfo> models)
    {
        var known = new HashSet<string>(models.Select(m => m.Package), StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, string)>();
        foreach (var pkgDir in Directory.EnumerateDirectories(cfg.PackagesDir))
        {
            var package = Path.GetFileName(pkgDir);
            if (known.Contains(package) || InfrastructureDirs.Contains(package) || package.StartsWith('$') || package.StartsWith('.')) continue;
            var parts = new List<string>();
            if (!Directory.Exists(Path.Combine(pkgDir, "Descriptor"))) parts.Add("no Descriptor");
            parts.Add("no Ax* XML");
            parts.Add("no .xref / bin\\*.md");
            if (Directory.Exists(Path.Combine(pkgDir, "Resources"))) parts.Add("has Resources only");
            result.Add((package, string.Join(", ", parts)));
        }
        return result;
    }

    /// <summary>Cheap change detector for a whole package: descriptors + compiled binaries.</summary>
    public static string PackageFingerprint(string packagesDir, string package)
    {
        long max = 0, count = 0, bytes = 0;
        void Add(string dir, string pattern)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern))
            {
                count++;
                bytes += f.Length;
                max = Math.Max(max, f.LastWriteTimeUtc.Ticks);
            }
        }
        var pkgDir = Path.Combine(packagesDir, package);
        Add(Path.Combine(pkgDir, "Descriptor"), "*.xml");
        Add(Path.Combine(pkgDir, "bin"), "*.dll");
        return $"{count}:{bytes}:{max}";
    }

    /// <summary>All object XML files of a model: Model\Ax*\*.xml (one level).</summary>
    public static IEnumerable<FileInfo> ObjectFiles(ModelInfo m)
    {
        foreach (var d in new DirectoryInfo(m.Dir).EnumerateDirectories("Ax*"))
        {
            if (d.Name.Equals("AxLabelFile", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in d.EnumerateFiles("*.xml"))
                yield return f;
        }
    }

    public static IEnumerable<FileInfo> LabelFiles(ModelInfo m, IEnumerable<string> languages)
    {
        var root = Path.Combine(m.Dir, "AxLabelFile", "LabelResources");
        if (!Directory.Exists(root)) yield break;
        foreach (var lang in languages)
        {
            var d = new DirectoryInfo(Path.Combine(root, lang));
            if (!d.Exists) continue;
            foreach (var f in d.EnumerateFiles("*.label.txt"))
                yield return f;
        }
    }

    public static bool IsObjectFile(string path) =>
        path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(Path.GetDirectoryName(path) ?? "") is { } dn
        && dn.StartsWith("Ax", StringComparison.OrdinalIgnoreCase)
        && !dn.Equals("AxLabelFile", StringComparison.OrdinalIgnoreCase);

    public static bool IsLabelFile(string path) =>
        path.EndsWith(".label.txt", StringComparison.OrdinalIgnoreCase)
        && path.Contains(@"\AxLabelFile\LabelResources\", StringComparison.OrdinalIgnoreCase);
}

public static class Log
{
    public static bool Verbose { get; set; } = Environment.GetEnvironmentVariable("XPPGRAFT_VERBOSE") == "1";
    public static void Info(string msg) => Console.Error.WriteLine($"[xppgraft] {msg}");
    public static void Warn(string msg) => Console.Error.WriteLine($"[xppgraft] WARN {msg}");
    public static void Debug(string msg) { if (Verbose) Console.Error.WriteLine($"[xppgraft] {msg}"); }
}
