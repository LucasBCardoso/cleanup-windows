using System.Text.Json;
using System.Text.Json.Serialization;
using Cleanup.Core.Cleaning;
using Cleanup.Core.Health;
using Cleanup.Core.History;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;

namespace Cleanup.Core.AutoClean;

[JsonConverter(typeof(JsonStringEnumConverter<AutoCleanFrequency>))]
public enum AutoCleanFrequency
{
    [JsonStringEnumMemberName("off")] Off,
    [JsonStringEnumMemberName("daily")] Daily,
    [JsonStringEnumMemberName("weekly")] Weekly,
}

[JsonConverter(typeof(JsonStringEnumConverter<AutoCleanMode>))]
public enum AutoCleanMode
{
    /// <summary>Only tell the user how much can be freed.</summary>
    [JsonStringEnumMemberName("notify")] Notify,
    /// <summary>Move safe junk to the Recycle Bin (recorded, undoable).</summary>
    [JsonStringEnumMemberName("clean")] Clean,
}

/// <summary>Automatic cleaning (macOS Phase 8): a periodic Smart Scan that reports what can be freed, or cleans what is safe.</summary>
public sealed record AutoCleanSettings(AutoCleanFrequency Frequency, AutoCleanMode Mode, long MinimumBytes)
{
    public static readonly AutoCleanSettings Default = new(AutoCleanFrequency.Off, AutoCleanMode.Notify, 200L * 1_048_576);

    public static TimeSpan? Interval(AutoCleanFrequency frequency) => frequency switch
    {
        AutoCleanFrequency.Daily => TimeSpan.FromDays(1),
        AutoCleanFrequency.Weekly => TimeSpan.FromDays(7),
        _ => null,
    };

    public static string Title(AutoCleanFrequency f) => f switch
    {
        AutoCleanFrequency.Daily => "Todo dia",
        AutoCleanFrequency.Weekly => "Toda semana",
        _ => "Desligada",
    };

    public static string Title(AutoCleanMode m) => m == AutoCleanMode.Notify ? "Só avisar" : "Limpar o que é seguro";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Missing or unreadable settings mean the defaults (off): never start cleaning on a guess.</summary>
    public static AutoCleanSettings Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try { return JsonSerializer.Deserialize<AutoCleanSettings>(json, Json) ?? Default; }
        catch (JsonException) { return Default; }
    }

    public string Encode() => JsonSerializer.Serialize(this, Json);
}

public static class AutoCleanSchedule
{
    /// <summary>Below this charge (and not charging), the run waits for the next check.</summary>
    public const int MinimumBatteryPercent = 20;

    public static bool IsDue(AutoCleanSettings settings, DateTimeOffset? lastRun, DateTimeOffset now)
    {
        if (AutoCleanSettings.Interval(settings.Frequency) is not { } interval) return false;
        if (lastRun is not { } last || last > now) return true; // a future lastRun (clock moved back) is due
        return now - last >= interval;
    }

    public static DateTimeOffset? NextRun(AutoCleanSettings settings, DateTimeOffset? lastRun) =>
        AutoCleanSettings.Interval(settings.Frequency) is { } interval && lastRun is { } last ? last + interval : null;

    /// <summary>Why a due run should wait, or null to go ahead.</summary>
    public static string? SkipReason(BatteryReading? battery) =>
        battery is { IsCharging: false, OnPower: false } b && b.ChargePercent < MinimumBatteryPercent
            ? $"bateria abaixo de {MinimumBatteryPercent}%"
            : null;
}

public sealed record AutoCleanPlan(IReadOnlyList<ScanItem> Items, long TotalBytes, IReadOnlyList<string> TopGroups);

