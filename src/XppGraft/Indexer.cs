using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace XppGraft;

public sealed class SyncStats
{
    public int Parsed, Deleted, Unchanged, Labels, Errors;
    public int Changed => Parsed + Deleted + Labels;
    public override string ToString() => $"parsed={Parsed} deleted={Deleted} unchanged={Unchanged} labels={Labels} errors={Errors}";
}

public sealed class WriterLock : IDisposable
{
    readonly Mutex _mutex;
    bool _held;

    public WriterLock(Mutex mutex, TimeSpan timeout)
    {
        _mutex = mutex;
        try
        {
            _held = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            _held = true;
        }
        if (!_held) throw new TimeoutException("index writer lock is held by another xppgraft process");
    }

    public void Dispose()
    {
        if (!_held) return;
        _held = false;
        _mutex.ReleaseMutex();
    }
}

public sealed class Indexer(Config cfg)
{
    /// <summary>
    /// Bumped whenever code analysis changes what the full tier stores (refs, members). A process that finds an
    /// older value in the index re-parses the full tier once, so an upgrade needs no manual rebuild.
    /// 2: chained calls (via "ret:…"), var locals, entity keys / data source joins, entry point grants.
    /// </summary>
    public const int AnalyzerVersion = 2;

    /// <summary>Only the full tier stores method sources (add a package to extraFullModels to promote it).</summary>
    public static bool StoreSources(ModelInfo m) => m.Full;

