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
    public string LastRefreshNote { get; private set; } = "";

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
        foreach (var w in _watchers) w.Dispose();
        _store.Dispose();
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

    /// <summary>Background work at MCP start: catalog, watchers, first freshness pass, standard fingerprints.</summary>
    public void StartBackground(bool syncCatalog = false)
    {
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
                lock (_gate) LastRefreshNote = RefreshLocked();
                if (Cfg.IndexStandard) SyncStandardTier(force: false, skipIfBusy: true);
            }
            catch (Exception ex)
            {
                BackgroundStatus = "error: " + ex.Message;
                Log.Warn($"background: {ex}");
            }
        });
    }

    /// <summary>Runs a read against a fresh index. All reads are serialized in-process.</summary>
    public T Read<T>(Func<Store, T> read)
    {
        lock (_gate)
        {
            LastRefreshNote = RefreshLocked();
            return read(_store);
        }
    }

    public bool IndexReadOnly => _store.ReadOnly;

    public string ReadOnlyNote =>
        $"[index is read-only ({Cfg.IndexPath}) — it is NOT being updated, results may be stale. " +
        $"Fix: run 'xpplens config --index-path %LOCALAPPDATA%\\xpp-lens\\index\\xpp.db' and 'xpplens build' as the user running Claude, " +
        "or grant that user write access to the index folder.]";

    string RefreshLocked()
    {
        if (_store.ReadOnly) return ReadOnlyNote;
        try
        {
            if (_needScan || DateTime.UtcNow - _lastScanUtc > TimeSpan.FromSeconds(Cfg.RescanIntervalSeconds))
            {
                _dirty.Clear();
                var total = new SyncStats();
                foreach (var m in Models.Where(m => m.Full))
                    Add(total, _indexer.SyncModel(_store, m, _analyzerStale, WriteLock));
                MarkAnalyzerCurrent();
                _lastScanUtc = DateTime.UtcNow;
                _needScan = false;
                if (total.Changed > 0) { _lastChangeUtc = DateTime.UtcNow; _lastChangeCount = total.Changed; }
                return total.Changed > 0 ? $"[index refreshed: {total.Changed} changed file(s)]" : "";
            }
            if (!_dirty.IsEmpty)
            {
                var paths = _dirty.Keys.ToList();
                foreach (var p in paths) _dirty.TryRemove(p, out _);
                var n = _indexer.ReindexPaths(_store, paths, Models, WriteLock);
                if (n > 0) { _lastChangeUtc = DateTime.UtcNow; _lastChangeCount = n; }
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
        if (s.ReadOnly) return false;
        var n = _indexer.ReindexPaths(s, [path], Models, WriteLock);
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
    public string StatusText(bool counts = false)
    {
        return Read(s =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"config: {Cfg.SourcePath ?? "(defaults)"}");
            if (_store.ReadOnly) sb.AppendLine(ReadOnlyNote);
            sb.AppendLine($"index: {Cfg.IndexPath} ({new FileInfo(Cfg.IndexPath).Length / 1048576} MB + WAL)");
            foreach (var tier in new[] { 1, 0, -1 })
            {
                var q = s.Query("""
                    SELECT COUNT(DISTINCT md.id), COUNT(f.id), MAX(md.indexed_utc)
                    FROM models md LEFT JOIN files f ON f.model_id = md.id WHERE md.tier = $t
                    """, ("$t", tier)).Select(r => (r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? "-" : r.GetString(2))).First();
                var title = tier switch { 1 => "full tier", 0 => "standard tier", _ => "compiled packages" };
                var unit = tier == -1 ? "objects" : "files";
                sb.AppendLine($"{title}: {q.Item1} models, {q.Item2} {unit}, last indexed {q.Item3}");
                if (tier == 0 && q.Item1 > 0 && q.Item2 == 0)
                    sb.AppendLine(Cfg.IndexStandard
                        ? "  standard models are listed but not indexed yet (first build not run or interrupted): run 'xpplens build --std-only'"
                        : "  standard models are listed but NOT indexed (indexStandard = false): run 'xpplens build --std-only' to index them");
            }
            foreach (var t in counts ? ["objects", "methods", "members", "sources", "refs", "labels"] : new[] { "objects", "methods", "members", "sources" })
                sb.AppendLine($"  {t}: {s.Scalar($"SELECT COUNT(*) FROM {t}")}");
            if (!counts) sb.AppendLine("  refs, labels: not counted (slow on a cold disk) — 'xpplens status --counts'");
            var full = string.Join(", ", Models.Where(m => m.Full).Select(m => m.Package == m.Name ? m.Name : $"{m.Package}/{m.Name}"));
            sb.AppendLine($"full-tier models: {full}");
            var compiled = Models.Where(m => m.Binary).Select(m => m.Package).ToList();
            if (compiled.Count > 0)
                sb.AppendLine($"compiled packages (from .xref / bin\\*.md / Resources, no source): {string.Join(", ", compiled)}");
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
            sb.AppendLine($"watchers: {_watchers.Count}, last full-tier scan: {(_lastScanUtc == DateTime.MinValue ? "never" : _lastScanUtc.ToLocalTime().ToString("HH:mm:ss"))}, " +
                          $"last change: {(_lastChangeUtc == DateTime.MinValue ? "-" : $"{_lastChangeUtc.ToLocalTime():HH:mm:ss} ({_lastChangeCount} files)")}");
            sb.AppendLine($"background: {BackgroundStatus}");
            if (Updater.PendingNotice() is { } notice) sb.AppendLine(notice);
            return sb.ToString();
        });
    }
}
