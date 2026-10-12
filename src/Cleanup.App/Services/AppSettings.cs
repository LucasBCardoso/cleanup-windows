using System.Text.Json;
using Cleanup.Core.AutoClean;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;
using Microsoft.Win32;

namespace Cleanup.App.Services;

public enum ThemeChoice { Auto, Daybreak, Ledger }

/// <summary>App preferences, in <c>%LOCALAPPDATA%\Cleanup\settings.json</c>.</summary>
public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.Auto;
    public string? LastModule { get; set; }

    /// <summary>Keep running in the notification area when the window closes (health alerts, automatic cleaning).</summary>
    public bool TrayEnabled { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool OnboardingDone { get; set; }

    public AutoCleanSettings AutoClean { get; set; } = AutoCleanSettings.Default;
    public DateTimeOffset? AutoCleanLastRun { get; set; }
    public string? AutoCleanLastSummary { get; set; }

    /// <summary>When each health alert was last notified (at most one every 6 h per alert, across restarts).</summary>
    public Dictionary<string, DateTimeOffset> LastNotified { get; set; } = [];

    private static string FilePath => Path.Combine(AppPaths.SupportDirectory, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static AppSettings Load()
    {
        try
        {
            var settings = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
            settings.AutoClean ??= AutoCleanSettings.Default;
            settings.LastNotified ??= [];
            return settings;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.SupportDirectory);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write("settings: " + e.Message); }
        }
    }
}

/// <summary>Launch at login through <c>HKCU\…\Run</c>, started in the background (tray only).</summary>
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Cleanup";
    public const string BackgroundArgument = "--background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(Name, $"\"{exe}\" {BackgroundArgument}");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
