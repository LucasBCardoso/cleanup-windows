using System.Diagnostics;
using Cleanup.Core.SpaceLens;

namespace Cleanup.Platform.Tests;

public class SpaceTreeTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("cleanup-spacetree-").FullName;

    public void Dispose()
    {
        Cleanup.Platform.Windows.HandleDelete.Delete(root); // removes the junction as a link, never its target
    }

    private string File(string relative, int size)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public async Task MeasuresEveryLevelOnceAndNeverFollowsLinks()
    {
        File(@"a\x.bin", 100);
        File(@"a\b\y.bin", 50);
        File(@"a\b\c\z.bin", 25);
        File(@"top.bin", 10);
        File(@"elsewhere\huge.bin", 5000);
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", Path.Combine(root, @"a\link"), Path.Combine(root, "elsewhere")])
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!) p.WaitForExit();

        var tree = await SpaceTree.BuildAsync(Path.Combine(root, "a"));
        Assert.Equal(175, tree.Total.Bytes);
        var children = tree.Children(Path.Combine(root, "a"));
        Assert.Equal(75, children.Single(c => c.Name == "b").Size);
        Assert.True(children.Single(c => c.Name == "link").IsLink);
        Assert.Equal(0, children.Single(c => c.Name == "link").Size);
        Assert.Equal(25, tree.Children(Path.Combine(root, @"a\b")).Single(c => c.Name == "c").Size);

        // Going up reuses what was measured below, and adds the rest.
        var parent = await SpaceTree.BuildAsync(root, reuse: tree);
        Assert.Equal(175 + 10 + 5000, parent.Total.Bytes);
        Assert.Equal(175, parent.Children(root).Single(c => c.Name == "a").Size);
    }
}
