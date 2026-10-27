using System.Text.Json;
using System.Text.RegularExpressions;

namespace XppLens;

/// <summary>
/// Claude Code skills shipped in the package's skills folder (one folder per skill), copied to ~/.claude/skills.
/// A file "NAME.pl.md" next to "NAME.md" is the Polish variant: installed as NAME.md when the language is pl, never
/// under its own name. skills-installed.json in the installation folder keeps the language and the SHA-256 of every
/// file xpp-lens wrote: a skill whose files differ from it was changed by the user and is neither overwritten nor
/// removed, and a folder of that name that xpp-lens did not write is left alone unless it equals the package's copy.
/// </summary>
internal sealed partial class Skills(string source, string target, string manifest)
{
    public const string ManifestName = "skills-installed.json";
    public const string DefaultLanguage = "en";

    sealed class Manifest
    {
        public string Language { get; set; } = DefaultLanguage;
        public Dictionary<string, Dictionary<string, string>> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [GeneratedRegex(@"^(?<base>.+)\.(?<lang>[a-z]{2})(?<ext>\.[^.]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VariantPattern();

    public static Skills Default() => new(
        Path.Combine(Updater.InstallDir(), "skills"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills"),
        Path.Combine(Updater.InstallDir(), ManifestName));

    /// <summary>
    /// Copies the package's skills in <paramref name="language"/> (null: the language of the last installation, else
    /// English); returns the report lines (a kept skill starts with "warning:").
    /// </summary>
    public List<string> Install(string? language = null)
    {
        var lines = new List<string>();
        if (!Directory.Exists(source))
        {
            lines.Add($"no skills in this package ({source})");
            return lines;
        }
        var m = Load();
        var lang = (language ?? m.Language).Trim().ToLowerInvariant();
        foreach (var dir in Directory.GetDirectories(source).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            var dest = Path.Combine(target, name);
            var files = Files(dir, lang);
            var fresh = files.ToDictionary(p => p.Key, p => Updater.Sha256Of(p.Value), StringComparer.OrdinalIgnoreCase);
            var known = m.Skills.GetValueOrDefault(name);
            var existed = known != null && Directory.Exists(dest);
            if (existed)
            {
                if (Changed(dest, known!) is { } file)
                {
                    lines.Add($"warning: {name} was changed locally ({file}) - kept, not updated: {dest}");
                    continue;
                }
                if (Same(known!, fresh))
                {
                    lines.Add($"{name}: up to date");
                    continue;
                }
            }
            else if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            {
                // Not written by xpp-lens (a manual copy, or a change kept through an uninstall): taken over only
                // when it is the same as the package's copy.
                if (Changed(dest, fresh) is { } file)
                {
                    lines.Add($"warning: {name} already exists and differs from this package ({file}) - kept, not installed: {dest}");
                    continue;
                }
            }

            // Files of the previous version that the new one no longer has (unchanged, as checked above).
            if (existed)
                foreach (var old in known!.Keys.Where(k => !fresh.ContainsKey(k)))
                    DeleteFile(dest, old);
            foreach (var (rel, from) in files)
            {
                var to = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true);
            }
            lines.Add($"{name}: {(existed ? "updated" : "installed")} ({lang}) -> {dest}");
            m.Skills[name] = fresh;
        }
        m.Language = lang;
        Save(m);
        return lines;
    }

    /// <summary>Removes the skills xpp-lens installed and the user did not change.</summary>
    public List<string> Remove()
    {
        var lines = new List<string>();
        foreach (var (name, known) in Load().Skills.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var dest = Path.Combine(target, name);
            if (!Directory.Exists(dest)) continue;
            if (Changed(dest, known) is { } file)
            {
                lines.Add($"warning: {name} was changed locally ({file}) - left in place: {dest}");
                continue;
            }
            foreach (var f in known.Keys) DeleteFile(dest, f);
            if (Directory.Exists(dest) && !Directory.EnumerateFileSystemEntries(dest).Any()) Directory.Delete(dest);
            lines.Add($"{name}: removed");
        }
        if (File.Exists(manifest)) File.Delete(manifest);
        return lines;
    }

    /// <summary>One line per skill of the package or of the last installation.</summary>
    public List<string> Status()
    {
        var m = Load();
        var names = m.Skills.Keys
            .Concat(Directory.Exists(source) ? Directory.GetDirectories(source).Select(d => Path.GetFileName(d)) : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var name in names)
        {
            var dest = Path.Combine(target, name);
            if (!m.Skills.TryGetValue(name, out var known))
                lines.Add($"{name}: not installed by xpp-lens{(Directory.Exists(dest) ? $" (a folder of that name exists: {dest})" : "")}");
            else if (!Directory.Exists(dest))
                lines.Add($"{name}: removed by the user (the next installation copies it again)");
            else if (Changed(dest, known) is { } file)
                lines.Add($"{name}: changed locally ({file}) - kept, updates skip it");
            else
                lines.Add($"{name}: installed ({m.Language}), unchanged -> {dest}");
        }
        return lines;
    }

    /// <summary>Installed skills the user changed since (kept by updates).</summary>
    public List<string> ChangedLocally() =>
        Load().Skills
            .Where(p => Directory.Exists(Path.Combine(target, p.Key)) && Changed(Path.Combine(target, p.Key), p.Value) != null)
            .Select(p => p.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Files of a skill as installed: relative path → source file, with the variant of <paramref name="lang"/> in
    /// place of its base file, and no variant under its own name.
    /// </summary>
    internal static Dictionary<string, string> Files(string dir, string lang)
    {
        var all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in all.Order(StringComparer.OrdinalIgnoreCase))
        {
            var v = VariantPattern().Match(rel);
            var baseFile = v.Success ? v.Groups["base"].Value + v.Groups["ext"].Value : null;
            if (baseFile != null && all.Contains(baseFile))
            {
                if (v.Groups["lang"].Value.Equals(lang, StringComparison.OrdinalIgnoreCase))
                    result[baseFile] = Path.Combine(dir, rel);
                continue;
            }
            result.TryAdd(rel, Path.Combine(dir, rel));
        }
        return result;
    }

    /// <summary>The first file that is missing or differs from its recorded hash, or null.</summary>
    static string? Changed(string dir, Dictionary<string, string> known)
    {
        foreach (var (file, hash) in known.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path) || !Updater.Sha256Of(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) return file;
        }
        return null;
    }

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v.Equals(p.Value, StringComparison.OrdinalIgnoreCase));

    static void DeleteFile(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        if (File.Exists(path)) File.Delete(path);
        // Empty subfolders left behind (never the skill folder itself here).
        var root = dir.TrimEnd('\\', '/').Length;
        for (var d = Path.GetDirectoryName(path); d != null && d.Length > root; d = Path.GetDirectoryName(d))
        {
            if (!Directory.Exists(d) || Directory.EnumerateFileSystemEntries(d).Any()) break;
            Directory.Delete(d);
        }
    }

    Manifest Load()
    {
        if (!File.Exists(manifest)) return new Manifest();
        var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifest), JsonOpts) ?? new Manifest();
        m.Skills = new(m.Skills.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value, StringComparer.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        return m;
    }

    void Save(Manifest m)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, JsonSerializer.Serialize(m, JsonOpts));
    }
}
