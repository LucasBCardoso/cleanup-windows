using Cleanup.Core.Models;

namespace Cleanup.Core.Safety;

public abstract record SafetyVerdict
{
    public sealed record Allowed : SafetyVerdict;
    public sealed record Denied(string Reason) : SafetyVerdict;

    public static readonly SafetyVerdict Allow = new Allowed();
    public bool IsAllowed => this is Allowed;
}

/// <summary>
/// Last line of defence before anything is removed: every item is re-checked here at clean time, whichever scanner
/// found it (invariant 2). Windows counterpart of the Swift <c>SafetyPolicy</c> (design §6).
/// </summary>
public sealed partial class SafetyPolicy
{
    public ScanEnvironment Environment { get; }
    public IReadOnlyCollection<string> IgnoredPaths { get; }
    public string? BlockedReason { get; }

    /// <summary>Folders under the Windows folder whose contents may be cleaned (by the elevated helper only).</summary>
    public IReadOnlyList<string> SystemCleanableRoots { get; }

    /// <summary>Home-relative trees never cleaned by anything: secrets and credentials.</summary>
    internal static readonly string[] ProtectedHomeTrees =
    [
        ".ssh", ".gnupg", @"AppData\Roaming\Microsoft\Credentials", @"AppData\Local\Microsoft\Credentials",
        @"AppData\Roaming\Microsoft\Protect", @"AppData\Roaming\Microsoft\Crypto",
        @"AppData\Roaming\Microsoft\SystemCertificates", @"AppData\Local\Microsoft\Vault",
        @"AppData\Roaming\Microsoft\Vault", @"AppData\Local\Microsoft\Windows\INetCookies",
    ];

    /// <summary>Home-relative containers: their children may be cleaned, the folder itself never.</summary>
    internal static readonly string[] ProtectedHomeContainers =
    [
        "", "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow", @"AppData\Local\Temp",
        @"AppData\Local\Microsoft", @"AppData\Roaming\Microsoft", @"AppData\Local\Programs", @"AppData\Local\Packages",
        @"AppData\Local\CrashDumps", @"AppData\Local\D3DSCache", @"AppData\Local\Microsoft\Windows",
        @"AppData\Local\Microsoft\Windows\Explorer", @"AppData\Local\Microsoft\Windows\WER",
        @"AppData\Local\Microsoft\Windows\WER\ReportArchive", @"AppData\Local\Microsoft\Windows\WER\ReportQueue",
        @"AppData\Roaming\Microsoft\Windows", @"AppData\Roaming\Microsoft\Windows\Start Menu",
        @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs", @"AppData\Roaming\Microsoft\Windows\Recent",
        "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "OneDrive", "Favorites", "Links",
        "Contacts", "Saved Games", "Searches", "3D Objects", "source", "source\\repos",
    ];

    /// <summary>Top-level folders of any drive that are never touched (relative to the drive root).</summary>
    internal static readonly string[] ProtectedDriveFolders =
    [
        "$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "EFI", "$WinREAgent", "$SysReset",
        "$Windows.~BT", "$Windows.~WS", "Config.Msi", "Documents and Settings", "PerfLogs",
    ];