public static class AutoCleanPlanner
{
    /// <summary>
    /// The same items "Limpar agora" takes — safe and needing no administrator — except the Recycle Bin: emptying it is
    /// permanent, so it is never done in the background.
    /// </summary>
    public static AutoCleanPlan Plan(IEnumerable<ScanReport> reports, long minimumBytes)
    {
        var groups = reports.Select(r => (r.Title, Items: r.Items.Where(i => i.Risk == RiskLevel.Safe && !i.RequiresAdmin && i.Kind != ItemKind.RecycleBin).ToList()))
            .Where(g => g.Items.Count > 0).ToList();
        var items = groups.SelectMany(g => g.Items).ToList();
        var total = items.Sum(i => i.Size);
        if (total < minimumBytes || total <= 0) return new AutoCleanPlan([], total, []);
        var top = groups.Select(g => (g.Title, Bytes: g.Items.Sum(i => i.Size))).OrderByDescending(g => g.Bytes).Take(3).Select(g => g.Title).ToList();
        return new AutoCleanPlan(items, total, top);
    }
}

public sealed record AutoCleanNotification(string Title, string Body);

public abstract record AutoCleanOutcome
{
    public sealed record Found(long Bytes) : AutoCleanOutcome;
    public sealed record Cleaned(long Bytes) : AutoCleanOutcome;
    public sealed record BelowMinimum(long Bytes) : AutoCleanOutcome;
    /// <summary>Clean mode removed nothing: every item failed.</summary>
    public sealed record Failed(int Count) : AutoCleanOutcome;
}

public sealed record AutoCleanResult(AutoCleanOutcome Outcome, AutoCleanNotification? Notification, CleanupRecord? Record,
    IReadOnlyList<CleanFailure> Failures)
{
    /// <summary>One line for Ajustes ("Última vez: …").</summary>
    public string Summary => Outcome switch
    {
        AutoCleanOutcome.Found f => $"{ByteFormat.Format(f.Bytes)} para liberar (avisado)",
        AutoCleanOutcome.Cleaned c => ByteFormat.Format(c.Bytes) + " liberados" + (Failures.Count switch
        {
            0 => "",
            1 => "; 1 item não pôde ser removido",
            var n => $"; {n} itens não puderam ser removidos",
        }),
        AutoCleanOutcome.BelowMinimum b => $"nada relevante ({ByteFormat.Format(b.Bytes)})",
        AutoCleanOutcome.Failed f => $"não foi possível limpar ({f.Count} falha{(f.Count == 1 ? "" : "s")})",
        _ => "",
    };

    /// <summary>A run where everything failed is retried at the next check instead of waiting a whole interval.</summary>
    public bool ShouldCountAsRun => Outcome is not AutoCleanOutcome.Failed;
}

public static class AutoCleanRunner
{
    public static AutoCleanResult Run(IEnumerable<ScanReport> reports, AutoCleanSettings settings, Cleaner cleaner,
        Func<string, string?> fileId, DateTimeOffset now)
    {
        var plan = AutoCleanPlanner.Plan(reports, settings.MinimumBytes);
        if (plan.Items.Count == 0) return new AutoCleanResult(new AutoCleanOutcome.BelowMinimum(plan.TotalBytes), null, null, []);
        var groups = string.Join(", ", plan.TopGroups);
        if (settings.Mode == AutoCleanMode.Notify)
        {
            return new AutoCleanResult(new AutoCleanOutcome.Found(plan.TotalBytes),
                new AutoCleanNotification($"{ByteFormat.Format(plan.TotalBytes)} para liberar", $"{groups}. Abra o Cleanup para revisar."), null, []);
        }
        var result = cleaner.Clean(plan.Items);
        if (result.Removed.Count == 0) return new AutoCleanResult(new AutoCleanOutcome.Failed(result.Failed.Count), null, null, result.Failed);
        var record = CleanupRecord.From(result, null, "Limpeza automática", fileId, now);
        return new AutoCleanResult(new AutoCleanOutcome.Cleaned(result.FreedBytes),
            new AutoCleanNotification($"{ByteFormat.Format(result.FreedBytes)} liberados", $"{groups}. Dá para desfazer em Hoje."), record, result.Failed);
    }
}
