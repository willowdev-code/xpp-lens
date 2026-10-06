using System.Text.Json;
using System.Text.Json.Serialization;

namespace XppGraft;

public sealed class Config
{
    public string PackagesDir { get; set; } = "";
    public string IndexPath { get; set; } = "";

    /// <summary>Models whose descriptor Publisher contains one of these go to the "standard" (dictionary) tier.</summary>
    public List<string> StandardPublisherPatterns { get; set; } = ["Microsoft"];

    /// <summary>Model names forced into the full tier regardless of publisher.</summary>
    public List<string> ExtraFullModels { get; set; } = [];

    /// <summary>Model names forced into the standard tier (e.g. a huge ISV model you do not modify).</summary>
    public List<string> ExtraStandardModels { get; set; } = [];

    public List<string> LabelLanguages { get; set; } = ["en-US"];

    /// <summary>Label language used when rendering label text inline.</summary>
    public string DisplayLanguage { get; set; } = "en-US";

    /// <summary>Safety-net stat scan of the full tier, on top of the file watcher.</summary>
    public int RescanIntervalSeconds { get; set; } = 300;

    public bool IndexStandard { get; set; } = true;

    /// <summary>Also store call references of standard code (callers inside Microsoft code; about +250 MB).</summary>
    public bool StandardCodeRefs { get; set; } = true;

    /// <summary>Local log of MCP calls (size, time, empty results) read by 'xppgraft stats'. Never leaves the machine.</summary>
    public bool UsageLog { get; set; } = true;

    [JsonIgnore]
    public string? SourcePath { get; set; }

    static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>xppgraft.json next to the executable or in a parent folder (XPPGRAFT_CONFIG wins).</summary>
    public static string DefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("XPPGRAFT_CONFIG");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "xppgraft.json");
            if (File.Exists(p)) return p;
        }
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var root = baseDir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) && baseDir.Parent != null
            ? baseDir.Parent.FullName
            : baseDir.FullName;
        return Path.Combine(root, "xppgraft.json");
    }

    public static Config Load()
    {
        var path = DefaultPath();
        var cfg = File.Exists(path)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path), ReadOpts) ?? new Config()
            : new Config();
        cfg.SourcePath = path;

        // Per-user location by default: the install folder is often writable only by admins.
        if (string.IsNullOrWhiteSpace(cfg.IndexPath))
            cfg.IndexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "xpp-graft", "index", "xpp.db");
        return cfg;
    }

    public void Save(string? path = null)
    {
        path ??= SourcePath ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOpts));
        SourcePath = path;
    }

    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"config file:        {SourcePath}{(File.Exists(SourcePath ?? "") ? "" : "  (not created yet)")}");
        sb.AppendLine($"packagesDir:        {(PackagesDir.Length == 0 ? "(not set — run: xppgraft detect)" : PackagesDir)}");
        sb.AppendLine($"indexPath:          {IndexPath}");
        sb.AppendLine($"labelLanguages:     {string.Join(", ", LabelLanguages)}");
        sb.AppendLine($"displayLanguage:    {DisplayLanguage}");
        sb.AppendLine($"extraFullModels:    {(ExtraFullModels.Count == 0 ? "(none)" : string.Join(", ", ExtraFullModels))}");
        sb.AppendLine($"extraStandardModels:{(ExtraStandardModels.Count == 0 ? "(none)" : string.Join(", ", ExtraStandardModels))}");
        sb.AppendLine($"standardPublishers: {string.Join(", ", StandardPublisherPatterns)}");
        sb.AppendLine($"indexStandard:      {IndexStandard}");
        sb.AppendLine($"standardCodeRefs:   {StandardCodeRefs}");
        sb.AppendLine($"rescanIntervalSec:  {RescanIntervalSeconds}");
        sb.AppendLine($"usageLog:           {UsageLog}  ({Usage.Dir})");
        return sb.ToString();
    }
}
