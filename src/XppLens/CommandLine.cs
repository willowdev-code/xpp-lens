using System.Text.RegularExpressions;

namespace XppLens;

/// <summary>
/// The xpplens command line. Options are checked against what the command understands, so a typo stops with an
/// error instead of being ignored; "-name" works like "--name", and "--name=value" like "--name value".
/// </summary>
public sealed partial class CommandLine
{
    // "name=" takes a value, "name" is a switch.
    internal static readonly Dictionary<string, string[]> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["update"] = ["install", "restart-claude", "pre"],
        ["migrate"] = ["from=", "config="],
        ["stats"] = ["days=", "top="],
        ["mcp"] = [],
        ["detect"] = ["set", "apply", "first"],
        ["config"] =
        [
            "packages-dir=", "index-path=", "display-language=", "rescan-seconds=", "index-standard=", "standard-code=",
            "usage-log=", "languages=", "add-language=", "remove-language=", "full-models=", "add-full-model=",
            "remove-full-model=", "standard-models=", "add-standard-model=", "remove-standard-model=",
            "standard-publishers=", "add-standard-publisher=", "remove-standard-publisher=",
        ],
        ["register"] = ["name=", "exe=", "desktop", "code"],
        ["unregister"] = ["name=", "exe=", "desktop", "code", "any"],
        ["skills"] = ["lang="],
        ["build"] = ["full-only", "std-only", "compiled-only", "force"],
        ["status"] = ["counts", "ascii"],
        ["find"] = ["kind=", "type=", "model=", "limit=", "property="],
        ["object"] = ["type=", "sections=", "parent=", "depth=", "filter="],
        ["method"] = ["type=", "match=", "lines=", "context="],
        ["callers"] = ["depth=", "limit=", "standard="],
        ["callees"] = ["type="],
        ["refs"] = ["member=", "kind=", "model=", "limit="],
        ["ext"] = [],
        ["scaffold"] = ["element=", "class=", "type="],
        ["build-errors"] = ["model=", "severity=", "limit="],
        ["security"] = ["type=", "limit="],
        ["join"] = ["hops=", "limit="],
        ["entity"] = ["sections=", "limit="],
        ["changed"] = ["since=", "model=", "type=", "limit="],
        ["grep"] = ["model=", "type=", "object=", "standard", "limit="],
        ["label"] = ["lang=", "limit="],
    };

    [GeneratedRegex(@"^--?(?<name>[A-Za-z][A-Za-z0-9-]*)(=(?<value>.*))?$", RegexOptions.Singleline)]
    private static partial Regex OptionPattern();

    readonly Dictionary<string, Queue<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _switches = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; }

    /// <summary>Arguments that are not options, in order. After "--" everything is positional.</summary>
    public List<string> Positional { get; } = [];

    CommandLine(string command) => Command = command;

    /// <exception cref="ArgumentException">An option the command does not know, or one without its value.</exception>
    public static CommandLine Parse(string[] args)
    {
        // "xpplens <command> --help" (or -h): the help of that command, as "xpplens help <command>".
        var end = Array.IndexOf(args, "--");
        if (args.Length > 1 && args.Take(end < 0 ? args.Length : end).Skip(1).Any(a => a is "--help" or "-h" or "-?"))
        {
            var help = new CommandLine("help");
            help.Positional.Add(args[0].ToLowerInvariant());
            return help;
        }
        var cl = new CommandLine(args.Length > 0 ? args[0].ToLowerInvariant() : "help");
        // help, version and unknown commands: no option checking (an unknown command fails on its own).
        Known.TryGetValue(cl.Command, out var spec);
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                cl.Positional.AddRange(args.Skip(i + 1));
                break;
            }
            var m = OptionPattern().Match(a);
            if (spec == null || !m.Success)
            {
                cl.Positional.Add(a);
                continue;
            }
            var name = m.Groups["name"].Value;
            if (spec.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (m.Groups["value"].Success) throw new ArgumentException($"option --{name} of '{cl.Command}' takes no value");
                cl._switches.Add(name);
            }
            else if (spec.Contains(name + "=", StringComparer.OrdinalIgnoreCase))
            {
                string value;
                if (m.Groups["value"].Success) value = m.Groups["value"].Value;
                else if (i + 1 < args.Length) value = args[++i];
                else throw new ArgumentException($"option --{name} of '{cl.Command}' needs a value");
                if (!cl._values.TryGetValue(name, out var q)) cl._values[name] = q = new Queue<string>();
                q.Enqueue(value);
            }
            else
            {
                var known = spec.Length == 0 ? "none" : string.Join(", ", spec.Select(s => "--" + s.TrimEnd('=')));
                throw new ArgumentException($"unknown option '{a}' for '{cl.Command}' (known: {known}; put '--' before an argument that starts with '-')");
            }
        }
        return cl;
    }

    /// <summary>The next value of an option given (possibly several times), or null.</summary>
    public string? Opt(string name) =>
        _values.TryGetValue(name, out var q) && q.Count > 0 ? q.Dequeue() : null;

    public bool Flag(string name) => _switches.Contains(name);

    /// <summary>
    /// The lines of <see cref="HelpText"/> about one command (its usage lines with their continuations) and the
    /// options note; null for a command the help does not describe.
    /// </summary>
    public static string? HelpFor(string command)
    {
        var lines = HelpText.Replace("\r", "").Split('\n');
        var picked = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("xpplens ", StringComparison.Ordinal) && line.StartsWith("  xpplens", StringComparison.Ordinal))
                inside = trimmed == $"xpplens {command}" || trimmed.StartsWith($"xpplens {command} ", StringComparison.Ordinal);
            else if (inside && !line.StartsWith("   ", StringComparison.Ordinal))
                inside = false;
            if (inside) picked.Add(line);
        }
        if (picked.Count == 0) return null;
        picked.Add("");
        picked.AddRange(lines.Where(l => l.StartsWith("options:", StringComparison.Ordinal)));
        return string.Join(Environment.NewLine, picked);
    }

    /// <summary>The text of "xpplens help"; CommandLineTests checks that every command and option in Known is in it.</summary>
    public const string HelpText = """
    xpplens — X++ code index (D365 F&O) · WillowDev

      xpplens detect [--set [--first]]                     find PackagesLocalDirectory on this machine
      xpplens config                                       show settings
      xpplens config --packages-dir <path> | --index-path <path>
                      --add-language pl | --remove-language pl | --languages en-US,pl
                      --display-language en-US
                      --add-full-model Contoso | --remove-full-model Contoso | --full-models A,B
                      --add-standard-model X | --remove-standard-model X | --standard-models A,B
                      --add-standard-publisher "Contoso" | --remove-standard-publisher "Contoso"
                      --standard-publishers Microsoft
                      --index-standard true|false | --standard-code true|false
                      --rescan-seconds 300 | --usage-log true|false
      xpplens build [--full-only] [--std-only] [--compiled-only] [--force]   build / update the index
                    (--std-only also switches indexStandard on when an installation was made without it)
      xpplens status [--counts] [--ascii]                    --counts: also count references and labels (slow on a cold disk)
                                                             --ascii: frames from +-| (automatic when redirected)
      xpplens update [--install [--restart-claude]] [--pre]  check GitHub for a newer release; --install downloads,
                                                             verifies (SHA-256) and installs it, keeping settings and index
                                                             (Claude may stay open; --restart-claude restarts Claude Desktop
                                                             at the end without asking); --pre: test releases too
      xpplens stats [--days 7] [--top 10]                   tokens, time and empty answers of MCP calls
      xpplens find <query[;query…]> [--kind any|object|method|field] [--type t] [--model m] [--limit n]
                   [--property "Name[;Name2]" | "Name=*value*"]   list root properties of the matching objects
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
      xpplens register [--desktop] [--code] [--name xpp-lens] [--exe path]   add to Claude config(s)
                                                             (--exe: another xpplens.exe than this one)
      xpplens unregister [--desktop] [--code] [--name xpp-lens] [--exe path] [--any]
                                                             (--any: also entries of other installations)
      xpplens skills [install [--lang en|pl]|remove|status]  Claude Code skills of the package in ~/.claude/skills
                                                             (a skill changed locally is kept, never overwritten;
                                                             --lang: the language of their texts, kept for updates)
      xpplens version

    options: --name value, --name=value or -name value; '--' ends the options (for an argument starting with '-')
    config: xpplens.json next to the executable or in a parent folder (or XPPLENS_CONFIG)
    """;
}
