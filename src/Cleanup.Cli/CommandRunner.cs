using Cleanup.Core.Cleaning;
using Cleanup.Core.History;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;
using Cleanup.Core.Scanning;
using Cleanup.Core.SpaceLens;
using Cleanup.Platform.Windows;

namespace Cleanup.Cli;

/// <summary>Executes a parsed command against the real machine. Nothing is changed without <c>--yes</c>.</summary>
public sealed class CommandRunner(TextWriter output)
{
    private readonly ScanEnvironment environment = ScanEnvironment.Current;

    private SafetyPolicy Policy() => new(environment, Cleanup.Core.Safety.IgnoreList.Load().Paths);

    private void Write(string line = "") => output.WriteLine(line);

    private static string Size(long bytes) => ByteFormat.Format(bytes).PadLeft(10);

    public async Task<int> RunAsync(Command command) => command switch
    {
        Command.Help => Help(),
        Command.Rules => Rules(),
        Command.Scan s => await ScanAsync(s),
        Command.Clean c => await CleanAsync(c),
        Command.Large l => Large(l),
        Command.Lens l => await LensAsync(l),
        Command.Disk => Disk(),
        Command.IgnoreList => IgnoreList(),
        Command.IgnoreAdd a => IgnoreChange(list => list.Adding(a.Path), $"Ignoring {a.Path}"),
        Command.IgnoreRemove r => IgnoreChange(list => list.Removing(r.Path), $"No longer ignoring {r.Path}"),
        Command.History => History(),
        Command.Undo u => Undo(u),
        _ => throw new UsageException("unsupported command"),
    };

    private int Help()
    {
        Write(CommandLine.Usage);
        return 0;
    }

    private int Rules()
    {
        foreach (var rule in DefaultRules.Load())
        {
            var admin = rule.RequiresAdmin ? " [admin]" : "";
            Write($"{rule.Id,-24} [{rule.Risk.ToString().ToLowerInvariant()}] {rule.Name}{admin}");
            Write("    " + string.Join(", ", rule.Paths));
        }
        return 0;
    }

    private async Task<IReadOnlyList<ScanOutcome>> SmartScanAsync()
    {
        var running = RunningProcesses.Names();
        var tasks = ScanEngine.SmartScanTasks(DefaultRules.Load(), running, [new RecycleBinTask()]);
        return await new ScanEngine(environment, Policy()).RunAsync(tasks);
    }

    private async Task<int> ScanAsync(Command.Scan command)
    {
        long total = 0, review = 0;
        foreach (var outcome in await SmartScanAsync())
        {
            switch (outcome)
            {
                case ScanOutcome.Failure f:
                    Write($"✗ {f.Title}: {f.Message}");
                    continue;
                case ScanOutcome.Success { Value: var report }:
                    var items = command.IncludeReview ? report.Items : report.Items.Where(i => i.Risk == RiskLevel.Safe).ToList();
                    review += report.Items.Where(i => i.Risk == RiskLevel.Review).Sum(i => i.Size);
                    if (items.Count == 0 && report.Skipped.Count == 0) continue;
                    var size = items.Sum(i => i.Size);
                    total += size;
                    var admin = report.Items.Any(i => i.RequiresAdmin) ? " [admin]" : "";
                    Write($"{Size(size)}  {report.Title} ({items.Count} items){admin}");
                    if (report.Skipped.Count > 0)
                        Write($"            ⚠ {report.Skipped.Count} location(s) unreadable{(admin.Length > 0 ? " — needs administrator" : "")}");
                    break;
            }
        }
        var note = !command.IncludeReview && review > 0 ? $" (+{ByteFormat.Format(review)} to review: --include-review)" : "";
        Write($"Total reclaimable: {ByteFormat.Format(total)}{note}");
        return 0;
    }

