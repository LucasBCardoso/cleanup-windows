using Cleanup.Core.Duplicates;

namespace Cleanup.Core.Tests.Duplicates;

/// <summary>Review findings for Phase 5: stale files, replaced files, nested roots, unreadable roots, hard links in photos.</summary>
public class DuplicateSafetyTests
{
    private static IReadOnlyList<DuplicateGroup> Scan(TempHome t, FakeIds ids)
    {
        var walk = FileWalker.Files([t.Home], new WalkOptions(1), ids.Identity, CancellationToken.None);
        return DuplicateFinder.Groups(walk.Files, CancellationToken.None);
    }

    [Fact]
    public void StaleSelectionsAreDroppedBeforeRemoval()
    {
        using var t = new TempHome();
        var ids = new FakeIds();
        var a = TestFiles.Write(t, @"a\x.bin", TestFiles.Pattern(50_000, 1));
        var b = TestFiles.Write(t, @"b\x.bin", TestFiles.Pattern(50_000, 1));
        var c = TestFiles.Write(t, @"c\x.bin", TestFiles.Pattern(50_000, 1));
        var groups = Scan(t, ids);
        Assert.Single(groups);

        // Unchanged: the selection stands.
        Assert.True(DuplicateVerifier.Verified(new HashSet<string> { b, c }, groups, ids.Identity).SetEquals([b, c]));

        // A selected copy changed: it's dropped.
        File.WriteAllBytes(c, TestFiles.Pattern(50_000, 9));
        Assert.True(DuplicateVerifier.Verified(new HashSet<string> { b, c }, groups, ids.Identity).SetEquals([b]));

        // The kept copy is gone: nothing in the group may be removed.
        File.Delete(a);
        Assert.Empty(DuplicateVerifier.Verified(new HashSet<string> { b }, groups, ids.Identity));
    }

    [Fact]
    public void ReplacedFileAtTheSamePathIsNotRemoved()
    {
        using var t = new TempHome();
        var ids = new FakeIds();
        TestFiles.Write(t, @"a\x.bin", TestFiles.Pattern(50_000, 1));
        var b = TestFiles.Write(t, @"b\x.bin", TestFiles.Pattern(50_000, 1));
        var groups = Scan(t, ids);
        ids.Overrides[b] = "a-new-file"; // same content and size, but a different file (new file index)
        Assert.Empty(DuplicateVerifier.Verified(new HashSet<string> { b }, groups, ids.Identity));
    }

    [Fact]
    public void PathsOutsideEveryGroupPassThrough()
    {
        using var t = new TempHome();
        var ids = new FakeIds();
        TestFiles.Write(t, @"a\x.bin", TestFiles.Pattern(500, 1));
        TestFiles.Write(t, @"b\x.bin", TestFiles.Pattern(500, 1));
        var groups = Scan(t, ids);
        Assert.True(DuplicateVerifier.Verified(new HashSet<string> { @"C:\elsewhere" }, groups, ids.Identity).SetEquals([@"C:\elsewhere"]));
    }

    [Fact]
    public void NestedAndRepeatedRootsAreWalkedOnce()
    {
        using var t = new TempHome();
        TestFiles.Write(t, @"Pictures\trip\a.bin", TestFiles.Pattern(5_000, 1));
        var pictures = Path.Combine(t.Home, "Pictures");
        var walk = FileWalker.Files([pictures, Path.Combine(pictures, "trip"), pictures + "\\", pictures.ToUpperInvariant()],
            new WalkOptions(1), new FakeIds().Identity, CancellationToken.None);
        Assert.Single(walk.Files);
    }

    [Fact]
    public void UnreadableRootsAreReported()
    {
        using var t = new TempHome();
        var missing = Path.Combine(t.Root, "Gone"); // outside home, so it isn't merged into it
        var walk = FileWalker.Files([t.Home, missing], new WalkOptions(1), new FakeIds().Identity, CancellationToken.None);
        Assert.Equal([missing], walk.UnreadableRoots);
    }

    [Fact]
    public void HardLinkedPhotosAreNotSimilarPairs()
    {
        using var t = new TempHome();
        var ids = new FakeIds();
        var a = TestFiles.Write(t, "a.jpg", TestFiles.Pattern(100, 1));
        var b = TestFiles.Write(t, "b.jpg", TestFiles.Pattern(100, 1));
        ids.Overrides[a] = ids.Overrides[b] = "vol:7";
        var images = FileWalker.Files([t.Home], new WalkOptions(1), ids.Identity, CancellationToken.None).Files;
        Assert.Equal(2, images.Count);
        var result = SimilarPhotoFinder.Groups(images, new HashSet<string>(), 64, new FakeFingerprinter(), CancellationToken.None);
        Assert.Empty(result.Groups);
    }
}
