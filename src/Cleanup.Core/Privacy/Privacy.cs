using Cleanup.Core.Cleaning;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;
using Cleanup.Core.Scanning;

namespace Cleanup.Core.Privacy;

/// <summary>
/// Privacidade: browsing traces, recent-item lists and shell history. Deleted permanently — moving them to the Recycle
/// Bin would keep the very data the user wants gone — so nothing here is pre-selected or recorded for undo
/// (invariant 11). Mirrors the Swift <c>Privacy.swift</c>.
/// </summary>
public enum PrivacyKind { History, Cookies, Forms, RecentItems, ShellHistory }

public static class PrivacyKinds
{
    public static string Title(this PrivacyKind kind) => kind switch
    {
        PrivacyKind.History => "Histórico",
        PrivacyKind.Cookies => "Cookies",
        PrivacyKind.Forms => "Formulários",
        PrivacyKind.RecentItems => "Itens recentes",
        _ => "Histórico de comandos",
    };
}

/// <summary>One thing the user can erase: a set of existing regular files.</summary>
public sealed record PrivacyItem
{
    public required string Id { get; init; }
    public required string App { get; init; }
    public string? Profile { get; init; }
    public required PrivacyKind Kind { get; init; }
    /// <summary>The existing files this item deletes.</summary>
    public required IReadOnlyList<string> Paths { get; init; }
    public required long Size { get; init; }
    /// <summary>Lowercase process image names that must not run (they keep these files open and write them back).</summary>
    public required IReadOnlySet<string> Processes { get; init; }
    public required bool IsBlocked { get; init; }
    public required string Loses { get; init; }
    /// <summary>What to close; "Feche o &lt;app&gt;" unless set.</summary>
    public string? CloseHint { get; init; }

    public string? BlockedReason => IsBlocked ? CloseHint ?? $"Feche o {App}" : null;
}

public sealed record PrivacyScanResult(IReadOnlyList<PrivacyItem> Items, IReadOnlyList<string> Unreadable);

/// <summary>
/// Finds privacy traces by allowlist only: a fixed set of file names in known folders. Only regular files are offered
/// and every folder walked must be a real directory (invariant 13): deleting a link would leave the data behind, and a
/// linked folder would point the delete somewhere else.
/// </summary>
public sealed class PrivacyScanner(ScanEnvironment environment, IReadOnlySet<string> runningProcesses)
{
    internal sealed record Chromium(string Name, bool Roaming, string Folder, string Process);

    /// <summary>Chromium-family browsers: their <c>User Data</c> folder and the image name of their process.</summary>
    internal static readonly Chromium[] Browsers =
    [
        new("Chrome", false, @"Google\Chrome\User Data", "chrome"),
        new("Edge", false, @"Microsoft\Edge\User Data", "msedge"),
        new("Brave", false, @"BraveSoftware\Brave-Browser\User Data", "brave"),
        new("Vivaldi", false, @"Vivaldi\User Data", "vivaldi"),
        new("Opera", true, @"Opera Software\Opera Stable", "opera"),
        new("Opera GX", true, @"Opera Software\Opera GX Stable", "opera"),
        new("Chromium", false, @"Chromium\User Data", "chrome"),
    ];

    /// <summary>
    /// Only these files are ever offered. Passwords (<c>Login Data</c>), bookmarks, settings and <c>Web Data</c>
    /// (payment methods, search engines) are never on the list.
    /// </summary>
    internal static readonly string[] ChromiumHistory = WithSidecars(["History", "Visited Links", "Top Sites", "Shortcuts", "Network Action Predictor"], "-journal", "-wal");
    internal static readonly string[] ChromiumCookies = WithSidecars(["Cookies", @"Network\Cookies"], "-journal", "-wal");

    internal static readonly string[] FirefoxCookies = WithSidecars(["cookies.sqlite"], "-wal", "-shm");
    internal static readonly string[] FirefoxForms = WithSidecars(["formhistory.sqlite"], "-wal", "-shm");

    /// <summary>Firefox, Developer Edition and Nightly all run as firefox.exe.</summary>
    internal static readonly IReadOnlySet<string> Firefox = new HashSet<string> { "firefox" };

    /// <summary>Open shells write their history back when they exit, so shell history waits until every terminal is closed.</summary>
    internal static readonly IReadOnlySet<string> Terminals = new HashSet<string> { "windowsterminal", "powershell", "pwsh", "cmd" };

    /// <summary>The Quick Access jump list: it holds the folders the user pinned in Explorer. Never touched.</summary>
    internal const string QuickAccessPins = "f01b4d95cf55d32a.automaticDestinations-ms";

    private readonly HashSet<string> running = runningProcesses.Select(p => p.ToLowerInvariant()).ToHashSet();

