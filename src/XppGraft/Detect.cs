using System.Xml.Linq;

namespace XppGraft;

public sealed record DetectedDir(string Path, string Source, int Packages);

/// <summary>Finds PackagesLocalDirectory on this machine (AOS web.config first, then well-known layouts).</summary>
public static class Detect
{
    public static List<DetectedDir> PackagesDirs()
    {
        var found = new Dictionary<string, DetectedDir>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? dir, string source)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            dir = dir.TrimEnd('\\', '/');
            if (found.ContainsKey(dir) || !Directory.Exists(dir)) return;
            int packages = CountPackages(dir);
            if (packages >= 3) found[dir] = new DetectedDir(dir, source, packages);
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Network)) continue;
            string root;
            try
            {
                if (!drive.IsReady) continue;
                root = drive.RootDirectory.FullName;
            }
            catch
            {
                continue;
            }

            IEnumerable<string> tops;
            try
            {
                tops = Directory.EnumerateDirectories(root).Take(200).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var top in tops)
            {
                try
                {
                    Consider(Path.Combine(top, "PackagesLocalDirectory"), "folder layout");
                    foreach (var web in new[] { "webroot", "WebRoot" })
                    {
                        var cfg = Path.Combine(top, web, "web.config");
                        if (!File.Exists(cfg)) continue;
                        Consider(ReadAosSetting(cfg, "Aos.MetadataDirectory"), $"{cfg} (Aos.MetadataDirectory)");
                        Consider(ReadAosSetting(cfg, "Aos.PackageDirectory"), $"{cfg} (Aos.PackageDirectory)");
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"detect {top}: {ex.Message}");
                }
            }
        }

        return found.Values.OrderByDescending(d => d.Source.Contains("web.config")).ThenByDescending(d => d.Packages).ToList();
    }

    static string? ReadAosSetting(string webConfig, string key)
    {
        try
        {
            return XDocument.Load(webConfig).Descendants("add")
                .FirstOrDefault(e => (string?)e.Attribute("key") == key)?.Attribute("value")?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A real PackagesLocalDirectory has packages with a Descriptor folder.</summary>
    public static int CountPackages(string dir)
    {
        try
        {
            int n = 0;
            foreach (var p in Directory.EnumerateDirectories(dir))
            {
                var desc = Path.Combine(p, "Descriptor");
                if (Directory.Exists(desc) && Directory.EnumerateFiles(desc, "*.xml").Any()) n++;
                if (n >= 200) break;
            }
            return n;
        }
        catch
        {
            return 0;
        }
    }
}
