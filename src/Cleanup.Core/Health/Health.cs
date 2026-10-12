using System.Text.Json;
using System.Text.Json.Serialization;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Health;

public sealed record BatteryReading(int ChargePercent, bool IsCharging, bool OnPower, int? CycleCount, int? HealthPercent);

/// <summary>On Windows: from the commit charge and physical memory load (design §5.2), mapped to the macOS levels.</summary>
public enum MemoryPressure { Normal = 1, Warning = 2, Critical = 4 }

/// <summary>The process using the most CPU right now.</summary>
public sealed record ProcessSample(string Name, double Cpu);

/// <summary>One reading of the PC's state. Every field is optional: a failed reading never hides the others.</summary>
public sealed record HealthSnapshot
{
    public DateTimeOffset Date { get; init; }
    public double? CpuUsage { get; init; }
    public long? MemoryUsed { get; init; }
    public long? MemoryTotal { get; init; }
    public MemoryPressure? Pressure { get; init; }
    public long? DiskFree { get; init; }
    public long? DiskTotal { get; init; }
    public BatteryReading? Battery { get; init; }
    public TimeSpan? Uptime { get; init; }
    public long? TrashSize { get; init; }
    public ProcessSample? TopProcess { get; init; }
}

public enum IssueSeverity { Info, Warning, Critical }

/// <summary>What the issue's button does in the app.</summary>
public enum IssueAction { SmartScan, Consumers, None }

public sealed record HealthIssue(string Id, IssueSeverity Severity, string Title, string Message, IssueAction Action);

/// <summary>macOS spec §4 thresholds. Disk and Recycle Bin sizes use decimal gigabytes.</summary>
public static class HealthThresholds
{
    public const long Gigabyte = 1_000_000_000;
    public const double DiskWarningFraction = 0.10;
    public const long DiskWarningBytes = 15 * Gigabyte;
    public const double DiskCriticalFraction = 0.05;
    public const long DiskCriticalBytes = 5 * Gigabyte;
    public const double CpuHogPercent = 80;
    public static readonly TimeSpan CpuHogWindow = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan CpuHogMinimumSpan = TimeSpan.FromSeconds(110);
    public static readonly TimeSpan CpuHogMaxGap = TimeSpan.FromSeconds(20);
    public const int BatteryHealthPercent = 80;
    public const long TrashBytes = 5 * Gigabyte;
    public const int UptimeDays = 14;
}

/// <summary>The top process over the last two minutes, to spot a sustained CPU hog.</summary>
public sealed record CpuHistory(IReadOnlyList<CpuHistory.Entry> Entries)
{
    public sealed record Entry(DateTimeOffset Date, string? Name, double Cpu);

    public CpuHistory() : this([]) { }

    /// <summary>A null sample (no reading) is kept as a cold entry, so it breaks any streak.</summary>
    public CpuHistory Appending(ProcessSample? top, DateTimeOffset date)
    {
        var cutoff = date - HealthThresholds.CpuHogWindow;
        return new CpuHistory([.. Entries.Where(e => e.Date >= cutoff), new Entry(date, top?.Name, top?.Cpu ?? 0)]);
    }

    /// <summary>The name on top at or above the threshold in every sample, over at least 110 s with no gap over 20 s.</summary>
    public string? SustainedHog()
    {
        if (Entries.Count == 0 || Entries[0].Name is not { } name) return null;
        if (Entries[^1].Date - Entries[0].Date < HealthThresholds.CpuHogMinimumSpan) return null;
        for (var i = 1; i < Entries.Count; i++)
        {
            if (Entries[i].Date - Entries[i - 1].Date > HealthThresholds.CpuHogMaxGap) return null;
        }
        return Entries.All(e => e.Name == name && e.Cpu >= HealthThresholds.CpuHogPercent) ? name : null;
    }
}

/// <summary>Turns a snapshot into issues. Pure: no clocks, no I/O. Same rules and shared vectors as macOS.</summary>
public static class HealthEvaluator
{
    public static IReadOnlyList<HealthIssue> Issues(HealthSnapshot s, CpuHistory history) =>
        new[] { Disk(s), Memory(s), Cpu(history), Battery(s), Trash(s), Uptime(s) }
            .OfType<HealthIssue>()
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

    internal static HealthIssue? Disk(HealthSnapshot s)
    {
        if (s.DiskFree is not { } free || s.DiskTotal is not { } total || total <= 0) return null;
        var fraction = (double)free / total;
        IssueSeverity severity;
        if (fraction < HealthThresholds.DiskCriticalFraction || free < HealthThresholds.DiskCriticalBytes) severity = IssueSeverity.Critical;
        else if (fraction < HealthThresholds.DiskWarningFraction || free < HealthThresholds.DiskWarningBytes) severity = IssueSeverity.Warning;
        else return null;
        return new HealthIssue("disk.low", severity, "Pouco espaço em disco",
            $"{ByteFormat.Format(free)} livres de {ByteFormat.Format(total)}.", IssueAction.SmartScan);
    }

