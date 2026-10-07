using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XppLens;

/// <summary>
/// 'xpplens update': looks up the latest GitHub release and, with --install, downloads its package, checks the
/// SHA-256 digest GitHub publishes for it and runs its install.ps1 against this installation. This is the only
/// network access of xpp-lens and happens only when the command is run.
/// </summary>
public static class Updater
{
    public const string Repository = "willowdev-code/xpp-lens";

    public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? AssetName, string? AssetUrl, string? Sha256, string Notes);

    public static Version Current => typeof(Config).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>"v1.2.10" / "1.2.10" → 1.2.10.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']);
        if (dash > 0) t = t[..dash];
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Reads the JSON of GET /repos/{repo}/releases/latest.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        var n = JsonNode.Parse(json);
        var tag = n?["tag_name"]?.GetValue<string>();
        if (ParseVersion(tag) is not { } version) return null;
        var asset = (n!["assets"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(a => a["name"]?.GetValue<string>() is { } name
                                 && name.StartsWith("xpp-lens-", StringComparison.OrdinalIgnoreCase)
                                 && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var digest = asset?["digest"]?.GetValue<string>();
        return new ReleaseInfo(version, tag!, n["html_url"]?.GetValue<string>() ?? $"https://github.com/{Repository}/releases",
            asset?["name"]?.GetValue<string>(), asset?["browser_download_url"]?.GetValue<string>(),
            digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null,
            n["body"]?.GetValue<string>() ?? "");
    }

    static string StateDir =>
        Environment.GetEnvironmentVariable("XPPLENS_STATE_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xpp-lens");

    static string StateFile => Path.Combine(StateDir, "update.json");

    /// <summary>Result of the last check, shown by 'status' without touching the network.</summary>
    public static string? PendingNotice()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            var n = JsonNode.Parse(File.ReadAllText(StateFile));
            var latest = ParseVersion(n?["latest"]?.GetValue<string>());
            return latest != null && latest > Current
                ? $"update available: {latest} (you have {Current}) — 'xpplens update --install'"
                : null;
        }
        catch
        {
            return null;
        }
    }

    static void SaveState(ReleaseInfo r)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(StateFile, new JsonObject { ["latest"] = r.Version.ToString(), ["checkedUtc"] = DateTime.UtcNow.ToString("o") }.ToJsonString());
        }
        catch (Exception ex)
        {
            Log.Debug($"update state: {ex.Message}");
        }
    }

    static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("xpp-lens", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>The installation folder of the running program: the parent of its 'bin' folder.</summary>
    public static string InstallDir()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && bin.Parent != null ? bin.Parent.FullName : bin.FullName;
    }

    public static async Task<int> Run(bool install)
    {
        using var http = Client();
        ReleaseInfo? latest;
        try
        {
            latest = ParseRelease(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cannot reach GitHub: {ex.Message}");
            return 1;
        }
        if (latest == null)
        {
            Console.Error.WriteLine("the latest release has no readable version tag");
            return 1;
        }
        SaveState(latest);

        Console.WriteLine($"installed: {Current}");
        Console.WriteLine($"latest:    {latest.Version}  {latest.PageUrl}");
        if (latest.Version <= Current)
        {
            Console.WriteLine("you have the latest version.");
            return 0;
        }
        if (!install)
        {
            var notes = latest.Notes.Replace("\r", "").Split('\n').Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)).Take(12).ToList();
            if (notes.Count > 0)
            {
                Console.WriteLine("\nchanges:");
                foreach (var l in notes) Console.WriteLine("  " + l.Trim());
            }
            Console.WriteLine("\ninstall it with: xpplens update --install   (close Claude first; your settings and index are kept)");
            return 0;
        }

        if (latest.AssetUrl == null || latest.AssetName == null)
        {
            Console.Error.WriteLine($"release {latest.Tag} has no xpp-lens-*.zip package - download it from {latest.PageUrl}");
            return 1;
        }
        if (latest.Sha256 == null)
        {
            Console.Error.WriteLine($"GitHub publishes no SHA-256 digest for {latest.AssetName} - not installing an unverified package; download it from {latest.PageUrl}");
            return 1;
        }

        var work = Path.Combine(Path.GetTempPath(), $"xpp-lens-update-{latest.Version}");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, latest.AssetName);
        Console.WriteLine($"downloading {latest.AssetName}…");
        await using (var src = await http.GetStreamAsync(latest.AssetUrl))
        await using (var dst = File.Create(zip))
            await src.CopyToAsync(dst);

        var actual = Sha256Of(zip);
        if (!actual.Equals(latest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"SHA-256 mismatch for {latest.AssetName}: expected {latest.Sha256}, got {actual} - not installing");
            return 1;
        }
        Console.WriteLine($"SHA-256 verified: {actual}");

        var extracted = Path.Combine(work, "package");
        ZipFile.ExtractToDirectory(zip, extracted);
        var script = Directory.EnumerateFiles(extracted, "install.ps1", SearchOption.AllDirectories).FirstOrDefault();
        if (script == null)
        {
            Console.Error.WriteLine("the package has no install.ps1");
            return 1;
        }

        // The installer replaces this program's own files, so it runs in its own window after this process ends.
        var target = InstallDir();
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{script}\" -InstallDir \"{target}\"",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        });
        Console.WriteLine($"the installer continues in a new window (target {target}); restart Claude when it is done.");
        return 0;
    }
}
