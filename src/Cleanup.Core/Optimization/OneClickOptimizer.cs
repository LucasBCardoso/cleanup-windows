using Cleanup.Core.Admin;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Maintenance;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Optimization;

/// <summary>
/// What one-click optimization will do. Only things that are safe without review: selected safe scan items, the user's
/// startup entries whose program is certainly gone, and quick maintenance tasks.
/// </summary>
public sealed record OptimizationPlan(IReadOnlyList<ScanItem> Trash, IReadOnlyList<ScanItem> Admin, IReadOnlyList<MaintenanceTask> UserTasks,
    IReadOnlyList<MaintenanceTask> AdminTasks, IReadOnlyList<StartupItem> BrokenStartup)
{
    public bool IsEmpty => Trash.Count == 0 && Admin.Count == 0 && UserTasks.Count == 0 && AdminTasks.Count == 0 && BrokenStartup.Count == 0;
    public bool NeedsAdmin => Admin.Count > 0 || AdminTasks.Count > 0;
}

/// <summary>What removing one startup entry did: a Startup-folder file goes to the Recycle Bin (recorded for undo), a Run value to a backup.</summary>
public sealed record StartupRemoval(bool Removed, CleanResult? Recycled = null, string? Error = null);

/// <summary>System items: removed by the elevated helper, or kept because the UAC prompt was cancelled.</summary>
public sealed record AdminCleanResult(bool Cancelled, IReadOnlyList<ScanItem> Removed, IReadOnlyList<CleanFailure> Failed, IReadOnlyList<ScanItem> Kept)
{
    public long FreedBytes => Removed.Sum(i => i.Size);
}

public sealed record OptimizationReport(CleanResult Clean, AdminCleanResult Admin, IReadOnlyList<string> TasksDone, IReadOnlyList<string> TasksFailed,
    IReadOnlyList<string> AdminTasksCancelled, CleanResult StartupClean, int StartupRemoved, IReadOnlyList<string> StartupFailures)
{
    public bool AdminCancelled => Admin.Cancelled || AdminTasksCancelled.Count > 0;
    public long FreedBytes => Clean.FreedBytes + Admin.FreedBytes;

    /// <summary>Everything that went to the Recycle Bin (junk and Startup-folder shortcuts), as one result for the history.</summary>
    public CleanResult Recordable
    {
        get
        {
            var locations = new Dictionary<string, string>(Clean.RecycleBinLocations);
            foreach (var (k, v) in StartupClean.RecycleBinLocations) locations.TryAdd(k, v);
            return new CleanResult(RemovalMode.RecycleBin, [.. Clean.Removed, .. StartupClean.Removed], [.. Clean.Failed, .. StartupClean.Failed], locations);
        }
    }
}

public static class OneClickOptimizer
{
    /// <summary>
    /// Quick and harmless: free memory, flush DNS, clear the thumbnail cache. Slow or disruptive tasks (search index,
    /// DISM, sfc, Storage Sense) stay manual in Manutenção.
    /// </summary>
    public static readonly IReadOnlyList<string> TaskIds = [MaintenanceTask.FreeRam, MaintenanceTask.FlushDns, MaintenanceTask.ClearThumbnails];

    /// <param name="isUnverifiableDrive">True for a path on a removable or network drive, or a drive letter that isn't there.</param>
    public static OptimizationPlan Plan(IEnumerable<ScanItem> items, ScanEnvironment environment, IEnumerable<StartupItem> startupItems,
        IEnumerable<MaintenanceTask> tasks, Func<string, bool> isUnverifiableDrive)
    {
        var safe = items.Where(i => i.Risk == RiskLevel.Safe).ToList();
        var quick = tasks.Where(t => TaskIds.Contains(t.Id)).ToList();
        return new OptimizationPlan(safe.Where(i => !i.RequiresAdmin).ToList(), safe.Where(i => i.RequiresAdmin).ToList(),
            quick.Where(t => !t.RequiresAdmin).ToList(), quick.Where(t => t.RequiresAdmin).ToList(),
            startupItems.Where(s => IsCertainlyBroken(s, environment, isUnverifiableDrive)).ToList());
    }

