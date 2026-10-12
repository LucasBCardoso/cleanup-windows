using Cleanup.Core.Cleaning;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;
using Cleanup.Core.Scanning;

namespace Cleanup.Core.Uninstaller;

/// <summary>Redefinir app (macOS Phase 11): what a reset takes. Saved windows and logs have no Windows counterpart.</summary>
public enum ResetKind
{
    Settings,
    Caches,
    Data,
}

public static class ResetKinds
{
    public static string Title(this ResetKind kind) => kind switch
    {
        ResetKind.Settings => "Ajustes",
        ResetKind.Caches => "Caches",
        _ => "Dados",
    };

    /// <summary>Data folders can hold the user's own things (documents, libraries, accounts): never pre-selected.</summary>
    public static bool IsPreselected(this ResetKind kind) => kind != ResetKind.Data;

    public static string Note(this ResetKind kind) => kind switch
    {
        ResetKind.Settings => "As preferências do app voltam ao padrão. Uma cópia .reg fica em backups.",
        ResetKind.Caches => "O app recria quando precisar; pode pedir login de novo.",
        _ => "Pode conter seus dados (documentos, bibliotecas, contas).",
    };
}

public sealed record ResetItem(ScanItem Item, ResetKind Kind)
{
    public bool Preselected => Kind.IsPreselected();
    public string Id => Item.Id;
}

/// <summary>
/// Redefinir app: back to first launch without uninstalling. Win32: the app's <c>HKCU\Software</c> key (settings,
/// exported to <c>.reg</c> first), <c>%LOCALAPPDATA%\Product\Cache*</c> (caches) and <c>%APPDATA%\Product</c> (data,
/// opt-in). Store apps: the package reset, like Settings. Refused while the app runs and for system apps.
/// </summary>
public static class AppReset
{
    public static IReadOnlyList<ResetItem> Plan(InstalledApp app, LeftoverFinder finder, IReadOnlyList<InstalledApp> installed)
    {
        if (app.Kind == AppKind.Store || app.IsSystemApp) return [];
        var names = new LeftoverFinder.Names(app, installed);
        if (names.Products.Count == 0) return [];
        var plan = new List<ResetItem>();
        plan.AddRange(finder.RegistryKeys(names).Select(k => new ResetItem(k, ResetKind.Settings)));

        foreach (var folder in LeftoverFinder.MatchedFolders(finder.Local, names))
        {
            foreach (var cache in CacheChildren(folder)) plan.Add(new ResetItem(FileItem(cache), ResetKind.Caches));
        }
        foreach (var folder in LeftoverFinder.MatchedFolders(finder.Roaming, names))
        {
            // Electron apps (Discord, VS Code…) keep "Cache", "Code Cache" and "GPUCache" in Roaming.
            foreach (var cache in CacheChildren(folder)) plan.Add(new ResetItem(FileItem(cache), ResetKind.Caches));
            plan.Add(new ResetItem(FileItem(folder), ResetKind.Data));
        }

        // Never the app itself: nothing inside its install folder.
        // Never the app itself (nothing inside its install folder), and only what the safety policy allows.
        return plan.Where(r => r.Item.RuleId == LeftoverFinder.RegistryRule
                               || ((app.InstallFolder is not { } f || !SafetyPolicy.IsWithin(r.Item.Path, f))
                                   && finder.Policy.Verdict(r.Item.Path).IsAllowed))
            .ToList();
    }

    /// <summary>"Cache", "Cache_Data", "GPUCache", "Code Cache"…: children whose name starts or ends with "cache".</summary>
    internal static IEnumerable<string> CacheChildren(string folder)
    {
        List<string> children;
        try { children = Directory.EnumerateDirectories(folder).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith("cache", StringComparison.OrdinalIgnoreCase) || name.EndsWith("cache", StringComparison.OrdinalIgnoreCase))
            {
                try { if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) continue; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                yield return child;
            }
        }
    }

    private static ScanItem FileItem(string path) => new()
    {
        Path = path, Size = FileMetrics.Size(path), RuleId = LeftoverFinder.FileRule, Category = ScanCategory.AppLeftovers, Risk = RiskLevel.Safe,
    };

    public sealed record Outcome(CleanResult? Files, RegistryRemoval Registry, bool PackageReset);

    /// <summary>
    /// Takes the chosen items (files to the Recycle Bin, keys after a <c>.reg</c> backup) or resets the package.
    /// Refused while the app runs — it would write its settings back — and for system apps.
    /// </summary>
    public static async Task<Outcome> CleanAsync(IReadOnlyList<ResetItem> items, InstalledApp app, IAppPlatform platform, Cleaner cleaner,
        IRegistryStore registry, string backupDirectory, CancellationToken cancellation = default)
    {
        if (app.IsSystemApp) throw new AppActionException($"{app.Name} é um componente do Windows e não pode ser redefinido.");
        if (platform.IsRunning(app)) throw new AppActionException($"Feche o {app.Name} antes de redefinir.");
        if (app.Kind == AppKind.Store)
        {
            await platform.ResetPackageAsync(app, cancellation);
            return new Outcome(null, RegistryRemoval.Nothing, true);
        }
        var (files, keys) = AppUninstaller.Split(items.Select(i => i.Item));
        var cleaned = files.Count == 0 ? null : cleaner.Clean(files);
        var regs = RegistryCleanup.Remove(keys, registry, backupDirectory);
        return new Outcome(cleaned, regs, false);
    }
}
