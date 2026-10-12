using Cleanup.Platform.Windows;

namespace Cleanup.Platform.Tests;

public class FastRecycleTests
{
    [Fact]
    public void InfoFileRoundTripsThroughTheReader()
    {
        var dir = Directory.CreateTempSubdirectory("cleanup-fastinfo-").FullName;
        try
        {
            var deleted = new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);
            var info = Path.Combine(dir, "$IABC123.tmp");
            File.WriteAllBytes(info, FastRecycle.InfoBytes(@"C:\Users\me\AppData\Local\Temp\ação.tmp", 4096, deleted));
            var parsed = RecycleBin.ReadInfo(info)!;
            Assert.Equal(@"C:\Users\me\AppData\Local\Temp\ação.tmp", parsed.OriginalPath);
            Assert.Equal(4096, parsed.Size);
            Assert.Equal(deleted, parsed.DeletedUtc);
            Assert.Equal(Path.Combine(dir, "$RABC123.tmp"), parsed.ItemPath);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Real Recycle Bin (opt-in, CLEANUP_REAL_RECYCLE=1): a folder with a file held open stays whole.</summary>
    [Fact]
    public void AFolderWithAnOpenFileStaysWhole()
    {
        if (Environment.GetEnvironmentVariable("CLEANUP_REAL_RECYCLE") != "1") return;
        var dir = Directory.CreateTempSubdirectory("cleanup-fastlock-").FullName;
        try
        {
            var file = Path.Combine(dir, "busy.txt");
            File.WriteAllText(file, "x");
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = FastRecycle.TryMove(dir, 1);
                Assert.IsType<FastRecycle.Failed>(result);
            }
            Assert.True(File.Exists(file));
        }
        finally { Directory.Delete(dir, true); }
    }
}
