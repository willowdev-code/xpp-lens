using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XppLens;

CommandLine line;
try
{
    line = CommandLine.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
var cmd = line.Command;
var rest = line.Positional;
string? Opt(string name) => line.Opt(name);
bool Flag(string name) => line.Flag(name);

string Arg(int i) => i < rest.Count ? rest[i] : throw new ArgumentException($"missing argument #{i + 1} for '{cmd}'");

static string AppVersion() => typeof(Config).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

// Box-drawing frames on a console (UTF-8 for the time of writing them); +-| when redirected to a file or pipe.
static void PrintTables(Func<TableStyle, string> render, bool ascii)
{
    if (ascii || Console.IsOutputRedirected)
    {
        Console.WriteLine(render(TableStyle.Ascii));
        return;
    }
    var previous = Console.OutputEncoding;
    try
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }
    catch (IOException)
    {
        Console.WriteLine(render(TableStyle.Ascii));
        return;
    }
    try
    {
        Console.WriteLine(render(TableStyle.Box));
    }
    finally
    {
        Console.OutputEncoding = previous;
    }
}

Updater.RemoveLeftovers();

if (cmd is "version" or "--version" or "-v")
{
    Console.WriteLine($"xpp-lens {AppVersion()} — Copyright © 2026 WillowDev");
    return 0;
}

if (cmd == "update")
    return await Updater.Run(install: Flag("install"), restartClaude: Flag("restart-claude"));

if (cmd == "migrate")
{
    // Used by install.ps1 when it finds an xpp-graft installation (the name up to 1.2.0).
    var from = Opt("from") ?? @"C:\Tools\xpp-graft";
    var to = Opt("config") ?? Config.DefaultPath();
    var res = Migration.FromXppGraft(from, to, Migration.DefaultOldDataRoot, Migration.DefaultNewDataRoot);
    foreach (var m in res.Messages) Console.WriteLine("  " + m);
    return 0;
}

var cfg = Config.Load();
Usage.Enabled = Usage.Enabled && cfg.UsageLog;

if (cmd == "stats")
{
    Console.WriteLine(Usage.Report(int.Parse(Opt("days") ?? "7"), int.Parse(Opt("top") ?? "10")));
    return 0;
}

