namespace XppLens.Tests;

public class CommandLineTests
{
    [Fact]
    public void Single_and_double_dash_are_the_same()
    {
        Assert.True(CommandLine.Parse(["update", "-install"]).Flag("install"));
        Assert.True(CommandLine.Parse(["update", "--install"]).Flag("install"));
        Assert.False(CommandLine.Parse(["update"]).Flag("install"));
    }

    [Fact]
    public void Unknown_option_is_an_error()
    {
        var ex = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["update", "--instal"]));
        Assert.Contains("unknown option '--instal' for 'update'", ex.Message);
        Assert.Contains("--install", ex.Message);
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["find", "Cust*", "--limt", "5"]));
        Assert.Contains("needs a value", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["find", "x", "--limit"])).Message);
    }

    [Fact]
    public void Options_can_come_before_the_arguments()
    {
        var cl = CommandLine.Parse(["find", "--kind", "method", "CustTable.find", "--limit=5"]);
        Assert.Equal(["CustTable.find"], cl.Positional);
        Assert.Equal("method", cl.Opt("kind"));
        Assert.Equal("5", cl.Opt("limit"));
        Assert.Null(cl.Opt("model"));
    }

    [Fact]
    public void Repeated_options_and_double_dash()
    {
        var cl = CommandLine.Parse(["config", "--add-language", "pl", "--add-language", "de"]);
        Assert.Equal("pl", cl.Opt("add-language"));
        Assert.Equal("de", cl.Opt("add-language"));
        Assert.Null(cl.Opt("add-language"));

        var grep = CommandLine.Parse(["grep", "--model", "Contoso*", "--", "-1"]);
        Assert.Equal(["-1"], grep.Positional);
        Assert.Equal("Contoso*", grep.Opt("model"));
    }
}
