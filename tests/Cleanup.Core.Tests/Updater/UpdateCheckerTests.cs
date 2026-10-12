using Cleanup.Core.Safety;
using Cleanup.Core.Updater;

namespace Cleanup.Core.Tests.Updater;

internal sealed class FakeSource(UpdateSourceKind kind, params UpdateCheckOutcome[] outcomes) : IUpdateSource
{
    public UpdateSourceKind Kind => kind;
    public Task<IReadOnlyList<UpdateCheckOutcome>> UpdatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UpdateCheckOutcome>>(outcomes);
}

internal sealed class ThrowingSource : IUpdateSource
{
    public UpdateSourceKind Kind => UpdateSourceKind.MicrosoftStore;
    public Task<IReadOnlyList<UpdateCheckOutcome>> UpdatesAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("boom");
}

internal static class Updates
{
    public static AvailableUpdate Make(string name, string id, UpdateSourceKind source = UpdateSourceKind.Winget, string version = "2.0",
        int copy = 0) => new()
    {
        AppId = id, Name = name, Source = source, CurrentVersion = "1.0", NewVersion = version, Copy = copy,
        SourceName = source == UpdateSourceKind.Winget ? "winget" : "msstore",
    };

    public static UpdateCheckOutcome Available(AvailableUpdate u) => new UpdateCheckOutcome.Available(u);
}

/// <summary>Mirrors the Swift "Update checker" suite, with winget / Microsoft Store in place of Homebrew / Sparkle / App Store.</summary>
public class UpdateCheckerTests
{
    private static readonly (string Name, string Id) Zed = ("Zed", "ZedIndustries.Zed");
    private static readonly (string Name, string Id) Arc = ("Arc", "TheBrowserCompany.Arc");

    [Fact]
    public async Task WingetWinsOverStoreAndResultsAreSorted()
    {
        var checker = new UpdateChecker([
            new FakeSource(UpdateSourceKind.Winget, Updates.Available(Updates.Make(Zed.Name, Zed.Id, version: "3.0"))),
            new FakeSource(UpdateSourceKind.MicrosoftStore, Updates.Available(Updates.Make(Zed.Name, Zed.Id, UpdateSourceKind.MicrosoftStore, "3.1")),
                Updates.Available(Updates.Make(Arc.Name, Arc.Id, UpdateSourceKind.MicrosoftStore))),
        ], new IgnoredUpdates());
        var report = await checker.CheckAsync();
        Assert.Equal(["Arc", "Zed"], report.Updates.Select(u => u.Name));
        Assert.Equal(UpdateSourceKind.Winget, report.Updates[^1].Source);
    }

    [Fact]
    public async Task UpToDateInHigherSourceClaimsTheApp()
    {
        var checker = new UpdateChecker([
            new FakeSource(UpdateSourceKind.Winget, new UpdateCheckOutcome.UpToDate(Zed.Id)),
            new FakeSource(UpdateSourceKind.MicrosoftStore, Updates.Available(Updates.Make(Zed.Name, Zed.Id.ToUpperInvariant(), UpdateSourceKind.MicrosoftStore))),
        ], new IgnoredUpdates());
        Assert.Empty((await checker.CheckAsync()).Updates);
    }

