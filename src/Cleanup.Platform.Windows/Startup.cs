using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Models;
using Cleanup.Core.Optimization;
using Cleanup.Core.Rules;
using Microsoft.Win32;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Where startup entries are read from. The real machine by default; tests point the hives at a scratch key
/// (<c>HKCU\Software\CleanupTests\&lt;guid&gt;</c>) and the folders at a temporary folder.
/// </summary>
public sealed class StartupLocations
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnce32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public required RegistryKey UserHive { get; init; }
    public RegistryKey? MachineHive { get; init; }
    public required string UserStartupFolder { get; init; }
    public string? CommonStartupFolder { get; init; }
    /// <summary>Services whose program is under this folder are Windows itself and aren't listed.</summary>
    public string SystemRoot { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    /// <summary><c>schtasks /query /xml ONE</c>, or null to skip scheduled tasks.</summary>
    public Func<string?>? ScheduledTasksXml { get; init; }

    public static StartupLocations Current() => new()
    {
        UserHive = Registry.CurrentUser,
        MachineHive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        UserStartupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        CommonStartupFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        ScheduledTasksXml = ReadScheduledTasks,
    };

    private static string? ReadScheduledTasks()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), ["/query", "/xml", "ONE"])
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000)) { process.Kill(); return null; }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}

/// <summary>Removable, network or missing drives: a program "missing" there may just be unplugged or offline.</summary>
public static class Drives
{
    public static bool IsUnverifiable(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return true;
            return new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Ram);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return true; }
    }
}

/// <summary>
/// Lists startup entries (design §5.2): HKCU/HKLM Run and RunOnce, the user's and the common Startup folders,
/// third-party logon/boot scheduled tasks and automatic third-party services. Read only.
/// </summary>
public sealed class StartupInventory(StartupLocations locations, Func<string, bool>? fileExists = null, Func<string, bool>? isUnverifiableDrive = null)
{
    private readonly Func<string, bool> exists = fileExists ?? File.Exists;
    private readonly Func<string, bool> unverifiable = isUnverifiableDrive ?? Drives.IsUnverifiable;

    public IReadOnlyList<StartupItem> Load()
    {
        var items = new List<StartupItem>();
        var user = locations.UserHive;
        items.AddRange(RegistryItems(user, StartupLocations.RunKey, StartupKind.Run, StartupScope.CurrentUser, false, user, StartupApproval.RunKey));
        items.AddRange(RegistryItems(user, StartupLocations.RunOnceKey, StartupKind.RunOnce, StartupScope.CurrentUser, false, null, null));
        items.AddRange(FolderItems(locations.UserStartupFolder, StartupScope.CurrentUser, user));
        if (locations.MachineHive is { } machine)
        {
            items.AddRange(RegistryItems(machine, StartupLocations.RunKey, StartupKind.Run, StartupScope.AllUsers, false, machine, StartupApproval.RunKey));
            items.AddRange(RegistryItems(machine, StartupLocations.Run32Key, StartupKind.Run, StartupScope.AllUsers, true, machine, StartupApproval.Run32Key));
            items.AddRange(RegistryItems(machine, StartupLocations.RunOnceKey, StartupKind.RunOnce, StartupScope.AllUsers, false, null, null));
            items.AddRange(RegistryItems(machine, StartupLocations.RunOnce32Key, StartupKind.RunOnce, StartupScope.AllUsers, true, null, null));
            if (locations.CommonStartupFolder is { } common) items.AddRange(FolderItems(common, StartupScope.AllUsers, machine));
            items.AddRange(ServiceItems(machine));
        }
        if (locations.ScheduledTasksXml?.Invoke() is { } xml) items.AddRange(TaskItems(xml));
        return items.OrderBy(i => StartupGroups.Of(i)).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private IEnumerable<StartupItem> RegistryItems(RegistryKey hive, string keyPath, StartupKind kind, StartupScope scope, bool is32,
        RegistryKey? approvedHive, string? approvedKey)
    {
        using var key = Open(hive, keyPath);
        if (key is null) yield break;
        using var approved = approvedKey is null || approvedHive is null ? null : Open(approvedHive, approvedKey);
        foreach (var name in key.GetValueNames())
        {
            if (name.Length == 0) continue; // "(Default)"
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command) continue;
            var program = StartupCommand.Program(command, exists, Environment.ExpandEnvironmentVariables);
            yield return new StartupItem
            {
                Name = name, Kind = kind, Scope = scope, Location = keyPath, ValueName = name, Command = command, Program = program,
                Is32Bit = is32, IsDisabled = StartupApproval.IsDisabled(approved?.GetValue(name) as byte[]),
                BrokenReason = StartupCommand.BrokenReason(program, exists, unverifiable), Publisher = PublisherOf(program),
            };
        }
    }

