using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cleanup.App.Services;
using Cleanup.Core.Health;
using Cleanup.Core.Models;
using Cleanup.Platform.Windows;
using Microsoft.UI.Dispatching;

namespace Cleanup.App.ViewModels;

public sealed class IssueRow(HealthIssue issue)
{
    public HealthIssue Issue { get; } = issue;
    public string Title => Issue.Title;
    public string Message => Issue.Message;
    public bool IsCritical => Issue.Severity == IssueSeverity.Critical;
    public bool IsWarning => Issue.Severity == IssueSeverity.Warning;
    public bool IsInfo => Issue.Severity == IssueSeverity.Info;
    public bool HasAction => Issue.Action != IssueAction.None;
    public string ActionText => Issue.Action == IssueAction.SmartScan ? "Liberar espaço" : "Ver consumo";
}

/// <summary>
/// The health monitor (macOS Phase 3): samples every 10 s off the UI thread, keeps the CPU history, evaluates alerts,
/// notifies (throttled, never for snoozed alerts) and feeds the tray panel, the tray tooltip and the Hoje tile.
/// </summary>
public sealed partial class HealthViewModel : ObservableObject
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly AppServices services = AppServices.Shared;
    private readonly HealthSampler sampler = new();
    private readonly DispatcherQueue dispatcher;
    private CpuHistory history = new();
    private HealthSnoozes snoozes = HealthSnoozes.Load();
    private Timer? timer;
    private int sampling;

    public ObservableCollection<IssueRow> Issues { get; } = [];

    [ObservableProperty] private HealthSnapshot? snapshot;
    [ObservableProperty] private string cpuText = "—";
    [ObservableProperty] private double cpuFraction;
    [ObservableProperty] private string memoryText = "—";
    [ObservableProperty] private double memoryFraction;
    [ObservableProperty] private string pressureText = "";
    [ObservableProperty] private string diskText = "—";
    [ObservableProperty] private double diskFraction;
    [ObservableProperty] private string batteryText = "";
    [ObservableProperty] private bool hasBattery;
    [ObservableProperty] private string uptimeText = "";
    [ObservableProperty] private string topProcessText = "";
    [ObservableProperty] private string summary = "Tudo em ordem";
    [ObservableProperty] private bool hasIssues;

    /// <summary>True once the first full sample (with CPU, which needs two readings) arrived. Automatic cleaning waits for it.</summary>
    public bool HasSample { get; private set; }

    public event Action? Sampled;

    public HealthViewModel(DispatcherQueue dispatcher) => this.dispatcher = dispatcher;

    public void Start()
    {
        timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, Interval);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref sampling, 1) == 1) return;
        try
        {
            var drive = Path.GetPathRoot(services.Environment.Home) ?? "C:\\";
            var reading = sampler.Sample(drive);
            dispatcher.TryEnqueue(() => Apply(reading));
        }
        catch (Exception e) { Log.Write("health: " + e.Message); }
        finally { Interlocked.Exchange(ref sampling, 0); }
    }

    private void Apply(HealthSnapshot s)
    {
        Snapshot = s;
        history = history.Appending(s.TopProcess, s.Date);
        if (s.CpuUsage is { } cpu) { CpuText = $"{cpu:0}%"; CpuFraction = cpu / 100; HasSample = true; }
        if (s.MemoryUsed is { } used && s.MemoryTotal is { } total && total > 0)
        {
            MemoryText = $"{ByteFormat.Format(used)} de {ByteFormat.Format(total)}";
            MemoryFraction = (double)used / total;
        }
        PressureText = s.Pressure switch
        {
            MemoryPressure.Critical => "pressão crítica",
            MemoryPressure.Warning => "sob pressão",
            MemoryPressure.Normal => "normal",
            _ => "",
        };
        if (s.DiskFree is { } free && s.DiskTotal is { } disk && disk > 0)
        {
            DiskText = $"{ByteFormat.Format(free)} livres de {ByteFormat.Format(disk)}";
            DiskFraction = 1 - (double)free / disk;
        }
        HasBattery = s.Battery is not null;
        if (s.Battery is { } b)
        {
            var state = b.IsCharging ? "carregando" : b.OnPower ? "na tomada" : "na bateria";
            var health = b.HealthPercent is { } h ? $" · saúde {h}%" : "";
            BatteryText = $"{b.ChargePercent}% · {state}{health}";
        }
        if (s.Uptime is { } up) UptimeText = up.TotalDays >= 1 ? $"ligado há {(int)up.TotalDays} d {up.Hours} h" : $"ligado há {up.Hours} h {up.Minutes} min";
        TopProcessText = s.TopProcess is { Cpu: >= 1 } top ? $"{top.Name} · {top.Cpu:0}% da CPU" : "";

        var now = DateTimeOffset.Now;
        var all = HealthEvaluator.Issues(s, history);
        var visible = all.Where(i => !snoozes.IsSnoozed(i.Id, now)).ToList();
        if (!visible.Select(i => i.Id).SequenceEqual(Issues.Select(r => r.Issue.Id)) ||
            !visible.Select(i => i.Message).SequenceEqual(Issues.Select(r => r.Message)))
        {
            Issues.Clear();
            foreach (var issue in visible) Issues.Add(new IssueRow(issue));
        }
        HasIssues = Issues.Count > 0;
        Summary = Issues.Count switch
        {
            0 => "Tudo em ordem",
            1 => Issues[0].Title,
            var n => $"{n} alertas",
        };
        Notify(all, now);
        Sampled?.Invoke();
    }

    private void Notify(IReadOnlyList<HealthIssue> issues, DateTimeOffset now)
    {
        var settings = services.Settings;
        // Nothing is sent before the introduction has explained the alerts.
        if (!settings.NotificationsEnabled || !settings.OnboardingDone) return;
        var due = NotificationPolicy.ToNotify(issues, snoozes, settings.LastNotified, now);
        if (due.Count == 0) return;
        foreach (var issue in due)
        {
            var link = issue.Action == IssueAction.SmartScan ? "cleanup://scan" : issue.Action == IssueAction.Consumers ? "cleanup://open/optimization" : "cleanup://home";
            Notifications.Show(issue.Title, issue.Message, link);
            settings.LastNotified[issue.Id] = now;
        }
        settings.Save();
    }

    public void Snooze(IssueRow row, SnoozeDuration duration)
    {
        var now = DateTimeOffset.Now;
        snoozes = snoozes.Snoozing(row.Issue.Id, duration, now);
        try { snoozes.Save(now); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write("snooze: " + e.Message); }
        Issues.Remove(row);
        HasIssues = Issues.Count > 0;
        Summary = Issues.Count == 0 ? "Tudo em ordem" : Issues.Count == 1 ? Issues[0].Title : $"{Issues.Count} alertas";
    }

    /// <summary>Tooltip for the tray icon: "Cleanup · CPU 12% · 18 GB livres".</summary>
    public string TrayTooltip()
    {
        var parts = new List<string> { "Cleanup" };
        if (Snapshot?.CpuUsage is { } cpu) parts.Add($"CPU {cpu:0}%");
        if (Snapshot?.DiskFree is { } free) parts.Add($"{ByteFormat.Format(free)} livres");
        if (HasIssues) parts.Add(Summary);
        return string.Join(" · ", parts);
    }
}
