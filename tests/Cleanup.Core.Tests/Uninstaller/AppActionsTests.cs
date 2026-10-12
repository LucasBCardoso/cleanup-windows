using Cleanup.Core.Cleaning;
using Cleanup.Core.Models;
using Cleanup.Core.Uninstaller;

namespace Cleanup.Core.Tests.Uninstaller;

/// <summary>Desinstalar with fakes: nothing real is ever uninstalled or deleted by these tests.</summary>
public class AppUninstallerTests
{
    private static ScanItem Key(string relative) => new()
    {
        Path = RegistryPolicy.UserSoftware + "\\" + relative, Size = 1, RuleId = LeftoverFinder.RegistryRule,
        Category = Core.Rules.ScanCategory.AppLeftovers, Risk = Core.Rules.RiskLevel.Safe,
    };

    private static ScanItem FileItem(string path, string rule = LeftoverFinder.FileRule) => Items.At(path) with { RuleId = rule };

    [Fact]
    public async Task LeftoversGoOnlyOnceTheAppIsGone()
    {
        using var t = new TempHome();
        var folder = t.Dir(@"AppData\Roaming\Foo");
        var remover = new FakeRemover();
        var registry = new FakeRegistry(@"Vendor\Foo");
        var platform = new FakePlatform();
        var app = Apps.Win32("Foo");
        var backups = Path.Combine(t.Root, "backups");

        var outcome = await AppUninstaller.UninstallAsync(app, [FileItem(folder), Key(@"Vendor\Foo")], platform,
            new Cleaner(t.Policy(), remover), registry, backups);

        Assert.True(outcome.AppRemoved);
        Assert.Equal(["uninstall Foo"], platform.Calls);
        Assert.Equal([folder], remover.Recycled);
        Assert.Equal([@"export HKEY_CURRENT_USER\Software\Vendor\Foo", @"delete HKEY_CURRENT_USER\Software\Vendor\Foo"], registry.Log);
        Assert.Single(outcome.Registry.Backups);
        Assert.True(File.Exists(outcome.Registry.Backups[0]));
    }

    /// <summary>The vendor uninstaller already took its folder and key: they are skipped, not reported as failures.</summary>
    [Fact]
    public async Task WhatTheUninstallerAlreadyRemovedIsSkipped()
    {
        using var t = new TempHome();
        var gone = Path.Combine(t.Home, @"AppData\Local\Programs\Foo");
        var remover = new FakeRemover();
        var registry = new FakeRegistry();
        var outcome = await AppUninstaller.UninstallAsync(Apps.Win32("Foo"), [FileItem(gone), Key(@"Vendor\Foo")], new FakePlatform(),
            new Cleaner(t.Policy(), remover), registry, Path.Combine(t.Root, "b"));
        Assert.True(outcome.AppRemoved);
        Assert.Null(outcome.Files);
        Assert.Empty(outcome.Registry.Failed);
        Assert.Empty(registry.Log);
    }

    [Fact]
    public async Task CancelledOrUnfinishedUninstallTouchesNothing()
    {
        using var t = new TempHome();
        var folder = t.Dir(@"AppData\Roaming\Foo");
        var remover = new FakeRemover();
        var registry = new FakeRegistry(@"Vendor\Foo");
        var platform = new FakePlatform { RemovesApp = false };
        var outcome = await AppUninstaller.UninstallAsync(Apps.Win32("Foo"), [FileItem(folder), Key(@"Vendor\Foo")], platform,
            new Cleaner(t.Policy(), remover), registry, Path.Combine(t.Root, "b"));
        Assert.False(outcome.AppRemoved);
        Assert.Empty(remover.Recycled);
        Assert.Empty(registry.Log);
    }

    [Fact]
    public async Task SystemAppsAreRefusedBeforeAnythingRuns()
    {
        using var t = new TempHome();
        var platform = new FakePlatform();
        await Assert.ThrowsAsync<AppActionException>(() => AppUninstaller.UninstallAsync(Apps.Win32("Paint", system: true), [], platform,
            new Cleaner(t.Policy(), new FakeRemover()), new FakeRegistry(), t.Root));
        Assert.Empty(platform.Calls);
    }

    [Fact]
    public async Task ReportOnlyItemsAndBlockedKeysAreNeverRemoved()
    {
        using var t = new TempHome();
        var programData = t.Dir(Path.Combine(t.ProgramData, "Foo"));
        var remover = new FakeRemover();
        var registry = new FakeRegistry(@"Microsoft\Foo");
        var outcome = await AppUninstaller.UninstallAsync(Apps.Win32("Foo"),
            [FileItem(programData, LeftoverFinder.ReportRule), Key(@"Microsoft\Foo")], new FakePlatform(),
            new Cleaner(t.Policy(), remover), registry, Path.Combine(t.Root, "b"));
        Assert.Empty(remover.Recycled);
        Assert.Empty(registry.Log);
        Assert.Single(outcome.Registry.Failed);
        Assert.Null(outcome.Files);
    }