    internal static HealthIssue? Memory(HealthSnapshot s) => s.Pressure switch
    {
        MemoryPressure.Warning => new HealthIssue("memory.pressure", IssueSeverity.Warning, "Memória sob pressão",
            "Feche apps que você não está usando.", IssueAction.Consumers),
        MemoryPressure.Critical => new HealthIssue("memory.pressure", IssueSeverity.Critical, "Memória em nível crítico",
            "O PC está ficando lento. Feche apps pesados.", IssueAction.Consumers),
        _ => null,
    };

    internal static HealthIssue? Cpu(CpuHistory history) => history.SustainedHog() is { } name
        ? new HealthIssue($"cpu.hog:{name}", IssueSeverity.Warning, $"{name} está usando muita CPU",
            $"Acima de {(int)HealthThresholds.CpuHogPercent}% há mais de 2 minutos.", IssueAction.Consumers)
        : null;

    internal static HealthIssue? Battery(HealthSnapshot s)
    {
        if (s.Battery is not { HealthPercent: { } health } reading || health >= HealthThresholds.BatteryHealthPercent) return null;
        var cycles = reading.CycleCount is { } c ? $" · {c} ciclos" : "";
        return new HealthIssue("battery.health", IssueSeverity.Info, "Bateria desgastada", $"Capacidade de {health}% da original{cycles}.", IssueAction.None);
    }

    internal static HealthIssue? Trash(HealthSnapshot s) =>
        s.TrashSize is { } size && size >= HealthThresholds.TrashBytes
            ? new HealthIssue("trash.large", IssueSeverity.Info, "Lixeira grande", $"{ByteFormat.Format(size)} na Lixeira.", IssueAction.SmartScan)
            : null;

    internal static HealthIssue? Uptime(HealthSnapshot s) =>
        s.Uptime is { } uptime && uptime > TimeSpan.FromDays(HealthThresholds.UptimeDays)
            ? new HealthIssue("uptime.long", IssueSeverity.Info, $"PC ligado há {(int)uptime.TotalDays} dias",
                "Reinicie o PC para aplicar atualizações e liberar memória.", IssueAction.None)
            : null;
}

public enum SnoozeDuration { Day, Week, Forever }

/// <summary>Snoozed health issues, persisted as <c>[{"id": "disk.low", "until": "…"|null}]</c> (null = forever), like macOS.</summary>
public sealed record HealthSnoozes(IReadOnlyDictionary<string, DateTimeOffset> Entries)
{
    public HealthSnoozes() : this(new Dictionary<string, DateTimeOffset>()) { }

    public static string DefaultPath => Path.Combine(AppPaths.SupportDirectory, "health-snoozes.json");

    public bool IsSnoozed(string id, DateTimeOffset now) => Entries.TryGetValue(id, out var until) && now < until;

    public HealthSnoozes Snoozing(string id, SnoozeDuration duration, DateTimeOffset now)
    {
        var until = duration switch
        {
            SnoozeDuration.Day => now.AddDays(1),
            SnoozeDuration.Week => now.AddDays(7),
            _ => DateTimeOffset.MaxValue,
        };
        return new HealthSnoozes(new Dictionary<string, DateTimeOffset>(Entries) { [id] = until });
    }

    public HealthSnoozes RemovingExpired(DateTimeOffset now) => new(Entries.Where(e => now < e.Value).ToDictionary());

    private sealed record Record([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("until")] DateTimeOffset? Until);

    public static HealthSnoozes Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new HealthSnoozes();
        try
        {
            var records = JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(path)) ?? [];
            var entries = new Dictionary<string, DateTimeOffset>();
            foreach (var r in records) entries[r.Id] = r.Until ?? DateTimeOffset.MaxValue;
            return new HealthSnoozes(entries);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new HealthSnoozes(); }
    }

    /// <summary>Writes only the snoozes still active at <paramref name="now"/>.</summary>
    public void Save(DateTimeOffset now, string? path = null)
    {
        path ??= DefaultPath;
        var records = RemovingExpired(now).Entries.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new Record(e.Key, e.Value == DateTimeOffset.MaxValue ? null : e.Value)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Which issues deserve a notification right now: warnings and criticals, not snoozed, at most every 6 h each.</summary>
public static class NotificationPolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public static IReadOnlyList<HealthIssue> ToNotify(IEnumerable<HealthIssue> issues, HealthSnoozes snoozes,
        IReadOnlyDictionary<string, DateTimeOffset> lastNotified, DateTimeOffset now) =>
        issues.Where(i => i.Severity >= IssueSeverity.Warning && !snoozes.IsSnoozed(i.Id, now)
                          && (!lastNotified.TryGetValue(i.Id, out var last) || now - last >= Interval)).ToList();
}
