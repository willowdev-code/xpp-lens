using System.Collections.Concurrent;

namespace XppLens;

/// <summary>
/// Owns the query connection and keeps the index fresh:
/// file watcher on full-tier models + periodic stat scan + package fingerprints for the standard tier.
/// </summary>
public sealed class IndexService : IDisposable
{
    public Config Cfg { get; }
    public List<ModelInfo> Models { get; private set; } = [];

    readonly Store _store;
    readonly Indexer _indexer;
    readonly Mutex _mutex;
    readonly string _mutexName;
    readonly object _gate = new();
    readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FileSystemWatcher> _watchers = [];
    volatile bool _needScan = true;
    DateTime _lastScanUtc = DateTime.MinValue;
    DateTime _lastChangeUtc = DateTime.MinValue;
    int _lastChangeCount;

    public string BackgroundStatus { get; private set; } = "idle";

    [ThreadStatic] static string? _refreshNote;

    /// <summary>Note of the refresh done for the read running on this thread ("[index refreshed: …]").</summary>
    public string LastRefreshNote => _refreshNote ?? "";

    /// <summary>Names for wildcard searches, kept in memory by the MCP server (see <see cref="StartBackground"/>).</summary>
    public NameDirectory Names { get; } = new();

    // Queries run on their own connections, so a slow one (cold disk) does not hold up the others.
    readonly System.Collections.Concurrent.ConcurrentBag<Store> _readers = [];
    const int MaxIdleReaders = 4;

    // MCP server: the periodic rescan runs on a timer instead of inside a query.
    Timer? _rescanTimer;
    bool _background;
    // The MCP server reads the model catalog in the background; queries wait for it (it used to be the lock).
    readonly ManualResetEventSlim _catalogReady = new(false);
    int _namesBuilding;
    bool _disposed;

    public IndexService(Config cfg)
    {
        Cfg = cfg;
        _store = new Store(cfg.IndexPath);
        _indexer = new Indexer(cfg);
        _mutex = Indexer.CreateWriterMutex(cfg.IndexPath, out _mutexName);
    }

    public IDisposable WriteLock() => new WriterLock(_mutex, TimeSpan.FromMinutes(10));

    public void Dispose()
    {
        _disposed = true;
        _rescanTimer?.Dispose();
        foreach (var w in _watchers) w.Dispose();
        lock (_gate)
        {
            while (_readers.TryTake(out var r)) r.Dispose();
            _store.Dispose();
        }
        _mutex.Dispose();
    }

