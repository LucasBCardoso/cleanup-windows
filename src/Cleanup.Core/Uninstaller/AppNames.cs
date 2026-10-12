using System.Text.RegularExpressions;
using Cleanup.Core.Lists;

namespace Cleanup.Core.Uninstaller;

/// <summary>
/// The names an app's leftovers are found by: its product names (display name without version or architecture,
/// install folder, main executable) and its vendor (publisher without "Inc.", "Corporation"…). Windows has no bundle
/// identifiers, so matching is by exact folder name (ignoring case and accents), never by prefix.
/// </summary>
public static partial class AppNames
{
    /// <summary>Names shorter than this are too generic to match on ("Go", "Git", "VLC"…), as on macOS.</summary>
    public const int MinimumNameLength = 4;

    /// <summary>Folder and key names that many programs share: never matched as an app's own.</summary>
    internal static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "temp", "tmp", "cache", "caches", "programs", "packages", "program",
        "app", "apps", "application", "applications", "launcher", "update", "updater", "updates", "setup", "install",
        "installer", "uninstall", "uninstaller", "bin", "data", "common", "common files", "shared", "local", "roaming",
        "locallow", "default", "user", "users", "help", "tools", "plugins", "client", "service", "services", "main",
        "logs", "settings", "config", "crashdumps", "crashpad", "google", "apple", "intel", "nvidia", "amd", "software",
        "classes", "policies", "wow6432node", "clients", "registeredapplications", "appdatalow", "cleanup", "desktop",
        "documents", "downloads", "pictures", "music", "videos", "onedrive", "start menu", "startup", "system",
        "system32", "driver", "drivers", "runtime", "framework", "redistributable", "sdk",
        "package cache", "explorer", "inetcache", "history", "games", "game",
    };

    private static readonly string[] CompanySuffixes =
    [
        "incorporated", "inc", "corporation", "corp", "company", "co", "llc", "l.l.c", "ltd", "limited", "gmbh", "ag", "sa",
        "s.a", "s.a.s", "sas", "bv", "b.v", "ab", "oy", "srl", "s.r.l", "spa", "s.p.a", "pty", "plc", "kk", "k.k", "ltda",
        "me", "eireli", "sarl", "software", "technologies", "technology", "labs", "studio", "studios",
    ];

    /// <summary>"Mozilla Firefox (x64 pt-BR)" → "Mozilla Firefox"; "7-Zip 23.01 (x64)" → "7-Zip"; "Foo v2.1 64-bit" → "Foo".</summary>
    public static string CleanProductName(string displayName)
    {
        var s = Parenthesized().Replace(displayName, " ");
        var words = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && (VersionToken().IsMatch(words[^1]) || ArchToken().IsMatch(words[^1]) || words[^1] is "-" or "–"))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words).Trim(' ', '-', '–', ',');
    }

    /// <summary>"Docker Inc." → "Docker"; "Mozilla Corporation" → "Mozilla"; "Google LLC" → "Google".</summary>
    public static string? Vendor(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return null;
        var s = publisher.Trim();
        if (s.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) // MSIX publisher distinguished name
        {
            var end = s.IndexOf(',');
            s = (end < 0 ? s[3..] : s[3..end]).Trim().Trim('"');
        }
        var comma = s.IndexOf(',');
        if (comma > 0) s = s[..comma];
        var words = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && CompanySuffixes.Contains(words[^1].TrimEnd('.').ToLowerInvariant()))
            words.RemoveAt(words.Count - 1);
        var vendor = string.Join(' ', words).Trim().TrimEnd('.', ',');
        return vendor.Length == 0 ? null : vendor;
    }

    /// <summary>Product names to look for, most specific first, each long enough and not generic.</summary>
    public static IReadOnlyList<string> ProductNames(InstalledApp app)
    {
        var names = new List<string>();
        var clean = CleanProductName(app.Name);
        names.Add(clean);
        names.Add(app.Name);
        if (Vendor(app.Publisher) is { } vendor && clean.StartsWith(vendor + " ", StringComparison.OrdinalIgnoreCase))
            names.Add(clean[(vendor.Length + 1)..]); // "Google Chrome" → "Chrome"
        if (app.InstallFolder is { } folder) names.Add(Path.GetFileName(folder));
        if (app.MainExecutable is { } exe) names.Add(Path.GetFileNameWithoutExtension(exe));
        return Usable(names);
    }

    /// <summary>Vendor names: the cleaned publisher and, for "Vendor\Product" install folders, the parent folder name.</summary>
    public static IReadOnlyList<string> VendorNames(InstalledApp app)
    {
        var names = new List<string>();
        if (Vendor(app.Publisher) is { } vendor) names.Add(vendor);
        if (app.InstallFolder is { } folder && Path.GetDirectoryName(folder) is { } parent && Path.GetFileName(parent) is { Length: > 0 } p
            && !p.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) && !p.Equals("Programs", StringComparison.OrdinalIgnoreCase))
            names.Add(p);
        // Vendors may be short ("AMD", "HP") or broad ("Microsoft", "Google"): only the product below them is ever matched.
        return names.Where(n => n.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> Usable(IEnumerable<string> names) => names
        .Select(n => n.Trim())
        .Where(n => n.Length >= MinimumNameLength && !Generic.Contains(Key(n)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Comparison key: case- and accent-insensitive, inner whitespace collapsed.</summary>
    public static string Key(string name) =>
        string.Join(' ', SearchFilter.Fold(name).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool Same(string a, string b) => Key(a) == Key(b);

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Parenthesized();

    [GeneratedRegex(@"^v?\d+([.\-_]\d+)*[a-z]?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionToken();

    [GeneratedRegex(@"^(x64|x86|x86_64|amd64|arm64|64-?bit|32-?bit|win64|win32)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchToken();
}
