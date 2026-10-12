namespace Cleanup.Core.Optimization;

/// <summary>One reading of a process: its total CPU time so far and its private working set.</summary>
public sealed record ProcessSnapshot(int Pid, string Name, long CreateTime, TimeSpan CpuTime, long PrivateBytes);

/// <summary>
/// A process with its CPU use since the previous reading, as a share of the whole machine (0–100, like Task Manager),
/// and its private working set. <see cref="CreateTime"/> tells a process from a later one reusing its PID.
/// </summary>
public sealed record ProcessSample(int Pid, string Name, long CreateTime, double Cpu, long MemoryBytes);

public enum ProcessMetric { Cpu, Memory }

public static class ProcessSampler
{
    /// <summary>
    /// CPU % from two readings <paramref name="elapsed"/> apart: CPU-time delta / (elapsed × cores). A process missing
    /// from the previous reading (new, or a reused PID) shows 0 until the next one.
    /// </summary>
    public static IReadOnlyList<ProcessSample> Compute(IReadOnlyList<ProcessSnapshot> previous, IReadOnlyList<ProcessSnapshot> current,
        TimeSpan elapsed, int processorCount)
    {
        var before = new Dictionary<(int, long), ProcessSnapshot>();
        foreach (var p in previous) before[(p.Pid, p.CreateTime)] = p;
        var capacity = elapsed.TotalMilliseconds * Math.Max(1, processorCount);
        return current.Select(p =>
        {
            var cpu = 0.0;
            if (capacity > 0 && before.TryGetValue((p.Pid, p.CreateTime), out var old))
                cpu = Math.Clamp((p.CpuTime - old.CpuTime).TotalMilliseconds / capacity * 100, 0, 100);
            return new ProcessSample(p.Pid, p.Name, p.CreateTime, cpu, p.PrivateBytes);
        }).ToList();
    }

    public static IReadOnlyList<ProcessSample> Top(IEnumerable<ProcessSample> samples, ProcessMetric metric, int limit) =>
        (metric == ProcessMetric.Cpu
            ? samples.OrderByDescending(s => s.Cpu).ThenBy(s => s.Pid)
            : samples.OrderByDescending(s => s.MemoryBytes).ThenBy(s => s.Pid)).Take(limit).ToList();

    /// <summary>"12,3%" (pt-BR).</summary>
    public static string FormatCpu(double cpu) => cpu.ToString("0.0", new System.Globalization.CultureInfo("pt-BR")) + "%";
}

/// <summary>Who a running process belongs to, read right before acting on it.</summary>
public sealed record ProcessOwner(string UserSid, int SessionId, long CreateTime, bool IsCritical);

/// <summary>The operating system's side of quitting a process (faked in tests).</summary>
public interface IProcessControl
{
    /// <summary>Null when no such process runs (or it can't be inspected, e.g. another user's).</summary>
    ProcessOwner? Owner(int pid);

    /// <summary>Asks the process to close its main window, like clicking its X. False when it has no window.</summary>
    bool CloseMainWindow(int pid);

    /// <summary>Ends the process at once (<c>TerminateProcess</c>).</summary>
    void Kill(int pid);
}

public sealed class TerminateException(string message) : Exception(message);

/// <summary>
/// Quits or force-quits only the user's own processes (same user and session), never Cleanup itself, critical
/// processes or the parts of the Windows shell. Ownership and identity are re-checked right before acting.
/// </summary>
public sealed class ProcessTerminator(string userSid, int sessionId, int ownPid, IProcessControl control)
{
    /// <summary>Processes of the user's session that keep Windows itself running; quitting them logs out or breaks the desktop.</summary>
    public static readonly IReadOnlySet<string> ProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "sihost", "ctfmon", "taskhostw", "dwm", "csrss", "winlogon", "wininit", "lsass", "services", "smss",
        "svchost", "fontdrvhost", "userinit", "LogonUI", "Cleanup.Elevated",
    };

    public static bool IsProtected(string name) => ProtectedNames.Contains(Path.GetFileNameWithoutExtension(name));

    public void Terminate(ProcessSample sample, bool force)
    {
        if (sample.Pid <= 4 || IsProtected(sample.Name)) throw new TerminateException("Processo do sistema: não pode ser encerrado.");
        if (sample.Pid == ownPid) throw new TerminateException("O Cleanup não encerra a si mesmo.");
        var owner = control.Owner(sample.Pid);
        if (owner is null || owner.CreateTime != sample.CreateTime) throw new TerminateException("O processo não está mais em execução.");
        if (!string.Equals(owner.UserSid, userSid, StringComparison.OrdinalIgnoreCase) || owner.SessionId != sessionId)
            throw new TerminateException("O processo pertence a outro usuário.");
        if (owner.IsCritical) throw new TerminateException("Processo do sistema: não pode ser encerrado.");
        if (force) control.Kill(sample.Pid);
        else if (!control.CloseMainWindow(sample.Pid))
            throw new TerminateException("Esse processo não tem janela para fechar. Use “Forçar encerramento”.");
    }
}
