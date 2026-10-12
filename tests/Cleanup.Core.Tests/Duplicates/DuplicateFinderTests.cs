using System.Diagnostics;
using Cleanup.Core.Duplicates;

namespace Cleanup.Core.Tests.Duplicates;

/// <summary>File identities for tests: the path, unless a test says two paths are one file (a hard link) or a path
/// now holds a different file.</summary>
public sealed class FakeIds
{
    public Dictionary<string, string> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FileIdentity Identity => path => Overrides.TryGetValue(path, out var id) ? id : "id:" + path.ToLowerInvariant();
}

public static class TestFiles
{
    public static byte[] Pattern(int count, int seed) => Enumerable.Range(0, count).Select(i => (byte)(i * 31 + seed)).ToArray();

    public static string Write(TempHome t, string relative, byte[] data)
    {
        var path = Path.Combine(t.Home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>A junction (no privilege needed, unlike symlinks), made with mklink /J.</summary>
    public static bool Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}

public class DuplicateFinderTests
{
    private static IReadOnlyList<DuplicateGroup> Groups(TempHome t, FakeIds? ids = null, long minSize = 1)
    {
        var walk = FileWalker.Files([t.Home], new WalkOptions(minSize), (ids ?? new FakeIds()).Identity, CancellationToken.None);
        return DuplicateFinder.Groups(walk.Files, CancellationToken.None);
    }

    [Fact]
    public void FindsIdenticalFilesAcrossFolders()
    {
        using var t = new TempHome();
        var data = TestFiles.Pattern(300_000, 1);
        var a = TestFiles.Write(t, @"a\movie.bin", data);
        var b = TestFiles.Write(t, @"b\movie copy.bin", data);
        TestFiles.Write(t, @"c\other.bin", TestFiles.Pattern(300_000, 2));
        var groups = Groups(t);
        var group = Assert.Single(groups);
        Assert.Equal([a, b], group.Files.Select(f => f.Path));
        Assert.Equal(300_000, group.Wasted);
    }

    [Fact]
    public void SameHeadAndTailButDifferentMiddleIsNotADuplicate()
    {
        using var t = new TempHome();
        var data = TestFiles.Pattern(400_000, 1);
        var changed = (byte[])data.Clone();
        changed[200_000] ^= 0xFF;
        TestFiles.Write(t, @"a\x.bin", data);
        TestFiles.Write(t, @"b\x.bin", changed);
        Assert.Equal(DuplicateFinder.PartialHash(Path.Combine(t.Home, @"a\x.bin"), data.Length),
            DuplicateFinder.PartialHash(Path.Combine(t.Home, @"b\x.bin"), data.Length));
        Assert.Empty(Groups(t));
    }

    [Fact]
    public void HardLinksAndSymlinksAreNotDuplicates()
    {
        using var t = new TempHome();
        var data = TestFiles.Pattern(10_000, 3);
        var a = TestFiles.Write(t, @"a\x.bin", data);
        var b = TestFiles.Write(t, @"b\x.bin", data);
        var ids = new FakeIds();
        ids.Overrides[a] = "vol:1";
        ids.Overrides[b] = "vol:1"; // a hard link: one file under two names
        Assert.Empty(Groups(t, ids));

        // A junction to a folder holding a copy is never entered.
        var outside = Path.Combine(t.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "x.bin"), data);
        Assert.True(TestFiles.Junction(Path.Combine(t.Home, "link"), outside));
        Assert.Empty(Groups(t, ids));
    }

    [Fact]
    public void HiddenAndDotFilesAreSkipped()
    {
        using var t = new TempHome();
        var data = TestFiles.Pattern(10_000, 4);
        TestFiles.Write(t, @"a\x.bin", data);
        TestFiles.Write(t, @".cache\x.bin", data);
        var hidden = TestFiles.Write(t, @"b\x.bin", data);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Assert.Empty(Groups(t));
    }

    [Fact]
    public void MinimumSizeAndEntryLimit()
    {
        using var t = new TempHome();
        TestFiles.Write(t, @"a\small.bin", TestFiles.Pattern(500, 1));
        TestFiles.Write(t, @"b\small.bin", TestFiles.Pattern(500, 1));
        Assert.Single(Groups(t));
        Assert.Empty(Groups(t, minSize: 1000));
        for (var i = 0; i < 20; i++) TestFiles.Write(t, $@"wide\f{i}.txt", [1]);
        var capped = FileWalker.Files([t.Home], new WalkOptions(1, EntryLimit: 10), new FakeIds().Identity, CancellationToken.None);
        Assert.Equal([t.Home], capped.PartialRoots);
    }

    [Fact]
    public void CancellationStopsTheFinder()
    {
        using var t = new TempHome();
        TestFiles.Write(t, @"a\x.bin", TestFiles.Pattern(1000, 1));
        TestFiles.Write(t, @"b\x.bin", TestFiles.Pattern(1000, 1));
        var walk = FileWalker.Files([t.Home], new WalkOptions(1), new FakeIds().Identity, CancellationToken.None);
        Assert.Throws<OperationCanceledException>(() => DuplicateFinder.Groups(walk.Files, new CancellationToken(canceled: true)));
        var cancelledWalk = FileWalker.Files([t.Home], new WalkOptions(1), new FakeIds().Identity, new CancellationToken(canceled: true));
        Assert.Equal([t.Home], cancelledWalk.PartialRoots);
    }

    [Fact]
    public void WalkedFilesCarryDatesAndAreFilteredByExtension()
    {
        using var t = new TempHome();
        var photo = TestFiles.Write(t, @"Pictures\a.JPG", TestFiles.Pattern(100, 1));
        TestFiles.Write(t, @"Pictures\notes.txt", TestFiles.Pattern(100, 1));
        var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(photo, created);
        var walk = FileWalker.Files([t.Home], new WalkOptions(1, Extensions: new HashSet<string> { "jpg" }), new FakeIds().Identity, CancellationToken.None);
        var file = Assert.Single(walk.Files);
        Assert.Equal(photo, file.Path);
        Assert.Equal(created, file.CreatedUtc);
        Assert.NotNull(file.ModifiedUtc);
    }

    [Fact]
    public void CloudOnlyAttributeIsRecognized()
    {
        // OneDrive "online only" files carry RECALL_ON_DATA_ACCESS; the walker never opens them (can't be set in a test).
        Assert.True(Cleanup.Core.Scanning.FileMetrics.IsCloudOnly((FileAttributes)0x00400000));
        Assert.False(Cleanup.Core.Scanning.FileMetrics.IsCloudOnly(FileAttributes.Archive));
    }
}