    [Fact]
    public void NoBackupMeansTheKeyStays()
    {
        using var t = new TempHome();
        var registry = new FakeRegistry(@"Vendor\Foo", @"Vendor\Bar");
        registry.FailExport.Add(@"HKEY_CURRENT_USER\Software\Vendor\Foo");
        var result = RegistryCleanup.Remove([Key(@"Vendor\Foo"), Key(@"Vendor\Bar")], registry, Path.Combine(t.Root, "b"));
        Assert.Equal([@"HKEY_CURRENT_USER\Software\Vendor\Bar"], result.Removed.Select(i => i.Path));
        Assert.Contains("sem cópia de segurança", Assert.Single(result.Failed).Reason);
        Assert.Contains(@"HKEY_CURRENT_USER\Software\Vendor\Foo", registry.Keys);
        Assert.DoesNotContain(registry.Log, l => l == @"delete HKEY_CURRENT_USER\Software\Vendor\Foo");
    }
}

/// <summary>Mirrors the Swift "Redefinir app" suite.</summary>
public class AppResetTests
{
    private static (InstalledApp App, LeftoverFinder Finder, FakeRegistry Registry) Setup(TempHome t)
    {
        t.File(@"AppData\Local\Acme\Rocket\Cache\a", 30);
        t.File(@"AppData\Local\Acme\Rocket\GPUCache\b", 20);
        t.File(@"AppData\Local\Acme\Rocket\User Data\keep", 99);
        t.File(@"AppData\Roaming\Rocket\settings.json", 40);
        t.File(@"AppData\Roaming\Rocket\Code Cache\c", 10);
        t.File(@"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Rocket.lnk");
        t.File(@"AppData\Local\Programs\Rocket\Rocket.exe");
        var registry = new FakeRegistry(@"Acme\Rocket");
        var folder = Path.Combine(t.Home, @"AppData\Local\Programs\Rocket");
        var app = Apps.Win32("Rocket 2.0", "Acme Inc.", folder: folder, exe: Path.Combine(folder, "Rocket.exe"));
        return (app, new LeftoverFinder(t.Environment, t.Policy(), registry), registry);
    }

    [Fact]
    public void PlanGroupsByKindAndPreselectsOnlySettingsAndCaches()
    {
        using var t = new TempHome();
        var (app, finder, _) = Setup(t);
        var plan = AppReset.Plan(app, finder, [app]);
        var byKind = plan.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Select(r => Path.GetFileName(r.Item.Path)).Order().ToList());
        Assert.Equal(["Rocket"], byKind[ResetKind.Settings]);
        Assert.Equal(["Cache", "Code Cache", "GPUCache"], byKind[ResetKind.Caches]);
        Assert.Equal(["Rocket"], byKind[ResetKind.Data]);
        Assert.All(plan, r => Assert.Equal(r.Kind != ResetKind.Data, r.Preselected));
    }

    [Fact]
    public void NeverTheAppItselfOrItsShortcuts()
    {
        using var t = new TempHome();
        var (app, finder, _) = Setup(t);
        var plan = AppReset.Plan(app, finder, [app]);
        Assert.DoesNotContain(plan, r => r.Item.Path.Contains(@"Local\Programs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, r => r.Item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, r => r.Item.Path.EndsWith("User Data", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(AppReset.Plan(Apps.Store("Calc", "Calc_x"), finder, []));
    }

    [Fact]
    public async Task CleanTakesTheChosenItemsAndRefusesRunningOrSystemApps()
    {
        using var t = new TempHome();
        var (app, finder, registry) = Setup(t);
        var plan = AppReset.Plan(app, finder, [app]);
        var chosen = plan.Where(r => r.Preselected).ToList();
        var remover = new FakeRemover();
        var cleaner = new Cleaner(t.Policy(), remover);
        var backups = Path.Combine(t.Root, "backups");

        var running = new FakePlatform { Running = true };
        var e = await Assert.ThrowsAsync<AppActionException>(() => AppReset.CleanAsync(chosen, app, running, cleaner, registry, backups));
        Assert.Equal("Feche o Rocket 2.0 antes de redefinir.", e.Message);
        await Assert.ThrowsAsync<AppActionException>(() => AppReset.CleanAsync(chosen, app with { IsSystemApp = true }, new FakePlatform(), cleaner, registry, backups));
        Assert.Empty(remover.Recycled);
        Assert.Empty(registry.Log);

        var outcome = await AppReset.CleanAsync(chosen, app, new FakePlatform(), cleaner, registry, backups);
        Assert.Equal(["Cache", "Code Cache", "GPUCache"], remover.Recycled.Select(Path.GetFileName).Order());
        Assert.Equal([@"export HKEY_CURRENT_USER\Software\Acme\Rocket", @"delete HKEY_CURRENT_USER\Software\Acme\Rocket"], registry.Log);
        Assert.False(outcome.PackageReset);
        Assert.True(Directory.Exists(Path.Combine(t.Home, @"AppData\Roaming\Rocket"))); // data was not chosen
    }

    [Fact]
    public async Task StoreAppsUseThePackageReset()
    {
        using var t = new TempHome();
        var platform = new FakePlatform();
        var calc = Apps.Store("Calculadora", "Microsoft.WindowsCalculator_8wekyb3d8bbwe");
        var outcome = await AppReset.CleanAsync([], calc, platform, new Cleaner(t.Policy(), new FakeRemover()), new FakeRegistry(), t.Root);
        Assert.True(outcome.PackageReset);
        Assert.Equal(["reset Calculadora"], platform.Calls);
        platform.Running = true;
        await Assert.ThrowsAsync<AppActionException>(() => AppReset.CleanAsync([], calc, platform, new Cleaner(t.Policy(), new FakeRemover()), new FakeRegistry(), t.Root));
    }
}
