using System.Diagnostics;
using Cleanup.Core.Admin;
using Cleanup.Core.Maintenance;
using Cleanup.Core.Optimization;
using Cleanup.Platform.Windows;

namespace Cleanup.Platform.Tests;

public sealed class ThumbnailCacheCleanerTests : IDisposable
{
    private readonly string localAppData = Directory.CreateTempSubdirectory("cleanup-thumbs-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(localAppData, recursive: true); } catch (IOException) { }
    }

    /// <summary>Never touches the real Explorer: records the calls instead.</summary>
    private sealed class FakeShell : IExplorerShell
    {
        public List<string> Calls { get; } = [];
        public void Stop() => Calls.Add("stop");
        public void Start() => Calls.Add("start");
    }

    private string Cache(string name)
    {
        var path = Path.Combine(localAppData, @"Microsoft\Windows\Explorer", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void DeletesOnlyTheCachesBetweenStoppingAndStartingExplorer()
    {
        var thumb = Cache("thumbcache_256.db");
        var icon = Cache("iconcache_32.db");
        var other = Cache("ExplorerStartupLog.etl");
        var legacy = Path.Combine(localAppData, "IconCache.db");
        File.WriteAllText(legacy, "x");
        var shell = new FakeShell();
        var result = new ThumbnailCacheCleaner(localAppData, shell).Run();
        Assert.True(result.Ok);
        Assert.Equal(["stop", "start"], shell.Calls);
        Assert.False(File.Exists(thumb));
        Assert.False(File.Exists(icon));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(other));
        Assert.StartsWith("3 arquivo(s)", result.Message);
    }

    [Fact]
    public void AFileInUseIsKeptAndExplorerStillComesBack()
    {
        var thumb = Cache("thumbcache_96.db");
        var shell = new FakeShell();
        Elevation.StepResult result;
        using (new FileStream(thumb, FileMode.Open, FileAccess.Read, FileShare.None))
            result = new ThumbnailCacheCleaner(localAppData, shell).Run();
        Assert.False(result.Ok);
        Assert.Contains("1 em uso", result.Message);
        Assert.Equal(["stop", "start"], shell.Calls);
        Assert.True(File.Exists(thumb));
    }
}

public class MaintenanceRunnerTests
{
    [Fact]
    public async Task UserStepsRunInTheAppOneResultEach()
    {
        var seen = new List<string?>();
        var runner = new MaintenanceRunner(step => { seen.Add(step.Id); return new Elevation.StepResult(true, "ok"); });
        var steps = new[] { new Elevation.Step(Elevation.Ops.Run, Id: "a"), new Elevation.Step(Elevation.Ops.Run, Id: "b") };
        var run = await runner.RunAsync(steps, asAdmin: false);
        Assert.False(run.Cancelled);
        Assert.Equal(2, run.Results.Count);
        Assert.Equal(["a", "b"], seen);
    }

    /// <summary>The user runner is an allow-list too: admin operations and unknown ids are refused, never run.</summary>
    [Theory]
    [InlineData(Elevation.Ops.FlushDns, null)]
    [InlineData(Elevation.Ops.Remove, null)]
    [InlineData(Elevation.Ops.Run, "sfc")]
    [InlineData(Elevation.Ops.Run, "free-ram")]
    [InlineData(Elevation.Ops.Run, "cmd /c whoami")]
    public void UserTasksRefuseAnythingElse(string op, string? id) =>
        Assert.False(UserTasks.Run(new Elevation.Step(op, Id: id)).Ok);
}

/// <summary>Uses only a process this test starts (and ends).</summary>
public sealed class ProcessTests : IDisposable
{
    private readonly Process child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), ["-n", "60", "127.0.0.1"])
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
    })!;

    public void Dispose()
    {
        try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
        child.Dispose();
    }

    [Fact]
    public void SnapshotsListTheUsersProcessesButNotCleanupItself()
    {
        var snapshot = new ProcessSnapshotter().OwnProcesses();
        var mine = Assert.Single(snapshot, p => p.Pid == child.Id);
        Assert.Equal("PING.EXE", mine.Name, ignoreCase: true);
        Assert.True(mine.PrivateBytes > 0);
        Assert.DoesNotContain(snapshot, p => p.Pid == Environment.ProcessId);
        Assert.DoesNotContain(snapshot, p => p.Pid <= 4);
        var owner = new WindowsProcessControl().Owner(child.Id);
        Assert.NotNull(owner);
        Assert.Equal(ProcessSnapshotter.CurrentSid, owner!.UserSid);
        Assert.Equal(mine.CreateTime, owner.CreateTime);
        Assert.False(owner.IsCritical);
    }

    [Fact]
    public void ForceQuitEndsTheUsersProcessAndQuitNeedsAWindow()
    {
        var snapshot = new ProcessSnapshotter().OwnProcesses().Single(p => p.Pid == child.Id);
        var sample = new ProcessSample(snapshot.Pid, snapshot.Name, snapshot.CreateTime, 0, snapshot.PrivateBytes);
        var terminator = WindowsProcessControl.Terminator();
        Assert.Throws<TerminateException>(() => terminator.Terminate(sample, force: false)); // no window to close
        terminator.Terminate(sample, force: true);
        Assert.True(child.WaitForExit(5000));
        Assert.Throws<TerminateException>(() => terminator.Terminate(sample, force: true)); // already gone
    }

    [Fact]
    public void ProcessesOfOtherUsersAreNotReadable()
    {
        // PID 4 (System) and services belong to SYSTEM: never listed, and the terminator refuses them.
        Assert.Null(new WindowsProcessControl().Owner(4));
        Assert.Throws<TerminateException>(() => WindowsProcessControl.Terminator().Terminate(new ProcessSample(4, "System", 0, 0, 0), force: true));
    }
}
