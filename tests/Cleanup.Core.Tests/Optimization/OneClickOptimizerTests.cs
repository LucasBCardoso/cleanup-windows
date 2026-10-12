using Cleanup.Core.Admin;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Maintenance;
using Cleanup.Core.Models;
using Cleanup.Core.Optimization;
using Cleanup.Core.Rules;
using Cleanup.Core.Tests.Maintenance;

namespace Cleanup.Core.Tests.Optimization;

public sealed class OneClickOptimizerTests : IDisposable
{
    private readonly TempHome t = new();

    public void Dispose() => t.Dispose();

    private static MaintenanceTask Task(string id, bool admin) => new(id, id, "", admin);

    private ScanItem Junk(string name, long size = 10, RiskLevel risk = RiskLevel.Safe) =>
        Items.At(Path.Combine(t.Home, @"AppData\Local\Temp", name), size, risk: risk);

    private ScanItem SystemJunk(string name, long size = 9) => Items.At(Path.Combine(t.Windows, "Temp", name), size, admin: true);

    /// <summary>A broken entry's program is missing from Program Files (an uninstalled app): certain enough for one click.</summary>
    private StartupItem Startup(string name, bool broken, StartupScope scope = StartupScope.CurrentUser, StartupKind kind = StartupKind.Run,
        string program = @"C:\Program Files\Gone\gone.exe", string? reason = null) => new()
    {
        Name = name, Kind = kind, Scope = scope, Location = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = name,
        Command = $"\"{program}\" --tray", Program = program,
        BrokenReason = broken ? reason ?? StartupCommand.MissingPrefix + program : null,
    };

    private AdminPathPolicy AdminPolicy => new(t.Windows, t.ProgramData);
    private Cleaner Cleaner(FakeRemover? remover = null) => new(t.Policy(), remover ?? new FakeRemover());
    private static bool LocalDrive(string _) => false;

    [Fact]
    public void PlanTakesOnlySafeItemsAndBrokenUserStartupEntries()
    {
        var safe = Junk("a");
        var review = Junk("b", risk: RiskLevel.Review);
        var admin = SystemJunk("x");
        var plan = OneClickOptimizer.Plan([safe, review, admin], t.Environment,
            [Startup("ok", false), Startup("bad", true), Startup("sysbad", true, StartupScope.AllUsers),
             Startup("taskbad", true, StartupScope.AllUsers, StartupKind.ScheduledTask)],
            [Task("free-ram", true), Task("clear-thumbnails", false), Task("sfc", true), Task("flush-dns", true)], LocalDrive);
        Assert.Equal([safe], plan.Trash);
        Assert.Equal([admin], plan.Admin);
        Assert.Equal(["bad"], plan.BrokenStartup.Select(s => s.Name));
        Assert.Equal(["free-ram", "flush-dns"], plan.AdminTasks.Select(x => x.Id)); // only the quick, harmless ones
        Assert.Equal(["clear-thumbnails"], plan.UserTasks.Select(x => x.Id));
        Assert.True(plan.NeedsAdmin);
    }

    [Fact]
    public async Task RunUsesOnePromptForAdminItemsAndAdminTasks()
    {
        var runner = new FakePrivilegedRunner();
        var admin = SystemJunk("vendor");
        var plan = new OptimizationPlan([Junk("a", 5)], [admin], [Task("clear-thumbnails", false)],
            [Task("free-ram", true), Task("flush-dns", true)], []);
        var report = await OneClickOptimizer.RunAsync(plan, Cleaner(), AdminPolicy, runner, _ => new StartupRemoval(false));
        Assert.Equal([false, true], runner.Batches.Select(b => b.AsAdmin));
        Assert.Equal(3, runner.Batches[1].Steps.Count); // 1 removal + 2 tasks, one prompt
        Assert.Equal(Elevation.Ops.Remove, runner.Batches[1].Steps[0].Op);
        Assert.Single(report.Clean.Removed);
        Assert.Equal([admin], report.Admin.Removed);
        Assert.Equal(["clear-thumbnails", "flush-dns", "free-ram"], report.TasksDone.Order());
        Assert.False(report.AdminCancelled);
        Assert.Equal(14, report.FreedBytes);
    }

    /// <summary>Invariant 5: a cancelled prompt keeps the system items and skips the admin tasks; the rest still happens.</summary>
    [Fact]
    public async Task CancelledPromptKeepsAdminWorkButFinishesTheRest()
    {
        var admin = SystemJunk("vendor");
        var plan = new OptimizationPlan([Junk("a")], [admin], [Task("clear-thumbnails", false)], [Task("free-ram", true)], [Startup("bad", true)]);
        var removed = new List<string>();
        var report = await OneClickOptimizer.RunAsync(plan, Cleaner(), AdminPolicy, new CancellingAdminRunner(), item =>
        {
            removed.Add(item.Name);
            return new StartupRemoval(true);
        });
        Assert.True(report.AdminCancelled);
        Assert.Equal([admin], report.Admin.Kept);
        Assert.Empty(report.Admin.Removed);
        Assert.Equal(["clear-thumbnails"], report.TasksDone);
        Assert.Single(report.Clean.Removed);
        Assert.Equal(["bad"], removed);
        Assert.Equal(1, report.StartupRemoved);
    }