if (cmd == "mcp")
{
    var builder = Host.CreateApplicationBuilder([]);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    var svc = new IndexService(cfg);
    svc.StartBackground(syncCatalog: true);

    builder.Services.AddSingleton(svc);
    builder.Services.AddSingleton<Queries>();
    builder.Services
        .AddMcpServer(o =>
        {
            o.ServerInfo = new() { Name = "xpp-lens", Version = AppVersion() };
            o.ServerInstructions = McpTools.Instructions;
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();
    await builder.Build().RunAsync();
    return 0;
}

if (cmd == "detect")
{
    var found = Detect.PackagesDirs();
    if (found.Count == 0)
    {
        Console.WriteLine("No PackagesLocalDirectory found. Set it manually: xpplens config --packages-dir <path>");
        return 1;
    }
    foreach (var d in found) Console.WriteLine($"{d.Path}\t{d.Packages} packages\t{d.Source}");
    if (Flag("set") || Flag("apply"))
    {
        if (found.Count > 1 && !Flag("first"))
        {
            Console.Error.WriteLine("Several candidates found — pass --first or use: xpplens config --packages-dir <path>");
            return 1;
        }
        cfg.PackagesDir = found[0].Path;
        cfg.Save();
        Console.WriteLine($"\nsaved packagesDir = {cfg.PackagesDir} to {cfg.SourcePath}\nnext: xpplens build");
    }
    return 0;
}

if (cmd == "config")
{
    bool changed = false, rebuild = false;

    void SetList(List<string> list, string addOpt, string removeOpt, string setOpt)
    {
        if (Opt(setOpt) is { } setVal)
        {
            list.Clear();
            list.AddRange(setVal.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            changed = rebuild = true;
        }
        while (Opt(addOpt) is { } add)
        {
            foreach (var v in add.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!list.Contains(v, StringComparer.OrdinalIgnoreCase)) { list.Add(v); changed = rebuild = true; }
        }
        while (Opt(removeOpt) is { } rem)
        {
            foreach (var v in rem.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (list.RemoveAll(x => x.Equals(v, StringComparison.OrdinalIgnoreCase)) > 0) { changed = rebuild = true; }
        }
    }

    if (Opt("packages-dir") is { } pd)
    {
        var full = Path.GetFullPath(pd.Trim('"'));
        if (Detect.CountPackages(full) < 3)
        {
            Console.Error.WriteLine($"'{full}' does not look like a PackagesLocalDirectory (no packages with a Descriptor folder).");
            return 1;
        }
        cfg.PackagesDir = full;
        changed = rebuild = true;
    }
    if (Opt("index-path") is { } ip) { cfg.IndexPath = Path.GetFullPath(ip.Trim('"')); changed = true; }
    if (Opt("display-language") is { } dl) { cfg.DisplayLanguage = dl; changed = true; }
    if (Opt("rescan-seconds") is { } rs) { cfg.RescanIntervalSeconds = int.Parse(rs); changed = true; }
    if (Opt("index-standard") is { } istd) { cfg.IndexStandard = istd is "1" or "true" or "yes"; changed = rebuild = true; }
    if (Opt("standard-code") is { } sc) { cfg.StandardCodeRefs = sc is "1" or "true" or "yes"; changed = rebuild = true; }
    if (Opt("usage-log") is { } ul) { cfg.UsageLog = ul is "1" or "true" or "yes"; changed = true; }
    SetList(cfg.LabelLanguages, "add-language", "remove-language", "languages");
    SetList(cfg.ExtraFullModels, "add-full-model", "remove-full-model", "full-models");
    SetList(cfg.ExtraStandardModels, "add-standard-model", "remove-standard-model", "standard-models");
    SetList(cfg.StandardPublisherPatterns, "add-standard-publisher", "remove-standard-publisher", "standard-publishers");

    if (rest.Count > 0) Console.Error.WriteLine($"ignored arguments: {string.Join(" ", rest)}");
    if (changed)
    {
        if (cfg.DisplayLanguage.Length > 0 && !cfg.LabelLanguages.Contains(cfg.DisplayLanguage, StringComparer.OrdinalIgnoreCase))
            cfg.LabelLanguages.Add(cfg.DisplayLanguage);
        cfg.Save();
        Console.WriteLine($"saved {cfg.SourcePath}\n");
    }
    Console.Write(cfg.Describe());
    if (changed && rebuild) Console.WriteLine("\nrun 'xpplens build' to apply (models/languages are re-read from disk)");
    return 0;
}

if (cmd is "register" or "unregister")
{
    var serverName = Opt("name") ?? "xpp-lens";
    var exePath = Opt("exe") ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "xpplens.exe");
    bool desktopOnly = Flag("desktop"), codeOnly = Flag("code");

    var targets = new List<(string Path, bool CreateIfMissing)>();
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    if (!codeOnly || desktopOnly)
    {
        var desktopCfg = Path.Combine(appData, "Claude", "claude_desktop_config.json");
        if (Directory.Exists(Path.GetDirectoryName(desktopCfg)!) || File.Exists(desktopCfg)) targets.Add((desktopCfg, true));
    }
    if (!desktopOnly || codeOnly)
        targets.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"), true));

    foreach (var (path, create) in targets)
    {
        try
        {
            if (!File.Exists(path))
            {
                if (!create || cmd == "unregister") { Console.WriteLine($"skipped (no file): {path}"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "{}");
            }
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            if (node["mcpServers"] is not System.Text.Json.Nodes.JsonObject servers)
            {
                if (cmd == "unregister") { Console.WriteLine($"not registered in {path}"); continue; }
                servers = new System.Text.Json.Nodes.JsonObject();
                node["mcpServers"] = servers;
            }
            if (cmd == "register")
            {
                servers[serverName] = new System.Text.Json.Nodes.JsonObject
                {
                    ["command"] = exePath,
                    ["args"] = new System.Text.Json.Nodes.JsonArray("mcp"),
                };
            }
            else
            {
                if (servers[serverName] is not System.Text.Json.Nodes.JsonObject existing)
                {
                    Console.WriteLine($"not registered in {path}");
                    continue;
                }
                var registeredExe = existing["command"]?.GetValue<string>() ?? "";
                if (!Flag("any") && registeredExe.Length > 0
                    && !string.Equals(Path.GetFullPath(registeredExe), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"left alone in {path}: '{serverName}' points to another installation ({registeredExe}); use --any to remove it anyway");
                    continue;
                }
                servers.Remove(serverName);
            }
            File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{(cmd == "register" ? "registered" : "removed")} '{serverName}' in {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{path}: {ex.Message}");
            return 1;
        }
    }
    Console.WriteLine("restart Claude Desktop / start a new Claude Code session to pick this up");
    return 0;
}

if (cmd is "help" or "-h" or "--help")
{
    Console.WriteLine("""
        xpplens — X++ code index (D365 F&O) · WillowDev

          xpplens detect [--set [--first]]                     find PackagesLocalDirectory on this machine
          xpplens config                                       show settings
          xpplens config --packages-dir <path> | --index-path <path>
                          --add-language pl | --remove-language pl | --languages en-US,pl
                          --display-language en-US
                          --add-full-model XPL | --remove-full-model XPL | --full-models A,B
                          --add-standard-model X | --remove-standard-model X
                          --add-standard-publisher "Contoso" | --standard-publishers Microsoft
                          --index-standard true|false | --standard-code true|false
                          --rescan-seconds 300 | --usage-log true|false
          xpplens build [--full-only] [--std-only] [--compiled-only] [--force]   build / update the index
                        (--std-only also switches indexStandard on when an installation was made without it)
          xpplens status [--counts] [--ascii]                    --counts: also count references and labels (slow on a cold disk)
                                                                 --ascii: frames from +-| (automatic when redirected)
          xpplens update [--install [--restart-claude]]          check GitHub for a newer release; --install downloads,
                                                                 verifies (SHA-256) and installs it, keeping settings and index
                                                                 (Claude may stay open; --restart-claude restarts Claude Desktop
                                                                 at the end without asking)
          xpplens stats [--days 7] [--top 10]                   tokens, time and empty answers of MCP calls
          xpplens find <query[;query…]> [--kind any|object|method|field] [--type t] [--model m] [--limit n]
          xpplens object <name[;name…]> [--type t] [--sections list] [--parent control] [--depth n] [--filter *text*]
          xpplens method <object> [<method[;method…]>] [--type t] [--match regex] [--lines from-to] [--context n]
          xpplens callers <object> <method> [--depth n] [--limit n] [--standard true|false]
          xpplens callees <object> <method> [--type t]
          xpplens refs <name> [--member m] [--kind k] [--model m] [--limit n]
          xpplens ext <name>
          xpplens scaffold coc|event|delegate|pre|post <object> <member> [--element ds|ds.field|control] [--class name] [--type t]
          xpplens build-errors [--model m] [--severity error|warning|all] [--limit n]
                          (newest of the model build and the project build, with objects not compiled since)
          xpplens security <menuitem|form|privilege|duty|role> [--type t] [--limit n]
          xpplens join <fromTable> <toTable> [--hops n] [--limit n]
          xpplens entity <entity|publicName|table> [--sections list] [--limit n]
          xpplens changed [--since 24h|3d|2026-10-01|build] [--model m] [--type t] [--limit n]
                          (--since build: objects changed after the package's last build, i.e. not compiled yet)
          xpplens grep <regex> [--model m] [--type t] [--object o] [--standard] [--limit n]
          xpplens label <@id|text> [--lang l] [--limit n]
          xpplens mcp                                           MCP server over stdio
          xpplens register [--desktop] [--code] [--name xpp-lens]    add to Claude config(s)
          xpplens unregister [--desktop] [--code] [--any]             (--any: also other installations)
          xpplens version

        options: --name value, --name=value or -name value; '--' ends the options (for an argument starting with '-')
        config: xpplens.json next to the executable or in a parent folder (or XPPLENS_CONFIG)
        """);
    return 0;
}

using var service = new IndexService(cfg);
var sw = Stopwatch.StartNew();

try
{
    switch (cmd)
    {
        case "build":
        {
            bool fullOnly = Flag("full-only"), stdOnly = Flag("std-only"), force = Flag("force"), compiledOnly = Flag("compiled-only");
            if (service.IndexReadOnly)
            {
                Console.Error.WriteLine(service.ReadOnlyNote);
                return 1;
            }
            if (stdOnly && !cfg.IndexStandard)
            {
                // Asking for the standard tier explicitly is the way to add it to an installation made without it.
                cfg.IndexStandard = true;
                cfg.Save();
                Log.Info($"indexStandard was false - switched on in {cfg.SourcePath} (restart Claude afterwards)");
            }
            Log.Info($"catalog: {cfg.PackagesDir}");
            service.SyncCatalog();
            Log.Info($"models: {service.Models.Count(m => m.Full)} full, {service.Models.Count(m => !m.Full)} standard");
            var last = Stopwatch.StartNew();
            void Progress(string msg)
            {
                if (last.ElapsedMilliseconds < 5000) return;
                last.Restart();
                Log.Info($"{msg}  ({sw.Elapsed:hh\\:mm\\:ss})");
            }
            if (compiledOnly)
            {
                var cs = service.SyncCompiledPackages(force, Log.Info);
                Log.Info($"compiled packages: {cs} ({sw.Elapsed:hh\\:mm\\:ss})");
                PrintTables(style => service.StatusText(style: style), ascii: false);
                break;
            }
            if (!stdOnly)
            {
                var st = service.SyncFullTier(force, Progress);
                Log.Info($"full tier: {st} ({sw.Elapsed:hh\\:mm\\:ss})");
            }
            if (!fullOnly && cfg.IndexStandard)
            {
                var st = service.SyncStandardTier(force, Progress);
                Log.Info($"standard tier: {st} ({sw.Elapsed:hh\\:mm\\:ss})");
            }
            PrintTables(style => service.StatusText(style: style), ascii: false);
            break;
        }
        case "status":
        {
            bool counts = Flag("counts"), ascii = Flag("ascii");
            service.SyncCatalog();
            PrintTables(style => service.StatusText(counts, style), ascii);
            break;
        }
        default:
        {
            service.SyncCatalog();
            var q = new Queries(service);

            // Options first: the method name is an optional second positional argument.
            string MethodCommand()
            {
                var type = Opt("type");
                var match = Opt("match");
                var lines = Opt("lines");
                var context = int.Parse(Opt("context") ?? "3");
                return q.Method(Arg(0), rest.Count > 1 ? rest[1] : null, type, match, lines, context);
            }

            string output = cmd switch
            {
                "find" => q.Find(Arg(0), Opt("kind"), Opt("type"), Opt("model"), int.Parse(Opt("limit") ?? "40")),
                "object" => q.Object(Arg(0), Opt("type"), Opt("sections"), Opt("parent"), int.Parse(Opt("depth") ?? "0"), Opt("filter")),
                "method" => MethodCommand(),
                "callers" => q.Callers(Arg(0), Arg(1), int.Parse(Opt("depth") ?? "1"), int.Parse(Opt("limit") ?? "80"),
                    Opt("standard") is not ("0" or "false" or "no")),
                "callees" => q.Callees(Arg(0), Arg(1), Opt("type")),
                "refs" => q.Refs(Arg(0), Opt("member"), Opt("kind"), Opt("model"), int.Parse(Opt("limit") ?? "150")),
                "ext" => q.Extensions(Arg(0)),
                "scaffold" => q.Scaffold(Arg(0), Arg(1), Arg(2), Opt("element"), Opt("class"), Opt("type")),
                "build-errors" => q.BuildErrors(Opt("model"), Opt("severity"), int.Parse(Opt("limit") ?? "50")),
                "security" => q.Security(Arg(0), Opt("type"), int.Parse(Opt("limit") ?? "40")),
                "join" => q.Join(Arg(0), Arg(1), int.Parse(Opt("hops") ?? "3"), int.Parse(Opt("limit") ?? "3")),
                "entity" => q.Entity(Arg(0), Opt("sections"), int.Parse(Opt("limit") ?? "200")),
                "changed" => q.Changed(Opt("since"), Opt("model"), Opt("type"), int.Parse(Opt("limit") ?? "100")),
                "grep" => q.Grep(Arg(0), Opt("model"), Opt("type"), Opt("object"), Flag("standard"), int.Parse(Opt("limit") ?? "80")),
                "label" => q.Label(Arg(0), Opt("lang"), int.Parse(Opt("limit") ?? "30")),
                _ => throw new ArgumentException($"unknown command '{cmd}' (see xpplens help)"),
            };
            Console.WriteLine(output);
            Log.Debug($"{sw.ElapsedMilliseconds} ms");
            if (Environment.GetEnvironmentVariable("XPPLENS_TIMING") == "1") Console.Error.WriteLine($"[{sw.ElapsedMilliseconds} ms]");
            break;
        }
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Log.Debug(ex.ToString());
    return 1;
}