    /// <summary>Profile folders that may be redirected, synced or cloud-backed: a program "missing" there may just not be synced yet.</summary>
    internal static readonly string[] UnverifiableHomeFolders = ["Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "OneDrive"];

    /// <summary>
    /// Removed in one click only when its program is certainly gone — e.g. the app was uninstalled from Program Files.
    /// Not when the program might be on an unplugged or network drive or in a synced folder, and never for a shortcut
    /// that couldn't be read (those stay in the list, marked, for the user to decide).
    /// </summary>
    internal static bool IsCertainlyBroken(StartupItem item, ScanEnvironment environment, Func<string, bool> isUnverifiableDrive)
    {
        if (!item.IsManageable || item.BrokenReason is not { } reason || !reason.StartsWith(StartupCommand.MissingPrefix, StringComparison.Ordinal))
            return false;
        if (item.Program is not { } program || !Path.IsPathFullyQualified(program) || program.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        if (isUnverifiableDrive(program)) return false;
        var home = environment.Home;
        if (Directory.Exists(home))
        {
            // OneDrive, "OneDrive - Company"…
            foreach (var dir in SafeDirectories(home).Where(d => Path.GetFileName(d).StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)))
                if (SafetyPolicy.IsWithin(program, dir)) return false;
        }
        var folders = UnverifiableHomeFolders.Select(f => Path.Combine(home, f))
            .Concat(new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(environment.Variable).OfType<string>());
        return !folders.Any(f => SafetyPolicy.IsWithin(program, f));
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try { return Directory.GetDirectories(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Runs the plan: the Recycle Bin part, broken startup entries and user tasks first (a cancelled prompt can't cost
    /// them), then every admin removal and admin task in <b>one</b> batch — one UAC prompt.
    /// </summary>
    public static async Task<OptimizationReport> RunAsync(OptimizationPlan plan, Cleaner cleaner, AdminPathPolicy adminPolicy,
        IPrivilegedRunner runner, Func<StartupItem, StartupRemoval> removeStartup, CancellationToken cancellation = default)
    {
        var clean = cleaner.Clean(plan.Trash);

        var startupRemoved = 0;
        var recycled = new List<ScanItem>();
        var recycledFailed = new List<CleanFailure>();
        var recycledLocations = new Dictionary<string, string>();
        var startupFailures = new List<string>();
        foreach (var item in plan.BrokenStartup)
        {
            try
            {
                var removal = removeStartup(item);
                if (removal.Removed) startupRemoved++;
                if (removal.Error is { } error) startupFailures.Add($"{item.Name}: {error}");
                if (removal.Recycled is { } result)
                {
                    recycled.AddRange(result.Removed);
                    recycledFailed.AddRange(result.Failed);
                    foreach (var (k, v) in result.RecycleBinLocations) recycledLocations.TryAdd(k, v);
                }
            }
            catch (Exception e) { startupFailures.Add($"{item.Name}: {e.Message}"); }
        }
        var startupClean = new CleanResult(RemovalMode.RecycleBin, recycled, recycledFailed, recycledLocations);

        var done = new List<string>();
        var failed = new List<string>();
        var user = await MaintenanceBatch.RunAsync(plan.UserTasks, runner, cancellation);
        foreach (var task in plan.UserTasks)
        {
            if (user.Results.TryGetValue(task.Id, out var r) && r.Succeeded) done.Add(task.Id); else failed.Add(task.Id);
        }

        var cancelledTasks = new List<string>();
        var admin = await RunAdminAsync(plan, adminPolicy, runner, done, failed, cancelledTasks, cancellation);
        return new OptimizationReport(clean, admin, done, failed, cancelledTasks, startupClean, startupRemoved, startupFailures);
    }

    private static async Task<AdminCleanResult> RunAdminAsync(OptimizationPlan plan, AdminPathPolicy policy, IPrivilegedRunner runner,
        List<string> done, List<string> failed, List<string> cancelled, CancellationToken cancellation)
    {
        var accepted = new List<ScanItem>();
        var rejected = new List<CleanFailure>();
        foreach (var item in plan.Admin)
        {
            if (policy.Reject(item.Path) is { } reason) rejected.Add(new CleanFailure(item, "recusado: " + reason));
            else accepted.Add(item);
        }
        var steps = accepted.Select(i => new Elevation.Step(Elevation.Ops.Remove, i.Path)).Concat(plan.AdminTasks.Select(t => t.Step)).ToList();
        if (steps.Count == 0) return new AdminCleanResult(false, [], rejected, []);

        PrivilegedRun run;
        try { run = await runner.RunAsync(steps, asAdmin: true, cancellation); }
        catch (Exception e) when (e is not OperationCanceledException) { run = PrivilegedRun.Failed(e.Message); }

        if (run.Cancelled)
        {
            cancelled.AddRange(plan.AdminTasks.Select(t => t.Id));
            return new AdminCleanResult(true, [], rejected, accepted);
        }
        if (run.Error is { } error)
        {
            failed.AddRange(plan.AdminTasks.Select(t => t.Id));
            return new AdminCleanResult(false, [], [.. rejected, .. accepted.Select(i => new CleanFailure(i, error))], []);
        }
        var removed = new List<ScanItem>();
        var failures = new List<CleanFailure>(rejected);
        for (var i = 0; i < accepted.Count; i++)
        {
            var result = i < run.Results.Count ? run.Results[i] : null;
            if (result is { Ok: true }) removed.Add(accepted[i]);
            else failures.Add(new CleanFailure(accepted[i], result?.Message ?? "não executado"));
        }
        for (var offset = 0; offset < plan.AdminTasks.Count; offset++)
        {
            var index = accepted.Count + offset;
            if (index < run.Results.Count && run.Results[index].Ok) done.Add(plan.AdminTasks[offset].Id);
            else failed.Add(plan.AdminTasks[offset].Id);
        }
        return new AdminCleanResult(false, removed, failures, []);
    }
}
