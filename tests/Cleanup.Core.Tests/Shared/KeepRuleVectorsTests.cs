using Cleanup.Core.Duplicates;

namespace Cleanup.Core.Tests.Shared;

/// <summary>The duplicate keep-rule vectors, run against the real .NET <see cref="KeepRules"/> (W5).</summary>
public class KeepRuleVectorsTests
{
    private sealed record FileCase(string Path, DateTime? Created, DateTime? Modified);

    private sealed record KeeperCase(string Name, string Rule, List<FileCase> Files, string Keeper, List<string> Selected);

    private sealed record EnforceCase(string Name, string Rule, List<List<FileCase>> Groups, List<string> Selection, List<string> Expected);

    private sealed record Vectors(string Home, List<KeeperCase> Keeper, List<EnforceCase> EnforceKeepOne);

    private static KeepRule Rule(string name) => name switch
    {
        "oldest" => KeepRule.Oldest,
        "newest" => KeepRule.Newest,
        "outsideDownloads" => KeepRule.OutsideDownloads,
        _ => throw new ArgumentException(name),
    };

    private static DuplicateGroup Group(IEnumerable<FileCase> files) =>
        new("g", files.Select(f => new WalkedFile(f.Path, 1, f.Created?.ToUniversalTime(), f.Modified?.ToUniversalTime(), "id:" + f.Path)).ToList(), 1);

    [Fact]
    public void KeeperAndAutoSelect()
    {
        var vectors = SharedFiles.Load<Vectors>("test-vectors/keep-rule.json");
        Assert.NotEmpty(vectors.Keeper);
        Assert.All(vectors.Keeper, c =>
        {
            var group = Group(c.Files);
            Assert.True(Rule(c.Rule).Keeper(group, vectors.Home)?.Path == c.Keeper, c.Name);
            Assert.True(KeepRules.AutoSelect([group], Rule(c.Rule), vectors.Home).SetEquals(c.Selected), c.Name);
        });
    }

    [Fact]
    public void EnforceKeepOne()
    {
        var vectors = SharedFiles.Load<Vectors>("test-vectors/keep-rule.json");
        Assert.NotEmpty(vectors.EnforceKeepOne);
        Assert.All(vectors.EnforceKeepOne, c =>
        {
            var safe = KeepRules.EnforceKeepOne(c.Selection.ToHashSet(), c.Groups.Select(Group), Rule(c.Rule), vectors.Home);
            Assert.True(safe.SetEquals(c.Expected), c.Name);
        });
    }
}