    [Fact]
    public async Task FailuresHiddenWhenAnotherSourceFoundTheUpdate()
    {
        var checker = new UpdateChecker([
            new FakeSource(UpdateSourceKind.Winget, new UpdateCheckOutcome.Failed(new UpdateFailure(Zed.Id, "Zed", "x"))),
            new FakeSource(UpdateSourceKind.MicrosoftStore, Updates.Available(Updates.Make(Zed.Name, Zed.Id, UpdateSourceKind.MicrosoftStore))),
        ], new IgnoredUpdates());
        var report = await checker.CheckAsync();
        Assert.Single(report.Updates);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task FailingSourceDoesNotHideOthers()
    {
        var checker = new UpdateChecker([
            new FakeSource(UpdateSourceKind.Winget, new UpdateCheckOutcome.Failed(new UpdateFailure("winget", "winget", "boom"))),
            new FakeSource(UpdateSourceKind.MicrosoftStore, Updates.Available(Updates.Make(Arc.Name, Arc.Id, UpdateSourceKind.MicrosoftStore))),
        ], new IgnoredUpdates());
        var report = await checker.CheckAsync();
        Assert.Equal(["Arc"], report.Updates.Select(u => u.Name));
        Assert.Equal(["winget"], report.Failures.Select(f => f.AppName));
    }

    [Fact]
    public async Task ThrowingSourceBecomesAFailure()
    {
        var checker = new UpdateChecker([new ThrowingSource(), new FakeSource(UpdateSourceKind.Winget, Updates.Available(Updates.Make(Arc.Name, Arc.Id)))],
            new IgnoredUpdates());
        var report = await checker.CheckAsync();
        Assert.Equal(["Arc"], report.Updates.Select(u => u.Name));
        Assert.Equal("boom", Assert.Single(report.Failures).Message);
    }

    [Fact]
    public async Task IgnoredUpdatesAreFilteredAndCounted()
    {
        var ignored = new IgnoredUpdates().Adding(Zed.Id, "2.0").Adding(Arc.Id.ToLowerInvariant(), null);
        var checker = new UpdateChecker([
            new FakeSource(UpdateSourceKind.Winget, Updates.Available(Updates.Make(Zed.Name, Zed.Id, version: "2.0")), Updates.Available(Updates.Make(Arc.Name, Arc.Id))),
        ], ignored);
        var report = await checker.CheckAsync();
        Assert.Empty(report.Updates);
        Assert.Equal(2, report.IgnoredCount);
    }

    [Fact]
    public async Task CopiesOfTheSameAppAreKeptApart()
    {
        var checker = new UpdateChecker([
            new FakeSource(UpdateSourceKind.Winget, Updates.Available(Updates.Make(Zed.Name, Zed.Id)), Updates.Available(Updates.Make(Zed.Name, Zed.Id, copy: 1))),
        ], new IgnoredUpdates());
        var report = await checker.CheckAsync();
        Assert.Equal(2, report.Updates.Count);
        Assert.Equal(2, report.Updates.Select(u => u.Id).Distinct().Count());
    }

    [Fact]
    public void IgnoringOneVersionStillShowsNewerOnes()
    {
        var ignored = new IgnoredUpdates().Adding(Zed.Id, "2.0");
        Assert.True(ignored.IsIgnored(Updates.Make(Zed.Name, Zed.Id, version: "2.0")));
        Assert.False(ignored.IsIgnored(Updates.Make(Zed.Name, Zed.Id, version: "2.1")));
    }
}

/// <summary>Mirrors the Swift "Ignored updates persistence" suite.</summary>
public class IgnoredUpdatesTests
{
    [Fact]
    public void RoundTripsAndIsImmutable()
    {
        using var t = new TempHome();
        var path = Path.Combine(t.Root, "support", "ignored-updates.json");
        var empty = IgnoredUpdates.Load(path);
        var one = empty.Adding("ZedIndustries.Zed", null).Adding("Arc", "1.2");
        Assert.Empty(empty.Entries);
        one.Save(path);
        Assert.Equal(one, IgnoredUpdates.Load(path));
        Assert.Contains("\"id\": \"Arc\"", File.ReadAllText(path));
        Assert.Single(one.Removing(new IgnoredUpdate("zedindustries.zed", null)).Entries);
    }

    [Fact]
    public void AddingTheSameEntryTwiceKeepsOne()
    {
        var twice = new IgnoredUpdates().Adding("A.B", "1").Adding("a.b", "1");
        Assert.Single(twice.Entries);
    }

    [Fact]
    public void CorruptFileThrows()
    {
        using var t = new TempHome();
        var path = t.File("ignored-updates.json");
        Assert.Throws<IgnoreListException>(() => IgnoredUpdates.Load(path));
        File.WriteAllText(path, "[{\"version\":\"1\"}]");
        Assert.Throws<IgnoreListException>(() => IgnoredUpdates.Load(path));
    }
}
