using Cleanup.Core.Optimization;

namespace Cleanup.Core.Tests.Optimization;

public class ProcessSamplerTests
{
    private static ProcessSnapshot Snap(int pid, double cpuMs, long bytes = 0, long created = 1, string name = "app") =>
        new(pid, name, created, TimeSpan.FromMilliseconds(cpuMs), bytes);

    [Fact]
    public void CpuIsTheShareOfTheWholeMachineSinceTheLastReading()
    {
        var before = new[] { Snap(10, 1000), Snap(11, 0) };
        var after = new[] { Snap(10, 3000), Snap(11, 500), Snap(12, 9000) };
        var samples = ProcessSampler.Compute(before, after, TimeSpan.FromSeconds(1), processorCount: 4);
        Assert.Equal(50, samples.Single(s => s.Pid == 10).Cpu, 3);    // 2 s of CPU in 1 s on 4 cores
        Assert.Equal(12.5, samples.Single(s => s.Pid == 11).Cpu, 3);
        Assert.Equal(0, samples.Single(s => s.Pid == 12).Cpu);        // new: no previous reading
    }

    [Fact]
    public void AReusedPidStartsFromZeroAndCpuIsClamped()
    {
        var samples = ProcessSampler.Compute([Snap(10, 1000, created: 1), Snap(11, 0)],
            [Snap(10, 5000, created: 2), Snap(11, 99_000)], TimeSpan.FromSeconds(1), 2);
        Assert.Equal(0, samples.Single(s => s.Pid == 10).Cpu);
        Assert.Equal(100, samples.Single(s => s.Pid == 11).Cpu);
    }

    [Fact]
    public void TopSortsByMetricThenPid()
    {
        var samples = new[]
        {
            new ProcessSample(3, "c", 1, 5, 100), new ProcessSample(1, "a", 1, 5, 300), new ProcessSample(2, "b", 1, 9, 200),
        };
        Assert.Equal([2, 1], ProcessSampler.Top(samples, ProcessMetric.Cpu, 2).Select(s => s.Pid));
        Assert.Equal([1, 2, 3], ProcessSampler.Top(samples, ProcessMetric.Memory, 10).Select(s => s.Pid));
        Assert.Equal("12,5%", ProcessSampler.FormatCpu(12.5));
    }
}

public class ProcessTerminatorTests
{
    private const string Me = "S-1-5-21-1-1001";

    private sealed class FakeControl : IProcessControl
    {
        public Dictionary<int, ProcessOwner> Owners { get; } = [];
        public List<(int Pid, bool Force)> Calls { get; } = [];
        public bool HasWindow { get; set; } = true;

        public ProcessOwner? Owner(int pid) => Owners.GetValueOrDefault(pid);
        public bool CloseMainWindow(int pid) { Calls.Add((pid, false)); return HasWindow; }
        public void Kill(int pid) => Calls.Add((pid, true));
    }

    private static ProcessSample Sample(int pid, string name = "notepad.exe", long created = 7) => new(pid, name, created, 0, 0);

    private static (ProcessTerminator, FakeControl) Make()
    {
        var control = new FakeControl();
        control.Owners[100] = new ProcessOwner(Me, 1, 7, false);
        control.Owners[200] = new ProcessOwner("S-1-5-18", 0, 7, false);
        control.Owners[300] = new ProcessOwner(Me, 2, 7, false);
        control.Owners[400] = new ProcessOwner(Me, 1, 7, true);
        return (new ProcessTerminator(Me, 1, ownPid: 999, control), control);
    }

    [Fact]
    public void QuitsAndForceQuitsTheUsersOwnProcess()
    {
        var (terminator, control) = Make();
        terminator.Terminate(Sample(100), force: false);
        terminator.Terminate(Sample(100), force: true);
        Assert.Equal([(100, false), (100, true)], control.Calls);
    }

    [Theory]
    [InlineData(4, "System", "Processo do sistema")]
    [InlineData(999, "Cleanup.exe", "a si mesmo")]
    [InlineData(555, "gone.exe", "não está mais em execução")]
    [InlineData(200, "service.exe", "outro usuário")]
    [InlineData(300, "other-session.exe", "outro usuário")]
    [InlineData(400, "critical.exe", "Processo do sistema")]
    [InlineData(100, "explorer.exe", "Processo do sistema")]
    public void RefusesEverythingElse(int pid, string name, string message)
    {
        var (terminator, control) = Make();
        var e = Assert.Throws<TerminateException>(() => terminator.Terminate(Sample(pid, name), force: true));
        Assert.Contains(message, e.Message);
        Assert.Empty(control.Calls);
    }

    /// <summary>The PID was reused by another process since the list was drawn: nothing is sent.</summary>
    [Fact]
    public void AReusedPidIsNotTouched()
    {
        var (terminator, control) = Make();
        Assert.Throws<TerminateException>(() => terminator.Terminate(Sample(100, created: 8), force: true));
        Assert.Empty(control.Calls);
    }

    [Fact]
    public void QuitWithoutAWindowSuggestsForceQuit()
    {
        var (terminator, control) = Make();
        control.HasWindow = false;
        var e = Assert.Throws<TerminateException>(() => terminator.Terminate(Sample(100), force: false));
        Assert.Contains("Forçar encerramento", e.Message);
    }
}