    public static Mutex CreateWriterMutex(string indexPath, out string name)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(indexPath).ToLowerInvariant())))[..16];
        name = $@"Local\xppgraft-writer-{hash}";
        return new Mutex(false, name);
    }

    /// <summary>
    /// What the standard tier stores: with standardCodeRefs it also keeps call references (see
    /// <see cref="XmlObjectParser"/>), so callers inside Microsoft code are answered from the index.
    /// </summary>
    public static string StandardAnalyzer(Config cfg) => $"{AnalyzerVersion}:{(cfg.StandardCodeRefs ? "calls" : "dictionary")}";

    ParseMode ModeFor(ModelInfo m) => m.Full ? ParseMode.Full : cfg.StandardCodeRefs ? ParseMode.StandardCode : ParseMode.Standard;

    public SyncStats SyncModel(Store s, ModelInfo m, bool force, Func<IDisposable> writeLock, Action<string>? progress = null)
    {
        var mode = ModeFor(m);
        var stats = new SyncStats();

        var db = s.FilesOfModel(m.Id);
        var todo = new List<(string Path, long Size, long Mtime)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Catalog.ObjectFiles(m))
        {
            var d = (f.FullName, f.Length, f.LastWriteTimeUtc.Ticks);
            seen.Add(d.FullName);
            if (!force && db.TryGetValue(d.FullName, out var e) && e.Size == d.Length && e.Mtime == d.Ticks) stats.Unchanged++;
            else todo.Add(d);
        }
        var deletes = db.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value.Id).ToList();

        var ldb = s.FilesOfModel(m.Id, labels: true);
        var labelTodo = new List<(string Path, long Size, long Mtime)>();
        var lseen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Catalog.LabelFiles(m, cfg.LabelLanguages))
        {
            lseen.Add(f.FullName);
            if (!force && ldb.TryGetValue(f.FullName, out var e) && e.Size == f.Length && e.Mtime == f.LastWriteTimeUtc.Ticks) continue;
            labelTodo.Add((f.FullName, f.Length, f.LastWriteTimeUtc.Ticks));
        }
        var labelDeletes = ldb.Keys.Where(k => !lseen.Contains(k)).ToList();

        if (todo.Count == 0 && deletes.Count == 0 && labelTodo.Count == 0 && labelDeletes.Count == 0)
            return stats;

        if (todo.Count > 0)
        {
            using var queue = new BlockingCollection<(string Path, long Size, long Mtime, ParsedObject? Po)>(boundedCapacity: 256);
            var producer = Task.Run(() =>
            {
                try
                {
                    Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, d =>
                    {
                        ParsedObject? po = null;
                        try
                        {
                            po = XmlObjectParser.Parse(d.Path, mode);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref stats.Errors);
                            Log.Warn($"parse {d.Path}: {ex.Message}");
                        }
                        queue.Add((d.Path, d.Size, d.Mtime, po));
                    });
                }
                finally
                {
                    queue.CompleteAdding();
                }
            });

            const int batchSize = 300;
            var buffer = new List<(string Path, long Size, long Mtime, ParsedObject? Po)>(batchSize);
            void Flush()
            {
                using (writeLock())
                using (var w = new Store.Writer(s))
                {
                    foreach (var it in buffer) w.WriteObject(m.Id, it.Path, it.Size, it.Mtime, it.Po, StoreSources(m));
                    w.Commit();
                }
                stats.Parsed += buffer.Count;
                buffer.Clear();
                progress?.Invoke($"{m.Package}/{m.Name}: {stats.Parsed}/{todo.Count}");
            }

            foreach (var item in queue.GetConsumingEnumerable())
            {
                buffer.Add(item);
                if (buffer.Count >= batchSize) Flush();
            }
            if (buffer.Count > 0) Flush();
            producer.GetAwaiter().GetResult();
        }

        using (writeLock())
        using (var w = new Store.Writer(s))
        {
            foreach (var id in deletes) w.DeleteFileId(id);
            foreach (var p in labelDeletes) w.DeleteLabelFile(p);
            foreach (var l in labelTodo)
            {
                try
                {
                    w.WriteLabelFile(m.Id, l.Path, l.Size, l.Mtime);
                }
                catch (Exception ex)
                {
                    stats.Errors++;
                    Log.Warn($"labels {l.Path}: {ex.Message}");
                }
            }
            w.Commit();
        }
        stats.Deleted += deletes.Count + labelDeletes.Count;
        stats.Labels += labelTodo.Count;
        s.Exec("UPDATE models SET indexed_utc=$t WHERE id=$m", ("$t", DateTime.UtcNow.ToString("u")), ("$m", m.Id));
        return stats;
    }

    /// <summary>Compiled-only package: rebuilt as a whole whenever its .xref / .md / resources change.</summary>
    public SyncStats SyncBinaryModel(Store s, ModelInfo m, bool force, Func<IDisposable> writeLock)
    {
        var stats = new SyncStats();
        var fp = BinaryPackage.Fingerprint(m, cfg.LabelLanguages);
        if (!force && s.Scalar("SELECT fingerprint FROM models WHERE id=$id", ("$id", m.Id)) as string == fp)
            return stats;

        var objects = BinaryPackage.ReadObjects(m);
        var labels = BinaryPackage.ReadLabels(m, cfg.LabelLanguages).ToList();
        var xref = BinaryPackage.XrefPath(m.Dir, m.Package);
        var fi = xref != null ? new FileInfo(xref) : null;
        long size = fi?.Length ?? 0, mtime = fi?.LastWriteTimeUtc.Ticks ?? 0;

        using (writeLock())
        {
            s.DeleteModelData(m.Id);
            using (var w = new Store.Writer(s))
            {
                foreach (var o in objects)
                    w.WriteObject(m.Id, BinaryPackage.ObjectPath(m, o.Type, o.Name), size, mtime, o, storeSources: false);
                foreach (var (path, labelFile, lang, list) in labels)
                {
                    var lfi = new FileInfo(path);
                    w.WriteLabels(m.Id, path, labelFile, lang, lfi.Length, lfi.LastWriteTimeUtc.Ticks, list);
                }
                w.Commit();
            }
            s.Exec("UPDATE models SET fingerprint=$fp, indexed_utc=$t WHERE id=$id",
                ("$fp", fp), ("$t", DateTime.UtcNow.ToString("u")), ("$id", m.Id));
        }
        stats.Parsed = objects.Count;
        stats.Labels = labels.Count;
        return stats;
    }

    /// <summary>Re-indexes individual files reported by the watcher (or found stale by a query).</summary>
    public int ReindexPaths(Store s, IEnumerable<string> paths, IReadOnlyList<ModelInfo> models, Func<IDisposable> writeLock)
    {
        var work = new List<(string Path, ModelInfo Model, bool Label, FileInfo? Info)>();
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool isObj = Catalog.IsObjectFile(p), isLabel = Catalog.IsLabelFile(p);
            if (!isObj && !isLabel) continue;
            var model = models
                .Where(m => p.StartsWith(m.Dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .MaxBy(m => m.Dir.Length);
            if (model == null || model.Id == 0) continue;
            if (isObj && Path.GetDirectoryName(Path.GetDirectoryName(p))?.Equals(model.Dir, StringComparison.OrdinalIgnoreCase) != true) continue;

            var fi = new FileInfo(p);
            if (fi.Exists && DateTime.UtcNow - fi.LastWriteTimeUtc < TimeSpan.FromMilliseconds(400))
                Thread.Sleep(400);
            fi.Refresh();

            var table = isLabel ? "label_files" : "files";
            long? size = null, mtime = null;
            foreach (var r in s.Query($"SELECT size, mtime FROM {table} WHERE path=$p", ("$p", p)))
            {
                size = r.GetInt64(0);
                mtime = r.GetInt64(1);
            }
            if (fi.Exists && size == fi.Length && mtime == fi.LastWriteTimeUtc.Ticks) continue;
            if (!fi.Exists && size == null) continue;
            work.Add((p, model, isLabel, fi.Exists ? fi : null));
        }
        if (work.Count == 0) return 0;

        var parsed = work.AsParallel().Select(w =>
        {
            ParsedObject? po = null;
            if (!w.Label && w.Info != null)
            {
                try
                {
                    po = XmlObjectParser.Parse(w.Path, ModeFor(w.Model));
                }
                catch (Exception ex)
                {
                    Log.Warn($"parse {w.Path}: {ex.Message}");
                }
            }
            return (w, po);
        }).ToList();

        using (writeLock())
        using (var wr = new Store.Writer(s))
        {
            foreach (var (w, po) in parsed)
            {
                if (w.Label)
                {
                    if (w.Info == null) wr.DeleteLabelFile(w.Path);
                    else wr.WriteLabelFile(w.Model.Id, w.Path, w.Info.Length, w.Info.LastWriteTimeUtc.Ticks);
                }
                else if (w.Info == null) wr.DeleteFile(w.Path);
                else wr.WriteObject(w.Model.Id, w.Path, w.Info.Length, w.Info.LastWriteTimeUtc.Ticks, po, StoreSources(w.Model));
            }
            wr.Commit();
        }
        return work.Count;
    }
}
