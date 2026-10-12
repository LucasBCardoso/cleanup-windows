using Cleanup.Core.Lists;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Uninstaller;

public sealed record AppWithUsage(InstalledApp App, DateTime? LastUsedUtc);

public abstract record UsageFilter
{
    public sealed record All : UsageFilter;
    public sealed record NotUsed(int Days) : UsageFilter;
    public sealed record NoRecord : UsageFilter;

    public static readonly UsageFilter Everything = new All();
}

/// <summary>
/// "Apps esquecidos" (the Swift <c>AppUsage</c>): when each app was last opened, and the unused filters. Unknown
/// usage never counts as "unused", and system apps are left out of the unused filters.
/// </summary>
public static class AppUsage
{
    /// <summary>Hoje's "Apps esquecidos" tile: apps not used in 6 months.</summary>
    public const int ForgottenDays = 180;

    public static IReadOnlyList<AppWithUsage> Filter(IEnumerable<AppWithUsage> apps, UsageFilter filter, DateTime nowUtc)
    {
        switch (filter)
        {
            case UsageFilter.NotUsed(var days):
                var cutoff = nowUtc.AddDays(-days);
                return apps.Where(a => !a.App.IsSystemApp && a.LastUsedUtc is { } d && d <= cutoff)
                    .OrderBy(a => a.LastUsedUtc).ThenBy(a => SearchFilter.Fold(a.App.Name), StringComparer.Ordinal).ToList();
            case UsageFilter.NoRecord:
                return apps.Where(a => !a.App.IsSystemApp && a.LastUsedUtc is null)
                    .OrderBy(a => SearchFilter.Fold(a.App.Name), StringComparer.Ordinal).ToList();
            default:
                return apps.OrderBy(a => SearchFilter.Fold(a.App.Name), StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>How many apps weren't used in <paramref name="days"/> days (Hoje's tile uses 6 months).</summary>
    public static int UnusedCount(IEnumerable<AppWithUsage> apps, DateTime nowUtc, int days = ForgottenDays) =>
        Filter(apps, new UsageFilter.NotUsed(days), nowUtc).Count;

    /// <summary>
    /// The newest UserAssist run of <paramref name="app"/>: Store apps by package family ("Family!App"), programs by an
    /// executable inside their install folder (or their main executable), a Start Menu shortcut or an app id named
    /// after them ("Chrome", "Valve.Steam.Client").
    /// </summary>
    public static DateTime? LastUsed(InstalledApp app, IEnumerable<UserAssistEntry> entries, ScanEnvironment environment)
    {
        DateTime? best = null;
        var products = AppNames.ProductNames(app).Select(AppNames.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.LastRunUtc is not { } when) continue;
            if (Matches(app, entry.Name, products, environment) && (best is null || when > best)) best = when;
        }
        return best;
    }

    internal static bool Matches(InstalledApp app, string name, HashSet<string> products, ScanEnvironment environment)
    {
        if (app.Kind == AppKind.Store)
            return app.PackageFamilyName is { Length: > 0 } family && name.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase);

        var path = UserAssist.ExpandKnownFolder(name, environment);
        if (path is not null)
        {
            if (app.MainExecutable is { } exe && PathUtil.SameOrdinalIgnoreCase(path, exe)) return true;
            if (app.InstallFolder is { } folder && SafetyPolicy.IsWithin(path, folder)) return true;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                return products.Contains(AppNames.Key(Path.GetFileNameWithoutExtension(path)));
            return false;
        }
        if (name.Contains('!') || name.Contains('{')) return false; // another package, or an unresolved folder
        // An app user model id: "Chrome", "Valve.Steam.Client", "Microsoft.Windows.Explorer".
        if (products.Contains(AppNames.Key(name))) return true;
        return name.Split('.').Any(token => products.Contains(AppNames.Key(token)));
    }
}

/// <summary>One decoded UserAssist value: what was run and when it last ran.</summary>
public sealed record UserAssistEntry(string Name, int RunCount, DateTime? LastRunUtc);

