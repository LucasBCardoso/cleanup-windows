using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Cleanup.Core.Health;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Reads the PC's state for the health monitor (design §5.2 Tray + Health): CPU from GetSystemTimes deltas, memory and
/// commit charge from GlobalMemoryStatusEx / GetPerformanceInfo, the system drive, battery from GetSystemPowerStatus and
/// WMI (design vs full-charge capacity), uptime, Recycle Bin size and the top process. Each part fails on its own.
/// </summary>
public sealed class HealthSampler
{
    private (ulong Idle, ulong Total)? lastSystem;
    private Dictionary<int, (TimeSpan Cpu, string Name)> lastProcesses = [];
    private DateTime lastProcessSample;
    private (int? Health, int? Cycles, DateTime At)? batteryCache;

    public HealthSnapshot Sample(string systemDrive)
    {
        var snapshot = new HealthSnapshot { Date = DateTimeOffset.Now };
        snapshot = Try(snapshot, s => s with { CpuUsage = Cpu() });
        snapshot = Try(snapshot, s =>
        {
            var (used, total, pressure) = Memory();
            return s with { MemoryUsed = used, MemoryTotal = total, Pressure = pressure };
        });
        snapshot = Try(snapshot, s =>
        {
            var drive = new DriveInfo(systemDrive);
            return drive.IsReady ? s with { DiskFree = drive.AvailableFreeSpace, DiskTotal = drive.TotalSize } : s;
        });
        snapshot = Try(snapshot, s => s with { Battery = Battery() });
        snapshot = Try(snapshot, s => s with { Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64) });
        snapshot = Try(snapshot, s => s with { TrashSize = RecycleBin.Query().Size });
        snapshot = Try(snapshot, s => s with { TopProcess = TopProcess() });
        return snapshot;
    }

    private static HealthSnapshot Try(HealthSnapshot snapshot, Func<HealthSnapshot, HealthSnapshot> read)
    {
        try { return read(snapshot); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ManagementException
                                      or UnauthorizedAccessException or COMException or ArgumentException) { return snapshot; }
    }


    /// <summary>Total CPU busy % since the previous sample (null on the first one).</summary>
    private double? Cpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var now = (Idle: idle, Total: kernel + user); // kernel time includes idle time
        var previous = lastSystem;
        lastSystem = now;
        if (previous is not { } p || now.Total <= p.Total) return null;
        var total = now.Total - p.Total;
        var busy = total - (now.Idle - p.Idle);
        return Math.Clamp(100.0 * busy / total, 0, 100);
    }

    /// <summary>
    /// Pressure: the worse of physical load (≥ 90 % warning, ≥ 97 % critical) and commit charge against the commit
    /// limit (≥ 85 % warning, ≥ 95 % critical) — when commit runs out Windows can't hand out memory at all.
    /// </summary>
    internal static MemoryPressure PressureFrom(uint physicalLoadPercent, double commitRatio)
    {
        var physical = physicalLoadPercent >= 97 ? MemoryPressure.Critical : physicalLoadPercent >= 90 ? MemoryPressure.Warning : MemoryPressure.Normal;
        var commit = commitRatio >= 0.95 ? MemoryPressure.Critical : commitRatio >= 0.85 ? MemoryPressure.Warning : MemoryPressure.Normal;
        return (MemoryPressure)Math.Max((int)physical, (int)commit);
    }

    private static (long Used, long Total, MemoryPressure Pressure) Memory()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("GlobalMemoryStatusEx");
        var info = new Native.PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>() };
        var commitRatio = 0.0;
        if (Native.GetPerformanceInfo(out info, info.cb) && info.CommitLimit != 0)
            commitRatio = (double)info.CommitTotal / info.CommitLimit;
        var total = (long)status.ullTotalPhys;
        return (total - (long)status.ullAvailPhys, total, PressureFrom(status.dwMemoryLoad, commitRatio));
    }

    /// <summary>Null on desktops (no system battery).</summary>
    private BatteryReading? Battery()
    {
        if (!Native.GetSystemPowerStatus(out var power)) return null;
        if ((power.BatteryFlag & 128) != 0 || power.BatteryFlag == 255 || power.BatteryLifePercent > 100) return null; // no battery / unknown
        var (health, cycles) = BatteryWear();
        return new BatteryReading(power.BatteryLifePercent, (power.BatteryFlag & 8) != 0, power.ACLineStatus == 1, cycles, health);
    }

    /// <summary>Full-charge vs design capacity from root\wmi (cached for an hour: WMI is slow and it changes slowly).</summary>
    private (int? Health, int? Cycles) BatteryWear()
    {
        if (batteryCache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromHours(1)) return (c.Health, c.Cycles);
        int? health = null, cycles = null;
        try
        {
            var scope = new ManagementScope(@"root\wmi");
            long? design = null, full = null;
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DesignedCapacity FROM BatteryStaticData")))
                foreach (ManagementObject o in s.Get()) design = Convert.ToInt64(o["DesignedCapacity"]);
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")))
                foreach (ManagementObject o in s.Get()) full = Convert.ToInt64(o["FullChargedCapacity"]);
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CycleCount FROM BatteryCycleCount")))
                foreach (ManagementObject o in s.Get()) cycles = Convert.ToInt32(o["CycleCount"]) is var n and > 0 ? n : null;
            if (design is > 0 && full is > 0) health = (int)Math.Round(100.0 * full.Value / design.Value);
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException) { }
        batteryCache = (health, cycles, DateTime.UtcNow);
        return (health, cycles);
    }

    /// <summary>The process with the most CPU since the previous sample, as % of the whole machine.</summary>
    private ProcessSample? TopProcess()
    {
        var now = DateTime.UtcNow;
        var current = new Dictionary<int, (TimeSpan Cpu, string Name)>();
        foreach (var p in Process.GetProcesses())
        {
            try { current[p.Id] = (p.TotalProcessorTime, p.ProcessName); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            finally { p.Dispose(); }
        }
        var elapsed = now - lastProcessSample;
        var previous = lastProcesses;
        lastProcesses = current;
        lastProcessSample = now;
        if (previous.Count == 0 || elapsed <= TimeSpan.Zero) return null;
        ProcessSample? top = null;
        foreach (var (id, (cpu, name)) in current)
        {
            if (id == 0 || !previous.TryGetValue(id, out var before) || before.Name != name) continue;
            var percent = 100.0 * (cpu - before.Cpu).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount);
            if (top is null || percent > top.Cpu) top = new ProcessSample(name, Math.Round(percent, 1));
        }
        return top;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        public static bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)
        {
            var ok = GetSystemTimes(out long i, out long k, out long u);
            (idle, kernel, user) = ((ulong)i, (ulong)k, (ulong)u);
            return ok;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public nuint CommitTotal;
            public nuint CommitLimit;
            public nuint CommitPeak;
            public nuint PhysicalTotal;
            public nuint PhysicalAvailable;
            public nuint SystemCache;
            public nuint KernelTotal;
            public nuint KernelPaged;
            public nuint KernelNonpaged;
            public nuint PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    }
}
