using System.Text.Json;
using System.Text.Json.Nodes;

namespace XppLens;

/// <summary>
/// Takes over an installation of xpp-graft (the name up to 1.2.0): its settings become xpplens.json, and the index
/// and usage log in the default per-user folder move to the new one. The index files are moved one by one and checked;
/// when they cannot be moved (in use, or the new folder already holds an index) the new configuration keeps using
/// the old index where it is — never a silent rebuild from scratch.
/// </summary>
public static class Migration
{
    public const string OldConfigName = "xppgraft.json";
    static readonly string[] IndexFiles = ["xpp.db", "xpp.db-wal", "xpp.db-shm"];

    public sealed class Result
    {
        public bool ConfigWritten;
        public string? IndexPath;
        public bool IndexMoved;
        public List<string> Messages { get; } = [];
    }

    public static string DefaultOldDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xpp-graft");
    public static string DefaultNewDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xpp-lens");

    static string Full(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);

    static bool IsUnder(string path, string root) =>
        Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static bool IsLocked(string file)
    {
        try
        {
            using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static Result FromXppGraft(string oldInstallDir, string newConfigPath, string oldDataRoot, string newDataRoot)
    {
        var r = new Result();
        var oldConfigPath = Path.Combine(oldInstallDir, OldConfigName);
        if (!File.Exists(oldConfigPath))
        {
            r.Messages.Add($"no {OldConfigName} in {oldInstallDir} - nothing to take over");
            return r;
        }
        if (File.Exists(newConfigPath))
        {
            r.Messages.Add($"{newConfigPath} already exists - settings were taken over before, nothing changed");
            return r;
        }

        var node = JsonNode.Parse(File.ReadAllText(oldConfigPath)) as JsonObject
                   ?? throw new InvalidDataException($"{oldConfigPath} is not a JSON object");
        var configured = node["indexPath"]?.GetValue<string>();
        var oldIndex = string.IsNullOrWhiteSpace(configured) ? Path.Combine(oldDataRoot, "index", "xpp.db") : configured;
        r.IndexPath = oldIndex;

        if (!IsUnder(oldIndex, oldDataRoot))
        {
            r.Messages.Add($"index stays where it is (not in the default folder): {oldIndex}");
        }
        else
        {
            var oldDir = Path.GetDirectoryName(Full(oldIndex))!;
            var newDir = Path.Combine(newDataRoot, Path.GetRelativePath(Full(oldDataRoot), oldDir));
            var newIndex = Path.Combine(newDir, Path.GetFileName(oldIndex));
            var present = IndexFiles.Select(f => Path.Combine(oldDir, f.Replace("xpp.db", Path.GetFileName(oldIndex)))).Where(File.Exists).ToList();

            if (present.Count == 0)
                r.Messages.Add($"no index found in {oldDir} - a new one will be built");
            else if (File.Exists(newIndex))
                r.Messages.Add($"{newIndex} already exists - the old index is used where it is: {oldIndex}");
            else if (present.FirstOrDefault(IsLocked) is { } locked)
                r.Messages.Add($"{locked} is in use (close Claude and other xpp-graft / xpp-lens programs) - the old index is used where it is");
            else
            {
                Directory.CreateDirectory(newDir);
                var moved = new List<(string From, string To, long Size)>();
                try
                {
                    foreach (var from in present)
                    {
                        var to = Path.Combine(newDir, Path.GetFileName(from));
                        var size = new FileInfo(from).Length;
                        File.Move(from, to);
                        moved.Add((from, to, size));
                        if (new FileInfo(to).Length != size) throw new IOException($"{to}: size differs after the move");
                    }
                    r.IndexPath = newIndex;
                    r.IndexMoved = true;
                    r.Messages.Add($"index moved: {oldDir} -> {newDir} ({moved.Sum(m => m.Size) / 1048576} MB in {moved.Count} file(s))");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    foreach (var m in moved.AsEnumerable().Reverse())
                        if (File.Exists(m.To) && !File.Exists(m.From)) File.Move(m.To, m.From);
                    r.IndexPath = oldIndex;
                    r.Messages.Add($"moving the index failed ({ex.Message}) - moved files were put back, the old index is used where it is");
                }
            }

            // The usage log is a convenience: move the monthly files that are not there yet.
            var oldUsage = Path.Combine(oldDataRoot, "usage");
            var newUsage = Path.Combine(newDataRoot, "usage");
            if (Directory.Exists(oldUsage))
            {
                int n = 0;
                foreach (var f in Directory.EnumerateFiles(oldUsage))
                {
                    var to = Path.Combine(newUsage, Path.GetFileName(f));
                    if (File.Exists(to)) continue;
                    Directory.CreateDirectory(newUsage);
                    File.Move(f, to);
                    n++;
                }
                if (n > 0) r.Messages.Add($"usage log moved: {n} file(s)");
            }
        }

        node["indexPath"] = r.IndexPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Full(newConfigPath))!);
        File.WriteAllText(newConfigPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        r.ConfigWritten = true;
        r.Messages.Add($"settings taken over: {newConfigPath}");
        return r;
    }
}
