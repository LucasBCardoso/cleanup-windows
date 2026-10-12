using Cleanup.Core.Cleaning;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Tests;

/// <summary>A fake profile in a temporary folder, with the environment variables rules use pointing inside it.</summary>
public sealed class TempHome : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cleanup-tests-" + Guid.NewGuid().ToString("N")[..10]);
    public string Home => Path.Combine(Root, "Users", "me");
    public string Windows => Path.Combine(Root, "Windows");
    public string ProgramData => Path.Combine(Root, "ProgramData");
    public ScanEnvironment Environment { get; }

    public TempHome()
    {
        Directory.CreateDirectory(Home);
        Environment = new ScanEnvironment(Home, new Dictionary<string, string>
        {
            ["USERPROFILE"] = Home,
            ["LOCALAPPDATA"] = Path.Combine(Home, @"AppData\Local"),
            ["APPDATA"] = Path.Combine(Home, @"AppData\Roaming"),
            ["TEMP"] = Path.Combine(Home, @"AppData\Local\Temp"),
            ["SystemRoot"] = Windows,
            ["PROGRAMDATA"] = ProgramData,
            ["ProgramFiles"] = Path.Combine(Root, "Program Files"),
        });
    }

    public SafetyPolicy Policy(params string[] ignored) => new(Environment, ignored);

    /// <summary>Creates a file of <paramref name="size"/> bytes (relative to home unless absolute).</summary>
    public string File(string relative, int size = 10, DateTime? modifiedUtc = null)
    {
        var path = Path.IsPathFullyQualified(relative) ? relative : Path.Combine(Home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        if (modifiedUtc is { } m) System.IO.File.SetLastWriteTimeUtc(path, m);
        return path;
    }

    public string Dir(string relative)
    {
        var path = Path.IsPathFullyQualified(relative) ? relative : Path.Combine(Home, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Records removals instead of touching the Recycle Bin.</summary>
public sealed class FakeRemover : IFileRemover
{
    public List<string> Recycled { get; } = [];
    public List<string> Deleted { get; } = [];
    public bool Emptied { get; private set; }
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths) =>
        paths.Select(p =>
        {
            if (Locked.Contains(p)) return new RecycleOutcome(p, false, null, "em uso por outro programa");
            Recycled.Add(p);
            return new RecycleOutcome(p, true, @"C:\$Recycle.Bin\S-1-5-21-1\$R" + Recycled.Count, null);
        }).ToList();

    public void Delete(string path) => Deleted.Add(path);
    public void EmptyRecycleBin() => Emptied = true;
}

public static class Items
{
    public static ScanItem At(string path, long size = 10, bool admin = false, RiskLevel risk = RiskLevel.Safe) => new()
    {
        Path = path, Size = size, RuleId = "test", Category = ScanCategory.SystemJunk, Risk = risk, RequiresAdmin = admin,
    };
}
