using System.Diagnostics;
using Cleanup.Platform.Windows;

namespace Cleanup.Platform.Tests;

public class HandleDeleteTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("cleanup-handledelete-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void DeletesATreeIncludingReadOnlyFiles()
    {
        var tree = Path.Combine(root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
        File.WriteAllText(Path.Combine(tree, "a", "b", "f.txt"), "x");
        File.WriteAllText(Path.Combine(tree, "ro.txt"), "x");
        File.SetAttributes(Path.Combine(tree, "ro.txt"), FileAttributes.ReadOnly);
        HandleDelete.Delete(tree);
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void AJunctionInsideIsRemovedAsALinkNeverEntered()
    {
        var precious = Path.Combine(root, "precious");
        Directory.CreateDirectory(precious);
        File.WriteAllText(Path.Combine(precious, "keep.txt"), "x");
        var tree = Path.Combine(root, "tree");
        Directory.CreateDirectory(tree);
        Junction(Path.Combine(tree, "link"), precious);
        HandleDelete.Delete(tree);
        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(precious, "keep.txt")));
    }

    [Fact]
    public void ATopLevelJunctionIsRemovedAsALink()
    {
        var precious = Path.Combine(root, "precious2");
        Directory.CreateDirectory(precious);
        File.WriteAllText(Path.Combine(precious, "keep.txt"), "x");
        var link = Path.Combine(root, "link2");
        Junction(link, precious);
        HandleDelete.Delete(link);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(precious, "keep.txt")));
    }

    [Fact]
    public void MissingPathIsReported() =>
        Assert.Throws<FileNotFoundException>(() => HandleDelete.Delete(Path.Combine(root, "nope")));
}
