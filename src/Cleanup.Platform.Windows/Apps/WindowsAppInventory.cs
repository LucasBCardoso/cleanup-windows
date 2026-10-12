using Cleanup.Core.Models;
using Cleanup.Core.Scanning;
using Cleanup.Core.Uninstaller;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Cleanup.Platform.Windows.Apps;

/// <summary>
/// Installed apps on this PC (design §5.2): the registry <c>Uninstall</c> keys of HKLM (64- and 32-bit views) and HKCU,
/// plus this user's MSIX / Store packages. Read-only.
/// </summary>
public sealed class WindowsAppInventory(ScanEnvironment environment)
{
    public const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>One place Uninstall entries live. Tests point a source at a scratch key.</summary>
    public sealed record Source(RegistryHive Hive, RegistryView View, string SubKey, string Label);

    public static IReadOnlyList<Source> DefaultSources { get; } =
    [
        new(RegistryHive.LocalMachine, RegistryView.Registry64, UninstallPath, "HKLM64"),
        new(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallPath, "HKLM32"),
        new(RegistryHive.CurrentUser, RegistryView.Default, UninstallPath, "HKCU"),
    ];

    /// <summary>Programs and Store apps, one entry per app, sorted by name.</summary>
    public List<InstalledApp> Load(bool measure = true)
    {
        var win32 = Win32Apps(DefaultSources);
        var store = StoreApps();
        var all = AppInventory.Merge(win32.Concat(store));
        if (measure) MeasureMissingSizes(all);
        return all;
    }

    public List<InstalledApp> Win32Apps(IEnumerable<Source> sources)
    {
        var apps = new List<InstalledApp>();
        foreach (var source in sources)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(source.Hive, source.View);
                using var uninstall = root.OpenSubKey(source.SubKey);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        if (key is null) continue;
                        var values = key.GetValueNames().ToDictionary(v => v, v => key.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                            StringComparer.OrdinalIgnoreCase);
                        ExpandStrings(values);
                        var view = source.View == RegistryView.Registry32 ? 32 : 64;
                        if (AppInventory.FromUninstallKey(key.Name, source.Label, view, values, environment) is { } app) apps.Add(app);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return apps;
    }

    /// <summary>Paths like <c>%ProgramFiles%\App</c> in REG_EXPAND_SZ values, expanded with this machine's variables.</summary>
    private static void ExpandStrings(Dictionary<string, object?> values)
    {
        foreach (var name in values.Keys.ToList())
        {
            if (values[name] is string s && s.Contains('%')) values[name] = Environment.ExpandEnvironmentVariables(s);
        }
    }

    /// <summary>This user's MSIX packages with a Start Menu entry: no frameworks, resources or system packages.</summary>
    public List<InstalledApp> StoreApps()
    {
        var apps = new List<InstalledApp>();
        IEnumerable<Package> packages;
        try { packages = new PackageManager().FindPackagesForUser(string.Empty).ToList(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return apps; }
        var nonRemovable = NonRemovablePackages();
        foreach (var package in packages)
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.SignatureKind == PackageSignatureKind.System) continue;
                var name = package.DisplayName?.Trim();
                if (string.IsNullOrEmpty(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;
                if (package.GetAppListEntries().Count == 0) continue; // services and extensions with nothing to open
                var id = package.Id;
                var v = id.Version;
                string? folder = null;
                try { folder = package.InstalledLocation?.Path; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
                apps.Add(new InstalledApp
                {
                    Id = "store:" + id.FullName.ToLowerInvariant(),
                    Name = name,
                    Kind = AppKind.Store,
                    Publisher = string.IsNullOrWhiteSpace(package.PublisherDisplayName) ? AppNames.Vendor(id.Publisher) : package.PublisherDisplayName,
                    Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                    InstallFolder = folder,
                    PackageFullName = id.FullName,
                    PackageFamilyName = id.FamilyName,
                    // Inbox apps Windows won't remove (Segurança do Windows…) are system apps here too.
                    IsSystemApp = AppInventory.IsWindowsPublisher(id.Publisher) || nonRemovable.Contains(id.FullName),
                });
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException) { }
        }
        return apps;
    }

    /// <summary>
    /// Packages marked NonRemovable. The 19041 WinRT surface doesn't expose the flag, so this asks the Appx PowerShell
    /// module once (read-only); on any failure nothing is marked and Windows itself still refuses the removal.
    /// </summary>
    internal HashSet<string> NonRemovablePackages()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var powershell = Path.Combine(environment.SystemRoot, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell)) return result;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(powershell)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in (string[])["-NoProfile", "-NonInteractive", "-Command",
                         "Get-AppxPackage | Where-Object { $_.NonRemovable } | ForEach-Object { $_.PackageFullName }"])
                psi.ArgumentList.Add(arg);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return result;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20_000))
            {
                p.Kill();
                return result;
            }
            foreach (var line in output.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) result.Add(line);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return result;
    }

    /// <summary>Apps without <c>EstimatedSize</c> get the size of their folder (Store apps always do).</summary>
    public static void MeasureMissingSizes(List<InstalledApp> apps)
    {
        var sizes = new long[apps.Count];
        Parallel.For(0, apps.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            var app = apps[i];
            sizes[i] = app.Size > 0 || app.InstallFolder is not { } folder || !Directory.Exists(folder)
                ? app.Size
                : FileMetrics.DirectorySize(folder, CancellationToken.None);
        });
        for (var i = 0; i < apps.Count; i++)
        {
            if (sizes[i] != apps[i].Size) apps[i] = apps[i] with { Size = sizes[i] };
        }
    }
}

/// <summary>Explorer's UserAssist counters for this user (read-only).</summary>
public static class UserAssistReader
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    public static List<UserAssistEntry> Read()
    {
        var entries = new List<UserAssistEntry>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is null) return entries;
            foreach (var guid in root.GetSubKeyNames())
            {
                using var count = root.OpenSubKey(guid + @"\Count");
                if (count is null) continue;
                foreach (var name in count.GetValueNames())
                {
                    if (UserAssist.Parse(name, count.GetValue(name) as byte[]) is { } entry) entries.Add(entry);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return entries;
    }

    /// <summary>
    /// When each app was last used: UserAssist first, then the main executable's last-access time (NTFS may not keep
    /// it), else null ("Sem registro").
    /// </summary>
    public static Dictionary<string, DateTime?> LastUsed(IEnumerable<InstalledApp> apps, ScanEnvironment environment)
    {
        var entries = Read();
        var result = new Dictionary<string, DateTime?>();
        foreach (var app in apps)
        {
            var when = AppUsage.LastUsed(app, entries, environment);
            if (when is null && app.MainExecutable is { } exe)
            {
                try
                {
                    if (File.Exists(exe))
                    {
                        var access = File.GetLastAccessTimeUtc(exe);
                        // Access times older than the file itself mean "never updated".
                        if (access > File.GetLastWriteTimeUtc(exe).AddMinutes(1)) when = access;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            result[app.Id] = when;
        }
        return result;
    }
}
