using Cleanup.Core.Optimization;

namespace Cleanup.Core.Tests.Optimization;

public class StartupItemTests
{
    private static readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Program Files\Vendor App\app.exe", @"C:\Windows\System32\rundll32.exe",
    };

    private static string? Program(string command) => StartupCommand.Program(command, Existing.Contains,
        s => s.Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("\"C:\\Program Files\\Vendor App\\app.exe\" --tray", @"C:\Program Files\Vendor App\app.exe")]
    [InlineData("\"C:\\Gone\\x.exe\"", @"C:\Gone\x.exe")]
    [InlineData("C:\\Program Files\\Vendor App\\app.exe /min", @"C:\Program Files\Vendor App\app.exe")]
    [InlineData("C:\\Program Files\\Gone App\\gone.exe /min", @"C:\Program Files\Gone App\gone.exe")]
    [InlineData("%ProgramFiles%\\Vendor App\\app.exe -s", @"C:\Program Files\Vendor App\app.exe")]
    [InlineData("C:\\Tools\\run.exe", @"C:\Tools\run.exe")]
    [InlineData("ctfmon.exe", "ctfmon.exe")]
    [InlineData("C:\\Windows\\System32\\rundll32.exe C:\\Gone\\helper.dll,Start", @"C:\Gone\helper.dll")]
    [InlineData("rundll32.exe \"C:\\Gone Dir\\helper.dll\",Start", @"C:\Gone Dir\helper.dll")]
    public void FindsTheProgramInACommandLine(string command, string expected) => Assert.Equal(expected, Program(command));

    [Fact]
    public void EmptyCommandHasNoProgram()
    {
        Assert.Null(Program(""));
        Assert.Null(Program("   "));
        Assert.Null(StartupCommand.Program(null, _ => true));
    }

    [Fact]
    public void BrokenOnlyWhenAFullLocalPathIsMissing()
    {
        static bool Removable(string p) => p.StartsWith(@"E:\", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StartupCommand.MissingPrefix + @"C:\Gone\x.exe", StartupCommand.BrokenReason(@"C:\Gone\x.exe", Existing.Contains, Removable));
        Assert.Null(StartupCommand.BrokenReason(@"C:\Program Files\Vendor App\app.exe", Existing.Contains, Removable));
        Assert.Null(StartupCommand.BrokenReason("ctfmon.exe", Existing.Contains, Removable));        // found through PATH
        Assert.Null(StartupCommand.BrokenReason(@"E:\Portable\x.exe", Existing.Contains, Removable)); // may be unplugged
        Assert.Null(StartupCommand.BrokenReason(@"\\server\share\x.exe", Existing.Contains, Removable));
        Assert.Null(StartupCommand.BrokenReason(null, Existing.Contains, Removable));
    }

    /// <summary>Task Manager's StartupApproved bytes: 02/06 enabled, 03 disabled; no value = enabled.</summary>
    [Fact]
    public void ReadsAndWritesStartupApprovedLikeTaskManager()
    {
        Assert.False(StartupApproval.IsDisabled(null));
        Assert.False(StartupApproval.IsDisabled([]));
        Assert.False(StartupApproval.IsDisabled([0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(StartupApproval.IsDisabled([0x06, 0, 0, 0]));
        Assert.True(StartupApproval.IsDisabled([0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.False(StartupApproval.IsDisabled(StartupApproval.Enabled()));
        var when = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var disabled = StartupApproval.Disabled(when);
        Assert.Equal(12, disabled.Length);
        Assert.True(StartupApproval.IsDisabled(disabled));
        Assert.Equal(when, DateTime.FromFileTimeUtc(BitConverter.ToInt64(disabled, 4)));
    }

    [Fact]
    public void OnlyTheUsersOwnRegistryAndFolderEntriesAreManageable()
    {
        StartupItem Item(StartupKind kind, StartupScope scope) => new() { Name = "x", Kind = kind, Scope = scope, Location = "l" };
        Assert.True(Item(StartupKind.Run, StartupScope.CurrentUser).CanToggle);
        Assert.True(Item(StartupKind.StartupFolder, StartupScope.CurrentUser).CanToggle);
        Assert.True(Item(StartupKind.RunOnce, StartupScope.CurrentUser).IsManageable);
        Assert.False(Item(StartupKind.RunOnce, StartupScope.CurrentUser).CanToggle);
        Assert.False(Item(StartupKind.Run, StartupScope.AllUsers).IsManageable);
        Assert.False(Item(StartupKind.ScheduledTask, StartupScope.CurrentUser).IsManageable);
        Assert.False(Item(StartupKind.Service, StartupScope.AllUsers).IsManageable);
        Assert.Equal(StartupGroup.Mine, StartupGroups.Of(Item(StartupKind.Run, StartupScope.CurrentUser)));
        Assert.Equal(StartupGroup.AllUsers, StartupGroups.Of(Item(StartupKind.StartupFolder, StartupScope.AllUsers)));
        Assert.Equal(StartupGroup.Services, StartupGroups.Of(Item(StartupKind.Service, StartupScope.AllUsers)));
    }

    /// <summary>Real <c>schtasks /query /xml ONE</c> shape (trimmed): comments name each task.</summary>
    private const string TasksXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Tasks>
        <!-- \Adobe-GC -->
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>Adobe Systems Inc.</Author><URI>\Adobe-GC</URI></RegistrationInfo>
          <Triggers><CalendarTrigger><StartBoundary>2026-06-22T12:33:00</StartBoundary></CalendarTrigger></Triggers>
          <Actions Context="Author"><Exec><Command>C:\Program Files (x86)\Adobe\AGS.exe</Command></Exec></Actions>
        </Task>
        <!-- \Vendor\Updater Logon -->
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>Vendor</Author></RegistrationInfo>
          <Settings><Enabled>false</Enabled></Settings>
          <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
          <Actions Context="Author"><Exec><Command>"C:\Vendor\up.exe"</Command><Arguments>/silent</Arguments></Exec></Actions>
        </Task>
        <!-- \BootThing -->
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><BootTrigger /><LogonTrigger><Enabled>false</Enabled></LogonTrigger></Triggers>
          <Actions Context="Author"><ComHandler><ClassId>{1}</ClassId></ComHandler></Actions>
        </Task>
        <!-- \Microsoft\Windows\Defrag\Logon -->
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><LogonTrigger /></Triggers>
          <Actions Context="Author"><Exec><Command>defrag.exe</Command></Exec></Actions>
        </Task>
        </Tasks>
        """;

    [Fact]
    public void ParsesScheduledTasksAndKeepsThirdPartyLogonAndBootOnes()
    {
        var all = ScheduledTaskParser.Parse(TasksXml);
        Assert.Equal([@"\Adobe-GC", @"\Vendor\Updater Logon", @"\BootThing", @"\Microsoft\Windows\Defrag\Logon"], all.Select(t => t.Path));
        var vendor = all[1];
        Assert.False(vendor.Enabled);
        Assert.True(vendor.AtLogon);
        Assert.Equal("\"C:\\Vendor\\up.exe\"", vendor.Command);
        Assert.Equal("/silent", vendor.Arguments);
        Assert.True(all[2].AtBoot);
        Assert.False(all[2].AtLogon); // its logon trigger is disabled
        Assert.Null(all[2].Command);
        Assert.Equal([@"\Vendor\Updater Logon", @"\BootThing"], ScheduledTaskParser.StartupTasks(all).Select(t => t.Path));
    }

    [Fact]
    public void GarbageTaskXmlIsEmpty() => Assert.Empty(ScheduledTaskParser.Parse("<Tasks><unclosed>"));
}

public sealed class StartupBackupStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "cleanup-backups-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void SavesLoadsNewestFirstAndDeletes()
    {
        var store = new StartupBackupStore(folder);
        Assert.Empty(store.Load());
        var older = new StartupBackup { Name = "Old", KeyPath = StartupBackup.RestorableKeys[0], Command = "a.exe", Date = DateTimeOffset.Now.AddDays(-1) };
        var newer = new StartupBackup { Name = "New", KeyPath = StartupBackup.RestorableKeys[1], Command = "%X%\\b.exe", Expandable = true };
        store.Save(older);
        store.Save(newer);
        var loaded = store.Load();
        Assert.Equal(["New", "Old"], loaded.Select(b => b.Name));
        Assert.True(loaded[0].Expandable);
        store.Delete(newer.Id);
        Assert.Equal(["Old"], store.Load().Select(b => b.Name));
    }

    /// <summary>Invariant 9: the backup file is user-writable, so a key outside the user's Run keys is never offered.</summary>
    [Fact]
    public void BackupsForOtherKeysOrGarbageAreIgnored()
    {
        var store = new StartupBackupStore(folder);
        var bad = new StartupBackup { Name = "Evil", KeyPath = @"Software\Classes\exefile\shell\open\command", Command = "x" };
        store.Save(bad);
        File.WriteAllText(Path.Combine(folder, "startup-garbage.json"), "{ not json");
        Assert.Empty(store.Load());
    }
}
