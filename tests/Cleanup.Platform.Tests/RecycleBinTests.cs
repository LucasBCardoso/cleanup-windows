using Cleanup.Core.Cleaning;
using Cleanup.Core.History;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;
using Cleanup.Platform.Windows;

namespace Cleanup.Platform.Tests;

public class RecycleBinTests
{
    [Fact]
    public void ParsesVersion2InfoFiles()
    {
        var dir = Directory.CreateTempSubdirectory("cleanup-info-").FullName;
        try
        {
            var original = @"C:\Users\me\AppData\Local\Temp\é.txt";
            var name = original + "\0";
            using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "$IABC123.txt"))))
            {
                w.Write(2L);
                w.Write(1234L);
                w.Write(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
                w.Write(name.Length);
                w.Write(System.Text.Encoding.Unicode.GetBytes(name));
            }
            var info = RecycleBin.ReadInfo(Path.Combine(dir, "$IABC123.txt"))!;
            Assert.Equal(original, info.OriginalPath);
            Assert.Equal(1234, info.Size);
            Assert.Equal(Path.Combine(dir, "$RABC123.txt"), info.ItemPath);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OwnRecycleBinFolderIsStrict()
    {
        var sid = RecycleBin.CurrentUserSid;
        Assert.False(RecycleBin.IsOwnRecycleBinFolder(@"C:\$Recycle.Bin\S-1-5-21-0-0-0-500"));
        Assert.False(RecycleBin.IsOwnRecycleBinFolder($@"C:\Temp\$Recycle.Bin\{sid}"));
        Assert.False(RecycleBin.IsOwnRecycleBinFolder($@"C:\$Recycle.Bin\..\$Recycle.Bin\{sid}"));
    }

    /// <summary>
    /// Real round trip through this user's Recycle Bin. Opt-in (CLEANUP_REAL_RECYCLE=1) because it touches the
    /// actual Recycle Bin, like CLEANUP_REAL_TRASH on macOS.
    /// </summary>
    [Fact]
    public void RecycleRecordAndRestoreRoundTrip()
    {
        if (Environment.GetEnvironmentVariable("CLEANUP_REAL_RECYCLE") != "1") return;
        var env = ScanEnvironment.Current;
        var dir = Path.Combine(env.Temp, "cleanup-roundtrip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "item.txt");
        File.WriteAllText(file, "hello");
        try
        {
            var policy = new SafetyPolicy(env);
            var item = new ScanItem
            {
                Path = file, Size = 5, RuleId = "t", Category = Core.Rules.ScanCategory.SystemJunk, Risk = Core.Rules.RiskLevel.Safe,
            };
            var result = new Cleaner(policy, new WindowsFileRemover()).Clean([item]);
            Assert.Single(result.Removed);
            Assert.False(File.Exists(file));
            var record = CleanupRecord.From(result, null, "test", RecycleBin.FileId)!;
            var entry = Assert.Single(record.Entries);
            Assert.NotNull(entry.TrashPath);
            Assert.NotNull(entry.FileId);

            var context = RecycleBin.RestoreContext(policy);
            Assert.True(Restorer.IsRestorable(entry, context));
            var restored = Restorer.Restore(record, context);
            Assert.Single(restored.Restored);
            Assert.Equal("hello", File.ReadAllText(file));
            Assert.False(File.Exists(entry.TrashPath));
        }
        finally { Directory.Delete(dir, true); }
    }
}
