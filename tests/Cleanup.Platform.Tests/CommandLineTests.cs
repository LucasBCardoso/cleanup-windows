using Cleanup.Cli;

namespace Cleanup.Platform.Tests;

/// <summary>Argument parsing for cleanup-cli.exe; everything that changes something is a dry run unless --yes.</summary>
public class CommandLineTests
{
    [Fact]
    public void NoArgumentsIsHelp() => Assert.IsType<Command.Help>(CommandLine.Parse([]));

    [Fact]
    public void CleanIsADryRunUnlessYes()
    {
        var dry = Assert.IsType<Command.Clean>(CommandLine.Parse(["clean"]));
        Assert.False(dry.Confirm);
        var real = Assert.IsType<Command.Clean>(CommandLine.Parse(["clean", "--rule", "user-temp", "--rule", "crash-dumps", "--yes", "--admin"]));
        Assert.True(real.Confirm && real.Admin && !real.Permanent && !real.IncludeReview);
        Assert.Equal(["user-temp", "crash-dumps"], real.RuleIds);
    }

    [Fact]
    public void UndoTakesAnOptionalId()
    {
        Assert.Equal(new Command.Undo(null, false), CommandLine.Parse(["undo"]));
        Assert.Equal(new Command.Undo("ab12", true), CommandLine.Parse(["undo", "ab12", "--yes"]));
    }

    [Theory]
    [InlineData("500MB", 500_000_000)]
    [InlineData("1.5GB", 1_500_000_000)]
    [InlineData("200K", 200_000)]
    [InlineData("1024", 1024)]
    public void SizesUseDecimalUnits(string text, long bytes) => Assert.Equal(bytes, CommandLine.ParseSize(text));

    [Theory]
    [InlineData("nope")]
    [InlineData("scan", "--what")]
    [InlineData("large", "--min-size", "lots")]
    [InlineData("clean", "--rule")]
    [InlineData("ignore", "add")]
    public void BadArgumentsAreUsageErrors(params string[] args) => Assert.Throws<UsageException>(() => CommandLine.Parse(args));

    [Fact]
    public void HealthAll() => Assert.Equal(new Command.Health(true), CommandLine.Parse(["health", "--all"]));
}
