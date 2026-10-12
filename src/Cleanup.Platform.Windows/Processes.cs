using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Cleanup.Core.Optimization;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Reads every process in one <c>NtQuerySystemInformation(SystemProcessInformation)</c> call — CPU time, private
/// working set, session — and keeps only the current user's own (same user SID and session), never Cleanup itself.
/// </summary>
public sealed class ProcessSnapshotter
{
    public static string CurrentSid { get; } = WindowsIdentity.GetCurrent().User?.Value ?? "";
    public static int CurrentSessionId { get; } = Process.GetCurrentProcess().SessionId;

    /// <summary>Owner SID per (pid, create time): a token is opened once per process, not every refresh.</summary>
    private readonly Dictionary<(int, long), string?> owners = [];

    public IReadOnlyList<ProcessSnapshot> OwnProcesses()
    {
        var own = Environment.ProcessId;
        var result = new List<ProcessSnapshot>();
        var seen = new HashSet<(int, long)>();
        foreach (var p in ProcessNative.AllProcesses())
        {
            if (p.Pid <= 4 || p.Pid == own || p.SessionId != CurrentSessionId) continue;
            var key = (p.Pid, p.CreateTime);
            seen.Add(key);
            if (!owners.TryGetValue(key, out var sid)) owners[key] = sid = ProcessNative.OwnerSid(p.Pid);
            if (!string.Equals(sid, CurrentSid, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new ProcessSnapshot(p.Pid, p.Name, p.CreateTime, p.CpuTime, p.PrivateWorkingSet));
        }
        foreach (var gone in owners.Keys.Where(k => !seen.Contains(k)).ToList()) owners.Remove(gone);
        return result;
    }
}

/// <summary>The real <see cref="IProcessControl"/>: reads the owner from the process token, closes or terminates by PID.</summary>
public sealed class WindowsProcessControl : IProcessControl
{
    public static ProcessTerminator Terminator() =>
        new(ProcessSnapshotter.CurrentSid, ProcessSnapshotter.CurrentSessionId, Environment.ProcessId, new WindowsProcessControl());

    public ProcessOwner? Owner(int pid)
    {
        var handle = ProcessNative.OpenProcess(ProcessNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!ProcessNative.GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
            if (!ProcessNative.GetExitCodeProcess(handle, out var code) || code != 259 /* STILL_ACTIVE */) return null;
            var sid = ProcessNative.TokenSid(handle);
            if (sid is null || !ProcessNative.ProcessIdToSessionId(pid, out var session)) return null;
            var critical = ProcessNative.IsProcessCritical(handle, out var isCritical) && isCritical;
            return new ProcessOwner(sid, (int)session, created, critical);
        }
        finally { ProcessNative.CloseHandle(handle); }
    }

    public bool CloseMainWindow(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.CloseMainWindow();
    }

    public void Kill(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill();
    }
}

internal static class ProcessNative
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public sealed record Entry(int Pid, string Name, long CreateTime, TimeSpan CpuTime, long PrivateWorkingSet, int SessionId);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>
    /// Walks <c>SYSTEM_PROCESS_INFORMATION</c> (x64 layout): NextEntryOffset @0, WorkingSetPrivateSize @8, CreateTime @32,
    /// UserTime @40, KernelTime @48, ImageName (UNICODE_STRING) @56, UniqueProcessId @80, SessionId @100.
    /// </summary>
    public static List<Entry> AllProcesses()
    {
        var size = 512 * 1024;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch) { size = Math.Max(size * 2, needed + 64 * 1024); continue; }
                if (status != 0) return [];
                var result = new List<Entry>();
                var offset = 0L;
                while (true)
                {
                    var entry = buffer + (nint)offset;
                    var next = Marshal.ReadInt32(entry, 0);
                    var nameLength = Marshal.ReadInt16(entry, 56);
                    var namePointer = Marshal.ReadIntPtr(entry, 64);
                    var name = namePointer == IntPtr.Zero ? (Marshal.ReadInt64(entry, 80) == 0 ? "Idle" : "System")
                        : Marshal.PtrToStringUni(namePointer, (ushort)nameLength / 2);
                    result.Add(new Entry(
                        Pid: (int)Marshal.ReadInt64(entry, 80),
                        Name: name,
                        CreateTime: Marshal.ReadInt64(entry, 32),
                        CpuTime: TimeSpan.FromTicks(Marshal.ReadInt64(entry, 40) + Marshal.ReadInt64(entry, 48)),
                        PrivateWorkingSet: Marshal.ReadInt64(entry, 8),
                        SessionId: Marshal.ReadInt32(entry, 100)));
                    if (next == 0) break;
                    offset += next;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return [];
    }

    public static string? OwnerSid(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try { return TokenSid(handle); }
        finally { CloseHandle(handle); }
    }

    public static string? TokenSid(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return null;
        try
        {
            GetTokenInformation(token, 1 /* TokenUser */, IntPtr.Zero, 0, out var length);
            if (length <= 0) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, 1, buffer, length, out _)) return null;
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProcessIdToSessionId(int pid, out uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsProcessCritical(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);
}
