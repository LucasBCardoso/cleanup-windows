using Cleanup.Core.Duplicates;

namespace Cleanup.Core.Tests.Duplicates;

public class KeepRuleTests
{
    private const string Home = @"C:\Users\me";

    private static WalkedFile File(string path, int createdYear, int? modifiedYear = null) =>
        new(path, 10, new DateTime(createdYear, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(modifiedYear ?? createdYear, 1, 1, 0, 0, 0, DateTimeKind.Utc), "id:" + path);

    private static readonly DuplicateGroup Movies = new("h",
    [
        File(@"C:\Users\me\Downloads\movie.mov", 2020, 2024),
        File(@"C:\Users\me\Videos\movie.mov", 2022),
        File(@"C:\Users\me\Videos\Old\movie copy.mov", 2021),
    ], 10);

    [Fact]
    public void OldestKeepsTheEarliestCreated() =>
        Assert.Equal(@"C:\Users\me\Downloads\movie.mov", KeepRule.Oldest.Keeper(Movies, Home)?.Path);

    [Fact]
    public void NewestKeepsTheLatestCreated() =>
        Assert.Equal(@"C:\Users\me\Videos\movie.mov", KeepRule.Newest.Keeper(Movies, Home)?.Path);

    [Fact]
    public void OutsideDownloadsPrefersACopyElsewhereThenTheOldest()
    {
        Assert.Equal(@"C:\Users\me\Videos\Old\movie copy.mov", KeepRule.OutsideDownloads.Keeper(Movies, Home)?.Path);
        var allInDownloads = new DuplicateGroup("h", [File(@"C:\Users\me\Downloads\a.zip", 2021), File(@"C:\Users\me\DOWNLOADS\sub\a.zip", 2020)], 10);
        Assert.Equal(@"C:\Users\me\DOWNLOADS\sub\a.zip", KeepRule.OutsideDownloads.Keeper(allInDownloads, Home)?.Path);
    }

    [Fact]
    public void TiesGoToTheShortestPath()
    {
        var group = new DuplicateGroup("h", [File(@"C:\a\very\long\x", 2020), File(@"C:\a\x", 2020)], 10);
        Assert.Equal(@"C:\a\x", KeepRule.Oldest.Keeper(group, Home)?.Path);
        Assert.True(KeepRules.AutoSelect([group], KeepRule.Oldest, Home).SetEquals([@"C:\a\very\long\x"]));
    }

    [Fact]
    public void EnforceKeepOneUnselectsTheKeeperWhenEverythingIsSelected()
    {
        var all = Movies.Files.Select(f => f.Path).ToHashSet();
        var safe = KeepRules.EnforceKeepOne(all, [Movies], KeepRule.Oldest, Home);
        Assert.Equal(2, safe.Count);
        Assert.DoesNotContain(@"C:\Users\me\Downloads\movie.mov", safe);
    }

    [Fact]
    public void DisplayNamesMatchMacOS() =>
        Assert.Equal(["Manter a mais antiga", "Manter a mais recente", "Manter a que está fora de Downloads"],
            KeepRules.All.Select(r => r.DisplayName()));
}
