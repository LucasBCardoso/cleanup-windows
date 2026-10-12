using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Scanning;

/// <summary>A unit of scanning work (the Swift <c>ScanTask</c>).</summary>
public interface IScanTask
{
    string Id { get; }
    string Title { get; }
    ScanCategory Category { get; }
    ScanReport Scan(ScanEnvironment environment, SafetyPolicy policy, CancellationToken cancellation);
}

/// <summary>Runs scan tasks concurrently and returns outcomes in task order. A failing task never hides the others.</summary>
public sealed class ScanEngine(ScanEnvironment environment, SafetyPolicy policy)
{
    public ScanEnvironment Environment { get; } = environment;
    public SafetyPolicy Policy { get; } = policy;

    /// <summary>
    /// Everything Smart Scan runs: one task per path rule plus the code-based tasks. Rules that aren't plain globs
    /// (<see cref="DefaultRules.CodeBasedRuleIds"/>) are covered by their own tasks.
    /// </summary>
    public static IReadOnlyList<IScanTask> SmartScanTasks(IReadOnlyList<CleanupRule> rules, IReadOnlySet<string> runningProcesses,
        IEnumerable<IScanTask>? extra = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var tasks = new List<IScanTask>();
        foreach (var rule in rules)
        {
            if (DefaultRules.CodeBasedRuleIds.Contains(rule.Id)) continue;
            tasks.Add(new RuleScanTask(rule, runningProcesses, now));
        }
        if (rules.Any(r => r.Id == ChromiumCacheTask.RuleId)) tasks.Add(new ChromiumCacheTask(runningProcesses));
        tasks.AddRange(extra ?? []);
        return tasks;
    }

    public async Task<IReadOnlyList<ScanOutcome>> RunAsync(IReadOnlyList<IScanTask> tasks, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var completed = 0;
        var outcomes = new ScanOutcome[tasks.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, tasks.Count),
            new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Math.Max(2, System.Environment.ProcessorCount / 2) },
            (index, token) =>
            {
                var task = tasks[index];
                try
                {
                    outcomes[index] = new ScanOutcome.Success(task.Scan(Environment, Policy, token));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    outcomes[index] = new ScanOutcome.Failure(task.Id, task.Title, e.Message);
                }
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new ScanProgress(done, tasks.Count, task.Title));
                return ValueTask.CompletedTask;
            });
        return outcomes;
    }
}
