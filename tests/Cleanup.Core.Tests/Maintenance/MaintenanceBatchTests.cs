using Cleanup.Core.Admin;
using Cleanup.Core.Maintenance;

namespace Cleanup.Core.Tests.Maintenance;

/// <summary>Records each batch; every step succeeds unless its id is in <see cref="FailingIds"/>.</summary>
public sealed class FakePrivilegedRunner : IPrivilegedRunner
{
    public List<(IReadOnlyList<Elevation.Step> Steps, bool AsAdmin)> Batches { get; } = [];
    public HashSet<string> FailingIds { get; } = [];
    public string? Error { get; set; }

    public Task<PrivilegedRun> RunAsync(IReadOnlyList<Elevation.Step> steps, bool asAdmin, CancellationToken cancellation = default)
    {
        Batches.Add((steps, asAdmin));
        if (Error is { } error) return Task.FromResult(PrivilegedRun.Failed(error));
        return Task.FromResult(new PrivilegedRun(false, steps.Select(s =>
            FailingIds.Contains(StepId(s)) ? new Elevation.StepResult(false, "código 2") : new Elevation.StepResult(true, "ok")).ToList()));
    }

    public static string StepId(Elevation.Step step) => step.Id ?? step.Path ?? step.Op;
}

/// <summary>Runs user batches; the UAC prompt is always cancelled.</summary>
public sealed class CancellingAdminRunner : IPrivilegedRunner
{
    public List<bool> Calls { get; } = [];

    public Task<PrivilegedRun> RunAsync(IReadOnlyList<Elevation.Step> steps, bool asAdmin, CancellationToken cancellation = default)
    {
        Calls.Add(asAdmin);
        return Task.FromResult(asAdmin ? PrivilegedRun.WasCancelled
            : new PrivilegedRun(false, steps.Select(_ => new Elevation.StepResult(true, "")).ToList()));
    }
}

/// <summary>Answers fewer results than steps.</summary>
public sealed class ShortRunner : IPrivilegedRunner
{
    public Task<PrivilegedRun> RunAsync(IReadOnlyList<Elevation.Step> steps, bool asAdmin, CancellationToken cancellation = default) =>
        Task.FromResult(new PrivilegedRun(false, [new Elevation.StepResult(true, "")]));
}

public class MaintenanceBatchTests
{
    private static MaintenanceTask Task(string id, bool admin) => new(id, id, "", admin);

    [Fact]
    public async Task UserTasksNeverRunAsAdminAndAdminTasksShareOnePrompt()
    {
        var runner = new FakePrivilegedRunner();
        var outcome = await MaintenanceBatch.RunAsync([Task("a", false), Task("b", true), Task("c", true), Task("d", false)], runner);
        Assert.Equal(2, runner.Batches.Count);
        Assert.False(runner.Batches[0].AsAdmin);
        Assert.Equal(["a", "d"], runner.Batches[0].Steps.Select(FakePrivilegedRunner.StepId));
        Assert.True(runner.Batches[1].AsAdmin);
        Assert.Equal(["b", "c"], runner.Batches[1].Steps.Select(FakePrivilegedRunner.StepId));
        Assert.Equal(["a", "b", "c", "d"], outcome.Results.Keys.Order());
        Assert.All(outcome.Results.Values, r => Assert.True(r.Succeeded));
        Assert.Empty(outcome.CancelledIds);
    }

    [Fact]
    public async Task NoAdminTaskMeansNoPrompt()
    {
        var runner = new FakePrivilegedRunner();
        await MaintenanceBatch.RunAsync([Task("a", false)], runner);
        Assert.Equal([false], runner.Batches.Select(b => b.AsAdmin));
    }

    [Fact]
    public async Task FailuresAreReportedPerTask()
    {
        var runner = new FakePrivilegedRunner();
        runner.FailingIds.Add("b");
        var outcome = await MaintenanceBatch.RunAsync([Task("a", true), Task("b", true)], runner);
        Assert.True(outcome.Results["a"].Succeeded);
        Assert.False(outcome.Results["b"].Succeeded);
        Assert.Equal("código 2", outcome.Results["b"].Output);
    }

    /// <summary>Invariant 5: a cancelled prompt runs nothing elevated and reports those tasks as kept, not failed.</summary>
    [Fact]
    public async Task CancelledPromptOnlyCancelsTheAdminTasks()
    {
        var outcome = await MaintenanceBatch.RunAsync([Task("a", false), Task("b", true)], new CancellingAdminRunner());
        Assert.True(outcome.Results["a"].Succeeded);
        Assert.False(outcome.Results.ContainsKey("b"));
        Assert.Equal(["b"], outcome.CancelledIds);
    }

    [Fact]
    public async Task MissingResultsAreReportedAsNotRun()
    {
        var outcome = await MaintenanceBatch.RunAsync([Task("a", false), Task("b", false)], new ShortRunner());
        Assert.True(outcome.Results["a"].Succeeded);
        Assert.False(outcome.Results["b"].Succeeded);
        Assert.Equal("não executada", outcome.Results["b"].Output);
    }

    /// <summary>The helper missing, or the 15-minute timeout: every task of that batch fails with the reason.</summary>
    [Fact]
    public async Task RunnerErrorFailsItsTasks()
    {
        var runner = new FakePrivilegedRunner { Error = "o Cleanup parou de esperar depois de 15 minutos" };
        var outcome = await MaintenanceBatch.RunAsync([Task("b", true)], runner);
        Assert.False(outcome.Results["b"].Succeeded);
        Assert.Equal("o Cleanup parou de esperar depois de 15 minutos", outcome.Results["b"].Output);
    }

    [Fact]
    public void AdminTasksAreAllowListedByTheHelperAndUserTasksAreNot()
    {
        foreach (var task in MaintenanceTask.All)
        {
            var allowed = task.Step.Op == Elevation.Ops.FlushDns || Elevation.AllowedMaintenance.Contains(task.Step.Id ?? "");
            Assert.Equal(task.RequiresAdmin, allowed);
        }
        Assert.Equal(Elevation.Ops.FlushDns, MaintenanceTask.All.Single(t => t.Id == MaintenanceTask.FlushDns).Step.Op);
    }

    [Fact]
    public void SelectAllLeavesOutSlowTasks()
    {
        Assert.Equal(["dism-cleanup", "reset-search", "sfc", "storage-sense"], MaintenanceTask.DeliberateOnly.Order());
        Assert.DoesNotContain(MaintenanceTask.FreeRam, MaintenanceTask.DeliberateOnly);
    }
}
