using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Extensions;

/// <summary>
/// Decides what "Remover selecionados" may touch. Files go to the Recycle Bin through <c>Cleaner</c> (which re-checks
/// the safety policy); Explorer extensions registered for this user are removed from HKCU after a <c>.reg</c> backup.
/// Browser extensions are refused while their browser runs: it keeps them open and writes its state back.
/// </summary>
public static class ExtensionRemoval
{
    public sealed record Plan(IReadOnlyList<ScanItem> Files, IReadOnlyList<InstalledExtension> RegistryItems,
        IReadOnlyList<(InstalledExtension Item, string Reason)> Refused);

    public static Plan Make(IEnumerable<InstalledExtension> selected, IReadOnlySet<string> runningProcesses, ScanEnvironment environment)
    {
        var files = new List<ScanItem>();
        var registry = new List<InstalledExtension>();
        var refused = new List<(InstalledExtension, string)>();
        foreach (var item in selected)
        {
            if (Refusal(item, runningProcesses, environment) is { } reason) { refused.Add((item, reason)); continue; }
            if (item.Kind == ExtensionKind.ShellExtension) registry.Add(item);
            else if (item.ScanItem is { } scanItem) files.Add(scanItem);
        }
        return new Plan(files, registry, refused);
    }

    internal static string? Refusal(InstalledExtension item, IReadOnlySet<string> running, ScanEnvironment environment)
    {
        if (!item.IsRemovable) return "só extensões instaladas por você podem ser removidas";
        switch (item.Kind)
        {
            case ExtensionKind.Browser:
                var process = ProcessName(item.Browser);
                if (process is null) return "navegador desconhecido";
                if (running.Contains(process)) return $"feche o {item.Browser} antes de remover";
                return BrowserPathIsExpected(item, environment) ? null : "fora da pasta de extensões do navegador";
            case ExtensionKind.ScreenSaver:
                return item.Path is { } p && p.EndsWith(".scr", StringComparison.OrdinalIgnoreCase)
                       && SafetyPolicy.IsWithin(p, environment.Home) && !SafetyPolicy.IsWithin(p, environment.SystemRoot)
                    ? null : "protetor de tela do sistema";
            case ExtensionKind.ShellExtension:
                return item.RegistryKeys.All(IsUserShellExtensionKey) ? null : "registrada para todos os usuários";
            default:
                return "somente para consulta";
        }
    }

    public static string? ProcessName(string? browser) =>
        browser == ExtensionInventory.FirefoxName ? ExtensionInventory.FirefoxProcess
            : ExtensionInventory.ChromiumBrowsers.FirstOrDefault(b => b.Name == browser)?.ProcessName;

    /// <summary>
    /// A Chromium extension is <c>&lt;User Data&gt;\&lt;profile&gt;\Extensions\&lt;id&gt;</c>; a Firefox one is a file or
    /// folder in <c>&lt;profile&gt;\extensions</c>. Anything else (an unpacked development folder) is never removed here.
    /// </summary>
    internal static bool BrowserPathIsExpected(InstalledExtension item, ScanEnvironment environment)
    {
        if (item.Path is not { } path || !PathUtil.IsNormalizedAbsolute(path)) return false;
        var parent = Path.GetDirectoryName(path);
        if (parent is null) return false;
        if (item.Browser == ExtensionInventory.FirefoxName)
        {
            var profiles = Path.Combine(environment.RoamingAppData, @"Mozilla\Firefox\Profiles");
            return Path.GetFileName(parent).Equals("extensions", StringComparison.OrdinalIgnoreCase)
                   && Path.GetDirectoryName(Path.GetDirectoryName(parent)) is { } root && PathUtil.SameOrdinalIgnoreCase(root, profiles);
        }
        var browser = ExtensionInventory.ChromiumBrowsers.FirstOrDefault(b => b.Name == item.Browser);
        if (browser is null || !ExtensionInventory.ChromiumId().IsMatch(Path.GetFileName(path))) return false;
        var profile = Path.GetDirectoryName(parent);
        return Path.GetFileName(parent).Equals("Extensions", StringComparison.Ordinal)
               && profile is not null && ExtensionInventory.IsProfile(Path.GetFileName(profile))
               && Path.GetDirectoryName(profile) is { } userData
               && PathUtil.SameOrdinalIgnoreCase(userData, Path.Combine(environment.LocalAppData, browser.UserDataFolder));
    }

    /// <summary>Only per-user context-menu handler keys under <c>HKCU\Software\Classes\…\shellex\ContextMenuHandlers</c>.</summary>
    public static bool IsUserShellExtensionKey(string key)
    {
        const string prefix = @"HKCU\Software\Classes\";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || key.Contains("..") || key.EndsWith('\\')) return false;
        var marker = @"\shellex\ContextMenuHandlers\";
        var at = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at <= prefix.Length) return false;
        var handler = key[(at + marker.Length)..];
        return handler.Length > 0 && !handler.Contains('\\');
    }
}
