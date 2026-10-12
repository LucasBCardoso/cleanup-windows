using Cleanup.Core.Extensions;
using Cleanup.Platform.Windows;
using Microsoft.Win32;

namespace Cleanup.Platform.Tests;

public class ProcessToolRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    [Fact]
    public async Task ReturnsTheExitCodeAndOutput()
    {
        var result = await new ProcessToolRunner().RunAsync(Cmd, ["/d", "/c", "echo hello& exit 3"], TimeSpan.FromSeconds(20));
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("hello", result.Output);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task KillsTheProcessOnTimeout()
    {
        var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
        var started = DateTime.UtcNow;
        var result = await new ProcessToolRunner().RunAsync(ping, ["-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(500));
        Assert.True(result.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task MissingProgramIsToolNotFound() =>
        await Assert.ThrowsAsync<Cleanup.Core.Updater.ToolNotFoundException>(() =>
            new ProcessToolRunner().RunAsync(@"C:\nonexistent\winget.exe", [], TimeSpan.FromSeconds(5)));
}

/// <summary>Registry tests on a throwaway key under HKCU\Software\Cleanup.Tests, deleted afterwards.</summary>
public sealed class RegistryExtensionsTests : IDisposable
{
    private readonly string basePath = @"Software\Cleanup.Tests\" + Guid.NewGuid().ToString("N")[..10];
    private readonly RegistryKey user;
    private readonly RegistryKey machine;
    private readonly string backups = Directory.CreateTempSubdirectory("cleanup-regbackup-").FullName;
    private const string Clsid = "{23170F69-40C1-278A-1000-000100020000}";
    private const string WindowsClsid = "{09A47860-11B0-4DA5-AFA5-26D86198A780}";

    public RegistryExtensionsTests()
    {
        user = Registry.CurrentUser.CreateSubKey(basePath + @"\hkcu");
        machine = Registry.CurrentUser.CreateSubKey(basePath + @"\hklm");
        foreach (var cls in (string[])["*", "Directory"])
        {
            using var k = user.CreateSubKey($@"Software\Classes\{cls}\shellex\ContextMenuHandlers\7-Zip");
            k.SetValue(null, Clsid);
        }
        using (var c = user.CreateSubKey($@"Software\Classes\CLSID\{Clsid}\InprocServer32")) c.SetValue(null, @"C:\Users\me\AppData\Local\7-Zip\7-zip.dll");
        using (var m = machine.CreateSubKey($@"Software\Classes\*\shellex\ContextMenuHandlers\EPP")) m.SetValue(null, WindowsClsid);
        using (var c = machine.CreateSubKey($@"Software\Classes\CLSID\{WindowsClsid}\InprocServer32")) c.SetValue(null, @"C:\Windows\System32\shell32.dll");
        using (var m = machine.CreateSubKey(@"Software\Classes\Folder\shellex\ContextMenuHandlers\Dropbox")) m.SetValue(null, "{ECD3AB7C-6A63-4B91-AE2D-E62C3F4C9D59}");
        using (var c = machine.CreateSubKey(@"Software\Classes\CLSID\{ECD3AB7C-6A63-4B91-AE2D-E62C3F4C9D59}"))
        {
            c.SetValue(null, "DropboxExt");
            using var s = c.CreateSubKey("InprocServer32");
            s.SetValue(null, @"C:\Program Files (x86)\Dropbox\DropboxExt64.dll");
        }
        using (var o = user.CreateSubKey(@"Software\Microsoft\Office\Excel\Addins\Vendor.Tools"))
        {
            o.SetValue("FriendlyName", "Vendor Tools");
            o.SetValue("LoadBehavior", 3, RegistryValueKind.DWord);
        }
    }

    private RegistryExtensions Extensions => new(user, machine, @"C:\Windows");

    [Fact]
    public void ListsContextMenuHandlersOncePerClsidAndSkipsWindowsOwn()
    {
        var items = Extensions.ShellExtensions();
        Assert.Equal(["7-Zip", "Dropbox"], items.Select(i => i.Name).Order());
        var zip = items.Single(i => i.Name == "7-Zip");
        Assert.Equal(ExtensionScope.User, zip.Scope);
        Assert.Equal(2, zip.RegistryKeys.Count);
        Assert.Contains("arquivos, pastas", zip.Detail);
        Assert.True(zip.IsRemovable);
        var dropbox = items.Single(i => i.Name == "Dropbox");
        Assert.Equal(ExtensionScope.System, dropbox.Scope);
        Assert.False(dropbox.IsRemovable);
    }

    [Fact]
    public void ListsOfficeAddInsAsReportOnly()
    {
        var addin = Assert.Single(Extensions.OfficeAddIns());
        Assert.Equal("Vendor Tools", addin.Name);
        Assert.Equal("Excel · carrega ao abrir", addin.Detail);
        Assert.False(addin.IsRemovable);
    }

    [Fact]
    public async Task RemovingAUserHandlerBacksItUpFirst()
    {
        var zip = Extensions.ShellExtensions().Single(i => i.Name == "7-Zip");
        var remover = new ShellExtensionRemover(user, $@"HKCU\{basePath}\hkcu", backups, new ProcessToolRunner());
        var result = await remover.RemoveAsync([zip]);
        Assert.Equal([zip], result.Removed);
        Assert.Equal(2, result.Backups.Count);
        Assert.All(result.Backups, b => Assert.Contains("ContextMenuHandlers", File.ReadAllText(b)));
        Assert.Null(user.OpenSubKey(@"Software\Classes\*\shellex\ContextMenuHandlers\7-Zip"));
        Assert.NotNull(user.OpenSubKey(@"Software\Classes\*\shellex\ContextMenuHandlers")); // only the handler went
        Assert.DoesNotContain(Extensions.ShellExtensions(), i => i.Name == "7-Zip");
    }

    [Fact]
    public async Task NothingIsDeletedWhenTheBackupFails()
    {
        var zip = Extensions.ShellExtensions().Single(i => i.Name == "7-Zip");
        var remover = new ShellExtensionRemover(user, @"HKCU\Software\Cleanup.Tests\does-not-exist", backups, new ProcessToolRunner());
        var result = await remover.RemoveAsync([zip]);
        Assert.Empty(result.Removed);
        Assert.Single(result.Failed);
        Assert.NotNull(user.OpenSubKey(@"Software\Classes\*\shellex\ContextMenuHandlers\7-Zip"));
    }

    [Fact]
    public async Task MachineHandlersAreNeverRemoved()
    {
        var dropbox = Extensions.ShellExtensions().Single(i => i.Name == "Dropbox");
        var forged = dropbox with { Scope = ExtensionScope.User };
        var result = await new ShellExtensionRemover(user, "HKCU", backups, new ProcessToolRunner()).RemoveAsync([dropbox, forged]);
        Assert.Empty(result.Removed);
        Assert.Equal(2, result.Failed.Count);
        Assert.NotNull(machine.OpenSubKey(@"Software\Classes\Folder\shellex\ContextMenuHandlers\Dropbox"));
    }

    public void Dispose()
    {
        user.Dispose();
        machine.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(basePath, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\Cleanup.Tests", writable: true))
        {
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\Cleanup.Tests", throwOnMissingSubKey: false);
        }
        try { Directory.Delete(backups, recursive: true); } catch (IOException) { }
    }
}