    private IEnumerable<StartupItem> FolderItems(string folder, StartupScope scope, RegistryKey approvedHive)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) yield break;
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .Where(f => !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        using var approved = Open(approvedHive, StartupApproval.StartupFolderKey);
        foreach (var file in files)
        {
            string? program = file.FullName;
            string? command = file.FullName;
            string? broken = null;
            if (file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var link = ShellLink.Read(file.FullName);
                if (link is null) { program = null; command = null; broken = "atalho ilegível"; }
                else
                {
                    program = string.IsNullOrEmpty(link.Target) ? null : link.Target;
                    command = program is null ? null : string.IsNullOrEmpty(link.Arguments) ? $"\"{program}\"" : $"\"{program}\" {link.Arguments}";
                }
            }
            yield return new StartupItem
            {
                Name = Path.GetFileNameWithoutExtension(file.Name), Kind = StartupKind.StartupFolder, Scope = scope, Location = file.FullName,
                ValueName = file.Name, Command = command, Program = program,
                IsDisabled = StartupApproval.IsDisabled(approved?.GetValue(file.Name) as byte[]),
                BrokenReason = broken ?? StartupCommand.BrokenReason(program, exists, unverifiable), Publisher = PublisherOf(program),
            };
        }
    }

    private IEnumerable<StartupItem> ServiceItems(RegistryKey machine)
    {
        using var services = Open(machine, StartupLocations.ServicesKey);
        if (services is null) yield break;
        foreach (var name in services.GetSubKeyNames())
        {
            using var service = Open(services, name);
            if (service is null) continue;
            var start = service.GetValue("Start") as int?;
            var type = service.GetValue("Type") as int? ?? 0;
            if (start != 2 || (type & 0x30) == 0) continue; // automatic, Win32 service (not a driver)
            if (service.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command) continue;
            var program = StartupCommand.Program(command, exists, Environment.ExpandEnvironmentVariables);
            if (program is null || !Path.IsPathFullyQualified(program) || Core.Safety.SafetyPolicy.IsWithin(program, locations.SystemRoot)) continue;
            var display = service.GetValue("DisplayName") as string;
            yield return new StartupItem
            {
                Name = string.IsNullOrWhiteSpace(display) || display.StartsWith('@') ? name : display, Kind = StartupKind.Service,
                Scope = StartupScope.AllUsers, Location = name, Command = command, Program = program,
                BrokenReason = StartupCommand.BrokenReason(program, exists, unverifiable), Publisher = PublisherOf(program),
            };
        }
    }

    private IEnumerable<StartupItem> TaskItems(string xml)
    {
        foreach (var task in ScheduledTaskParser.StartupTasks(ScheduledTaskParser.Parse(xml)))
        {
            var program = StartupCommand.Program(task.Command, exists, Environment.ExpandEnvironmentVariables);
            var name = task.Path.TrimEnd('\\');
            yield return new StartupItem
            {
                Name = name[(name.LastIndexOf('\\') + 1)..], Kind = StartupKind.ScheduledTask, Scope = StartupScope.AllUsers, Location = task.Path,
                Command = task.Command is null ? null : string.IsNullOrEmpty(task.Arguments) ? task.Command : $"{task.Command} {task.Arguments}",
                Program = program, IsDisabled = !task.Enabled,
                BrokenReason = StartupCommand.BrokenReason(program, exists, unverifiable), Publisher = PublisherOf(program) ?? task.Author,
            };
        }
    }

    private string? PublisherOf(string? program)
    {
        if (program is null || !Path.IsPathFullyQualified(program) || !exists(program)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(program);
            var text = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.CompanyName;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or ArgumentException) { return null; }
    }

    internal static RegistryKey? Open(RegistryKey parent, string path)
    {
        try { return parent.OpenSubKey(path, writable: false); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}

/// <summary>
/// Enables, disables and removes the user's own startup entries — the same switches Task Manager flips. Run values
/// are backed up before removal (so they can be restored); Startup-folder files go to the Recycle Bin.
/// Entries of all users, tasks and services are never changed here.
/// </summary>
public sealed class StartupController(RegistryKey userHive, string userStartupFolder, StartupBackupStore backups, Func<Cleaner> cleaner)
{
    public static StartupController Current(Func<Cleaner> cleaner) => new(Registry.CurrentUser,
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), new StartupBackupStore(StartupBackupStore.DefaultFolder), cleaner);

    public StartupBackupStore Backups => backups;

    public void SetEnabled(StartupItem item, bool enabled)
    {
        if (!item.CanToggle || item.ValueName is not { } valueName) throw new InvalidOperationException(NotManageable);
        string approvedKey;
        if (item.Kind == StartupKind.Run)
        {
            RequireUserRunKey(item);
            using var run = StartupInventory.Open(userHive, item.Location);
            if (run?.GetValue(valueName) is null) throw new InvalidOperationException(Gone);
            approvedKey = StartupApproval.RunKey;
        }
        else
        {
            RequireInUserStartupFolder(item);
            if (!File.Exists(item.Location)) throw new InvalidOperationException(Gone);
            approvedKey = StartupApproval.StartupFolderKey;
        }
        using var approved = userHive.CreateSubKey(approvedKey, writable: true);
        approved.SetValue(valueName, enabled ? StartupApproval.Enabled() : StartupApproval.Disabled(DateTime.UtcNow), RegistryValueKind.Binary);
    }

    public StartupRemoval Remove(StartupItem item)
    {
        if (!item.IsManageable) throw new InvalidOperationException(NotManageable);
        if (item.Kind == StartupKind.StartupFolder)
        {
            RequireInUserStartupFolder(item);
            var size = File.Exists(item.Location) ? new FileInfo(item.Location).Length : 0;
            var scanItem = new ScanItem
            {
                Path = item.Location, Size = size, RuleId = "startup-item", Category = ScanCategory.AppLeftovers, Risk = RiskLevel.Review,
            };
            var result = cleaner().Clean([scanItem]);
            return new StartupRemoval(result.Removed.Count > 0, result, result.Failed.FirstOrDefault()?.Reason);
        }
        RequireUserRunKey(item);
        if (item.ValueName is not { } name) throw new InvalidOperationException(NotManageable);
        using var key = userHive.OpenSubKey(item.Location, writable: true);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command)
            return new StartupRemoval(false, Error: Gone);
        // Saved first: if the backup can't be written, nothing is removed.
        backups.Save(new StartupBackup
        {
            Name = name, KeyPath = item.Location, Command = command, Expandable = key.GetValueKind(name) == RegistryValueKind.ExpandString,
        });
        key.DeleteValue(name, throwOnMissingValue: false);
        return new StartupRemoval(true);
    }

    /// <summary>Puts a removed Run value back. Never overwrites a value of the same name; the backup goes once restored.</summary>
    public void Restore(StartupBackup backup)
    {
        if (!backup.HasRestorableKey || backup.Name.Length == 0) throw new InvalidOperationException("Cópia de segurança inválida.");
        using var key = userHive.CreateSubKey(backup.KeyPath, writable: true);
        if (key.GetValue(backup.Name) is not null)
            throw new InvalidOperationException($"Já existe um item de inicialização chamado “{backup.Name}”.");
        key.SetValue(backup.Name, backup.Command, backup.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        backups.Delete(backup.Id);
    }

    private const string NotManageable = "Este item é de todos os usuários ou do sistema e precisa de administrador para ser alterado.";
    private const string Gone = "O item não existe mais.";

    private static void RequireUserRunKey(StartupItem item)
    {
        if (item.Scope != StartupScope.CurrentUser || item.Is32Bit
            || !StartupBackup.RestorableKeys.Any(k => string.Equals(k, item.Location, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(NotManageable);
    }

    private void RequireInUserStartupFolder(StartupItem item)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(item.Location));
        if (item.Scope != StartupScope.CurrentUser || parent is null
            || !string.Equals(parent.TrimEnd('\\'), Path.GetFullPath(userStartupFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(NotManageable);
    }
}

/// <summary>Reads a <c>.lnk</c> through the shell's <c>IShellLinkW</c>, without resolving (no search, no UI).</summary>
public static class ShellLink
{
    public sealed record Link(string Target, string Arguments);

    public static Link? Read(string path)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            if (instance is not IShellLinkW link) return null;
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            var arguments = new StringBuilder(2048);
            link.GetArguments(arguments, arguments.Capacity);
            return new Link(Environment.ExpandEnvironmentVariables(target.ToString()), arguments.ToString());
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    /// <summary>Writes a shortcut (tests, and nothing else).</summary>
    public static void Write(string path, string target, string arguments = "")
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(target);
            link.SetArguments(arguments);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.ReleaseComObject(instance); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
