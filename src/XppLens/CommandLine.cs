using System.Text.RegularExpressions;

namespace XppLens;

/// <summary>
/// The xpplens command line. Options are checked against what the command understands, so a typo stops with an
/// error instead of being ignored; "-name" works like "--name", and "--name=value" like "--name value".
/// </summary>
public sealed partial class CommandLine
{
    // "name=" takes a value, "name" is a switch.
    static readonly Dictionary<string, string[]> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["update"] = ["install", "restart-claude"],
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
        ["build"] = ["full-only", "std-only", "compiled-only", "force"],
        ["status"] = ["counts", "ascii"],
        ["find"] = ["kind=", "type=", "model=", "limit="],
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
}
