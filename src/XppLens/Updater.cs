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
/// <summary>
/// A release version: "1.3.0", or a test release "1.3.0-beta.2". A test release comes before its final version
/// (1.3.0-beta.2 &lt; 1.3.0); its parts after the dash compare as numbers where they are numbers (beta.10 &gt; beta.9).
/// </summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Pre = null) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Pre != null;

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre == null || other.Pre == null) return Pre == null ? (other.Pre == null ? 0 : 1) : -1;
        var a = Pre.Split('.');
        var b = other.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = int.TryParse(a[i], out var ai), bn = int.TryParse(b[i], out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(ReleaseVersion? a, ReleaseVersion? b) => a is not null && a.CompareTo(b) > 0;
    public static bool operator <(ReleaseVersion? a, ReleaseVersion? b) => b is not null && b.CompareTo(a) > 0;
    public static bool operator >=(ReleaseVersion? a, ReleaseVersion? b) => !(a < b);
    public static bool operator <=(ReleaseVersion? a, ReleaseVersion? b) => !(a > b);

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Pre != null ? "-" + Pre : "")}";
}

public static class Updater
{
    public const string Repository = "willowdev-code/xpp-lens";

    /// <param name="Setup">The setup program of the release (xpp-lens-setup-X.Y.Z.exe, from 1.3.0), when it has one.</param>
    public sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string PageUrl, string? AssetName, string? AssetUrl, string? Sha256,
        string Notes, bool Prerelease = false, ReleaseAsset? Setup = null);

    public sealed record ReleaseAsset(string Name, string Url, string? Sha256);

    /// <summary>
    /// The running version, with a test-release suffix ("1.3.0-beta.1") — from the informational version, which keeps
    /// it (the assembly version holds numbers only); "+commit" build metadata is dropped.
    /// </summary>
    public static ReleaseVersion Current { get; } =
        ParseVersion(typeof(Config).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion)
        ?? (typeof(Config).Assembly.GetName().Version is { } v ? new ReleaseVersion(v.Major, v.Minor, Math.Max(0, v.Build)) : new ReleaseVersion(0, 0, 0));

    /// <summary>"v1.2.10" / "1.2.10" → 1.2.10; "v1.3.0-beta.2" → 1.3.0-beta.2; "+…" build metadata is dropped.</summary>
    public static ReleaseVersion? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        if (t.IndexOf('+') is var plus and >= 0) t = t[..plus];
        string? pre = null;
        if (t.IndexOf('-') is var dash and > 0)
        {
            pre = t[(dash + 1)..];
            t = t[..dash];
            if (pre.Length == 0) pre = null;
        }
        return Version.TryParse(t, out var v) ? new ReleaseVersion(v.Major, v.Minor, Math.Max(0, v.Build), pre) : null;
    }

    /// <summary>Reads the JSON of GET /repos/{repo}/releases/latest.</summary>
    public static ReleaseInfo? ParseRelease(string json) => ParseRelease(JsonNode.Parse(json));

    /// <summary>
    /// The newest release in the JSON of GET /repos/{repo}/releases, test releases included (drafts are skipped).
    /// </summary>
    public static ReleaseInfo? NewestRelease(string json) =>
        (JsonNode.Parse(json) as JsonArray)?.OfType<JsonObject>()
            .Where(r => r["draft"]?.GetValue<bool>() != true)
            .Select(r => ParseRelease(r))
            .OfType<ReleaseInfo>()
            .MaxBy(r => r.Version);

    static ReleaseInfo? ParseRelease(JsonNode? n)
    {
        var tag = n?["tag_name"]?.GetValue<string>();
        if (ParseVersion(tag) is not { } version) return null;
        var assets = (n!["assets"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        JsonObject? Find(string prefix, string extension) => assets.FirstOrDefault(a => a["name"]?.GetValue<string>() is { } name
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        static string? Sha(JsonObject? a) => a?["digest"]?.GetValue<string>() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? d[7..].ToLowerInvariant()
            : null;
        var asset = Find("xpp-lens-", ".zip");
        var setup = Find("xpp-lens-setup-", ".exe");
        return new ReleaseInfo(version, tag!, n["html_url"]?.GetValue<string>() ?? $"https://github.com/{Repository}/releases",
            asset?["name"]?.GetValue<string>(), asset?["browser_download_url"]?.GetValue<string>(), Sha(asset),
            n["body"]?.GetValue<string>() ?? "", n["prerelease"]?.GetValue<bool>() ?? version.IsPrerelease,
            setup?["browser_download_url"]?.GetValue<string>() is { } setupUrl
                ? new ReleaseAsset(setup["name"]!.GetValue<string>(), setupUrl, Sha(setup))
                : null);
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

    /// <summary>
    /// The installer renames a running xpplens.exe to *.old (Windows lets a running program be renamed, not replaced);
    /// the next start removes those that are no longer in use.
    /// </summary>
    public static void RemoveLeftovers()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old"))
            {
                try
                {
                    File.Delete(f);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // still running in an older Claude session
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // read-only installation folder
        }
    }

    /// <summary>The installation folder of the running program: the parent of its 'bin' folder.</summary>
    public static string InstallDir()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && bin.Parent != null ? bin.Parent.FullName : bin.FullName;
    }

    /// <param name="restartClaude">Passed to the installer: restart Claude Desktop when done, without asking.</param>
    /// <param name="pre">Also consider test releases (e.g. 1.3.0-beta.1); without it only regular releases count.</param>
    public static async Task<int> Run(bool install, bool restartClaude = false, bool pre = false)
    {
        using var http = Client();
        ReleaseInfo? latest;
        try
        {
            // /releases/latest never returns a test release; the list holds both kinds.
            latest = pre
                ? NewestRelease(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30"))
                : ParseRelease(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest"));
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
        // 'status' reports regular releases only, so a test release found with --pre is not remembered.
        if (!latest.Prerelease) SaveState(latest);

        Console.WriteLine($"installed: {Current}");
        Console.WriteLine($"latest:    {latest.Version}{(latest.Prerelease ? " (test release)" : "")}  {latest.PageUrl}");
        if (latest.Version <= Current)
        {
            Console.WriteLine(pre ? "you have the latest version (test releases included)." : "you have the latest version.");
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
            Console.WriteLine($"\ninstall it with: xpplens update --install{(pre ? " --pre" : "")}   (Claude may stay open; your settings and index are kept)");
            return 0;
        }

        // From 1.3.0 a release has a setup program: it is run silently into this installation. Older releases (and a
        // release without its digest) go the ZIP + install.ps1 way.
        if (latest.Setup is { Sha256: not null } setup)
            return await RunSetup(http, latest, setup, restartClaude);

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
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{script}\" -InstallDir \"{target}\"" + (restartClaude ? " -RestartClaude" : ""),
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        });
        Console.WriteLine($"the installer continues in a new window (target {target}).");
        Console.WriteLine(restartClaude
            ? "Claude Desktop is restarted when it is done."
            : "At the end it offers to restart Claude Desktop; otherwise running sessions keep the old version until restarted.");
        return 0;
    }

    /// <summary>The arguments of a silent update by the setup program into <paramref name="installDir"/>.</summary>
    internal static string SetupArguments(string installDir, bool restartClaude) =>
        $"/SILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{installDir.TrimEnd('\\')}\"" + (restartClaude ? " /RESTARTCLAUDE=1" : "");

    static async Task<int> RunSetup(HttpClient http, ReleaseInfo latest, ReleaseAsset setup, bool restartClaude)
    {
        var work = Path.Combine(Path.GetTempPath(), $"xpp-lens-update-{latest.Version}");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var exe = Path.Combine(work, setup.Name);
        Console.WriteLine($"downloading {setup.Name}…");
        await using (var src = await http.GetStreamAsync(setup.Url))
        await using (var dst = File.Create(exe))
            await src.CopyToAsync(dst);

        var actual = Sha256Of(exe);
        if (!actual.Equals(setup.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"SHA-256 mismatch for {setup.Name}: expected {setup.Sha256}, got {actual} - not installing");
            return 1;
        }
        Console.WriteLine($"SHA-256 verified: {actual}");

        // The setup program renames this running xpplens.exe and copies the new one; settings and index are kept.
        var target = InstallDir();
        Process.Start(new ProcessStartInfo { FileName = exe, Arguments = SetupArguments(target, restartClaude), UseShellExecute = true });
        Console.WriteLine($"the setup program updates {target} in its own window (progress only, no questions).");
        Console.WriteLine(restartClaude
            ? "Claude Desktop is restarted when it is done."
            : "Running Claude sessions keep the old version until restarted; new sessions use the new one.");
        return 0;
    }
}
