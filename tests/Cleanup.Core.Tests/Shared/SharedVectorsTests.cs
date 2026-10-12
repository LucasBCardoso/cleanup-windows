using Cleanup.Core.Safety;
using Cleanup.Core.Updater;

namespace Cleanup.Core.Tests.Shared;

/// <summary>
/// Runs the language-neutral cases in <c>shared/test-vectors/</c>; the macOS version runs the same files
/// (<c>Tests/CleanupCoreTests/SharedVectorsTests.swift</c>). Same case names as the Swift suite.
/// </summary>
public class SharedVectorsTests
{
    private sealed record VersionVectors(List<VersionVectors.CompareCase> Compare, List<VersionVectors.IsNewerCase> IsNewer)
    {
        public sealed record CompareCase(string Lhs, string Rhs, string Expected);
        public sealed record IsNewerCase(string Candidate, string Installed, string? CandidateBuild, string? InstalledBuild, bool Expected);
    }

    private sealed record ContainmentVectors(List<ContainmentVectors.Case> Cases)
    {
        public sealed record Case(string Path, string Root, bool Expected);
    }

    private static string Name(int result) => result switch
    {
        < 0 => "ascending",
        0 => "same",
        > 0 => "descending",
    };

    [Fact]
    public void VersionComparison()
    {
        var vectors = SharedFiles.Load<VersionVectors>("test-vectors/version-compare.json");
        Assert.NotEmpty(vectors.Compare);
        Assert.All(vectors.Compare, c =>
            Assert.True(Name(VersionComparator.Compare(c.Lhs, c.Rhs)) == c.Expected, $"{c.Lhs} vs {c.Rhs}: expected {c.Expected}"));
    }

    [Fact]
    public void VersionIsNewer()
    {
        var vectors = SharedFiles.Load<VersionVectors>("test-vectors/version-compare.json");
        Assert.NotEmpty(vectors.IsNewer);
        Assert.All(vectors.IsNewer, c =>
            Assert.True(VersionComparator.IsNewer(c.Candidate, c.CandidateBuild, c.Installed, c.InstalledBuild) == c.Expected,
                $"{c.Candidate} ({c.CandidateBuild ?? "-"}) vs {c.Installed} ({c.InstalledBuild ?? "-"})"));
    }

    [Fact]
    public void PathContainment()
    {
        var vectors = SharedFiles.Load<ContainmentVectors>("test-vectors/path-containment.json");
        Assert.NotEmpty(vectors.Cases);
        Assert.All(vectors.Cases, c =>
        {
            Assert.True(SafetyPolicy.IsWithin(c.Path, c.Root) == c.Expected, $"{c.Path} in {c.Root}");
            // The Windows spelling of the same case gives the same answer (backslashes, any case).
            var winPath = "C:" + c.Path.Replace('/', '\\').ToUpperInvariant();
            var winRoot = "c:" + c.Root.Replace('/', '\\');
            Assert.True(SafetyPolicy.IsWithin(winPath, winRoot) == c.Expected, $"{winPath} in {winRoot}");
        });
    }

    /// <summary>
    /// Vectors whose Windows implementation comes in a later phase (search/selection W1–W2, keep rule W5, batch W6,
    /// health W8, autoclean W8b, deeplinks W8e, disk space W8d). Until then they must at least be well-formed:
    /// a description and at least one non-empty case list.
    /// </summary>
    [Theory]
    
    
    [InlineData("privileged-batch.json")]
    
    
    
    
    public void PendingVectorsAreWellFormed(string file)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(SharedFiles.Read("test-vectors/" + file));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("description", out var description) && description.GetString() is { Length: > 0 },
            $"{file} has no description");
        var lists = root.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Array).ToList();
        Assert.Contains(lists, p => p.Value.GetArrayLength() > 0);
    }

    [Fact]
    public void EveryVectorFileIsCovered()
    {
        string[] covered =
        [
            "version-compare.json", "path-containment.json", "search-filter.json", "selection.json", "keep-rule.json",
            "privileged-batch.json", "health.json", "autoclean.json", "deeplinks.json", "disk-space.json",
        ];
        var present = Directory.GetFiles(Path.Combine(SharedFiles.SharedRoot, "test-vectors"), "*.json").Select(Path.GetFileName);
        Assert.Equal(covered.Order(), present.Order()!);
    }
}