/// <summary>
/// Explorer's UserAssist (<c>HKCU\…\Explorer\UserAssist\{GUID}\Count</c>): value names are ROT13-encoded paths or app
/// ids; Windows 7+ data is 72 bytes with the run count at offset 4 and the last run (a FILETIME) at offset 60.
/// </summary>
public static class UserAssist
{
    public static string Rot13(string s) => string.Create(s.Length, s, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            span[i] = c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            };
        }
    });

    /// <summary>Decodes one value; null for the bookkeeping values (<c>UEME_CTLSESSION</c>…) and old formats.</summary>
    public static UserAssistEntry? Parse(string encodedName, byte[]? data)
    {
        var name = Rot13(encodedName);
        if (name.StartsWith("UEME_", StringComparison.Ordinal) || data is null || data.Length < 68) return null;
        var count = BitConverter.ToInt32(data, 4);
        var fileTime = BitConverter.ToInt64(data, 60);
        DateTime? last = null;
        // Zero means never; anything beyond year 9999 or before 1980 is garbage.
        if (fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc())
        {
            var when = DateTime.FromFileTimeUtc(fileTime);
            if (when.Year >= 1980) last = when;
        }
        return new UserAssistEntry(name, count, last);
    }

    /// <summary>Known folders UserAssist writes as GUIDs, mapped to environment variables / profile paths.</summary>
    internal static readonly IReadOnlyDictionary<Guid, Func<ScanEnvironment, string?>> KnownFolders = new Dictionary<Guid, Func<ScanEnvironment, string?>>
    {
        [new("6D809377-6AF0-444B-8957-A3773F02200E")] = e => e.Variable("ProgramW6432") ?? e.Variable("ProgramFiles"),  // ProgramFilesX64
        [new("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E")] = e => e.Variable("ProgramFiles(x86)"),                           // ProgramFilesX86
        [new("905E63B6-C1BF-494E-B29C-65B732D3D21A")] = e => e.Variable("ProgramFiles"),                                // ProgramFiles
        [new("F38BF404-1D43-42F2-9305-67DE0B28FC23")] = e => e.SystemRoot,                                              // Windows
        [new("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7")] = e => Path.Combine(e.SystemRoot, "System32"),                    // System
        [new("D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27")] = e => Path.Combine(e.SystemRoot, "SysWOW64"),                    // SystemX86
        [new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091")] = e => e.LocalAppData,                                            // LocalAppData
        [new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D")] = e => e.RoamingAppData,                                          // RoamingAppData
        [new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97")] = e => e.ProgramData,                                             // ProgramData
        [new("5E6C858F-0E22-4760-9AFE-EA3317B67173")] = e => e.Home,                                                    // Profile
        [new("5CD7AEEE-2219-4A67-B85D-6C9CE15660CB")] = e => Path.Combine(e.LocalAppData, "Programs"),                  // UserProgramFiles
        [new("A77F5D77-2E2B-44C3-A6A2-ABA601054A51")] = e => Path.Combine(e.RoamingAppData, @"Microsoft\Windows\Start Menu\Programs"), // Programs
        [new("0139D44E-6AFE-49F2-8690-3DAFCAE6FFB8")] = e => Path.Combine(e.ProgramData, @"Microsoft\Windows\Start Menu\Programs"),    // CommonPrograms
        [new("9E3995AB-1F9C-4F13-B827-48B24B6C7174")] = e => Path.Combine(e.RoamingAppData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned"), // UserPinned
        [new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")] = e => Path.Combine(e.Home, "Desktop"),                           // Desktop
        [new("374DE290-123F-4565-9164-39C4925E467B")] = e => Path.Combine(e.Home, "Downloads"),                         // Downloads
    };

    /// <summary>
    /// "{6D809377-…}\WinRAR\WinRAR.exe" → "C:\Program Files\WinRAR\WinRAR.exe"; an absolute path stays as is; anything
    /// else (an app id, an unknown folder) is null.
    /// </summary>
    public static string? ExpandKnownFolder(string name, ScanEnvironment environment)
    {
        if (name.Length > 38 && name[0] == '{' && name[37] == '}' && Guid.TryParse(name[1..37], out var guid))
        {
            if (!KnownFolders.TryGetValue(guid, out var resolve) || resolve(environment) is not { } folder) return null;
            var rest = name[38..].TrimStart('\\');
            return rest.Length == 0 ? folder : Path.Combine(folder, rest);
        }
        return name.Length > 3 && Path.IsPathFullyQualified(name) && !name.StartsWith(@"\\", StringComparison.Ordinal) ? name : null;
    }
}
