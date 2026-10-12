using System.Security.Cryptography;

namespace Cleanup.Core.Tests.Shared;

/// <summary>
/// <c>shared/</c> is vendored from the macOS repo by <c>scripts/sync-shared.ps1</c>. A file edited here instead of
/// there, or a sync that was not recorded, fails this test (design §4).
/// </summary>
public class SharedSourceTests
{
    private static (string Commit, Dictionary<string, string> Hashes) ReadSource()
    {
        var lines = File.ReadAllLines(Path.Combine(SharedFiles.SharedRoot, "SOURCE"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var commit = lines.Single(l => l.StartsWith("commit ")).Split(' ')[1];
        var hashes = lines.Where(l => l.Contains("  "))
            .Select(l => l.Split("  ", 2))
            .ToDictionary(p => p[1], p => p[0]);
        return (commit, hashes);
    }

    [Fact]
    public void SourceRecordsAFullCommit()
    {
        var (commit, _) = ReadSource();
        Assert.Matches("^[0-9a-f]{40}$", commit);
    }

    [Fact]
    public void VendoredFilesMatchTheRecordedHashes()
    {
        var (_, recorded) = ReadSource();
        var root = SharedFiles.SharedRoot;
        var actual = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => f != "SOURCE")
            .ToDictionary(f => f, f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, f)))));

        Assert.Equal(recorded.Keys.Order(), actual.Keys.Order());
        Assert.All(actual, kv => Assert.True(recorded[kv.Key] == kv.Value,
            $"shared/{kv.Key} differs from the macOS repo; change it there and run scripts/sync-shared.ps1"));
    }
}
