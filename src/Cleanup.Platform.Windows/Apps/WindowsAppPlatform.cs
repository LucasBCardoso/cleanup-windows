using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;
using Cleanup.Core.Uninstaller;
using Microsoft.Win32;
using Windows.Management.Deployment;
using Windows.Management.Core;

namespace Cleanup.Platform.Windows.Apps;

/// <summary>
/// The real <see cref="IAppPlatform"/>: starts the vendor uninstaller directly (never through a shell), removes or
/// resets MSIX packages through <c>PackageManager</c>, and finds an app's processes by their image path.
/// </summary>
public sealed partial class WindowsAppPlatform(ScanEnvironment environment) : IAppPlatform
{
    /// <summary>How long to wait for an uninstaller that handed over to a helper and exited (NSIS, Inno Setup).</summary>
    public TimeSpan HelperTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public bool IsRunning(InstalledApp app)
    {
        var folder = app.Kind == AppKind.Store ? app.InstallFolder : AppInventory.SpecificFolder(app.InstallFolder, environment);
        var exe = app.MainExecutable;
        if (folder is null && exe is null) return false;
        foreach (var image in ProcessImages.All())
        {
            if (folder is not null && SafetyPolicy.IsWithin(image, folder)) return true;
            if (exe is not null && string.Equals(image, exe, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public bool IsInstalled(InstalledApp app)
    {
        if (app.Kind == AppKind.Store)
        {
            if (app.PackageFullName is not { } fullName) return false;
            try { return new PackageManager().FindPackageForUser(string.Empty, fullName) is not null; }
            catch (COMException) { return true; } // unknown: never treat as removed
        }
        return app.RegistryKey is { } key && RegistryKeyExists(key, app.RegistryView);
    }

    internal static bool RegistryKeyExists(string fullKey, int view)
    {
        var cut = fullKey.IndexOf('\\');
        if (cut < 0) return false;
        var hive = fullKey[..cut].ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => (RegistryHive?)null,
        };
        if (hive is null) return false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive.Value, view == 32 ? RegistryView.Registry32 : RegistryView.Registry64);
            using var key = root.OpenSubKey(fullKey[(cut + 1)..]);
            return key is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return true; }
    }

    public async Task<UninstallerRun> UninstallAsync(InstalledApp app, CancellationToken cancellation)
    {
        if (app.IsSystemApp) return new UninstallerRun(UninstallerExit.Failed, "componente do Windows");
        return app.Kind == AppKind.Store ? await RemovePackageAsync(app) : await RunUninstallerAsync(app, cancellation);
    }

    private static async Task<UninstallerRun> RemovePackageAsync(InstalledApp app)
    {
        if (app.PackageFullName is not { } fullName) return new UninstallerRun(UninstallerExit.Failed, "pacote desconhecido");
        try
        {
            var result = await new PackageManager().RemovePackageAsync(fullName);
            return result.IsRegistered || result.ExtendedErrorCode is not null && result.ExtendedErrorCode.HResult != 0
                ? new UninstallerRun(UninstallerExit.Failed, string.IsNullOrWhiteSpace(result.ErrorText) ? "o Windows recusou a remoção" : result.ErrorText)
                : new UninstallerRun(UninstallerExit.Finished);
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new UninstallerRun(UninstallerExit.Failed, e.Message);
        }
    }

    /// <summary>
    /// Starts the vendor's uninstaller with <c>ShellExecute</c> (so its own manifest can ask for administrator rights;
    /// no command interpreter is involved), waits for it, then for any helper it handed over to.
    /// </summary>
    private async Task<UninstallerRun> RunUninstallerAsync(InstalledApp app, CancellationToken cancellation)
    {
        var command = UninstallCommand.Parse(app.UninstallString, environment.SystemRoot)
                      ?? UninstallCommand.Parse(app.QuietUninstallString, environment.SystemRoot);
        if (command is null) return new UninstallerRun(UninstallerExit.Failed, "o app não informa um desinstalador que o Cleanup possa abrir");
        if (!File.Exists(command.FileName)) return new UninstallerRun(UninstallerExit.Failed, $"o desinstalador não existe mais ({command.FileName})");

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(command.FileName) ?? "",
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: the UAC prompt was refused
        {
            return new UninstallerRun(UninstallerExit.Cancelled);
        }
        catch (Win32Exception e) { return new UninstallerRun(UninstallerExit.Failed, e.Message); }

        var started = Path.GetFileNameWithoutExtension(command.FileName);
        if (process is not null)
        {
            using (process) await process.WaitForExitAsync(cancellation);
        }

        // NSIS and Inno Setup copy themselves to %TEMP% ("Au_.exe", "_iu14D2N.tmp") and exit right away.
        var deadline = DateTime.UtcNow + HelperTimeout;
        while (IsInstalled(app) && DateTime.UtcNow < deadline && HelperRunning(started))
            await Task.Delay(1000, cancellation);
        // Some uninstallers delete their key as the very last step.
        for (var i = 0; i < 3 && IsInstalled(app); i++) await Task.Delay(700, cancellation);
        return new UninstallerRun(UninstallerExit.Finished);
    }

    internal static bool HelperRunning(string startedName)
    {
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                string name;
                try { name = p.ProcessName; }
                catch (InvalidOperationException) { continue; }
                if (name.Equals(startedName, StringComparison.OrdinalIgnoreCase) || UninstallerHelper().IsMatch(name)) return true;
            }
        }
        return false;
    }

    [GeneratedRegex(@"^(Au_|Un_A|Un_|unins\d*|uninst|_iu|_is[0-9a-f]|.*uninstall)", RegexOptions.IgnoreCase)]
    private static partial Regex UninstallerHelper();

    [GeneratedRegex(@"^[A-Za-z0-9._~\-]+$")]
    private static partial Regex PackageName();

    /// <summary>
    /// Store reset, the same as Settings › Apps › Opções avançadas › Redefinir: <c>Reset-AppxPackage</c> (Windows 10 2004+),
    /// or clearing the package's app data when that cmdlet is missing.
    /// </summary>
    public async Task ResetPackageAsync(InstalledApp app, CancellationToken cancellation)
    {
        if (app.IsSystemApp) throw new AppActionException($"{app.Name} é um componente do Windows e não pode ser redefinido.");
        if (app.PackageFullName is not { } fullName || !PackageName().IsMatch(fullName)
            || app.PackageFamilyName is not { } family || !PackageName().IsMatch(family))
            throw new AppActionException("Pacote desconhecido.");

        var powershell = Path.Combine(environment.SystemRoot, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(powershell))
        {
            // The name is checked against [A-Za-z0-9._~-] above, so it can't break out of the quotes. Exit code 3 means
            // this Windows has no Reset-AppxPackage.
            var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
                         "if (-not (Get-Command Reset-AppxPackage -ErrorAction SilentlyContinue)) { exit 3 }; " +
                         $"Reset-AppxPackage -Package '{fullName}'";
            var psi = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in (string[])["-NoProfile", "-NonInteractive", "-Command", script]) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi) ?? throw new AppActionException("Não foi possível iniciar o PowerShell.");
            var error = p.StandardError.ReadToEndAsync(cancellation);
            _ = await p.StandardOutput.ReadToEndAsync(cancellation);
            await p.WaitForExitAsync(cancellation);
            var message = (await error).Trim();
            if (p.ExitCode == 0) return;
            if (p.ExitCode != 3) throw new AppActionException("O Windows não redefiniu o app: " + FirstLine(message));
        }
        // Older Windows: clear the package's local, roaming and temporary data.
        await ApplicationDataManager.CreateForPackageFamily(family).ClearAsync();
    }

    private static string FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "erro desconhecido";
}

/// <summary>Image paths of running processes (QueryFullProcessImageName works for elevated ones too).</summary>
public static class ProcessImages
{
    public static List<string> All()
    {
        var images = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (Of(p.Id) is { } image) images.Add(image);
            }
        }
        return images;
    }

    public static string? Of(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