    public PrivacyScanResult Scan()
    {
        var items = new List<PrivacyItem>();
        var unreadable = new List<string>();
        foreach (var browser in Browsers)
        {
            var support = browser.Roaming ? environment.RoamingAppData : environment.LocalAppData;
            if (RealDirectory(support, browser.Folder) is not { } root) continue;
            foreach (var profile in Profiles(root, unreadable))
            {
                var dir = profile == "." ? root : Path.Combine(root, profile);
                if (!GlobExpander.IsRealDirectory(dir)) continue; // never through a linked profile
                var label = profile == "." ? "Default" : profile;
                var key = $"{browser.Name.Replace(" ", "").ToLowerInvariant()}.{label}";
                HashSet<string> processes = [browser.Process];
                Append(items, new Draft($"{key}.history", browser.Name, label, PrivacyKind.History, processes,
                    "Histórico de navegação e de downloads e sugestões da barra de endereço. Se o navegador sincroniza com a sua conta, ele pode voltar."),
                    ChromiumHistory.Select(f => File(dir, f)));
                Append(items, new Draft($"{key}.cookies", browser.Name, label, PrivacyKind.Cookies, processes,
                    "Você vai sair dos sites em que está conectado."),
                    ChromiumCookies.Select(f => File(dir, f)));
            }
        }
        ScanFirefox(items, unreadable);
        ScanRecentItems(items, unreadable);
        var psReadLine = RealDirectory(environment.RoamingAppData, @"Microsoft\Windows\PowerShell\PSReadLine");
        Append(items, new Draft("powershell.shellHistory", "PowerShell", null, PrivacyKind.ShellHistory, Terminals,
                "Os comandos anteriores (seta para cima) no PowerShell.")
            { CloseHint = "Feche o PowerShell, o cmd e o Terminal" },
            psReadLine is null ? [] : [Path.Combine(psReadLine, "ConsoleHost_history.txt")]);
        return new PrivacyScanResult(items, unreadable);
    }

    /// <summary><c>Default</c> and <c>Profile N</c> folders; Opera keeps its single profile in the root (marked ".").</summary>
    internal static List<string> Profiles(string root, List<string> unreadable)
    {
        var names = Listing(root, unreadable, directories: true)?.Select(Path.GetFileName).OfType<string>() ?? [];
        var profiles = names.Where(ChromiumCacheTask.IsProfile).Order(StringComparer.Ordinal).ToList();
        if (profiles.Count == 0 && (IsRegularFile(Path.Combine(root, "History")) || IsRegularFile(Path.Combine(root, "Cookies"))
                                    || IsRegularFile(Path.Combine(root, @"Network\Cookies"))))
            return ["."];
        return profiles;
    }

    /// <summary>Firefox history lives in <c>places.sqlite</c> together with the bookmarks, so only cookies and form history.</summary>
    private void ScanFirefox(List<PrivacyItem> items, List<string> unreadable)
    {
        if (RealDirectory(environment.RoamingAppData, @"Mozilla\Firefox\Profiles") is not { } profiles) return;
        foreach (var profile in Listing(profiles, unreadable, directories: true) ?? [])
        {
            if (!GlobExpander.IsRealDirectory(profile)) continue;
            var name = Path.GetFileName(profile);
            Append(items, new Draft($"firefox.{name}.cookies", "Firefox", name, PrivacyKind.Cookies, Firefox,
                "Você vai sair dos sites em que está conectado."), FirefoxCookies.Select(f => Path.Combine(profile, f)));
            Append(items, new Draft($"firefox.{name}.forms", "Firefox", name, PrivacyKind.Forms, Firefox,
                "Sugestões salvas de formulários."), FirefoxForms.Select(f => Path.Combine(profile, f)));
        }
    }

    /// <summary>
    /// Explorer's recent files (<c>Recent\*.lnk</c>) and the apps' jump lists. The Quick Access list
    /// (<see cref="QuickAccessPins"/>) is never on it: it holds the user's pinned folders.
    /// </summary>
    private void ScanRecentItems(List<PrivacyItem> items, List<string> unreadable)
    {
        if (RealDirectory(environment.RoamingAppData, @"Microsoft\Windows\Recent") is not { } recent) return;
        var files = new List<string>();
        files.AddRange(FilesWithExtension(recent, ".lnk", unreadable));
        if (RealDirectory(recent, "AutomaticDestinations") is { } automatic)
        {
            files.AddRange(FilesWithExtension(automatic, ".automaticDestinations-ms", unreadable)
                .Where(f => !string.Equals(Path.GetFileName(f), QuickAccessPins, StringComparison.OrdinalIgnoreCase)));
        }
        if (RealDirectory(recent, "CustomDestinations") is { } custom)
            files.AddRange(FilesWithExtension(custom, ".customDestinations-ms", unreadable));
        Append(items, new Draft("windows.recentItems", "Windows", null, PrivacyKind.RecentItems, new HashSet<string>(),
            "Os arquivos recentes do Explorador e as listas de atalhos dos apps na barra de tarefas (inclusive os itens fixados nelas). Os itens fixados no Acesso rápido ficam."),
            files);
    }

