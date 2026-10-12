using Cleanup.Core.Cleaning;
using Cleanup.Core.Models;
using Cleanup.Core.Optimization;
using Cleanup.Core.Safety;
using Cleanup.Platform.Windows;
using Microsoft.Win32;

namespace Cleanup.Platform.Tests;

/// <summary>
/// A scratch registry key (<c>HKCU\Software\CleanupTests\&lt;guid&gt;</c>, deleted afterwards) standing in for HKCU and HKLM,
/// and a temporary profile with its own Startup folders. Nothing real is read or changed.
/// </summary>
public sealed class StartupFixture : IDisposable
{
    public string KeyPath { get; } = @"Software\CleanupTests\" + Guid.NewGuid().ToString("N");
    public RegistryKey User { get; }
    public RegistryKey Machine { get; }
    public string Root { get; } = Directory.CreateTempSubdirectory("cleanup-startup-").FullName;
    public string Home => Path.Combine(Root, "Users", "me");
    public string UserStartup => Path.Combine(Home, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");
    public string CommonStartup => Path.Combine(Root, @"ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp");
    public string SystemRoot => Path.Combine(Root, "Windows");
    public string Backups => Path.Combine(Root, "backups");
    public FakeRemover Remover { get; } = new();

    public StartupFixture()
    {
        User = Registry.CurrentUser.CreateSubKey(KeyPath + @"\user", writable: true);
        Machine = Registry.CurrentUser.CreateSubKey(KeyPath + @"\machine", writable: true);
        Directory.CreateDirectory(UserStartup);
        Directory.CreateDirectory(CommonStartup);
        Directory.CreateDirectory(SystemRoot);
    }

    public string Exe(string name)
    {
        var path = Path.Combine(Root, "Programs", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }

    public string Missing(string name) => Path.Combine(Root, "Gone", name);

    public void Set(RegistryKey hive, string key, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var k = hive.CreateSubKey(key, writable: true);
        k.SetValue(name, value, kind);
    }

    public object? Get(RegistryKey hive, string key, string name)
    {
        using var k = hive.OpenSubKey(key);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public StartupInventory Inventory(string? tasksXml = null) => new(new StartupLocations
    {
        UserHive = User, MachineHive = Machine, UserStartupFolder = UserStartup, CommonStartupFolder = CommonStartup,
        SystemRoot = SystemRoot, ScheduledTasksXml = tasksXml is null ? null : () => tasksXml,
    }, isUnverifiableDrive: _ => false);

    public StartupController Controller()
    {
        var environment = new ScanEnvironment(Home, new Dictionary<string, string>
        {
            ["USERPROFILE"] = Home, ["LOCALAPPDATA"] = Path.Combine(Home, @"AppData\Local"), ["APPDATA"] = Path.Combine(Home, @"AppData\Roaming"),
            ["SystemRoot"] = SystemRoot, ["PROGRAMDATA"] = Path.Combine(Root, "ProgramData"),
        });
        return new StartupController(User, UserStartup, new StartupBackupStore(Backups), () => new Cleaner(new SafetyPolicy(environment), Remover));
    }

    public void Dispose()
    {
        User.Dispose();
        Machine.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\CleanupTests", writable: true))
        {
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\CleanupTests", throwOnMissingSubKey: false);
        }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeRemover : IFileRemover
{
    public List<string> Recycled { get; } = [];

    public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths) => paths.Select(p =>
    {
        Recycled.Add(p);
        return new RecycleOutcome(p, true, @"C:\$Recycle.Bin\S-1-5-21-1\$R" + Recycled.Count, null);
    }).ToList();

    public void Delete(string path) => throw new InvalidOperationException("tests never delete");
    public void EmptyRecycleBin(long expectedBytes, long expectedItems) => throw new InvalidOperationException("tests never empty the Recycle Bin");
}

public sealed class StartupInventoryTests : IDisposable
{
    private readonly StartupFixture f = new();

    public void Dispose() => f.Dispose();

    [Fact]
    public void ListsRunValuesFlagsBrokenAndReadsTaskManagersSwitch()
    {
        var ok = f.Exe("ok app.exe");
        f.Set(f.User, StartupLocations.RunKey, "Ok", $"\"{ok}\" --tray");
        f.Set(f.User, StartupLocations.RunKey, "Gone", f.Missing("gone.exe") + " /min");
        f.Set(f.User, StartupLocations.RunKey, "Expand", @"%SystemRoot%\nothing-here.exe", RegistryValueKind.ExpandString);
        f.Set(f.User, StartupLocations.RunOnceKey, "Once", $"\"{ok}\"");
        f.Set(f.User, StartupApproval.RunKey, "Ok", StartupApproval.Disabled(DateTime.UtcNow), RegistryValueKind.Binary);
        f.Set(f.User, StartupApproval.RunKey, "Gone", StartupApproval.Enabled(), RegistryValueKind.Binary);

        var items = f.Inventory().Load().ToDictionary(i => i.Name);
        Assert.Equal(StartupKind.Run, items["Ok"].Kind);
        Assert.Equal(StartupScope.CurrentUser, items["Ok"].Scope);
        Assert.Equal(ok, items["Ok"].Program);
        Assert.True(items["Ok"].IsDisabled);
        Assert.False(items["Ok"].IsBroken);
        Assert.True(items["Gone"].IsBroken);
        Assert.False(items["Gone"].IsDisabled);
        Assert.Equal(StartupCommand.MissingPrefix + f.Missing("gone.exe"), items["Gone"].BrokenReason);
        Assert.Equal(@"%SystemRoot%\nothing-here.exe", items["Expand"].Command); // shown as registered
        Assert.True(items["Expand"].IsBroken);                                  // judged expanded
        Assert.Equal(StartupKind.RunOnce, items["Once"].Kind);
        Assert.False(items["Once"].CanToggle);
    }

    [Fact]
    public void ListsStartupFolderShortcutsAndSkipsDesktopIni()
    {
        var ok = f.Exe("tray.exe");
        ShellLink.Write(Path.Combine(f.UserStartup, "Tray.lnk"), ok, "/background");
        ShellLink.Write(Path.Combine(f.UserStartup, "Gone.lnk"), f.Missing("gone.exe"));
        File.WriteAllText(Path.Combine(f.UserStartup, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(f.UserStartup, "Broken.lnk"), "not a shortcut");
        File.WriteAllText(Path.Combine(f.CommonStartup, "script.bat"), "@echo off");
        f.Set(f.User, StartupApproval.StartupFolderKey, "Tray.lnk", StartupApproval.Disabled(DateTime.UtcNow), RegistryValueKind.Binary);

        var items = f.Inventory().Load();
        var mine = items.Where(i => i.Scope == StartupScope.CurrentUser).ToDictionary(i => i.Name);
        Assert.Equal(["Broken", "Gone", "Tray"], mine.Keys.Order());
        Assert.Equal(ok, mine["Tray"].Program, ignoreCase: true);
        Assert.Contains("/background", mine["Tray"].Command);
        Assert.True(mine["Tray"].IsDisabled);
        Assert.Equal("Tray.lnk", mine["Tray"].ValueName);
        Assert.True(mine["Gone"].IsBroken);
        Assert.Equal("atalho ilegível", mine["Broken"].BrokenReason);
        var common = items.Single(i => i.Scope == StartupScope.AllUsers);
        Assert.Equal("script", common.Name);
        Assert.False(common.IsManageable);
    }

    [Fact]
    public void MachineEntriesTasksAndThirdPartyServicesAreReadOnly()
    {
        var ok = f.Exe("svc.exe");
        f.Set(f.Machine, StartupLocations.RunKey, "Machine", $"\"{ok}\"");
        f.Set(f.Machine, StartupLocations.Run32Key, "Machine32", $"\"{ok}\"");
        f.Set(f.Machine, StartupApproval.Run32Key, "Machine32", StartupApproval.Disabled(DateTime.UtcNow), RegistryValueKind.Binary);
        void Service(string name, int start, int type, string image, string? display = null)
        {
            var key = StartupLocations.ServicesKey + "\\" + name;
            f.Set(f.Machine, key, "Start", start, RegistryValueKind.DWord);
            f.Set(f.Machine, key, "Type", type, RegistryValueKind.DWord);
            f.Set(f.Machine, key, "ImagePath", image, RegistryValueKind.ExpandString);
            if (display is not null) f.Set(f.Machine, key, "DisplayName", display);
        }
        Service("VendorSvc", 2, 0x10, $"\"{ok}\" -service", "Vendor Service");
        Service("WinSvc", 2, 0x20, Path.Combine(f.SystemRoot, @"System32\svchost.exe") + " -k netsvcs");
        Service("Driver", 2, 0x1, @"\SystemRoot\System32\drivers\x.sys");
        Service("Manual", 3, 0x10, $"\"{ok}\"");
        Service("NoName", 2, 0x10, f.Missing("svc2.exe"), "@%SystemRoot%\\x.dll,-1");
        const string xml = """
            <Tasks>
            <!-- \Vendor Updater -->
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger /></Triggers>
              <Actions Context="Author"><Exec><Command>C:\Vendor\up.exe</Command></Exec></Actions>
            </Task>
            </Tasks>
            """;

        var items = f.Inventory(xml).Load();
        Assert.All(items, i => Assert.False(i.IsManageable));
        var byName = items.ToDictionary(i => i.Name);
        Assert.Equal(["Machine", "Machine32", "NoName", "Vendor Service", "Vendor Updater"], byName.Keys.Order());
        Assert.True(byName["Machine32"].Is32Bit);
        Assert.True(byName["Machine32"].IsDisabled);
        Assert.False(byName["Machine"].IsDisabled);
        Assert.Equal(StartupKind.Service, byName["Vendor Service"].Kind);
        Assert.True(byName["NoName"].IsBroken);
        Assert.Equal(StartupKind.ScheduledTask, byName["Vendor Updater"].Kind);
        Assert.Equal(@"\Vendor Updater", byName["Vendor Updater"].Location);
        // Groups come in list order: mine, all users, tasks, services.
        Assert.Equal(items.Select(StartupGroups.Of).Order(), items.Select(StartupGroups.Of));
    }

    [Fact]
    public void ARealInventoryReadsWithoutThrowing()
    {
        // Read-only: lists this PC's startup entries (registry, folders, schtasks) and changes nothing.
        var items = new StartupInventory(StartupLocations.Current()).Load();
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Name)));
    }
}

public sealed class StartupControllerTests : IDisposable
{
    private readonly StartupFixture f = new();

    public void Dispose() => f.Dispose();

    private StartupItem Find(string name) => f.Inventory().Load().Single(i => i.Name == name);

    [Fact]
    public void DisablesAndEnablesLikeTaskManager()
    {
        f.Set(f.User, StartupLocations.RunKey, "App", $"\"{f.Exe("app.exe")}\"");
        var controller = f.Controller();
        controller.SetEnabled(Find("App"), enabled: false);
        Assert.True(Find("App").IsDisabled);
        Assert.Equal(0x03, ((byte[])f.Get(f.User, StartupApproval.RunKey, "App")!)[0]);
        controller.SetEnabled(Find("App"), enabled: true);
        Assert.False(Find("App").IsDisabled);
        Assert.Equal(0x02, ((byte[])f.Get(f.User, StartupApproval.RunKey, "App")!)[0]);
        Assert.NotNull(f.Get(f.User, StartupLocations.RunKey, "App")); // the entry itself is untouched
    }

    [Fact]
    public void DisablesAStartupFolderShortcut()
    {
        ShellLink.Write(Path.Combine(f.UserStartup, "Tray.lnk"), f.Exe("tray.exe"));
        f.Controller().SetEnabled(Find("Tray"), enabled: false);
        Assert.True(Find("Tray").IsDisabled);
        Assert.True(File.Exists(Path.Combine(f.UserStartup, "Tray.lnk")));
    }

    [Fact]
    public void RemovingARunValueBacksItUpAndRestoreNeverOverwrites()
    {
        f.Set(f.User, StartupLocations.RunKey, "Tool", @"%LOCALAPPDATA%\Tool\tool.exe -q", RegistryValueKind.ExpandString);
        var controller = f.Controller();
        var removal = controller.Remove(Find("Tool"));
        Assert.True(removal.Removed);
        Assert.Null(f.Get(f.User, StartupLocations.RunKey, "Tool"));
        var backup = Assert.Single(controller.Backups.Load());
        Assert.Equal("Tool", backup.Name);
        Assert.Equal(@"%LOCALAPPDATA%\Tool\tool.exe -q", backup.Command);
        Assert.True(backup.Expandable);

        // Something new took the name meanwhile: refused, and the backup stays.
        f.Set(f.User, StartupLocations.RunKey, "Tool", "other.exe");
        Assert.Throws<InvalidOperationException>(() => controller.Restore(backup));
        Assert.Single(controller.Backups.Load());

        using (var run = f.User.OpenSubKey(StartupLocations.RunKey, writable: true)) run!.DeleteValue("Tool");
        controller.Restore(backup);
        Assert.Equal(@"%LOCALAPPDATA%\Tool\tool.exe -q", f.Get(f.User, StartupLocations.RunKey, "Tool"));
        using (var run = f.User.OpenSubKey(StartupLocations.RunKey)) Assert.Equal(RegistryValueKind.ExpandString, run!.GetValueKind("Tool"));
        Assert.Empty(controller.Backups.Load());
    }

    [Fact]
    public void RemovingAStartupShortcutSendsItToTheRecycleBin()
    {
        var link = Path.Combine(f.UserStartup, "Gone.lnk");
        ShellLink.Write(link, f.Missing("gone.exe"));
        var removal = f.Controller().Remove(Find("Gone"));
        Assert.True(removal.Removed);
        Assert.Equal([link], f.Remover.Recycled);
        Assert.NotNull(removal.Recycled);
        Assert.Single(removal.Recycled!.RecycleBinLocations);
    }

    [Fact]
    public void EntriesOfAllUsersAndOutsideTheUsersPlacesAreRefused()
    {
        f.Set(f.Machine, StartupLocations.RunKey, "Machine", "x.exe");
        var controller = f.Controller();
        var machine = Find("Machine");
        Assert.Throws<InvalidOperationException>(() => controller.SetEnabled(machine, false));
        Assert.Throws<InvalidOperationException>(() => controller.Remove(machine));
        Assert.Equal("x.exe", f.Get(f.Machine, StartupLocations.RunKey, "Machine"));

        // A forged "user" item pointing outside the Startup folder or at another key.
        var outside = new StartupItem
        {
            Name = "x", Kind = StartupKind.StartupFolder, Scope = StartupScope.CurrentUser, Location = Path.Combine(f.Home, "Documents", "x.lnk"),
            ValueName = "x.lnk",
        };
        Assert.Throws<InvalidOperationException>(() => controller.Remove(outside));
        var otherKey = new StartupItem
        {
            Name = "x", Kind = StartupKind.Run, Scope = StartupScope.CurrentUser, Location = @"Software\Classes\exefile\shell\open\command", ValueName = "",
        };
        Assert.Throws<InvalidOperationException>(() => controller.Remove(otherKey));
        Assert.Empty(f.Remover.Recycled);
    }

    [Fact]
    public void RemovingAnEntryThatIsAlreadyGoneReportsIt()
    {
        f.Set(f.User, StartupLocations.RunKey, "Tool", "tool.exe");
        var item = Find("Tool");
        using (var run = f.User.OpenSubKey(StartupLocations.RunKey, writable: true)) run!.DeleteValue("Tool");
        var removal = f.Controller().Remove(item);
        Assert.False(removal.Removed);
        Assert.NotNull(removal.Error);
    }
}
