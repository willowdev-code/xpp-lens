using Microsoft.Data.Sqlite;

namespace XppGraft;

public sealed class Store : IDisposable
{
    public const int SchemaVersion = 1;

    public SqliteConnection Conn { get; }
    public string Path { get; }

    /// <summary>True when the index cannot be written (file/folder permissions) — refresh is then skipped.</summary>
    public bool ReadOnly { get; private set; }

    public Store(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 600,
        }.ToString();
        Conn = new SqliteConnection(cs);
        Conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=120000; PRAGMA temp_store=MEMORY; " +
             "PRAGMA cache_size=-262144; PRAGMA mmap_size=2147483648;");
        try
        {
            // A real write: in WAL mode BEGIN IMMEDIATE alone still succeeds without write access.
            var version = Convert.ToInt64(Scalar("PRAGMA user_version") ?? 0L);
            Exec($"PRAGMA user_version = {version}");
        }
        catch (SqliteException ex)
        {
            ReadOnly = true;
            Log.Warn($"index is read-only ({path}): {ex.Message}");
        }
        if (!ReadOnly) EnsureSchema();
    }

    public void Dispose() => Conn.Dispose();

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        var sw = Log.Verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
        using var cmd = Command(sql, args);
        var v = cmd.ExecuteScalar();
        if (sw != null) Log.Debug($"{sw.ElapsedMilliseconds,6} ms scalar  {CodeAnalyzer.Collapse(sql, 110)}");
        return v is DBNull ? null : v;
    }

    public SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public IEnumerable<SqliteDataReader> Query(string sql, params (string Name, object? Value)[] args)
    {
        var sw = Log.Verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
        using var cmd = Command(sql, args);
        using var rd = cmd.ExecuteReader();
        int rows = 0;
        while (rd.Read())
        {
            rows++;
            yield return rd;
        }
        if (sw != null) Log.Debug($"{sw.ElapsedMilliseconds,6} ms {rows,6} rows  {CodeAnalyzer.Collapse(sql, 110)}");
    }

    void EnsureSchema()
    {
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)");
        var ver = Scalar("SELECT value FROM meta WHERE key='schema'") as string;
        if (ver == SchemaVersion.ToString()) return;

        if (ver != null)
        {
            Log.Info($"schema {ver} -> {SchemaVersion}: recreating index");
            foreach (var t in new[] { "refs", "sources", "members", "methods", "objects", "files", "labels", "label_files", "models" })
                Exec($"DROP TABLE IF EXISTS {t}");
        }

        Exec("""
            CREATE TABLE IF NOT EXISTS models(
              id INTEGER PRIMARY KEY, package TEXT NOT NULL COLLATE NOCASE, name TEXT NOT NULL COLLATE NOCASE,
              publisher TEXT, layer INT, tier INT NOT NULL, dir TEXT, fingerprint TEXT, indexed_utc TEXT,
              UNIQUE(package, name));
            CREATE TABLE IF NOT EXISTS files(
              id INTEGER PRIMARY KEY, model_id INT NOT NULL, path TEXT NOT NULL UNIQUE COLLATE NOCASE, size INT, mtime INT);
            CREATE INDEX IF NOT EXISTS ix_files_model ON files(model_id);
            CREATE TABLE IF NOT EXISTS objects(
              id INTEGER PRIMARY KEY, file_id INT NOT NULL, type TEXT NOT NULL, name TEXT NOT NULL COLLATE NOCASE,
              target TEXT COLLATE NOCASE, extends TEXT COLLATE NOCASE, header TEXT, props TEXT);
            CREATE INDEX IF NOT EXISTS ix_objects_file ON objects(file_id);
            CREATE INDEX IF NOT EXISTS ix_objects_name ON objects(name);
            CREATE INDEX IF NOT EXISTS ix_objects_target ON objects(target) WHERE target IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_objects_extends ON objects(extends) WHERE extends IS NOT NULL;
            CREATE TABLE IF NOT EXISTS methods(
              id INTEGER PRIMARY KEY, file_id INT NOT NULL, object_id INT NOT NULL, owner TEXT, name TEXT NOT NULL COLLATE NOCASE,
              sig TEXT, is_static INT, start_line INT, end_line INT);
            CREATE INDEX IF NOT EXISTS ix_methods_file ON methods(file_id);
            CREATE INDEX IF NOT EXISTS ix_methods_object ON methods(object_id);
            CREATE INDEX IF NOT EXISTS ix_methods_name ON methods(name);
            CREATE TABLE IF NOT EXISTS sources(method_id INTEGER PRIMARY KEY, file_id INT NOT NULL, text TEXT);
            CREATE INDEX IF NOT EXISTS ix_sources_file ON sources(file_id);
            CREATE TABLE IF NOT EXISTS members(
              file_id INT NOT NULL, object_id INT NOT NULL, kind TEXT NOT NULL, owner TEXT, name TEXT NOT NULL COLLATE NOCASE, info TEXT);
            CREATE INDEX IF NOT EXISTS ix_members_file ON members(file_id);
            CREATE INDEX IF NOT EXISTS ix_members_object ON members(object_id);
            CREATE INDEX IF NOT EXISTS ix_members_name ON members(name);
            CREATE TABLE IF NOT EXISTS refs(
              file_id INT NOT NULL, object_id INT NOT NULL, method_id INT, line INT, kind TEXT NOT NULL,
              target TEXT COLLATE NOCASE, member TEXT COLLATE NOCASE, via TEXT);
            CREATE INDEX IF NOT EXISTS ix_refs_file ON refs(file_id);
            CREATE INDEX IF NOT EXISTS ix_refs_target ON refs(target, member);
            CREATE INDEX IF NOT EXISTS ix_refs_member ON refs(member) WHERE target IS NULL;
            CREATE INDEX IF NOT EXISTS ix_refs_method ON refs(method_id);
            CREATE TABLE IF NOT EXISTS label_files(
              id INTEGER PRIMARY KEY, model_id INT NOT NULL, path TEXT NOT NULL UNIQUE COLLATE NOCASE, label_file TEXT, lang TEXT,
              size INT, mtime INT);
            CREATE INDEX IF NOT EXISTS ix_label_files_model ON label_files(model_id);
            CREATE TABLE IF NOT EXISTS labels(lf_id INT NOT NULL, label_id TEXT NOT NULL COLLATE NOCASE, lang TEXT, text TEXT);
            CREATE INDEX IF NOT EXISTS ix_labels_id ON labels(label_id);
            CREATE INDEX IF NOT EXISTS ix_labels_lf ON labels(lf_id);
            """);
        Exec("INSERT INTO meta(key, value) VALUES('schema', $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$v", SchemaVersion.ToString()));
    }

    public void SetMeta(string key, string value) =>
        Exec("INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));

    public string? GetMeta(string key) => Scalar("SELECT value FROM meta WHERE key=$k", ("$k", key)) as string;

    public long UpsertModel(ModelInfo m)
    {
        var id = Scalar("""
            INSERT INTO models(package, name, publisher, layer, tier, dir) VALUES($p, $n, $pub, $l, $t, $d)
            ON CONFLICT(package, name) DO UPDATE SET publisher=excluded.publisher, layer=excluded.layer, tier=excluded.tier, dir=excluded.dir
            RETURNING id
            """, ("$p", m.Package), ("$n", m.Name), ("$pub", m.Publisher), ("$l", m.Layer), ("$t", m.Tier), ("$d", m.Dir));
        m.Id = Convert.ToInt64(id);
        return m.Id;
    }

    public Dictionary<string, (long Id, long Size, long Mtime)> FilesOfModel(long modelId, bool labels = false)
    {
        var d = new Dictionary<string, (long, long, long)>(StringComparer.OrdinalIgnoreCase);
        var table = labels ? "label_files" : "files";
        foreach (var r in Query($"SELECT id, path, size, mtime FROM {table} WHERE model_id=$m", ("$m", modelId)))
            d[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2), r.GetInt64(3));
        return d;
    }

    static readonly string[] FileTables = ["refs", "sources", "members", "methods", "objects"];

    public void DeleteModelData(long modelId)
    {
        foreach (var t in FileTables)
            Exec($"DELETE FROM {t} WHERE file_id IN (SELECT id FROM files WHERE model_id=$m)", ("$m", modelId));
        Exec("DELETE FROM files WHERE model_id=$m", ("$m", modelId));
        Exec("DELETE FROM labels WHERE lf_id IN (SELECT id FROM label_files WHERE model_id=$m)", ("$m", modelId));
        Exec("DELETE FROM label_files WHERE model_id=$m", ("$m", modelId));
    }

    public void DropModel(long modelId)
    {
        DeleteModelData(modelId);
        Exec("DELETE FROM models WHERE id=$m", ("$m", modelId));
    }

    /// <summary>Reusable prepared statements for bulk writes inside one transaction.</summary>
    public sealed class Writer : IDisposable
    {
        readonly Store _s;
        readonly SqliteTransaction _tx;
        readonly SqliteCommand _findFile, _insFile, _insObj, _insMethod, _insSource, _insMember, _insRef;
        readonly SqliteCommand[] _delByFile;
        readonly SqliteCommand _findLf, _insLf, _delLabels, _delLf, _insLabel;

        public Writer(Store s)
        {
            _s = s;
            _tx = s.Conn.BeginTransaction();
            SqliteCommand C(string sql, params string[] ps)
            {
                var c = s.Conn.CreateCommand();
                c.Transaction = _tx;
                c.CommandText = sql;
                foreach (var p in ps) c.Parameters.Add(new SqliteParameter(p, null));
                c.Prepare();
                return c;
            }
            _findFile = C("SELECT id FROM files WHERE path=$path", "$path");
            _insFile = C("INSERT INTO files(model_id, path, size, mtime) VALUES($m, $path, $size, $mtime) RETURNING id", "$m", "$path", "$size", "$mtime");
            _insObj = C("INSERT INTO objects(file_id, type, name, target, extends, header, props) VALUES($f, $type, $name, $target, $extends, $header, $props) RETURNING id",
                "$f", "$type", "$name", "$target", "$extends", "$header", "$props");
            _insMethod = C("INSERT INTO methods(file_id, object_id, owner, name, sig, is_static, start_line, end_line) VALUES($f, $o, $owner, $name, $sig, $st, $sl, $el) RETURNING id",
                "$f", "$o", "$owner", "$name", "$sig", "$st", "$sl", "$el");
            _insSource = C("INSERT INTO sources(method_id, file_id, text) VALUES($id, $f, $text)", "$id", "$f", "$text");
            _insMember = C("INSERT INTO members(file_id, object_id, kind, owner, name, info) VALUES($f, $o, $kind, $owner, $name, $info)",
                "$f", "$o", "$kind", "$owner", "$name", "$info");
            _insRef = C("INSERT INTO refs(file_id, object_id, method_id, line, kind, target, member, via) VALUES($f, $o, $mid, $line, $kind, $target, $member, $via)",
                "$f", "$o", "$mid", "$line", "$kind", "$target", "$member", "$via");
            _delByFile = FileTables.Select(t => C($"DELETE FROM {t} WHERE file_id=$f", "$f"))
                .Append(C("DELETE FROM files WHERE id=$f", "$f")).ToArray();
            _findLf = C("SELECT id FROM label_files WHERE path=$path", "$path");
            _insLf = C("INSERT INTO label_files(model_id, path, label_file, lang, size, mtime) VALUES($m, $path, $lf, $lang, $size, $mtime) RETURNING id",
                "$m", "$path", "$lf", "$lang", "$size", "$mtime");
            _delLabels = C("DELETE FROM labels WHERE lf_id=$id", "$id");
            _delLf = C("DELETE FROM label_files WHERE id=$id", "$id");
            _insLabel = C("INSERT INTO labels(lf_id, label_id, lang, text) VALUES($id, $label, $lang, $text)", "$id", "$label", "$lang", "$text");
        }

        static object V(object? v) => v ?? DBNull.Value;

        static object? Run(SqliteCommand c, bool scalar, params object?[] values)
        {
            for (int i = 0; i < values.Length; i++) c.Parameters[i].Value = V(values[i]);
            return scalar ? c.ExecuteScalar() : c.ExecuteNonQuery();
        }

        public void DeleteFile(string path)
        {
            var id = Run(_findFile, true, path);
            if (id is long fid) DeleteFileId(fid);
        }

        public void DeleteFileId(long fid)
        {
            foreach (var c in _delByFile) Run(c, false, fid);
        }

        public void WriteObject(long modelId, string path, long size, long mtime, ParsedObject? po, bool storeSources)
        {
            DeleteFile(path);
            var fid = (long)Run(_insFile, true, modelId, path, size, mtime)!;
            if (po == null) return;

            var oid = (long)Run(_insObj, true, fid, po.Type, po.Name, po.Target, po.Extends, po.Header, po.Props)!;
            var methodIds = new long[po.Methods.Count];
            for (int i = 0; i < po.Methods.Count; i++)
            {
                var m = po.Methods[i];
                methodIds[i] = (long)Run(_insMethod, true, fid, oid, m.Owner, m.Name, m.Signature, m.IsStatic ? 1 : 0, m.StartLine, m.EndLine)!;
                if (storeSources && m.Source != null) Run(_insSource, false, methodIds[i], fid, m.Source);
            }
            foreach (var mb in po.Members)
                Run(_insMember, false, fid, oid, mb.Kind, mb.Owner.Length > 0 ? mb.Owner : null, mb.Name, mb.Info.Length > 0 ? mb.Info : null);
            foreach (var r in po.Refs)
                Run(_insRef, false, fid, oid, r.MethodIndex >= 0 ? methodIds[r.MethodIndex] : null, r.Line, r.Kind, r.Target, r.Member, r.Via);
        }

        public void DeleteLabelFile(string path)
        {
            if (Run(_findLf, true, path) is long id)
            {
                Run(_delLabels, false, id);
                Run(_delLf, false, id);
            }
        }

        public void WriteLabelFile(long modelId, string path, long size, long mtime)
        {
            var fileName = System.IO.Path.GetFileName(path);
            var labelFile = fileName[..fileName.IndexOf('.')];
            var lang = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";

            var labels = new List<KeyValuePair<string, string>>();
            using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == ' ' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    labels.Add(new(line[..eq], line[(eq + 1)..]));
                }
            }
            WriteLabels(modelId, path, labelFile, lang, size, mtime, labels);
        }

        /// <summary>Keys are either full ids ("@SYS123") or keys of <paramref name="labelFile"/>.</summary>
        public void WriteLabels(long modelId, string path, string labelFile, string lang, long size, long mtime,
            IEnumerable<KeyValuePair<string, string>> labels)
        {
            DeleteLabelFile(path);
            var id = (long)Run(_insLf, true, modelId, path, labelFile, lang, size, mtime)!;
            foreach (var (key, text) in labels)
            {
                if (key.Length == 0) continue;
                var labelId = key[0] == '@' ? key : $"@{labelFile}:{key}";
                Run(_insLabel, false, id, labelId, lang, text);
            }
        }

        public void Commit() => _tx.Commit();

        public void Dispose()
        {
            foreach (var c in new[] { _findFile, _insFile, _insObj, _insMethod, _insSource, _insMember, _insRef, _findLf, _insLf, _delLabels, _delLf, _insLabel }.Concat(_delByFile))
                c.Dispose();
            _tx.Dispose();
        }
    }
}