    private sealed record Draft(string Id, string App, string? Profile, PrivacyKind Kind, IReadOnlySet<string> Processes, string Loses)
    {
        public string? CloseHint { get; init; }
    }

    private void Append(List<PrivacyItem> items, Draft draft, IEnumerable<string?> files)
    {
        // Regular files only: deleting a link leaves the data behind, so it is never offered.
        var existing = files.OfType<string>().Where(IsRegularFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return;
        items.Add(new PrivacyItem
        {
            Id = draft.Id, App = draft.App, Profile = draft.Profile, Kind = draft.Kind, Paths = existing,
            Size = existing.Sum(FileMetrics.Size), Processes = draft.Processes,
            IsBlocked = draft.Processes.Any(running.Contains), Loses = draft.Loses, CloseHint = draft.CloseHint,
        });
    }

    /// <summary><paramref name="relative"/> under <paramref name="dir"/>, or null when a folder on the way isn't a real directory.</summary>
    private static string? File(string dir, string relative)
    {
        var folder = Path.GetDirectoryName(relative);
        if (string.IsNullOrEmpty(folder)) return Path.Combine(dir, relative);
        return RealDirectory(dir, folder) is { } parent ? Path.Combine(parent, Path.GetFileName(relative)) : null;
    }

    /// <summary>
    /// <paramref name="baseDir"/>\<paramref name="relative"/> when the base and every folder below it are real
    /// directories (no junctions or symlinks on the way); otherwise null.
    /// </summary>
    internal static string? RealDirectory(string baseDir, string relative)
    {
        if (!GlobExpander.IsRealDirectory(baseDir)) return null;
        var current = baseDir;
        foreach (var part in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!GlobExpander.IsRealDirectory(current)) return null;
        }
        return current;
    }

    /// <summary>A regular file: not a folder, not a reparse point (symlink), not a cloud-only placeholder.</summary>
    internal static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = System.IO.File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint)
                   && !attributes.HasFlag(FileAttributes.Device) && !FileMetrics.IsCloudOnly(attributes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static IEnumerable<string> FilesWithExtension(string dir, string extension, List<string> unreadable) =>
        (Listing(dir, unreadable, directories: false) ?? [])
        .Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sorted direct children; null (and reported) when the folder exists but can't be listed.</summary>
    private static List<string>? Listing(string dir, List<string> unreadable, bool directories)
    {
        try
        {
            var entries = directories ? Directory.EnumerateDirectories(dir) : Directory.EnumerateFiles(dir);
            return entries.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(dir)) unreadable.Add(dir);
            return null;
        }
    }

    private static string[] WithSidecars(string[] names, params string[] suffixes) =>
        names.SelectMany(n => (string[])[n, .. suffixes.Select(s => n + s)]).ToArray();
}

public sealed record PrivacyRefusal(PrivacyItem Item, string Reason);

public sealed record PrivacyFailure(string Path, string Reason);

public sealed record PrivacyCleanResult(IReadOnlyList<PrivacyItem> Cleaned, IReadOnlyList<PrivacyRefusal> Refused,
    IReadOnlyList<PrivacyFailure> Failed)
{
    public long FreedBytes => Cleaned.Sum(i => i.Size);
}

public static class PrivacyCleaner
{
    /// <summary>
    /// Deletes each item's files permanently. An item whose app runs now is refused (it would write the data back),
    /// each path must still be a regular file, and every path goes through the safety policy (invariant 2).
    /// </summary>
    public static PrivacyCleanResult Clean(IReadOnlyList<PrivacyItem> items, IReadOnlySet<string> runningProcesses,
        SafetyPolicy policy, IFileRemover remover)
    {
        var running = runningProcesses.Select(p => p.ToLowerInvariant()).ToHashSet();
        var cleaned = new List<PrivacyItem>();
        var refused = new List<PrivacyRefusal>();
        var failed = new List<PrivacyFailure>();
        foreach (var item in items)
        {
            if (item.Processes.Any(running.Contains))
            {
                refused.Add(new(item, item.CloseHint ?? $"Feche o {item.App}"));
                continue;
            }
            var ok = true;
            foreach (var path in item.Paths)
            {
                if (!System.IO.File.Exists(path) && !Directory.Exists(path)) continue; // gone already (a journal file)
                if (!PrivacyScanner.IsRegularFile(path))
                {
                    failed.Add(new(path, "não é um arquivo comum"));
                    ok = false;
                    continue;
                }
                if (policy.Verdict(path) is SafetyVerdict.Denied denied)
                {
                    failed.Add(new(path, "bloqueado pela política de segurança: " + denied.Reason));
                    ok = false;
                    continue;
                }
                try { remover.Delete(path); }
                catch (Exception e)
                {
                    failed.Add(new(path, Cleaner.Describe(e)));
                    ok = false;
                }
            }
            if (ok) cleaned.Add(item);
        }
        return new PrivacyCleanResult(cleaned, refused, failed);
    }
}
