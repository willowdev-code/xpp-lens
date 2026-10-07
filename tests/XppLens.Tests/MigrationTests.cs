using System.Text.Json.Nodes;

namespace XppLens.Tests;

/// <summary>Taking over an xpp-graft installation: settings, index files and usage log.</summary>
public sealed class MigrationTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "xpplens-migration-" + Guid.NewGuid().ToString("N")[..8]);
    string OldInstall => Path.Combine(_root, "Tools", "xpp-graft");
    string NewConfig => Path.Combine(_root, "Tools", "xpp-lens", "xpplens.json");
    string OldData => Path.Combine(_root, "Local", "xpp-graft");
    string NewData => Path.Combine(_root, "Local", "xpp-lens");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    void OldInstallation(string? indexPath, bool withIndex = true)
    {
        Directory.CreateDirectory(OldInstall);
        File.WriteAllText(Path.Combine(OldInstall, "xppgraft.json"), new JsonObject
        {
            ["packagesDir"] = @"K:\AosService\PackagesLocalDirectory",
            ["indexPath"] = indexPath ?? "",
            ["labelLanguages"] = new JsonArray("en-US", "pl"),
        }.ToJsonString());
        if (!withIndex) return;
        var dir = Path.Combine(OldData, "index");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "xpp.db"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(dir, "xpp.db-wal"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(dir, "xpp.db-shm"), new byte[32]);
        Directory.CreateDirectory(Path.Combine(OldData, "usage"));
        File.WriteAllText(Path.Combine(OldData, "usage", "usage-202610.jsonl"), "{}\n");
    }

    JsonObject ReadNewConfig() => (JsonObject)JsonNode.Parse(File.ReadAllText(NewConfig))!;

    Migration.Result Migrate() => Migration.FromXppGraft(OldInstall, NewConfig, OldData, NewData);

    [Fact]
    public void Default_location_index_and_usage_are_moved_and_config_points_to_them()
    {
        OldInstallation(Path.Combine(OldData, "index", "xpp.db"));
        var r = Migrate();

        Assert.True(r.ConfigWritten);
        Assert.True(r.IndexMoved);
        var newIndex = Path.Combine(NewData, "index", "xpp.db");
        Assert.Equal(4096, new FileInfo(newIndex).Length);
        Assert.Equal(1024, new FileInfo(newIndex + "-wal").Length);
        Assert.True(File.Exists(newIndex + "-shm"));
        Assert.False(File.Exists(Path.Combine(OldData, "index", "xpp.db")));
        Assert.True(File.Exists(Path.Combine(NewData, "usage", "usage-202610.jsonl")));

        var cfg = ReadNewConfig();
        Assert.Equal(newIndex, cfg["indexPath"]!.GetValue<string>());
        Assert.Equal(@"K:\AosService\PackagesLocalDirectory", cfg["packagesDir"]!.GetValue<string>());
        Assert.Equal(["en-US", "pl"], cfg["labelLanguages"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void Empty_index_path_means_the_default_location()
    {
        OldInstallation(indexPath: null);
        var r = Migrate();
        Assert.True(r.IndexMoved);
        Assert.Equal(Path.Combine(NewData, "index", "xpp.db"), ReadNewConfig()["indexPath"]!.GetValue<string>());
    }

    [Fact]
    public void Index_in_a_custom_location_stays_where_it_is()
    {
        var custom = Path.Combine(_root, "Elsewhere", "xpp.db");
        OldInstallation(custom, withIndex: false);
        var r = Migrate();
        Assert.False(r.IndexMoved);
        Assert.Equal(custom, ReadNewConfig()["indexPath"]!.GetValue<string>());
        Assert.Contains(r.Messages, m => m.Contains("stays where it is"));
    }

    [Fact]
    public void Locked_index_is_used_in_place_instead_of_rebuilding()
    {
        var oldIndex = Path.Combine(OldData, "index", "xpp.db");
        OldInstallation(oldIndex);
        Migration.Result r;
        using (new FileStream(oldIndex, FileMode.Open, FileAccess.Read, FileShare.Read))
            r = Migrate();

        Assert.False(r.IndexMoved);
        Assert.True(File.Exists(oldIndex));
        Assert.False(File.Exists(Path.Combine(NewData, "index", "xpp.db")));
        Assert.Equal(oldIndex, ReadNewConfig()["indexPath"]!.GetValue<string>());
        Assert.Contains(r.Messages, m => m.Contains("is in use"));
    }

    [Fact]
    public void Existing_index_in_the_new_folder_is_not_overwritten()
    {
        var oldIndex = Path.Combine(OldData, "index", "xpp.db");
        OldInstallation(oldIndex);
        Directory.CreateDirectory(Path.Combine(NewData, "index"));
        File.WriteAllBytes(Path.Combine(NewData, "index", "xpp.db"), new byte[10]);

        var r = Migrate();
        Assert.False(r.IndexMoved);
        Assert.Equal(10, new FileInfo(Path.Combine(NewData, "index", "xpp.db")).Length);
        Assert.Equal(oldIndex, ReadNewConfig()["indexPath"]!.GetValue<string>());
    }

    [Fact]
    public void Second_run_changes_nothing()
    {
        OldInstallation(Path.Combine(OldData, "index", "xpp.db"));
        Assert.True(Migrate().ConfigWritten);
        var again = Migrate();
        Assert.False(again.ConfigWritten);
        Assert.Contains(again.Messages, m => m.Contains("already exists"));
    }
}

/// <summary>Release lookup and verification for 'xpplens update' (no network).</summary>
public class UpdaterTests
{
    const string Release = """
        {
          "tag_name": "v1.3.0",
          "html_url": "https://github.com/willowdev-code/xpp-lens/releases/tag/v1.3.0",
          "body": "## Changes\n\n- New tool\n- Faster status\n",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://example.invalid/notes.txt" },
            { "name": "xpp-lens-1.3.0.zip", "browser_download_url": "https://example.invalid/xpp-lens-1.3.0.zip",
              "digest": "sha256:ABCDEF0123" }
          ]
        }
        """;

    [Fact]
    public void Versions_compare_numerically()
    {
        Assert.True(Updater.ParseVersion("v1.2.10") > Updater.ParseVersion("1.2.9"));
        Assert.Equal(new Version(1, 2, 2), Updater.ParseVersion("v1.2.2-beta"));
        Assert.Equal(new Version(1, 2, 0), Updater.ParseVersion("1.2"));
        Assert.Null(Updater.ParseVersion("latest"));
    }

    [Fact]
    public void Release_json_gives_package_and_digest()
    {
        var r = Updater.ParseRelease(Release)!;
        Assert.Equal(new Version(1, 3, 0), r.Version);
        Assert.Equal("xpp-lens-1.3.0.zip", r.AssetName);
        Assert.Equal("https://example.invalid/xpp-lens-1.3.0.zip", r.AssetUrl);
        Assert.Equal("abcdef0123", r.Sha256);
        Assert.Contains("- Faster status", r.Notes);
    }

    [Fact]
    public void Release_without_package_has_no_asset()
    {
        var r = Updater.ParseRelease("""{ "tag_name": "v2.0.0", "assets": [] }""")!;
        Assert.Null(r.AssetUrl);
        Assert.Null(r.Sha256);
    }

    [Fact]
    public void Sha256_of_a_file()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Updater.Sha256Of(f));
        }
        finally
        {
            File.Delete(f);
        }
    }

    [Fact]
    public void Pending_notice_comes_from_the_last_check_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "xpplens-state-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("XPPLENS_STATE_DIR", dir);
        try
        {
            Assert.Null(Updater.PendingNotice());
            File.WriteAllText(Path.Combine(dir, "update.json"), """{ "latest": "99.0.0" }""");
            Assert.Contains("update available: 99.0.0", Updater.PendingNotice());
            File.WriteAllText(Path.Combine(dir, "update.json"), """{ "latest": "0.0.1" }""");
            Assert.Null(Updater.PendingNotice());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XPPLENS_STATE_DIR", null);
            Directory.Delete(dir, true);
        }
    }
}