    /// <summary>Discovers models and aligns the models table (tier changes wipe that model's data).</summary>
    public void SyncCatalog()
    {
        lock (_gate)
        {
            Models = Catalog.Discover(Cfg);
            if (_store.ReadOnly)
            {
                foreach (var m in Models)
                    if (_store.Scalar("SELECT id FROM models WHERE package=$p AND name=$n", ("$p", m.Package), ("$n", m.Name)) is { } id)
                        m.Id = Convert.ToInt64(id);
                _catalogReady.Set();
                return;
            }
            var existing = new Dictionary<string, (long Id, int Tier)>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _store.Query("SELECT id, package, name, tier FROM models"))
                existing[$"{r.GetString(1)}|{r.GetString(2)}"] = (r.GetInt64(0), r.GetInt32(3));

            using (WriteLock())
            {
                foreach (var m in Models)
                {
                    var key = $"{m.Package}|{m.Name}";
                    if (existing.TryGetValue(key, out var e))
                    {
                        existing.Remove(key);
                        if (e.Tier != m.Tier)
                        {
                            Log.Info($"tier change for {key}: reindexing");
                            _store.DeleteModelData(e.Id);
                            _store.Exec("UPDATE models SET fingerprint=NULL WHERE id=$id", ("$id", e.Id));
                        }
                    }
                    _store.UpsertModel(m);
                }
                foreach (var gone in existing.Values) _store.DropModel(gone.Id);
            }
            _analyzerStale = _store.GetMeta("analyzer") != Indexer.AnalyzerVersion.ToString();
            _needScan = true;
            _catalogReady.Set();
        }
    }

    /// <summary>The full tier was indexed by an older analyzer: the next full-tier pass re-parses everything once.</summary>
    volatile bool _analyzerStale;

    void MarkAnalyzerCurrent()
    {
        if (!_analyzerStale) return;
        using (WriteLock()) _store.SetMeta("analyzer", Indexer.AnalyzerVersion.ToString());
        _analyzerStale = false;
    }

    public SyncStats SyncFullTier(bool force, Action<string>? progress = null)
    {
        lock (_gate)
        {
            var total = new SyncStats();
            if (_analyzerStale && !force) Log.Info("index was built by an older analyzer: re-parsing custom models once");
            foreach (var m in Models.Where(m => m.Full))
                Add(total, _indexer.SyncModel(_store, m, force || _analyzerStale, WriteLock, progress));
            MarkAnalyzerCurrent();
            _dirty.Clear();
            _lastScanUtc = DateTime.UtcNow;
            _needScan = false;
            if (total.Changed > 0) { _lastChangeUtc = DateTime.UtcNow; _lastChangeCount = total.Changed; }
            return total;
        }
    }

    /// <summary>Standard tier: re-index only packages whose descriptor/binaries fingerprint changed.</summary>
    public SyncStats SyncStandardTier(bool force, Action<string>? progress = null, bool skipIfBusy = false)
    {
        var total = new SyncStats();
        using var stdMutex = new Mutex(false, _mutexName + "-std");
        bool held;
        try
        {
            held = stdMutex.WaitOne(skipIfBusy ? TimeSpan.Zero : Timeout.InfiniteTimeSpan);
        }
        catch (AbandonedMutexException)
        {
            held = true;
        }
        if (!held)
        {
            BackgroundStatus = "standard tier is being synced by another xpplens process";
            return total;
        }
        try
        {
            return SyncStandardTierLocked(force, progress, total);
        }
        finally
        {
            stdMutex.ReleaseMutex();
        }
    }

    SyncStats SyncStandardTierLocked(bool force, Action<string>? progress, SyncStats total)
    {
        using var s = new Store(Cfg.IndexPath);
        foreach (var m in Models.Where(m => m.Binary))
        {
            BackgroundStatus = $"indexing compiled package {m.Package}";
            progress?.Invoke(BackgroundStatus);
            Add(total, _indexer.SyncBinaryModel(s, m, force, WriteLock));
        }
        foreach (var pkg in Models.Where(m => !m.Full && !m.Binary).GroupBy(m => m.Package, StringComparer.OrdinalIgnoreCase))
        {
            // The analyzer is part of the fingerprint: an upgrade re-indexes each package once, and an
            // interrupted run resumes with the packages that are still stale.
            var analyzer = "|" + Indexer.StandardAnalyzer(Cfg);
            var fp = Catalog.PackageFingerprint(Cfg.PackagesDir, pkg.Key) + analyzer;
            var stale = pkg.Select(m => (Model: m, Stored: s.Scalar("SELECT fingerprint FROM models WHERE id=$id", ("$id", m.Id)) as string))
                .Where(x => force || x.Stored != fp).ToList();
            if (stale.Count == 0) continue;
            foreach (var (m, stored) in stale)
            {
                // Same files but another analyzer: every file must be parsed again, not only the changed ones.
                bool reparse = force || stored == null || !stored.EndsWith(analyzer, StringComparison.Ordinal);
                BackgroundStatus = $"indexing standard {m.Package}/{m.Name}{(reparse && !force ? " (new analyzer)" : "")}";
                progress?.Invoke(BackgroundStatus);
                Add(total, _indexer.SyncModel(s, m, reparse, WriteLock, p => { BackgroundStatus = $"indexing standard {p}"; progress?.Invoke(BackgroundStatus); }));
                using (WriteLock())
                    s.Exec("UPDATE models SET fingerprint=$fp WHERE id=$id", ("$fp", fp), ("$id", m.Id));
            }
        }
        BackgroundStatus = "idle";
        return total;
    }

    public SyncStats SyncCompiledPackages(bool force, Action<string>? progress = null)
    {
        using var s = new Store(Cfg.IndexPath);
        var total = new SyncStats();
        foreach (var m in Models.Where(m => m.Binary))
        {
            progress?.Invoke($"compiled package {m.Package}");
            Add(total, _indexer.SyncBinaryModel(s, m, force, WriteLock));
        }
        return total;
    }

    static void Add(SyncStats total, SyncStats s)
    {
        total.Parsed += s.Parsed;
        total.Deleted += s.Deleted;
        total.Unchanged += s.Unchanged;
        total.Labels += s.Labels;
        total.Errors += s.Errors;
    }

    public void StartWatching()
    {
        foreach (var m in Models.Where(m => m.Full))
        {
            try
            {
                var w = new FileSystemWatcher(m.Dir)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                w.Changed += (_, e) => Mark(e.FullPath);
                w.Created += (_, e) => Mark(e.FullPath);
                w.Deleted += (_, e) => Mark(e.FullPath);
                w.Renamed += (_, e) => { Mark(e.OldFullPath); Mark(e.FullPath); };
                w.Error += (_, e) => { _needScan = true; Log.Warn($"watcher {m.Dir}: {e.GetException().Message}"); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                _needScan = true;
                Log.Warn($"cannot watch {m.Dir}: {ex.Message}");
            }
        }
    }

    void Mark(string path)
    {
        if (Catalog.IsObjectFile(path) || Catalog.IsLabelFile(path)) _dirty[path] = 0;
        else if (!Path.HasExtension(path)) _needScan = true;
    }

    /// <summary>
    /// Background work at MCP start: catalog, watchers, first freshness pass, the in-memory names, standard
    /// fingerprints; then a rescan every <see cref="Config.RescanIntervalSeconds"/> on a timer, outside the queries.
    /// </summary>
    public void StartBackground(bool syncCatalog = false)
    {
        _background = true;
        _namesExpected = true;
        Task.Run(() =>
        {
            try
            {
                if (syncCatalog)
                {
                    BackgroundStatus = "reading model catalog";
                    SyncCatalog();
                    StartWatching();
                }
                BackgroundStatus = "checking custom models";
                lock (_gate) RefreshLocked(full: true);
                BackgroundStatus = "reading names";
                BuildNames();
                BackgroundStatus = "idle";
                if (Cfg.IndexStandard && SyncStandardTier(force: false, skipIfBusy: true).Changed > 0) IndexChanged();
            }
            catch (Exception ex)
            {
                BackgroundStatus = "error: " + ex.Message;
                Log.Warn($"background: {ex}");
                // Queries must not wait for what will not come.
                _catalogReady.Set();
                if (!Names.Ready && Volatile.Read(ref _namesBuilding) == 0) _namesExpected = false;
            }
            var every = TimeSpan.FromSeconds(Math.Max(30, Cfg.RescanIntervalSeconds));
            _rescanTimer = new Timer(_ => Rescan(), null, every, every);
        });
    }

    void Rescan()
    {
        if (_disposed || !Monitor.TryEnter(_gate)) return;
        try
        {
            RefreshLocked(full: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"rescan: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Reads the names for wildcard searches on a reader connection (one build at a time).</summary>
    internal void BuildNames()
    {
        if (Interlocked.Exchange(ref _namesBuilding, 1) == 1) return;
        bool failed = false;
        Store? s = null;
        try
        {
            s = RentReader();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Names.Build(s);
            Log.Debug($"names: {Names.Count} in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            failed = true;
            Log.Warn($"names: {ex.Message}");
        }
        finally
        {
            if (s != null) ReturnReader(s);
            Volatile.Write(ref _namesBuilding, 0);
            // Invalidated while reading (the result was dropped): read once more. After an error searches use LIKE.
            if (!failed && _background && !Names.Ready && !_disposed) Task.Run(BuildNames);
            else _namesExpected = false;
        }
    }

    /// <summary>The index changed: wildcard searches wait for the names to be read again in the background.</summary>
    void IndexChanged()
    {
        if (!_background) return;
        _namesExpected = true;
        Names.Invalidate();
        Task.Run(BuildNames);
    }

    // True while the MCP server is (re)reading the names: reading the name indexes is always cheaper than a LIKE
    // over the whole tables, so a wildcard search waits for it instead of scanning.
    volatile bool _namesExpected;

    /// <summary>Names matching a wildcard / substring pattern, or null when the search must use LIKE.</summary>
    public List<string>? MatchNames(NameDirectory.Kind kind, string pattern)
    {
        if (!Names.Ready && _namesExpected)
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!Names.Ready && _namesExpected && wait.Elapsed < TimeSpan.FromMinutes(2)) Thread.Sleep(25);
            CallTiming.AddWait(wait);
        }
        return Names.Match(kind, pattern);
    }

    Store RentReader() => _readers.TryTake(out var s) ? s : Store.OpenReader(Cfg.IndexPath);

    void ReturnReader(Store s)
    {
        if (!_disposed && _readers.Count < MaxIdleReaders) _readers.Add(s);
        else s.Dispose();
    }

    /// <summary>How long a query waits for a refresh another thread is doing before it reads without it.</summary>
    static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs a read against a fresh index. Files changed on disk (seen by the watchers) are indexed first; in the CLI,
    /// where no timer runs, also the periodic rescan. Reads run in parallel on their own connections.
    /// </summary>
    public T Read<T>(Func<Store, T> read)
    {
        _refreshNote = "";
        if (_background && !_catalogReady.IsSet)
        {
            var start = System.Diagnostics.Stopwatch.StartNew();
            _catalogReady.Wait(TimeSpan.FromMinutes(2));
            CallTiming.AddWait(start);
        }
        bool due = !_dirty.IsEmpty || (!_background && (_needScan || DateTime.UtcNow - _lastScanUtc > TimeSpan.FromSeconds(Cfg.RescanIntervalSeconds)));
        if (due && !_store.ReadOnly)
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            if (Monitor.TryEnter(_gate, RefreshWait))
            {
                CallTiming.AddWait(wait);
                var refresh = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    _refreshNote = RefreshLocked(full: !_background);
                }
                finally
                {
                    Monitor.Exit(_gate);
                    CallTiming.AddRefresh(refresh);
                }
            }
            else
            {
                CallTiming.AddWait(wait);
                _refreshNote = "[index is being refreshed in the background — files changed in the last moments may not be in this answer yet]";
            }
        }
        else if (_store.ReadOnly)
        {
            _refreshNote = ReadOnlyNote;
        }
        else if (_background && _lastScanUtc == DateTime.MinValue)
        {
            _refreshNote = "[the server has just started and is still checking custom models for changes — files changed while it was not running may be missing for a moment]";
        }

        var s = RentReader();
        try
        {
            return read(s);
        }
        finally
        {
            ReturnReader(s);
        }
    }

    public bool IndexReadOnly => _store.ReadOnly;

    public string ReadOnlyNote =>
        $"[index is read-only ({Cfg.IndexPath}) — it is NOT being updated, results may be stale. " +
        $"Fix: run 'xpplens config --index-path %LOCALAPPDATA%\\xpp-lens\\index\\xpp.db' and 'xpplens build' as the user running Claude, " +
        "or grant that user write access to the index folder.]";

    /// <param name="full">Also the periodic rescan of all custom model files when it is due (or forced by a watcher error).</param>
    string RefreshLocked(bool full)
    {
        if (_store.ReadOnly) return ReadOnlyNote;
        try
        {
            if (full && (_needScan || DateTime.UtcNow - _lastScanUtc > TimeSpan.FromSeconds(Cfg.RescanIntervalSeconds)))
            {
                _dirty.Clear();
                var total = new SyncStats();
                foreach (var m in Models.Where(m => m.Full))
                    Add(total, _indexer.SyncModel(_store, m, _analyzerStale, WriteLock));
                MarkAnalyzerCurrent();
                _lastScanUtc = DateTime.UtcNow;
                _needScan = false;
                if (total.Changed > 0) { _lastChangeUtc = DateTime.UtcNow; _lastChangeCount = total.Changed; IndexChanged(); }
                return total.Changed > 0 ? $"[index refreshed: {total.Changed} changed file(s)]" : "";
            }
            if (!_dirty.IsEmpty)
            {
                var paths = _dirty.Keys.ToList();
                foreach (var p in paths) _dirty.TryRemove(p, out _);
                var n = _indexer.ReindexPaths(_store, paths, Models, WriteLock);
                if (n > 0) { _lastChangeUtc = DateTime.UtcNow; _lastChangeCount = n; IndexChanged(); }
                return n > 0 ? $"[index refreshed: {n} changed file(s)]" : "";
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"refresh: {ex.Message}");
            return $"[WARN index refresh failed: {ex.Message}]";
        }
        return "";
    }

    /// <summary>Called by queries that are about to read a file directly: re-index it if it changed on disk.</summary>
    public bool EnsureFileFresh(Store s, string path)
    {
        if (_store.ReadOnly) return false;
        var n = _indexer.ReindexPaths(s, [path], Models, WriteLock);
        if (n > 0) IndexChanged();
        return n > 0;
    }

    /// <summary>True when the file on disk differs from what the index holds (then queries parse it live).</summary>
    public static bool IsFileStale(Store s, string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            foreach (var r in s.Query("SELECT size, mtime FROM files WHERE path = $p", ("$p", path)))
                return r.GetInt64(0) != fi.Length || r.GetInt64(1) != fi.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Index overview. Counting all references and labels reads most of the index file — tens of seconds on a cold
    /// disk — so those two counts only come with <paramref name="counts"/>.
    /// </summary>
    public string StatusText(bool counts = false, TableStyle style = TableStyle.Markdown)
    {
        return Read(s =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"xpp-lens {Updater.Current}{(Updater.PendingNotice() is { } notice ? " — " + notice : "")}");
            sb.AppendLine($"config: {Cfg.SourcePath ?? "(defaults)"}");
            sb.AppendLine($"index:  {Cfg.IndexPath} ({TextTable.Num(new FileInfo(Cfg.IndexPath).Length / 1048576)} MB + WAL)");
            if (_store.ReadOnly) sb.AppendLine(ReadOnlyNote);
            sb.AppendLine();

            var tiers = new TextTable("tier", "models", "files", "last indexed", "state").Right(1, 2);
            foreach (var tier in new[] { 1, 0, -1 })
            {
                var q = s.Query("""
                    SELECT COUNT(DISTINCT md.id), COUNT(f.id), MAX(md.indexed_utc)
                    FROM models md LEFT JOIN files f ON f.model_id = md.id WHERE md.tier = $t
                    """, ("$t", tier)).Select(r => (Models: r.GetInt64(0), Files: r.GetInt64(1), Last: r.IsDBNull(2) ? null : r.GetString(2))).First();
                var title = tier switch { 1 => "full (custom code)", 0 => "standard (Microsoft)", _ => "compiled (no source)" };
                var state = tier switch
                {
                    0 when !Cfg.IndexStandard && q.Files == 0 => "off — 'xpplens build --std-only'",
                    0 when q.Models > 0 && q.Files == 0 => "not indexed — 'xpplens build --std-only'",
                    _ when q.Models == 0 => "none",
                    _ => "ok",
                };
                tiers.Row(title, TextTable.Num(q.Models), TextTable.Num(q.Files) + (tier == -1 ? " obj" : ""), LocalTime(q.Last), state);
            }
            sb.Append(tiers.Render(style));
            sb.AppendLine();

            var names = counts ? new[] { "objects", "methods", "members", "sources", "refs", "labels" } : ["objects", "methods", "members", "sources"];
            var sizes = names.Select(t => TextTable.Num(Convert.ToInt64(s.Scalar($"SELECT COUNT(*) FROM {t}")))).ToList();
            if (!counts) { names = [.. names, "refs", "labels"]; sizes.AddRange(["-", "-"]); }
            sb.Append(new TextTable(names).Right(Enumerable.Range(0, names.Length).ToArray()).Row([.. sizes]).Render(style));
            if (!counts) sb.AppendLine("refs and labels are not counted (slow on a cold disk) — 'xpplens status --counts'");
            sb.AppendLine();

            sb.AppendLine($"watchers: {_watchers.Count}, last full-tier scan: {(_lastScanUtc == DateTime.MinValue ? "never" : _lastScanUtc.ToLocalTime().ToString("HH:mm:ss"))}, " +
                          $"last change: {(_lastChangeUtc == DateTime.MinValue ? "-" : $"{_lastChangeUtc.ToLocalTime():HH:mm:ss} ({_lastChangeCount} files)")}");
            sb.AppendLine($"background: {BackgroundStatus}");
            if (_background)
                sb.AppendLine(Names.Ready
                    ? $"names in memory: {TextTable.Num(Names.Count)} (wildcard searches without scanning the index)"
                    : "names in memory: being read");
            // A silent update cannot show the setup program's warning: changed skills are reported here.
            try
            {
                var kept = Skills.Default().ChangedLocally();
                if (kept.Count > 0)
                    sb.AppendLine($"skills changed locally (kept, updates skip them): {string.Join(", ", kept)} — 'xpplens skills'");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
            }
            sb.AppendLine();

            var full = Models.Where(m => m.Full).Select(m => m.Package == m.Name ? m.Name : $"{m.Package}/{m.Name}").ToList();
            sb.AppendLine($"full-tier models ({full.Count}): {string.Join(", ", full)}");
            var compiled = Models.Where(m => m.Binary).Select(m => m.Package).ToList();
            if (compiled.Count > 0)
                sb.AppendLine($"compiled packages ({compiled.Count}, from .xref / bin\\*.md / Resources): {string.Join(", ", compiled)}");
            try
            {
                var missing = Catalog.Unindexed(Cfg, Models);
                if (missing.Count > 0)
                    sb.AppendLine("on disk but NOT indexed: " + string.Join("; ", missing.Select(x => $"{x.Package} ({x.Reason})")));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"cannot list unindexed packages: {ex.Message}");
            }
            return sb.ToString();
        });
    }

    /// <summary>"2026-10-07T09:36:52Z" (as stored) → "2026-10-07 11:36" local; "-" when empty.</summary>
    static string LocalTime(string? utc) =>
        DateTime.TryParse(utc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : utc ?? "-";
}