    /// <param name="blockedReason">When set, every path is denied with this reason (fail closed, e.g. an unreadable ignore list).</param>
    public SafetyPolicy(ScanEnvironment environment, IEnumerable<string>? ignoredPaths = null, string? blockedReason = null)
    {
        Environment = environment;
        BlockedReason = blockedReason;
        IgnoredPaths = (ignoredPaths ?? []).Select(PathUtil.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var windows = environment.SystemRoot;
        SystemCleanableRoots =
        [
            Path.Combine(windows, "Temp"),
            Path.Combine(windows, "SoftwareDistribution", "Download"),
            Path.Combine(windows, "Logs"),
            Path.Combine(windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
        ];
    }

    public SafetyVerdict Verdict(string path)
    {
        if (BlockedReason is { } blocked) return new SafetyVerdict.Denied(blocked);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return new SafetyVerdict.Denied("caminho relativo");
        // Win32 strips a trailing dot or space, so "Tese.docx " would act on "Tese.docx" — a different file.
        if (path.TrimEnd('\\', '/').Split('\\', '/').Skip(1).Any(c => c.EndsWith('.') || c.EndsWith(' ')))
            return new SafetyVerdict.Denied("nome terminado em ponto ou espaço");
        string p;
        try { p = PathUtil.Normalize(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SafetyVerdict.Denied("caminho inválido");
        }
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return new SafetyVerdict.Denied("caminho de rede");

        var root = Path.GetPathRoot(p) ?? "";
        if (PathUtil.SameOrdinalIgnoreCase(p, root.TrimEnd('\\')) || PathUtil.SameOrdinalIgnoreCase(p, root))
            return new SafetyVerdict.Denied("raiz da unidade");
        var rest = p[root.Length..];
        if (!rest.Contains('\\')) // directly at the drive root
        {
            if (!Directory.Exists(p)) return new SafetyVerdict.Denied("arquivo na raiz da unidade");
        }
        var top = rest.Split('\\')[0];
        if (ProtectedDriveFolders.Any(f => PathUtil.SameOrdinalIgnoreCase(f, top)))
            return new SafetyVerdict.Denied($"local do sistema {root}{top}");

        var windows = Environment.SystemRoot;
        if (IsWithin(p, windows))
        {
            var cleanable = SystemCleanableRoots.Any(r => IsWithin(p, r) && !PathUtil.SameOrdinalIgnoreCase(p, r));
            if (!cleanable) return new SafetyVerdict.Denied($"local do sistema {windows}");
        }
        foreach (var programFiles in ProgramFilesFolders(root))
        {
            if (IsWithin(p, programFiles)) return new SafetyVerdict.Denied($"programas instalados ({programFiles})");
        }
        var programData = Environment.ProgramData;
        if (IsWithin(p, programData))
        {
            string[] allowed = [@"Microsoft\Windows\WER\ReportArchive", @"Microsoft\Windows\WER\ReportQueue"];
            var ok = allowed.Select(a => Path.Combine(programData, a)).Any(r => IsWithin(p, r) && !PathUtil.SameOrdinalIgnoreCase(p, r));
            if (!ok) return new SafetyVerdict.Denied($"dados de programas ({programData})");
        }

        if (IgnoredPaths.Any(i => IsWithin(p, i))) return new SafetyVerdict.Denied("na lista de ignorados");

        var home = Environment.Home;
        if (ProtectedHomeTrees.FirstOrDefault(t => IsWithin(p, Path.Combine(home, t))) is { } tree)
            return new SafetyVerdict.Denied($"dados protegidos (~\\{tree})");
        if (ProtectedHomeContainers.Any(c => PathUtil.SameOrdinalIgnoreCase(p, c.Length == 0 ? home : Path.Combine(home, c))))
            return new SafetyVerdict.Denied("pasta contêiner protegida");
        foreach (var name in (string[])["TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "USERPROFILE", "OneDrive"])
        {
            if (Environment.Variable(name) is { } v && PathUtil.SameOrdinalIgnoreCase(p, v))
                return new SafetyVerdict.Denied("pasta contêiner protegida");
        }
        if (IsWithin(home, p)) return new SafetyVerdict.Denied("pasta acima da sua pasta pessoal");

        var users = Path.GetDirectoryName(home);
        if (users is not null && IsWithin(p, users) && !IsWithin(p, home))
            return new SafetyVerdict.Denied("pasta de outro usuário");

        return SafetyVerdict.Allow;
    }

    private IEnumerable<string> ProgramFilesFolders(string root)
    {
        foreach (var name in (string[])["ProgramFiles", "ProgramFiles(x86)", "ProgramW6432"])
        {
            if (Environment.Variable(name) is { } v) yield return v;
        }
        yield return Path.Combine(root, "Program Files");
        yield return Path.Combine(root, "Program Files (x86)");
    }

    /// <summary>
    /// True when <paramref name="path"/> equals <paramref name="root"/> or lies underneath it, by path component,
    /// never by raw prefix. <c>\</c> is treated as <c>/</c> and the comparison ignores case, as NTFS does.
    /// </summary>
    public static bool IsWithin(string path, string root)
    {
        var p = path.Replace('\\', '/');
        var r = root.Replace('\\', '/');
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = r.EndsWith('/') ? r : r + "/";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
