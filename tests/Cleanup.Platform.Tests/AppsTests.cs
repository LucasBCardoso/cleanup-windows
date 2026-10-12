using System.Diagnostics;
using Cleanup.Core.Models;
using Cleanup.Core.Uninstaller;
using Cleanup.Platform.Windows.Apps;
using Microsoft.Win32;

namespace Cleanup.Platform.Tests;

/// <summary>A scratch key, <c>HKCU\Software\CleanupTests\{guid}</c>, deleted afterwards. Nothing else is written.</summary>
public sealed class ScratchKey : IDisposable
{
    public string Relative { get; } = @"Software\CleanupTests\" + Guid.NewGuid().ToString("N");
    public string Full => @"HKEY_CURRENT_USER\" + Relative;

    public RegistryKey Create(string sub = "") => Registry.CurrentUser.CreateSubKey(sub.Length == 0 ? Relative : Relative + "\\" + sub);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Relative, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\CleanupTests", writable: true);
        if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\CleanupTests", throwOnMissingSubKey: false);
    }
}

public class AppInventoryPlatformTests
{
    [Fact]
    public void ReadsAFakeUninstallKeySkippingSystemComponentsAndUpdates()
    {
        using var scratch = new ScratchKey();
        using (var fake = scratch.Create(@"Uninstall\FakeApp"))
        {
            fake.SetValue("DisplayName", "Fake App 1.2 (x64)");
            fake.SetValue("DisplayVersion", "1.2");
            fake.SetValue("Publisher", "Fake Corp.");
            fake.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
            fake.SetValue("UninstallString", "\"C:\\Program Files\\Fake App\\uninstall.exe\" /x");
            fake.SetValue("InstallLocation", @"%ProgramFiles%\Fake App", RegistryValueKind.ExpandString);
        }
        using (var hidden = scratch.Create(@"Uninstall\Hidden"))
        {
            hidden.SetValue("DisplayName", "Hidden");
            hidden.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
        }
        using (var patch = scratch.Create(@"Uninstall\Patch"))
        {
            patch.SetValue("DisplayName", "Patch");
            patch.SetValue("ParentKeyName", "FakeApp");
        }
        var inventory = new WindowsAppInventory(ScanEnvironment.Current);
        var apps = inventory.Win32Apps([new(RegistryHive.CurrentUser, RegistryView.Default, scratch.Relative + @"\Uninstall", "TEST")]);
        var app = Assert.Single(apps);
        Assert.Equal("Fake App 1.2 (x64)", app.Name);
        Assert.Equal(2048L * 1024, app.Size);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fake App"), app.InstallFolder);
        Assert.Equal(scratch.Full + @"\Uninstall\FakeApp", app.RegistryKey);

        var platform = new WindowsAppPlatform(ScanEnvironment.Current);
        Assert.True(platform.IsInstalled(app));
        Registry.CurrentUser.DeleteSubKeyTree(scratch.Relative + @"\Uninstall\FakeApp");
        Assert.False(platform.IsInstalled(app));
    }

    /// <summary>Read-only pass over this PC's real inventory: it must load, and never list frameworks.</summary>
    [Fact]
    public void RealInventoryLoadsReadOnly()
    {
        var apps = new WindowsAppInventory(ScanEnvironment.Current).Load(measure: false);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.All(apps.Where(a => a.Kind == AppKind.Store), a => Assert.NotNull(a.PackageFamilyName));
        Assert.Equal(apps.Count, apps.Select(a => a.Id).Distinct().Count());
        var usage = UserAssistReader.LastUsed(apps, ScanEnvironment.Current);
        Assert.All(usage.Values.OfType<DateTime>(), d => Assert.True(d <= DateTime.UtcNow.AddDays(1)));
    }

    [Fact]
    public void UserAssistReadsWithoutThrowing()
    {
        var entries = UserAssistReader.Read();
        Assert.DoesNotContain(entries, e => e.Name.StartsWith("UEME_", StringComparison.Ordinal));
    }
}

