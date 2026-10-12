using System.Diagnostics;
using Cleanup.Core.Extensions;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;
using Cleanup.Core.Updater;
using Microsoft.Win32;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Explorer shell extensions and Office COM add-ins from the registry. Takes the two hive roots so tests can point it
/// at a throwaway key. Windows' own extensions (DLLs under the Windows folder) are left out of the list.
/// </summary>
public sealed class RegistryExtensions(RegistryKey currentUser, RegistryKey localMachine, string systemRoot)
{
    public static RegistryExtensions Current() =>
        new(Registry.CurrentUser, Registry.LocalMachine, Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>Where context-menu handlers hang, and what they apply to.</summary>
    internal static readonly (string Class, string Label)[] HandlerClasses =
    [
        ("*", "arquivos"), ("AllFilesystemObjects", "arquivos e pastas"), ("Directory", "pastas"),
        (@"Directory\Background", "fundo da pasta"), ("Folder", "pastas"), ("Drive", "unidades"), ("DesktopBackground", "área de trabalho"),
    ];

    internal static readonly string[] OfficeApps = ["Word", "Excel", "PowerPoint", "Outlook", "Access", "OneNote", "Visio", "MS Project", "Publisher"];

    public IReadOnlyList<InstalledExtension> Load() => [.. ShellExtensions(), .. OfficeAddIns()];

    public IReadOnlyList<InstalledExtension> ShellExtensions()
    {
        var items = new List<InstalledExtension>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, label, scope) in Hives())
        {
            // Context-menu handlers, grouped by CLSID: one extension registered for files and folders is one row.
            var handlers = new Dictionary<string, (string Name, List<string> Keys, List<string> Targets)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cls, target) in HandlerClasses)
            {
                var path = $@"Software\Classes\{cls}\shellex\ContextMenuHandlers";
                using var key = Open(hive, path);
                if (key is null) continue;
                foreach (var name in SafeSubKeyNames(key))
                {
                    using var handler = Open(key, name);
                    var clsid = NormalizeClsid(handler?.GetValue(null) as string) ?? NormalizeClsid(name);
                    if (clsid is null) continue;
                    if (!handlers.TryGetValue(clsid, out var entry)) handlers[clsid] = entry = (name.Trim(), [], []);
                    entry.Keys.Add($@"{label}\{path}\{name}");
                    if (!entry.Targets.Contains(target)) entry.Targets.Add(target);
                }
            }
            foreach (var (clsid, (name, keys, targets)) in handlers)
            {
                var (friendly, dll) = ResolveClsid(hive, clsid);
                if (IsWindowsComponent(dll) || !seen.Add(scope + clsid)) continue;
                items.Add(Item(NormalizeClsid(name) is null ? name : friendly ?? clsid, dll, scope,
                    "Menu de contexto de " + string.Join(", ", targets), keys));
            }

            // Icon overlays (OneDrive, Dropbox, Git clients…): machine-wide only.
            if (scope == ExtensionScope.System)
            {
                const string overlays = @"Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";
                using var key = Open(hive, overlays);
                foreach (var name in key is null ? [] : SafeSubKeyNames(key))
                {
                    using var overlay = Open(key!, name);
                    if (NormalizeClsid(overlay?.GetValue(null) as string) is not { } clsid) continue;
                    var (friendly, dll) = ResolveClsid(hive, clsid);
                    if (IsWindowsComponent(dll) || !seen.Add(scope + clsid)) continue;
                    items.Add(Item(name.Trim() is { Length: > 0 } n ? n : friendly ?? clsid, dll, scope, "Sobreposição de ícone", []));
                }
            }

            // Approved shell extensions not already listed (property sheets, previewers, column handlers…).
            using (var approved = Open(hive, @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"))
            {
                foreach (var value in approved?.GetValueNames() ?? [])
                {
                    if (NormalizeClsid(value) is not { } clsid || seen.Contains(scope + clsid)) continue;
                    var (friendly, dll) = ResolveClsid(hive, clsid);
                    if (dll is null || IsWindowsComponent(dll)) continue;
                    seen.Add(scope + clsid);
                    var description = approved!.GetValue(value) as string;
                    items.Add(Item(string.IsNullOrWhiteSpace(description) ? friendly ?? clsid : description.Trim(), dll, scope,
                        "Extensão do shell", []));
                }
            }
        }
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public IReadOnlyList<InstalledExtension> OfficeAddIns()
    {
        var items = new List<InstalledExtension>();
        (RegistryKey Hive, string Prefix, ExtensionScope Scope)[] places =
        [
            (currentUser, @"Software\Microsoft\Office", ExtensionScope.User),
            (localMachine, @"Software\Microsoft\Office", ExtensionScope.System),
            (localMachine, @"Software\WOW6432Node\Microsoft\Office", ExtensionScope.System),
        ];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, prefix, scope) in places)
        {
            foreach (var app in OfficeApps)
            {
                using var addins = Open(hive, $@"{prefix}\{app}\Addins");
                foreach (var progId in addins is null ? [] : SafeSubKeyNames(addins))
                {
                    if (!seen.Add($"{scope}:{app}:{progId}")) continue;
                    using var addin = Open(addins!, progId);
                    var friendly = addin?.GetValue("FriendlyName") as string;
                    var loads = addin?.GetValue("LoadBehavior") is int behavior && (behavior & 1) == 1;
                    items.Add(new InstalledExtension
                    {
                        Kind = ExtensionKind.OfficeAddIn, Scope = scope,
                        Name = string.IsNullOrWhiteSpace(friendly) || friendly.StartsWith('@') ? progId : friendly.Trim(),
                        Detail = $"{(app == "MS Project" ? "Project" : app)} · {(loads ? "carrega ao abrir" : "desativado")}",
                    });
                }
            }
        }
        return items;
    }

    /// <summary>The screen saver chosen in Control Panel (it may live outside the usual folders).</summary>
    public string? CurrentScreenSaver()
    {
        using var desktop = Open(currentUser, @"Control Panel\Desktop");
        return desktop?.GetValue("SCRNSAVE.EXE") is string path && path.Length > 0 ? Environment.ExpandEnvironmentVariables(path) : null;
    }

    private IEnumerable<(RegistryKey Hive, string Label, ExtensionScope Scope)> Hives()
    {
        yield return (currentUser, "HKCU", ExtensionScope.User);
        yield return (localMachine, "HKLM", ExtensionScope.System);
    }

    private readonly Dictionary<string, DllInfo> dllInfo = new(StringComparer.OrdinalIgnoreCase);

    private sealed record DllInfo(string? Version, string? Description, bool IsWindows);

    /// <summary>Version resource of a handler's DLL (cached: many handlers share one DLL).</summary>
    private DllInfo Info(string? dll)
    {
        if (dll is null || !Path.IsPathFullyQualified(dll)) return new DllInfo(null, null, false);
        if (dllInfo.TryGetValue(dll, out var cached)) return cached;
        DllInfo info = new(null, null, false);
        try
        {
            if (File.Exists(dll))
            {
                var v = FileVersionInfo.GetVersionInfo(dll);
                // "Microsoft® Windows® Operating System" / "Sistema Operacional Microsoft® Windows®": part of Windows
                // even outside the Windows folder (Windows Defender, Common Files\System).
                var isWindows = (v.CompanyName ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                                && (v.ProductName ?? "").Contains("Windows®", StringComparison.Ordinal);
                info = new DllInfo(Clean(v.ProductVersion), Clean(v.FileDescription), isWindows);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        dllInfo[dll] = info;
        return info;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private InstalledExtension Item(string name, string? dll, ExtensionScope scope, string detail, IReadOnlyList<string> keys)
    {
        var info = Info(dll);
        // A bare CLSID says nothing: the DLL's description, then its file name, read better.
        if (NormalizeClsid(name) is not null) name = info.Description ?? (dll is null ? name : Path.GetFileNameWithoutExtension(dll));
        return new InstalledExtension
        {
            Kind = ExtensionKind.ShellExtension, Scope = scope, Name = name, Version = info.Version,
            Detail = dll is null ? detail : $"{detail} · {Path.GetFileName(dll)}", DllPath = dll, RegistryKeys = keys,
        };
    }

    /// <summary>The class name and in-process DLL of a CLSID, looked up in the same hive, then machine-wide.</summary>
    private (string? Name, string? Dll) ResolveClsid(RegistryKey hive, string clsid)
    {
        foreach (var root in hive == localMachine ? [localMachine] : new[] { hive, localMachine })
        {
            using var key = Open(root, $@"Software\Classes\CLSID\{clsid}");
            if (key is null) continue;
            using var server = Open(key, "InprocServer32");
            var dll = server?.GetValue(null) as string;
            var name = key.GetValue(null) as string;
            return (string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                string.IsNullOrWhiteSpace(dll) ? null : Environment.ExpandEnvironmentVariables(dll.Trim().Trim('"')));
        }
        return (null, null);
    }

    private bool IsWindowsComponent(string? dll)
    {
        if (dll is null) return false;
        if (!Path.IsPathFullyQualified(dll)) return true; // "shell32.dll": resolved from System32
        return SafetyPolicy.IsWithin(dll, systemRoot) || Info(dll).IsWindows;
    }

    internal static string? NormalizeClsid(string? value)
    {
        if (value is null) return null;
        var v = value.Trim();
        return Guid.TryParseExact(v, "B", out var guid) ? guid.ToString("B").ToUpperInvariant() : null;
    }

    private static RegistryKey? Open(RegistryKey root, string path)
    {
        try { return root.OpenSubKey(path, writable: false); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static string[] SafeSubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }
}

/// <summary>
/// Removes per-user context-menu handlers: each key is exported to a <c>.reg</c> file in
/// <c>%LOCALAPPDATA%\Cleanup\backups</c> first (design §6), and nothing is deleted when the backup fails.
/// </summary>
public sealed class ShellExtensionRemover(RegistryKey currentUser, string currentUserPath, string backupFolder, IToolRunner runner)
{
    /// <param name="currentUserPath">What <paramref name="currentUser"/> is, as <c>reg.exe</c> names it (<c>HKCU</c>).</param>
    public static ShellExtensionRemover Current(IToolRunner runner) =>
        new(Registry.CurrentUser, "HKCU", Path.Combine(AppPaths.SupportDirectory, "backups"), runner);

    public sealed record Result(IReadOnlyList<InstalledExtension> Removed, IReadOnlyList<(InstalledExtension Item, string Reason)> Failed,
        IReadOnlyList<string> Backups);

    public async Task<Result> RemoveAsync(IReadOnlyList<InstalledExtension> items)
    {
        var removed = new List<InstalledExtension>();
        var failed = new List<(InstalledExtension, string)>();
        var backups = new List<string>();
        foreach (var item in items)
        {
            if (!item.IsRemovable || item.Kind != ExtensionKind.ShellExtension || !item.RegistryKeys.All(ExtensionRemoval.IsUserShellExtensionKey))
            {
                failed.Add((item, "só extensões registradas para você podem ser removidas"));
                continue;
            }
            string? error = null;
            foreach (var key in item.RegistryKeys)
            {
                var relative = key[@"HKCU\".Length..];
                using (var existing = currentUser.OpenSubKey(relative))
                {
                    if (existing is null) continue; // already gone
                }
                Directory.CreateDirectory(backupFolder);
                var file = Path.Combine(backupFolder, $"shellext-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.reg");
                var export = await runner.RunAsync(RegExe, ["export", $@"{currentUserPath}\{relative}", file, "/y"], TimeSpan.FromSeconds(30));
                if (export.ExitCode != 0 || !File.Exists(file))
                {
                    error = "não foi possível salvar a cópia de segurança do registro";
                    break;
                }
                backups.Add(file);
                try { currentUser.DeleteSubKeyTree(relative, throwOnMissingSubKey: false); }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
                {
                    error = e.Message;
                    break;
                }
            }
            if (error is null) removed.Add(item);
            else failed.Add((item, error));
        }
        return new Result(removed, failed, backups);
    }

    private static string RegExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
}
