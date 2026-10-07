namespace XppLens.Tests;

/// <summary>
/// Builds an index of the sample PackagesLocalDirectory in Fixtures (a "Microsoft" package StdBase and a
/// custom package ContosoCore) in a temporary folder, once for all integration tests.
/// </summary>
public sealed class IndexFixture : IDisposable
{
    /// <summary>The only fixture file with a recent time stamp (see <see cref="QueryTests.Changed_lists_recent_files_only"/>).</summary>
    public const string RecentFile = "ContosoHelper.xml";

    public string Root { get; }
    public Config Config { get; }
    public IndexService Service { get; }
    public Queries Q { get; }

    public IndexFixture()
    {
        Root =Path.Combine(Path.GetTempPath(), "xpplens-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var packages = Path.Combine(Root, "PackagesLocalDirectory");
        CopyDir(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PackagesLocalDirectory"), packages);

        // Old time stamps everywhere (after the 2020 model build in BuildModelResult.xml, before the 2025 project build
        // in BuildProjectResult.xml), one recent file.
        var old = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var f in Directory.EnumerateFiles(packages, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(f, Path.GetFileName(f) == RecentFile ? DateTime.UtcNow.AddMinutes(-5) : old);

        Config = new Config
        {
            PackagesDir = packages,
            IndexPath = Path.Combine(Root, "index", "xpp.db"),
            LabelLanguages = ["en-US"],
            DisplayLanguage = "en-US",
        };
        Service = new IndexService(Config);
        Service.SyncCatalog();
        Service.SyncFullTier(force: true);
        Service.SyncStandardTier(force: true);
        Q = new Queries(Service);
    }

    static void CopyDir(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    public void Dispose()
    {
        Service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // a file still held by the OS — the temp folder is cleaned up later
        }
    }
}

[CollectionDefinition("index")]
public sealed class IndexCollection : ICollectionFixture<IndexFixture>;
