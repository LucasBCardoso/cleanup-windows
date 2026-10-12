using System.Text.Json;
using Cleanup.Core.AutoClean;
using Cleanup.Core.Health;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;

namespace Cleanup.Core.Tests.Shared;

/// <summary>health.json and autoclean.json, run against the real .NET code (W8 / W8b).</summary>
public class HealthVectorsTests
{
    private static JsonElement Load(string file)
    {
        using var doc = JsonDocument.Parse(SharedFiles.Read("test-vectors/" + file));
        return doc.RootElement.Clone();
    }

    private static DateTimeOffset Date(JsonElement e) => DateTimeOffset.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    private static long? Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static BatteryReading? Battery(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        int? Int(string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        return new BatteryReading(o.GetProperty("chargePercent").GetInt32(), o.GetProperty("isCharging").GetBoolean(),
            o.GetProperty("onPower").GetBoolean(), Int("cycleCount"), Int("healthPercent"));
    }

    private static CpuHistory Replay(JsonElement samples)
    {
        var origin = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var history = new CpuHistory();
        foreach (var s in samples.EnumerateArray())
        {
            var name = s.GetProperty("name");
            ProcessSample? top = name.ValueKind == JsonValueKind.Null ? null : new ProcessSample(name.GetString()!, s.GetProperty("cpu").GetDouble());
            history = history.Appending(top, origin.AddSeconds(s.GetProperty("t").GetDouble()));
        }
        return history;
    }

    private static HealthSnoozes Snoozes(JsonElement list)
    {
        var snoozes = new HealthSnoozes();
        foreach (var s in list.EnumerateArray())
        {
            var duration = s.GetProperty("duration").GetString() switch { "day" => SnoozeDuration.Day, "week" => SnoozeDuration.Week, _ => SnoozeDuration.Forever };
            snoozes = snoozes.Snoozing(s.GetProperty("id").GetString()!, duration, Date(s.GetProperty("at")));
        }
        return snoozes;
    }

    private static IssueSeverity Severity(string s) => s switch { "critical" => IssueSeverity.Critical, "warning" => IssueSeverity.Warning, _ => IssueSeverity.Info };

    [Fact]
    public void Evaluate()
    {
        foreach (var c in Load("health.json").GetProperty("evaluate").EnumerateArray())
        {
            var s = c.GetProperty("snapshot");
            var snapshot = new HealthSnapshot
            {
                DiskFree = Long(s, "diskFree"), DiskTotal = Long(s, "diskTotal"), TrashSize = Long(s, "trashSize"),
                Uptime = Long(s, "uptime") is { } u ? TimeSpan.FromSeconds(u) : null,
                Pressure = s.TryGetProperty("pressure", out var p) ? p.GetString() switch
                {
                    "warning" => MemoryPressure.Warning, "critical" => MemoryPressure.Critical, _ => MemoryPressure.Normal,
                } : null,
                Battery = s.TryGetProperty("battery", out var b) ? Battery(b) : null,
            };
            var history = c.TryGetProperty("cpuSamples", out var samples) ? Replay(samples) : new CpuHistory();
            var actual = HealthEvaluator.Issues(snapshot, history).Select(i => (i.Id, i.Severity)).ToList();
            var expected = c.GetProperty("expected").EnumerateArray().Select(e => (e.GetProperty("id").GetString()!, Severity(e.GetProperty("severity").GetString()!))).ToList();
            Assert.True(expected.SequenceEqual(actual), c.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void CpuHog()
    {
        foreach (var c in Load("health.json").GetProperty("cpuHog").EnumerateArray())
        {
            var expected = c.GetProperty("expected");
            Assert.True((expected.ValueKind == JsonValueKind.Null ? null : expected.GetString()) == Replay(c.GetProperty("samples")).SustainedHog(),
                c.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void SnoozeAndNotify()
    {
        var root = Load("health.json");
        var snoozes = Snoozes(root.GetProperty("snooze").GetProperty("snoozes"));
        foreach (var check in root.GetProperty("snooze").GetProperty("checks").EnumerateArray())
        {
            Assert.Equal(check.GetProperty("expected").GetBoolean(), snoozes.IsSnoozed(check.GetProperty("id").GetString()!, Date(check.GetProperty("now"))));
        }
        foreach (var c in root.GetProperty("notify").EnumerateArray())
        {
            var issues = c.GetProperty("issues").EnumerateArray()
                .Select(i => new HealthIssue(i.GetProperty("id").GetString()!, Severity(i.GetProperty("severity").GetString()!), "", "", IssueAction.None)).ToList();
            var last = c.GetProperty("lastNotified").EnumerateObject().ToDictionary(p => p.Name, p => Date(p.Value));
            var actual = NotificationPolicy.ToNotify(issues, Snoozes(c.GetProperty("snoozes")), last, Date(c.GetProperty("now"))).Select(i => i.Id);
            Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()), actual);
        }
    }

    private static AutoCleanFrequency Frequency(string s) => s switch { "daily" => AutoCleanFrequency.Daily, "weekly" => AutoCleanFrequency.Weekly, _ => AutoCleanFrequency.Off };

    [Fact]
    public void AutoCleanScheduleAndSkip()
    {
        var root = Load("autoclean.json");
        var defaults = root.GetProperty("defaults");
        Assert.Equal(AutoCleanFrequency.Off, AutoCleanSettings.Default.Frequency);
        Assert.Equal(AutoCleanMode.Notify, AutoCleanSettings.Default.Mode);
        Assert.Equal(defaults.GetProperty("minimumBytes").GetInt64(), AutoCleanSettings.Default.MinimumBytes);
        Assert.Equal(AutoCleanSchedule.MinimumBatteryPercent, defaults.GetProperty("minimumBatteryPercent").GetInt32());

        foreach (var c in root.GetProperty("isDue").EnumerateArray())
        {
            var settings = AutoCleanSettings.Default with { Frequency = Frequency(c.GetProperty("frequency").GetString()!) };
            var lastRun = c.GetProperty("lastRun").ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : Date(c.GetProperty("lastRun"));
            Assert.Equal(c.GetProperty("expected").GetBoolean(), AutoCleanSchedule.IsDue(settings, lastRun, Date(c.GetProperty("now"))));
        }
        foreach (var c in root.GetProperty("nextRun").EnumerateArray())
        {
            var settings = AutoCleanSettings.Default with { Frequency = Frequency(c.GetProperty("frequency").GetString()!) };
            var expected = c.GetProperty("expected").ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : Date(c.GetProperty("expected"));
            Assert.Equal(expected, AutoCleanSchedule.NextRun(settings, Date(c.GetProperty("lastRun"))));
        }
        foreach (var c in root.GetProperty("skip").EnumerateArray())
        {
            var battery = c.GetProperty("battery");
            var reading = battery.ValueKind == JsonValueKind.Null ? null
                : new BatteryReading(battery.GetProperty("chargePercent").GetInt32(), battery.GetProperty("isCharging").GetBoolean(), battery.GetProperty("onPower").GetBoolean(), null, null);
            Assert.Equal(c.GetProperty("expected").GetBoolean(), AutoCleanSchedule.SkipReason(reading) is not null);
        }
    }

    [Fact]
    public void AutoCleanPlan()
    {
        foreach (var c in Load("autoclean.json").GetProperty("plan").EnumerateArray())
        {
            var reports = c.GetProperty("reports").EnumerateArray().Select(r => new ScanReport("t", r.GetProperty("title").GetString()!, ScanCategory.SystemJunk,
                r.GetProperty("items").EnumerateArray().Select(i => new ScanItem
                {
                    Path = i.GetProperty("path").GetString()!, Size = i.GetProperty("size").GetInt64(), RuleId = "r", Category = ScanCategory.SystemJunk,
                    Risk = i.GetProperty("risk").GetString() == "safe" ? RiskLevel.Safe : RiskLevel.Review, RequiresAdmin = i.GetProperty("requiresAdmin").GetBoolean(),
                }).ToList(), [])).ToList();
            var plan = AutoCleanPlanner.Plan(reports, c.GetProperty("minimumBytes").GetInt64());
            var expected = c.GetProperty("expected");
            var name = c.GetProperty("name").GetString();
            Assert.True(expected.GetProperty("items").EnumerateArray().Select(e => e.GetString()).SequenceEqual(plan.Items.Select(i => i.Path)), name);
            Assert.Equal(expected.GetProperty("totalBytes").GetInt64(), plan.TotalBytes);
            Assert.True(expected.GetProperty("topGroups").EnumerateArray().Select(e => e.GetString()).SequenceEqual(plan.TopGroups), name);
        }
    }

    [Fact]
    public void UnreadableSettingsMeanOff()
    {
        Assert.Equal(AutoCleanSettings.Default, AutoCleanSettings.Decode("{ nope"));
        Assert.Equal(AutoCleanSettings.Default, AutoCleanSettings.Decode(null));
        var weekly = AutoCleanSettings.Default with { Frequency = AutoCleanFrequency.Weekly, Mode = AutoCleanMode.Clean };
        Assert.Equal(weekly, AutoCleanSettings.Decode(weekly.Encode()));
        Assert.Contains("\"weekly\"", weekly.Encode());
    }

    [Fact]
    public void AutoCleanRunnerRecordsCleanModeOnly()
    {
        using var home = new TempHome();
        var a = home.File(@"AppData\Local\Temp\a.tmp", 300);
        var report = new ScanReport("t", "Temporários", ScanCategory.SystemJunk,
            [new ScanItem { Path = a, Size = 300, RuleId = "user-temp", Category = ScanCategory.SystemJunk, Risk = RiskLevel.Safe }], []);
        var cleaner = new Core.Cleaning.Cleaner(home.Policy(), new FakeRemover());
        var notify = AutoCleanRunner.Run([report], AutoCleanSettings.Default with { MinimumBytes = 100 }, cleaner, _ => null, DateTimeOffset.Now);
        Assert.IsType<AutoCleanOutcome.Found>(notify.Outcome);
        Assert.Null(notify.Record);
        var clean = AutoCleanRunner.Run([report], AutoCleanSettings.Default with { MinimumBytes = 100, Mode = AutoCleanMode.Clean }, cleaner, _ => "id", DateTimeOffset.Now);
        Assert.IsType<AutoCleanOutcome.Cleaned>(clean.Outcome);
        Assert.Equal("Limpeza automática", clean.Record!.Source);
        Assert.Contains("Dá para desfazer", clean.Notification!.Body);
    }
}