    private async Task<int> CleanAsync(Command.Clean command)
    {
        var outcomes = await SmartScanAsync();
        var items = outcomes.Select(o => o.Report).OfType<ScanReport>().SelectMany(r => r.Items)
            .Where(i => command.RuleIds.Count == 0 || command.RuleIds.Contains(i.RuleId, StringComparer.OrdinalIgnoreCase))
            .Where(i => command.IncludeReview || i.Risk == RiskLevel.Safe)
            .ToList();
        var unknown = command.RuleIds.Where(id => DefaultRules.Load().All(r => !r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0) throw new UsageException("unknown rule: " + string.Join(", ", unknown));

        var adminItems = items.Where(i => i.RequiresAdmin).ToList();
        var userItems = items.Where(i => !i.RequiresAdmin).ToList();
        if (adminItems.Count > 0 && !command.Admin)
            Write($"Skipped {adminItems.Count} system item(s) [admin], {ByteFormat.Format(adminItems.Sum(i => i.Size))}. Add --admin to include them.");

        var mode = !command.Confirm ? RemovalMode.DryRun : command.Permanent ? RemovalMode.Permanent : RemovalMode.RecycleBin;
        var policy = Policy();
        var result = new Cleaner(policy, new WindowsFileRemover(), mode).Clean(userItems);
        var verb = mode switch { RemovalMode.DryRun => "Would remove", RemovalMode.Permanent => "Deleted", _ => "Moved to the Recycle Bin" };
        foreach (var item in result.Removed) Write($"  {verb}: {item.DisplayName ?? item.Path} ({ByteFormat.Format(item.Size)})");
        foreach (var failure in result.Failed) Write($"  ✗ {failure.Item.Path}: {failure.Reason}");

        IReadOnlyList<ScanItem> adminRemoved = [];
        if (command.Admin && adminItems.Count > 0)
        {
            var admin = await AdminCleanAsync(adminItems, command.Confirm);
            adminRemoved = admin;
        }

        Write($"{verb} {result.Removed.Count} item(s), {ByteFormat.Format(result.FreedBytes)}.");
        if (mode == RemovalMode.DryRun)
        {
            Write("Nothing was changed. Re-run with --yes to apply.");
            return 0;
        }
        if (CleanupRecord.From(result, adminRemoved, "cleanup clean", RecycleBin.FileId) is { } record)
            CleanupHistory.Update(h => h.Adding(record));
        return result.Failed.Count == 0 ? 0 : 2;
    }

    /// <summary>System junk goes through the elevated helper: one UAC prompt, deleted permanently.</summary>
    private async Task<IReadOnlyList<ScanItem>> AdminCleanAsync(IReadOnlyList<ScanItem> items, bool confirm)
    {
        var plan = AdminCleaner.Plan(items, environment);
        Write(confirm ? "As administrator (one UAC prompt, deleted permanently):" : "As administrator (one UAC prompt, deleted permanently), would remove:");
        foreach (var item in plan.Accepted) Write($"  {item.Path} ({ByteFormat.Format(item.Size)})");
        foreach (var (item, reason) in plan.Rejected) Write($"  ✗ {item.Path}: {reason}");
        if (!confirm || plan.Accepted.Count == 0) return [];
        var result = await AdminCleaner.RunAsync(plan.Accepted);
        if (result.Cancelled)
        {
            Write($"Administrator permission not given: {plan.Accepted.Count} system item(s) kept.");
            return [];
        }
        foreach (var failure in result.Failed) Write($"  ✗ {failure.Path}: {failure.Reason}");
        Write($"Deleted {result.Removed.Count} system item(s) as administrator, {ByteFormat.Format(result.Removed.Sum(i => i.Size))}.");
        return result.Removed;
    }

    private int Large(Command.Large command)
    {
        var task = new LargeOldFilesTask(command.MinimumSize, command.OlderThanDays);
        var report = task.Scan(environment, Policy(), CancellationToken.None);
        foreach (var item in report.Items)
        {
            var when = item.LastAccessedUtc is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd") : "unknown";
            Write($"{Size(item.Size)}  {item.Path}  (last used {when})");
        }
        Write($"{report.Items.Count} file(s), {ByteFormat.Format(report.TotalSize)} total");
        return 0;
    }

    private async Task<int> LensAsync(Command.Lens command)
    {
        var path = PathUtil.Normalize(command.Path ?? environment.Home);
        var entries = await DirectorySizer.ChildrenAsync(path);
        var max = Math.Max(1, entries.FirstOrDefault()?.Size ?? 1);
        foreach (var entry in entries.Take(40))
        {
            var bar = new string('█', (int)Math.Round(30.0 * entry.Size / max));
            var name = entry.Name + (entry.IsDirectory ? "\\" : "") + (entry.IsLink ? " → (link, not followed)" : "");
            Write($"{Size(entry.Size)}  {bar,-30} {name}");
        }
        Write($"{ByteFormat.Format(entries.Sum(e => e.Size))} in {path}");
        return 0;
    }

    private int Disk()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            if (DiskSpace.Of(drive.Name) is not { } disk) continue;
            Write($"{drive.Name,-4} {drive.VolumeLabel,-16} used {ByteFormat.Format(disk.Used),10}   free {ByteFormat.Format(disk.Free),10}   of {ByteFormat.Format(disk.Total)}");
        }
        var (bin, count) = RecycleBin.Query();
        Write($"Recycle Bin: {ByteFormat.Format(bin)} in {count} item(s)");
        return 0;
    }

    private int IgnoreList()
    {
        var list = Cleanup.Core.Safety.IgnoreList.Load();
        if (list.Paths.Count == 0) Write("Ignore list is empty.");
        foreach (var path in list.Paths.Order(StringComparer.OrdinalIgnoreCase)) Write(path);
        return 0;
    }

    private int IgnoreChange(Func<Cleanup.Core.Safety.IgnoreList, Cleanup.Core.Safety.IgnoreList> change, string message)
    {
        change(Cleanup.Core.Safety.IgnoreList.Load()).Save();
        Write(message);
        return 0;
    }

    private int History()
    {
        var history = CleanupHistory.Load();
        if (history.Records.Count == 0) { Write("No cleanups recorded yet."); return 0; }
        var context = RecycleBin.RestoreContext(Policy());
        foreach (var record in history.Records)
        {
            var a = Restorer.AvailabilityOf(record, context);
            var parts = new List<string>();
            if (a.RestorableCount > 0) parts.Add($"{a.RestorableCount} can come back ({ByteFormat.Format(a.RestorableBytes)})");
            if (a.GoneCount > 0) parts.Add($"{a.GoneCount} no longer in the Recycle Bin");
            if (a.BlockedCount > 0) parts.Add($"{a.BlockedCount} blocked");
            if (a.PermanentCount > 0) parts.Add($"{a.PermanentCount} deleted permanently");
            Write($"{record.Id.ToString()[..8]}  {record.Date.LocalDateTime:yyyy-MM-dd HH:mm}  {record.Source,-14} {ByteFormat.Format(record.TotalBytes),10}  {string.Join(", ", parts)}");
        }
        return 0;
    }

    private int Undo(Command.Undo command)
    {
        var history = CleanupHistory.Load();
        var context = RecycleBin.RestoreContext(Policy());
        var record = command.Id is { } id
            ? history.Records.FirstOrDefault(r => r.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase))
            : history.Records.FirstOrDefault(r => Restorer.AvailabilityOf(r, context).RestorableCount > 0);
        if (record is null) { Write(command.Id is null ? "Nothing to undo." : $"No cleanup with id {command.Id}."); return 1; }

        var availability = Restorer.AvailabilityOf(record, context);
        Write($"{record.Date.LocalDateTime:yyyy-MM-dd HH:mm} {record.Source}: {availability.RestorableCount} item(s) can come back ({ByteFormat.Format(availability.RestorableBytes)}).");
        if (!command.Confirm)
        {
            foreach (var entry in record.Entries.Where(e => Restorer.IsRestorable(e, context))) Write($"  Would put back: {entry.OriginalPath}");
            Write("Nothing was changed. Re-run with --yes to apply.");
            return 0;
        }
        var result = Restorer.Restore(record, context);
        foreach (var entry in result.Restored) Write($"  Put back: {entry.OriginalPath}");
        foreach (var failure in result.Failed) Write($"  ✗ {failure.Entry.OriginalPath}: {failure.Reason}");
        CleanupHistory.Update(h => h.ApplyingRestore(record.Id, result.Restored));
        Write($"Put back {result.Restored.Count} item(s), {ByteFormat.Format(result.RestoredBytes)}.");
        return result.Failed.Count == 0 ? 0 : 2;
    }
}
