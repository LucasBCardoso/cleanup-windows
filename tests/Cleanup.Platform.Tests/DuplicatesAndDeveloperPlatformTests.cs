using System.Runtime.InteropServices;
using Cleanup.Core.Developer;
using Cleanup.Core.Duplicates;
using Cleanup.Core.Models;
using Cleanup.Platform.Windows;

namespace Cleanup.Platform.Tests;

public class DuplicatesAndDeveloperPlatformTests
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr securityAttributes);

    private static readonly FileIdentity Ids = RecycleBin.FileId;

    private static IReadOnlyList<DuplicateGroup> Scan(string root) =>
        DuplicateFinder.Groups(FileWalker.Files([root], new WalkOptions(1), Ids, CancellationToken.None).Files, CancellationToken.None);

    [Fact]
    public void HardLinksCountOnceWithRealFileIds()
    {
        var dir = Directory.CreateTempSubdirectory("cleanup-dupes-").FullName;
        try
        {
            var a = Path.Combine(dir, "a.bin");
            File.WriteAllBytes(a, Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray());
            Assert.True(CreateHardLink(Path.Combine(dir, "b.bin"), a, IntPtr.Zero));
            Assert.Equal(Ids(a), Ids(Path.Combine(dir, "b.bin")));
            Assert.Empty(Scan(dir));

            File.Copy(a, Path.Combine(dir, "c.bin"));
            var group = Assert.Single(Scan(dir));
            Assert.Equal(2, group.Files.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReplacedFileAtTheSamePathIsNotRemoved()
    {
        var dir = Directory.CreateTempSubdirectory("cleanup-dupes-").FullName;
        try
        {
            var data = Enumerable.Range(0, 50_000).Select(i => (byte)(i * 7)).ToArray();
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), data);
            var b = Path.Combine(dir, "b.bin");
            File.WriteAllBytes(b, data);
            var groups = Scan(dir);
            Assert.True(DuplicateVerifier.Verified(new HashSet<string> { b }, groups, Ids).SetEquals([b]));
            File.Delete(b);
            File.WriteAllBytes(b, data); // same name, size and content; a new file index
            Assert.Empty(DuplicateVerifier.Verified(new HashSet<string> { b }, groups, Ids));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FingerprintsSurviveResizingAndUnreadableFilesAreNull()
    {
        var wallpaper = PerceptualHashCalibrationTests.Wallpapers().FirstOrDefault(w => w.EndsWith("img0.jpg", StringComparison.OrdinalIgnoreCase));
        if (wallpaper is null) return;
        var dir = Directory.CreateTempSubdirectory("cleanup-photos-").FullName;
        try
        {
            var copy = Path.Combine(dir, "small.jpg");
            await PerceptualHashCalibrationTests.Reencode(wallpaper, copy, 0.25, 0.5);
            var fingerprinter = new WindowsImageFingerprinter();
            var original = fingerprinter.Fingerprint(wallpaper)!;
            var small = fingerprinter.Fingerprint(copy)!;
            Assert.True(PerceptualHash.Distance(original.Hash, small.Hash) <= SimilarityLevel.Strict.MaxDistance());
            Assert.True(original.Pixels > small.Pixels);
            var broken = Path.Combine(dir, "broken.jpg");
            File.WriteAllText(broken, "not an image");
            Assert.Null(fingerprinter.Fingerprint(broken));

            var files = FileWalker.Files([dir], new WalkOptions(1), Ids, CancellationToken.None).Files.ToList();
            files.Add(new WalkedFile(wallpaper, new FileInfo(wallpaper).Length, null, null, Ids(wallpaper)!));
            var result = SimilarPhotoFinder.Groups(files, new HashSet<string>(), SimilarityLevel.Normal.MaxDistance(), fingerprinter, CancellationToken.None);
            var group = Assert.Single(result.Groups);
            Assert.Equal(1, result.Skipped);
            Assert.Equal(wallpaper, result.BestImageIds[group.Id]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ToolRunnerReportsOutputAndStopsWaiting()
    {
        var runner = new DeveloperToolRunner();
        var result = runner.Run(Path.Combine(Environment.SystemDirectory, "hostname.exe"), [], TimeSpan.FromSeconds(20));
        Assert.True(result.Succeeded);
        Assert.Equal(Environment.MachineName, result.Stdout.Trim(), ignoreCase: true);
        Assert.False(runner.Run(Path.Combine(Environment.SystemDirectory, "where.exe"), ["no-such-tool-&-x"], TimeSpan.FromSeconds(20)).Succeeded);
        var hang = Assert.Throws<DeveloperToolException>(() =>
            runner.Run(Path.Combine(Environment.SystemDirectory, "ping.exe"), ["-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(500)));
        Assert.Contains("parou de esperar", hang.Message);
    }

    /// <summary>Read-only: lists Docker's disk usage when Docker is installed (never prunes).</summary>
    [Fact]
    public void DockerListingIsReadOnly()
    {
        var env = ScanEnvironment.Current;
        var docker = DeveloperTools.DetectDocker(env, File.Exists);
        if (docker is null) return;
        var tools = new DeveloperTools(new DeveloperToolRunner(), docker);
        try
        {
            var usage = tools.DockerUsage();
            Assert.All(usage, u => Assert.False(string.IsNullOrEmpty(u.Type)));
        }
        catch (DeveloperToolException e)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Message)); // not running is fine
        }
    }

    [Fact]
    public void WslDistributionsAreReadFromTheRegistry()
    {
        var distributions = WslRegistry.Distributions();
        Assert.All(distributions, d => Assert.False(string.IsNullOrEmpty(d.Name)));
        Assert.All(DeveloperTools.WslDisks(ScanEnvironment.Current, distributions), d => Assert.True(d.Size > 0));
    }
}
