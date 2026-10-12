using System.Diagnostics;
using System.Text.Json;
using Cleanup.Core.Cleaning;
using Cleanup.Core.History;
using Cleanup.Core.Models;
using Cleanup.Core.Rules;
using Cleanup.Core.Safety;
using Cleanup.Platform.Windows;

namespace Cleanup.App.Services;

/// <summary>What every module shares: the environment, rules, safety policy, remover and the cleanup history.</summary>
public sealed class AppServices
{
    public static AppServices Shared { get; } = new();

    public ScanEnvironment Environment { get; } = ScanEnvironment.Current;
    public IReadOnlyList<CleanupRule> Rules { get; }
    public string? StartupError { get; }
    public IFileRemover Remover { get; } = new WindowsFileRemover();
    public AppSettings Settings { get; } = AppSettings.Load();

    /// <summary>Raised after any cleanup is recorded or undone, so Hoje's undo card and the history refresh.</summary>
    public event Action? HistoryChanged;

    private AppServices()
    {
        try { Rules = DefaultRules.Load(); }
        catch (RuleSetException e)
        {
            Rules = DefaultRules.All;
            StartupError = e.Message + " — usando as regras padrão.";
        }
    }

    /// <summary>A fresh policy each time, so ignore-list changes apply immediately.</summary>
    public SafetyPolicy Policy()
    {
        try { return new SafetyPolicy(Environment, IgnoreList.Load().Paths); }
        catch (IgnoreListException) { return new SafetyPolicy(Environment); }
    }

    public Restorer.Context RestoreContext() => RecycleBin.RestoreContext(Policy());

    public void Ignore(string path) => IgnoreList.Load().Adding(path).Save();

    /// <summary>User items to the Recycle Bin (re-checked by the policy), off the UI thread.</summary>
    public Task<CleanResult> CleanAsync(IReadOnlyList<ScanItem> items, RemovalMode mode = RemovalMode.RecycleBin) =>
        Task.Run(() => new Cleaner(Policy(), Remover, mode).Clean(items));

    /// <summary>System items through the elevated helper: one UAC prompt; nothing happens when the prompt is cancelled.</summary>
    public async Task<AdminCleaner.Result> CleanAsAdminAsync(IReadOnlyList<ScanItem> items)
    {
        if (items.Count == 0) return new AdminCleaner.Result(false, [], []);
        var plan = AdminCleaner.Plan(items, Environment);
        var result = await AdminCleaner.RunAsync(plan.Accepted);
        var rejected = plan.Rejected.Select(r => new AdminCleaner.Failure(r.Item.Path, r.Reason));
        return result with { Failed = [.. result.Failed, .. rejected] };
    }

    /// <summary>One history record for the user part and the admin part of a cleanup.</summary>
    public async Task RecordAsync(CleanResult result, IEnumerable<ScanItem>? adminRemoved, string source)
    {
        var record = CleanupRecord.From(result, adminRemoved, source, RecycleBin.FileId);
        if (record is null) return;
        await Task.Run(() => CleanupHistory.Update(h => h.Adding(record)));
        HistoryChanged?.Invoke();
    }

    public void NotifyHistoryChanged() => HistoryChanged?.Invoke();

    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", ["/select,", path]) { UseShellExecute = false });
        }
        catch (Exception e) { Log.Write("reveal: " + e.Message); }
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write("open: " + e.Message); }
    }
}

public enum ThemeChoice { Auto, Daybreak, Ledger }

/// <summary>App preferences, in <c>%LOCALAPPDATA%\Cleanup\settings.json</c>.</summary>
public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.Auto;
    public string? LastModule { get; set; }

    private static string FilePath => Path.Combine(AppPaths.SupportDirectory, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SupportDirectory);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write("settings: " + e.Message); }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.SupportDirectory);
                File.AppendAllText(Path.Combine(AppPaths.SupportDirectory, "cleanup.log"), $"{DateTime.Now:O} {message}{System.Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
