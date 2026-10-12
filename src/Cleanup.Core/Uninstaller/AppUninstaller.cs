using Cleanup.Core.Cleaning;
using Cleanup.Core.Models;

namespace Cleanup.Core.Uninstaller;

/// <summary>How the vendor uninstaller (or the package removal) ended.</summary>
public enum UninstallerExit
{
    Finished,
    /// <summary>The user refused the uninstaller's UAC prompt: nothing happened.</summary>
    Cancelled,
    /// <summary>No usable uninstall command (or the package manager refused).</summary>
    Failed,
}

public sealed record UninstallerRun(UninstallerExit Exit, string? Error = null);

/// <summary>The machine side of uninstalling and resetting: processes, the package manager, the Uninstall keys.</summary>
public interface IAppPlatform
{
    /// <summary>Whether a process of the app is running (its image lies in the app's install folder).</summary>
    bool IsRunning(InstalledApp app);

    /// <summary>Whether the app is still installed (its Uninstall key or its package still exists).</summary>
    bool IsInstalled(InstalledApp app);

    /// <summary>Win32: runs the vendor uninstaller and waits for it. Store: removes the package for this user.</summary>
    Task<UninstallerRun> UninstallAsync(InstalledApp app, CancellationToken cancellation);

    /// <summary>Store: the same as Settings › Apps › Opções avançadas › Redefinir.</summary>
    Task ResetPackageAsync(InstalledApp app, CancellationToken cancellation);
}

/// <summary>Deleting <c>HKCU</c> keys, always after a <c>.reg</c> export.</summary>
public interface IRegistryStore
{
    /// <summary>Writes the key and everything below it to <paramref name="file"/> (regedit format). Throws on failure.</summary>
    void Export(string key, string file);

    /// <summary>Deletes the key and its subkeys. Throws on failure.</summary>
    void DeleteTree(string key);

    bool Exists(string key);
}

public sealed record RegistryFailure(string Key, string Reason);

public sealed record RegistryRemoval(IReadOnlyList<ScanItem> Removed, IReadOnlyList<RegistryFailure> Failed, IReadOnlyList<string> Backups)
{
    public static readonly RegistryRemoval Nothing = new([], [], []);
}

/// <summary>Removes registry leftovers: policy check, <c>.reg</c> backup, then delete. No backup, no delete.</summary>
public static class RegistryCleanup
{
    /// <summary>Where the <c>.reg</c> backups go: <c>%LOCALAPPDATA%\Cleanup\backups</c>.</summary>
    public static string BackupDirectory => Path.Combine(AppPaths.SupportDirectory, "backups");

    public static RegistryRemoval Remove(IReadOnlyList<ScanItem> keys, IRegistryStore store, string backupDirectory, DateTime? now = null)
    {
        var removed = new List<ScanItem>();
        var failed = new List<RegistryFailure>();
        var backups = new List<string>();
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss");
        var index = 0;
        foreach (var item in keys)
        {
            if (!RegistryPolicy.IsAllowed(item.Path, out var reason))
            {
                failed.Add(new(item.Path, "bloqueado pela política de segurança: " + reason));
                continue;
            }
            var safeName = string.Concat(item.Path[(RegistryPolicy.UserSoftware.Length + 1)..].Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'));
            if (safeName.Length > 80) safeName = safeName[..80];
            var file = Path.Combine(backupDirectory, $"{stamp}-{++index}-{safeName}.reg");
            try
            {
                Directory.CreateDirectory(backupDirectory);
                store.Export(item.Path, file);
                if (!File.Exists(file)) throw new IOException("a cópia de segurança não foi criada");
            }
            catch (Exception e)
            {
                failed.Add(new(item.Path, "sem cópia de segurança, a chave foi mantida: " + e.Message));
                continue;
            }
            backups.Add(file);
            try
            {
                store.DeleteTree(item.Path);
                removed.Add(item);
            }
            catch (Exception e) { failed.Add(new(item.Path, Cleaner.Describe(e))); }
        }
        return new RegistryRemoval(removed, failed, backups);
    }
}

public sealed class AppActionException(string message) : Exception(message);

public sealed record UninstallOutcome(bool AppRemoved, UninstallerRun Run, CleanResult? Files, RegistryRemoval Registry);

/// <summary>
/// Desinstalar: the vendor uninstaller (or the package removal) first; the chosen leftovers only once the app is
/// really gone. Files go to the Recycle Bin through <see cref="Cleaner"/> (re-checked by the safety policy), registry
/// keys are exported to <c>.reg</c> before being deleted. Items only reported (ProgramData…) are never touched.
/// </summary>
public static class AppUninstaller
{
    public static async Task<UninstallOutcome> UninstallAsync(InstalledApp app, IReadOnlyList<ScanItem> leftovers, IAppPlatform platform,
        Cleaner cleaner, IRegistryStore registry, string backupDirectory, CancellationToken cancellation = default)
    {
        if (app.IsSystemApp) throw new AppActionException($"{app.Name} é um componente do Windows e não pode ser removido.");
        var run = await platform.UninstallAsync(app, cancellation);
        if (platform.IsInstalled(app)) return new UninstallOutcome(false, run, null, RegistryRemoval.Nothing);
        // The vendor uninstaller usually takes some of them (its own folder, its shortcut): only what is still there.
        var (files, keys) = Split(leftovers);
        files = files.Where(f => File.Exists(f.Path) || Directory.Exists(f.Path)).ToList();
        keys = keys.Where(k => registry.Exists(k.Path)).ToList();
        var cleaned = files.Count == 0 ? null : cleaner.Clean(files);
        var regs = RegistryCleanup.Remove(keys, registry, backupDirectory);
        return new UninstallOutcome(true, run, cleaned, regs);
    }

    /// <summary>Files for the Recycle Bin and keys for the registry; report-only items are dropped.</summary>
    internal static (List<ScanItem> Files, List<ScanItem> Keys) Split(IEnumerable<ScanItem> items)
    {
        var list = items.Where(i => i.RuleId != LeftoverFinder.ReportRule).ToList();
        return (list.Where(i => i.RuleId != LeftoverFinder.RegistryRule).ToList(), list.Where(i => i.RuleId == LeftoverFinder.RegistryRule).ToList());
    }
}