    /// <summary>
    /// Only certain breakage is removed in one click: not a program on an unplugged or network drive, not one in a
    /// synced folder, not a shortcut that couldn't be read.
    /// </summary>
    [Fact]
    public void UncertainBrokenStartupEntriesAreLeftForReview()
    {
        var home = t.Home;
        Directory.CreateDirectory(Path.Combine(home, "OneDrive - Contoso"));
        var entries = new[]
        {
            Startup("gone", true),
            Startup("external", true, program: @"E:\Tools\run.exe"),
            Startup("network", true, program: @"\\server\share\run.exe"),
            Startup("docs", true, program: Path.Combine(home, @"Documents\tools\run.exe")),
            Startup("onedrive", true, program: Path.Combine(home, @"OneDrive - Contoso\run.exe")),
            Startup("unreadable", true, kind: StartupKind.StartupFolder, reason: "atalho ilegível"),
            Startup("relative", true, program: "run.exe"),
        };
        var plan = OneClickOptimizer.Plan([], t.Environment, entries, [], p => p.StartsWith(@"E:\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["gone"], plan.BrokenStartup.Select(s => s.Name));
    }

    /// <summary>A cancelled prompt with no system items still means memory and DNS were skipped.</summary>
    [Fact]
    public async Task CancelledPromptWithOnlyAdminTasksIsReported()
    {
        var plan = new OptimizationPlan([], [], [], [Task("free-ram", true)], []);
        var report = await OneClickOptimizer.RunAsync(plan, Cleaner(), AdminPolicy, new CancellingAdminRunner(), _ => new StartupRemoval(false));
        Assert.True(report.AdminCancelled);
        Assert.Equal(["free-ram"], report.AdminTasksCancelled);
        Assert.Empty(report.TasksDone);
    }

    /// <summary>Startup-folder shortcuts that went to the Recycle Bin are part of the cleanup record, so Histórico can put them back.</summary>
    [Fact]
    public async Task StartupRemovalsAreReportedForTheHistory()
    {
        var link = Path.Combine(t.Home, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\gone.lnk");
        var plan = new OptimizationPlan([], [], [], [], [Startup("gone", true, kind: StartupKind.StartupFolder)]);
        var linkItem = Items.At(link, 1);
        var report = await OneClickOptimizer.RunAsync(plan, Cleaner(), AdminPolicy, new FakePrivilegedRunner(), _ =>
            new StartupRemoval(true, new CleanResult(RemovalMode.RecycleBin, [linkItem], [],
                new Dictionary<string, string> { [linkItem.Id] = @"C:\$Recycle.Bin\S-1\$Rgone.lnk" })));
        Assert.Equal([link], report.Recordable.Removed.Select(i => i.Path));
        Assert.True(report.Recordable.RecycleBinLocations.ContainsKey(linkItem.Id));
        Assert.Equal(1, report.StartupRemoved);
    }

    [Fact]
    public async Task StartupFailuresAreReportedAndDoNotStopTheRest()
    {
        var plan = new OptimizationPlan([Junk("a")], [], [], [], [Startup("bad", true), Startup("worse", true)]);
        var report = await OneClickOptimizer.RunAsync(plan, Cleaner(), AdminPolicy, new FakePrivilegedRunner(), item =>
            item.Name == "bad" ? throw new UnauthorizedAccessException("acesso negado") : new StartupRemoval(false, Error: "sumiu"));
        Assert.Equal(["bad: acesso negado", "worse: sumiu"], report.StartupFailures);
        Assert.Equal(0, report.StartupRemoved);
        Assert.Single(report.Clean.Removed);
    }

    /// <summary>Invariant 4: what the admin policy rejects never reaches the helper.</summary>
    [Fact]
    public async Task AdminItemsOutsideTheAllowListNeverReachTheHelper()
    {
        var runner = new FakePrivilegedRunner();
        var outside = Items.At(Path.Combine(t.Windows, @"System32\drivers"), 1, admin: true);
        var plan = new OptimizationPlan([], [outside], [], [], []);
        var report = await OneClickOptimizer.RunAsync(plan, Cleaner(), AdminPolicy, runner, _ => new StartupRemoval(false));
        Assert.Empty(runner.Batches);
        Assert.Single(report.Admin.Failed);
    }

    [Fact]
    public async Task HelperFailureFailsAdminItemsAndTasks()
    {
        var runner = new FakePrivilegedRunner();
        var plan = new OptimizationPlan([], [SystemJunk("x")], [], [Task("free-ram", true)], []);
        runner.FailingIds.Add("free-ram");
        var report = await OneClickOptimizer.RunAsync(plan, Cleaner(), AdminPolicy, runner, _ => new StartupRemoval(false));
        Assert.Single(report.Admin.Removed);
        Assert.Equal(["free-ram"], report.TasksFailed);

        runner.Error = "auxiliar não encontrado";
        report = await OneClickOptimizer.RunAsync(plan, Cleaner(), AdminPolicy, runner, _ => new StartupRemoval(false));
        Assert.Empty(report.Admin.Removed);
        Assert.Equal("auxiliar não encontrado", report.Admin.Failed.Single().Reason);
        Assert.Equal(["free-ram"], report.TasksFailed);
        Assert.False(report.AdminCancelled);
    }

    [Fact]
    public async Task NothingToDoAsksForNothing()
    {
        var runner = new FakePrivilegedRunner();
        var empty = new OptimizationPlan([], [], [], [], []);
        var report = await OneClickOptimizer.RunAsync(empty, Cleaner(), AdminPolicy, runner, _ => new StartupRemoval(false));
        Assert.Empty(runner.Batches);
        Assert.True(empty.IsEmpty);
        Assert.Equal(0, report.FreedBytes);
    }
}
