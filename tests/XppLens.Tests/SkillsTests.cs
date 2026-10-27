namespace XppLens.Tests;

public sealed class SkillsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "xpplens-skills-" + Guid.NewGuid().ToString("N"));
    readonly string _package, _target, _manifest;

    public SkillsTests()
    {
        _package = Path.Combine(_root, "package", "skills");
        _target = Path.Combine(_root, "home", ".claude", "skills");
        _manifest = Path.Combine(_root, "package", Skills.ManifestName);
        Write(_package, "demo/SKILL.md", "---\ndescription: English\n---\nbody");
        Write(_package, "demo/SKILL.pl.md", "---\ndescription: Polski\n---\nbody");
        Write(_package, "demo/reference/notes.md", "notes");
        Write(_package, "other/SKILL.md", "other");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    static void Write(string dir, string rel, string text)
    {
        var path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    Skills Make() => new(_package, _target, _manifest);
    string Installed(string rel) => File.ReadAllText(Path.Combine(_target, rel));

    [Fact]
    public void Installs_the_language_variant_as_the_base_file()
    {
        Make().Install("pl");
        Assert.Contains("Polski", Installed("demo/SKILL.md"));
        Assert.False(File.Exists(Path.Combine(_target, "demo", "SKILL.pl.md")));
        Assert.Equal("notes", Installed("demo/reference/notes.md"));
        Assert.Equal("other", Installed("other/SKILL.md"));

        // An update without a language keeps the one of the last installation.
        var again = Make().Install();
        Assert.Contains("demo: up to date", again);
        Assert.Contains("Polski", Installed("demo/SKILL.md"));

        Make().Install("en");
        Assert.Contains("English", Installed("demo/SKILL.md"));
    }

    [Fact]
    public void Unchanged_skill_is_updated_and_removed()
    {
        Make().Install("en");
        Write(_package, "demo/SKILL.md", "---\ndescription: English v2\n---\nbody");
        File.Delete(Path.Combine(_package, "demo", "reference", "notes.md"));

        var lines = Make().Install();
        Assert.Contains(lines, l => l.StartsWith("demo: updated", StringComparison.Ordinal));
        Assert.Contains("English v2", Installed("demo/SKILL.md"));
        Assert.False(Directory.Exists(Path.Combine(_target, "demo", "reference")));

        Make().Remove();
        Assert.False(Directory.Exists(Path.Combine(_target, "demo")));
        Assert.False(Directory.Exists(Path.Combine(_target, "other")));
        Assert.False(File.Exists(_manifest));
    }

    [Fact]
    public void Changed_skill_survives_install_and_remove_with_a_warning()
    {
        Make().Install("en");
        File.WriteAllText(Path.Combine(_target, "demo", "SKILL.md"), "my own version");
        Write(_package, "demo/SKILL.md", "---\ndescription: English v2\n---\nbody");

        var lines = Make().Install();
        Assert.Contains(lines, l => l.StartsWith("warning: demo was changed locally (SKILL.md)", StringComparison.Ordinal));
        Assert.Equal("my own version", Installed("demo/SKILL.md"));
        Assert.Equal(["demo"], Make().ChangedLocally());

        var removed = Make().Remove();
        Assert.Contains(removed, l => l.StartsWith("warning: demo was changed locally", StringComparison.Ordinal));
        Assert.Contains("other: removed", removed);
        Assert.Equal("my own version", Installed("demo/SKILL.md"));
        Assert.False(Directory.Exists(Path.Combine(_target, "other")));
    }

    [Fact]
    public void Folder_not_written_by_xpp_lens_is_left_alone()
    {
        Write(_target, "demo/SKILL.md", "someone else's skill");
        var lines = Make().Install("en");
        Assert.Contains(lines, l => l.StartsWith("warning: demo already exists and differs", StringComparison.Ordinal));
        Assert.Equal("someone else's skill", Installed("demo/SKILL.md"));
        Assert.Contains(lines, l => l.StartsWith("other: installed", StringComparison.Ordinal));

        // Not in the manifest, so removing never touches it.
        Make().Remove();
        Assert.Equal("someone else's skill", Installed("demo/SKILL.md"));
    }

    [Fact]
    public void Shipped_skills_have_both_languages()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "skills");
        foreach (var skill in new[] { "xpp-ponytail", "xpp-ponytail-review" })
        {
            var en = Skills.Files(Path.Combine(dir, skill), "en");
            var pl = Skills.Files(Path.Combine(dir, skill), "pl");
            Assert.Equal(["SKILL.md"], en.Keys);
            Assert.EndsWith("SKILL.pl.md", pl["SKILL.md"]);
            Assert.StartsWith($"---\nname: {skill}\n", File.ReadAllText(en["SKILL.md"]).Replace("\r", ""));
            Assert.StartsWith($"---\nname: {skill}\n", File.ReadAllText(pl["SKILL.md"]).Replace("\r", ""));
        }
    }
}
