using System.Text;
using Cleanup.Core.Uninstaller;

namespace Cleanup.Core.Tests.Uninstaller;

/// <summary>Mirrors the Swift "App usage" suite, plus UserAssist decoding (the Windows source of "last used").</summary>
public class AppUsageTests
{
    private static readonly DateTime Now = new(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime DaysAgo(double d) => Now.AddDays(-d);

    private static List<AppWithUsage> Sample =>
    [
        new(Apps.Win32("Fresh"), DaysAgo(3)),
        new(Apps.Win32("Stale"), DaysAgo(200)),
        new(Apps.Win32("Ancient"), DaysAgo(400)),
        new(Apps.Win32("Boundary"), DaysAgo(180)),
        new(Apps.Win32("Unknown"), null),
        new(Apps.Win32("Notepad", "Microsoft Windows", system: true), DaysAgo(900)),
    ];

    private static List<string> Names(UsageFilter filter) => AppUsage.Filter(Sample, filter, Now).Select(a => a.App.Name).ToList();

    [Fact]
    public void AllKeepsEverythingByName() =>
        Assert.Equal(["Ancient", "Boundary", "Fresh", "Notepad", "Stale", "Unknown"], Names(UsageFilter.Everything));

    [Fact]
    public void NotUsedListsOldestFirstAndSkipsSystemAndUnknown()
    {
        Assert.Equal(["Ancient", "Stale", "Boundary"], Names(new UsageFilter.NotUsed(180)));
        Assert.Equal(["Ancient"], Names(new UsageFilter.NotUsed(365)));
    }

    [Fact]
    public void NoRecordListsOnlyUnknownNonSystemApps() => Assert.Equal(["Unknown"], Names(new UsageFilter.NoRecord()));

    /// <summary>UserAssist cleared or off: no dates at all must not make every app "unused".</summary>
    [Fact]
    public void NoDatesMeansNothingIsUnused()
    {
        var blind = Sample.Select(a => a with { LastUsedUtc = null });
        Assert.Empty(AppUsage.Filter(blind, new UsageFilter.NotUsed(90), Now));
    }

    [Fact]
    public void UnusedCountForHojeIsSixMonths() => Assert.Equal(3, AppUsage.UnusedCount(Sample, Now));

    [Fact]
    public void Rot13RoundTripsAndLeavesPunctuation()
    {
        Assert.Equal("{6Q809377-6NS0-444O-8957-N3773S02200R}\\JvaENE\\JvaENE.rkr", UserAssist.Rot13(@"{6D809377-6AF0-444B-8957-A3773F02200E}\WinRAR\WinRAR.exe"));
        Assert.Equal("Chrome", UserAssist.Rot13(UserAssist.Rot13("Chrome")));
    }

    internal static byte[] Value(int count, DateTime? lastRunUtc)
    {
        var data = new byte[72];
        BitConverter.GetBytes(count).CopyTo(data, 4);
        if (lastRunUtc is { } d) BitConverter.GetBytes(d.ToFileTimeUtc()).CopyTo(data, 60);
        return data;
    }

    [Fact]
    public void ParsesWindows10FormatWithFiletimeAtOffset60()
    {
        var when = new DateTime(2026, 8, 9, 20, 42, 53, DateTimeKind.Utc);
        var entry = UserAssist.Parse(UserAssist.Rot13(@"C:\Tools\app.exe"), Value(7, when))!;
        Assert.Equal(@"C:\Tools\app.exe", entry.Name);
        Assert.Equal(7, entry.RunCount);
        Assert.Equal(when, entry.LastRunUtc);
    }

    [Fact]
    public void SkipsBookkeepingValuesShortDataAndZeroDates()
    {
        Assert.Null(UserAssist.Parse(UserAssist.Rot13("UEME_CTLSESSION"), new byte[1612]));
        Assert.Null(UserAssist.Parse(UserAssist.Rot13("Chrome"), new byte[16])); // Windows XP format
        Assert.Null(UserAssist.Parse(UserAssist.Rot13("Chrome"), null));
        Assert.Null(UserAssist.Parse(UserAssist.Rot13("Chrome"), Value(0, null))!.LastRunUtc);
    }

    [Fact]
    public void ExpandsKnownFolderGuids()
    {
        using var t = new TempHome();
        var x86 = t.Environment;
        Assert.Equal(Path.Combine(t.Home, @"AppData\Local\Programs\Foo\foo.exe"),
            UserAssist.ExpandKnownFolder(@"{5CD7AEEE-2219-4A67-B85D-6C9CE15660CB}\Foo\foo.exe", x86));
        Assert.Equal(Path.Combine(t.Windows, @"System32\cmd.exe"), UserAssist.ExpandKnownFolder(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\cmd.exe", x86));
        Assert.Equal(@"D:\Games\x.exe", UserAssist.ExpandKnownFolder(@"D:\Games\x.exe", x86));
        Assert.Null(UserAssist.ExpandKnownFolder(@"{00000000-0000-0000-0000-000000000001}\x.exe", x86));
        Assert.Null(UserAssist.ExpandKnownFolder("Chrome", x86));
    }

    [Fact]
    public void LastUsedMatchesInstallFolderShortcutAndAppIdTakingTheNewest()
    {
        using var t = new TempHome();
        var programs = Path.Combine(t.Home, @"AppData\Local\Programs");
        var app = Apps.Win32("WinRAR 7.01 (64-bit)", "win.rar GmbH", folder: Path.Combine(programs, "WinRAR"));
        var chrome = Apps.Win32("Google Chrome", "Google LLC", exe: @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        UserAssistEntry E(string name, int days) => new(name, 1, DaysAgo(days));
        List<UserAssistEntry> entries =
        [
            E(@"{5CD7AEEE-2219-4A67-B85D-6C9CE15660CB}\WinRAR\WinRAR.exe", 40),
            E(@"{A77F5D77-2E2B-44C3-A6A2-ABA601054A51}\WinRAR\WinRAR.lnk", 20),
            E(@"{5CD7AEEE-2219-4A67-B85D-6C9CE15660CB}\WinRARX\other.exe", 1), // a sibling folder is not inside
            E("Chrome", 2),
            E("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", 1),
        ];
        Assert.Equal(DaysAgo(20), AppUsage.LastUsed(app, entries, t.Environment));
        Assert.Equal(DaysAgo(2), AppUsage.LastUsed(chrome, entries, t.Environment));
        Assert.Null(AppUsage.LastUsed(Apps.Win32("Nunca Aberto"), entries, t.Environment));
    }

    [Fact]
    public void StoreAppsMatchByPackageFamilyOnly()
    {
        using var t = new TempHome();
        var calc = Apps.Store("Calculadora", "Microsoft.WindowsCalculator_8wekyb3d8bbwe");
        List<UserAssistEntry> entries =
        [
            new("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", 3, DaysAgo(5)),
            new("Microsoft.WindowsCalculatorX_8wekyb3d8bbwe!App", 3, DaysAgo(1)),
            new("Calculadora", 3, DaysAgo(1)),
        ];
        Assert.Equal(DaysAgo(5), AppUsage.LastUsed(calc, entries, t.Environment));
    }

    [Fact]
    public void Rot13IsAsciiOnly() => Assert.Equal("Ãçãb", UserAssist.Rot13(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("Ãção"))));
}