public class WindowsRegistryTests
{
    [Fact]
    public void ExportDeleteAndReimportRoundTrip()
    {
        using var scratch = new ScratchKey();
        using (var key = scratch.Create(@"Vendor\Product"))
        {
            key.SetValue("", "padrão");
            key.SetValue("Text", "C:\\a \"b\"");
            key.SetValue("Count", 255, RegistryValueKind.DWord);
            key.SetValue("Big", 1L << 40, RegistryValueKind.QWord);
            key.SetValue("Blob", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            key.SetValue("Expand", "%TEMP%\\x", RegistryValueKind.ExpandString);
            key.SetValue("Multi", new[] { "um", "dois" }, RegistryValueKind.MultiString);
            using var sub = key.CreateSubKey("Sub");
            sub.SetValue("N", 1, RegistryValueKind.DWord);
        }
        var registry = new WindowsRegistry();
        var full = scratch.Full + @"\Vendor\Product";
        Assert.Equal(["Product"], registry.SubKeyNames(scratch.Full + @"\Vendor"));
        Assert.True(registry.ApproximateSize(full) > 20);

        var file = Path.Combine(Path.GetTempPath(), "cleanup-reg-" + Guid.NewGuid().ToString("N") + ".reg");
        try
        {
            registry.Export(full, file);
            var text = File.ReadAllText(file);
            Assert.StartsWith(RegExport.Header, text);
            Assert.Contains("\"Count\"=dword:000000ff", text);
            Assert.Contains($"[{full}\\Sub]", text);

            registry.DeleteTree(full);
            Assert.Null(Registry.CurrentUser.OpenSubKey(scratch.Relative + @"\Vendor\Product"));

            // The backup puts everything back, byte for byte (reg.exe import, HKCU only: no elevation).
            using (var reg = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"), ["import", file])
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!)
            {
                reg.WaitForExit(30_000);
                Assert.Equal(0, reg.ExitCode);
            }
            using var back = Registry.CurrentUser.OpenSubKey(scratch.Relative + @"\Vendor\Product")!;
            Assert.Equal("padrão", back.GetValue(""));
            Assert.Equal("C:\\a \"b\"", back.GetValue("Text"));
            Assert.Equal(255, back.GetValue("Count"));
            Assert.Equal(1L << 40, back.GetValue("Big"));
            Assert.Equal(new byte[] { 1, 2, 3 }, back.GetValue("Blob"));
            Assert.Equal("%TEMP%\\x", back.GetValue("Expand", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(new[] { "um", "dois" }, back.GetValue("Multi"));
            using var backSub = back.OpenSubKey("Sub")!;
            Assert.Equal(1, backSub.GetValue("N"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void DeleteRefusesSharedAndForeignKeys()
    {
        var registry = new WindowsRegistry();
        Assert.Throws<UnauthorizedAccessException>(() => registry.DeleteTree(@"HKEY_CURRENT_USER\Software\Microsoft\CleanupNope"));
        Assert.Throws<UnauthorizedAccessException>(() => registry.DeleteTree(@"HKEY_LOCAL_MACHINE\SOFTWARE\CleanupNope\X"));
        Assert.Throws<UnauthorizedAccessException>(() => registry.DeleteTree(@"HKEY_CURRENT_USER\Software\Classes"));
    }

    /// <summary>The whole Desinstalador path for the registry, with real keys in the scratch area only.</summary>
    [Fact]
    public void RegistryCleanupBacksUpThenDeletes()
    {
        using var scratch = new ScratchKey();
        using (var key = scratch.Create(@"Acme\Rocket")) key.SetValue("x", 1, RegistryValueKind.DWord);
        var backups = Directory.CreateTempSubdirectory("cleanup-backups-").FullName;
        try
        {
            var item = new ScanItem
            {
                Path = scratch.Full + @"\Acme\Rocket", Size = 4, RuleId = LeftoverFinder.RegistryRule,
                Category = Core.Rules.ScanCategory.AppLeftovers, Risk = Core.Rules.RiskLevel.Safe,
            };
            var result = RegistryCleanup.Remove([item], new WindowsRegistry(), backups);
            Assert.Single(result.Removed);
            Assert.Contains("\"x\"=dword:00000001", File.ReadAllText(Assert.Single(result.Backups)));
            Assert.Null(Registry.CurrentUser.OpenSubKey(scratch.Relative + @"\Acme\Rocket"));
        }
        finally { Directory.Delete(backups, true); }
    }
}

public class AppPlatformTests
{
    /// <summary>Windows releases an exited process's image a moment later.</summary>
    private static void DeleteSoon(string folder)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(folder, true); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 20) { Thread.Sleep(250); }
        }
    }

    private static string CopyOfPing(string folder)
    {
        var copy = Path.Combine(folder, "cleanup-test-ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), copy);
        return copy;
    }

    /// <summary>Starts a harmless copy of ping from a temp folder (and stops only that process).</summary>
    [Fact]
    public void RunningMeansAProcessImageInsideTheInstallFolder()
    {
        var folder = Directory.CreateTempSubdirectory("cleanup-app-").FullName;
        var exe = CopyOfPing(folder);
        var app = new InstalledApp { Id = "t", Name = "Teste", Kind = AppKind.Win32, InstallFolder = folder, MainExecutable = exe };
        var platform = new WindowsAppPlatform(ScanEnvironment.Current);
        Assert.False(platform.IsRunning(app));
        var process = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            Assert.Equal(exe, ProcessImages.Of(process.Id), ignoreCase: true);
            Assert.True(platform.IsRunning(app));
        }
        finally
        {
            process.Kill();
            process.WaitForExit(10_000);
            process.Dispose();
            DeleteSoon(folder);
        }
    }

    /// <summary>
    /// The real uninstall path with a harmless "uninstaller" (ping, once): it is started directly, waited for, and the
    /// app counts as still installed because its key remains.
    /// </summary>
    [Fact]
    public async Task UninstallerIsStartedDirectlyAndTheKeyDecides()
    {
        using var scratch = new ScratchKey();
        var folder = Directory.CreateTempSubdirectory("cleanup-app-").FullName;
        try
        {
            var exe = CopyOfPing(folder);
            using (scratch.Create(@"Uninstall\Fake")) { }
            var app = new InstalledApp
            {
                Id = "t", Name = "Teste", Kind = AppKind.Win32, RegistryKey = scratch.Full + @"\Uninstall\Fake",
                UninstallString = $"\"{exe}\" -n 1 127.0.0.1",
            };
            var platform = new WindowsAppPlatform(ScanEnvironment.Current) { HelperTimeout = TimeSpan.FromSeconds(5) };
            var run = await platform.UninstallAsync(app, CancellationToken.None);
            Assert.Equal(UninstallerExit.Finished, run.Exit);
            Assert.True(platform.IsInstalled(app));

            var missing = app with { UninstallString = @"C:\nao\existe\uninstall.exe", QuietUninstallString = null };
            Assert.Equal(UninstallerExit.Failed, (await platform.UninstallAsync(missing, CancellationToken.None)).Exit);
            var shell = app with { UninstallString = @"cmd.exe /c echo x", QuietUninstallString = null };
            Assert.Equal(UninstallerExit.Failed, (await platform.UninstallAsync(shell, CancellationToken.None)).Exit);
            Assert.Equal(UninstallerExit.Failed, (await platform.UninstallAsync(app with { IsSystemApp = true }, CancellationToken.None)).Exit);
        }
        finally { DeleteSoon(folder); }
    }
}
