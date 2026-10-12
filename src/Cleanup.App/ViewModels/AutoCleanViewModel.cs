using CommunityToolkit.Mvvm.ComponentModel;
using Cleanup.App.Services;
using Cleanup.Core.AutoClean;
using Cleanup.Core.Cleaning;
using Cleanup.Core.History;
using Cleanup.Core.Models;
using Cleanup.Core.Scanning;
using Cleanup.Platform.Windows;
using Microsoft.UI.Dispatching;

namespace Cleanup.App.ViewModels;

/// <summary>
/// Limpeza automática (macOS Phase 8). Runs while Cleanup is open (window or tray): off / daily / weekly, "Só avisar"
/// or "Limpar o que é seguro". Takes only what Limpar agora would (safe, non-admin), never asks for anything
/// (invariant 10), skipped on low battery, during the introduction, while the user scans/cleans/optimizes, and before
/// the first health sample. A run where everything fails is retried at the next check.
/// </summary>
public sealed partial class AutoCleanViewModel : ObservableObject
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);

    private readonly AppServices services = AppServices.Shared;
    private readonly DispatcherQueue dispatcher;
    private readonly HealthViewModel health;
    private readonly ScanViewModel scan;
    private Timer? timer;

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string lastRunText = "";
    [ObservableProperty] private string nextRunText = "";

    /// <summary>Set by the introduction while it's open: nothing runs behind it.</summary>
    public bool IsSuspended { get; set; }

    public AutoCleanViewModel(DispatcherQueue dispatcher, HealthViewModel health, ScanViewModel scan)
    {
        this.dispatcher = dispatcher;
        this.health = health;
        this.scan = scan;
        UpdateTexts();
    }

    public AutoCleanSettings Settings
    {
        get => services.Settings.AutoClean;
        set
        {
            services.Settings.AutoClean = value;
            services.Settings.Save();
            UpdateTexts();
        }
    }

    public void Start() => timer ??= new Timer(_ => dispatcher.TryEnqueue(() => _ = CheckAsync()), null, FirstCheck, CheckInterval);

    public void UpdateTexts()
    {
        var s = services.Settings;
        LastRunText = s.AutoCleanLastRun is { } last
            ? $"Última vez: {last.LocalDateTime:dd/MM HH:mm} — {s.AutoCleanLastSummary}"
            : "Ainda não rodou.";
        NextRunText = AutoCleanSchedule.NextRun(s.AutoClean, s.AutoCleanLastRun) is { } next
            ? $"Próxima: {next.LocalDateTime:dd/MM HH:mm} (com o Cleanup aberto ou na bandeja)"
            : s.AutoClean.Frequency == AutoCleanFrequency.Off ? "Desligada." : "Próxima: na próxima verificação.";
    }

    /// <summary>The periodic check: runs only when due and nothing else is going on.</summary>
    public async Task CheckAsync()
    {
        var settings = services.Settings;
        if (!AutoCleanSchedule.IsDue(settings.AutoClean, settings.AutoCleanLastRun, DateTimeOffset.Now)) return;
        if (IsSuspended || !settings.OnboardingDone || !health.HasSample) return;
        if (AutoCleanSchedule.SkipReason(health.Snapshot?.Battery) is { } reason) { Log.Write("autoclean skipped: " + reason); return; }
        await RunAsync(settings.AutoClean);
    }

    /// <summary>"Verificar agora" / "Limpar agora" in Ajustes, or a due run.</summary>
    public async Task RunAsync(AutoCleanSettings settings)
    {
        if (IsRunning || scan.IsScanning || scan.IsBusy) return; // never alongside a user scan, clean or optimization
        IsRunning = true;
        scan.IsOptimizing = true; // manual cleans wait while it runs
        try
        {
            var result = await Task.Run(async () =>
            {
                var tasks = ScanEngine.SmartScanTasks(services.Rules, RunningProcesses.Names(), [new RecycleBinTask()]);
                var outcomes = await new ScanEngine(services.Environment, services.Policy()).RunAsync(tasks);
                var reports = outcomes.Select(o => o.Report).OfType<ScanReport>().ToList();
                var cleaner = new Cleaner(services.Policy(), services.Remover);
                return AutoCleanRunner.Run(reports, settings, cleaner, RecycleBin.FileId, DateTimeOffset.Now);
            });
            if (result.Record is { } record)
            {
                await Task.Run(() => CleanupHistory.Update(h => h.Adding(record)));
                services.NotifyHistoryChanged();
                scan.RemoveCleaned(record.Entries.Select(e => e.OriginalPath.ToLowerInvariant()).ToHashSet());
            }
            if (result.Notification is { } note && services.Settings.NotificationsEnabled)
                Notifications.Show(note.Title, note.Body, settings.Mode == AutoCleanMode.Clean ? "cleanup://home" : "cleanup://scan");
            if (result.ShouldCountAsRun)
            {
                services.Settings.AutoCleanLastRun = DateTimeOffset.Now;
                services.Settings.AutoCleanLastSummary = result.Summary;
                services.Settings.Save();
            }
            Log.Write("autoclean: " + result.Summary);
        }
        catch (Exception e) { Log.Write("autoclean failed: " + e.Message); }
        finally
        {
            scan.IsOptimizing = false;
            IsRunning = false;
            UpdateTexts();
        }
    }
}
