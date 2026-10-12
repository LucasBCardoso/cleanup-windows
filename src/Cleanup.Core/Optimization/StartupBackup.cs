using System.Text.Json;
using System.Text.Json.Serialization;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Optimization;

/// <summary>
/// A removed <c>HKCU\…\Run</c> / <c>RunOnce</c> value, kept so the removal can be undone (macOS made manual
/// launch-agent removal undoable through the Trash; a registry value has no Recycle Bin, so it is saved here first).
/// </summary>
public sealed record StartupBackup
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("date")] public DateTimeOffset Date { get; init; } = DateTimeOffset.Now;
    /// <summary>The registry value name.</summary>
    [JsonPropertyName("name")] public required string Name { get; init; }
    /// <summary>The key, relative to HKCU (only the user's Run and RunOnce keys are ever restored).</summary>
    [JsonPropertyName("key")] public required string KeyPath { get; init; }
    [JsonPropertyName("command")] public required string Command { get; init; }
    /// <summary>REG_EXPAND_SZ rather than REG_SZ.</summary>
    [JsonPropertyName("expandable")] public bool Expandable { get; init; }

    /// <summary>The keys a backup may be restored into (the file is user-writable: never trust its key).</summary>
    public static readonly IReadOnlyList<string> RestorableKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
    ];

    public bool HasRestorableKey => RestorableKeys.Any(k => string.Equals(k, KeyPath, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The backups, one JSON file each, in <c>%LOCALAPPDATA%\Cleanup\backups</c>.</summary>
public sealed class StartupBackupStore(string folder)
{
    private const string Prefix = "startup-";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Folder { get; } = folder;

    public static string DefaultFolder => Path.Combine(AppPaths.SupportDirectory, "backups");

    public string PathFor(Guid id) => Path.Combine(Folder, $"{Prefix}{id:N}.json");

    public void Save(StartupBackup backup)
    {
        Directory.CreateDirectory(Folder);
        AtomicFile.WriteAllText(PathFor(backup.Id), JsonSerializer.Serialize(backup, Json));
    }

    /// <summary>Newest first. Unreadable files and entries for keys that can't be restored are skipped.</summary>
    public IReadOnlyList<StartupBackup> Load()
    {
        if (!Directory.Exists(Folder)) return [];
        var result = new List<StartupBackup>();
        foreach (var file in Directory.EnumerateFiles(Folder, Prefix + "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<StartupBackup>(File.ReadAllText(file), Json) is { } backup
                    && backup.HasRestorableKey && backup.Name.Length > 0
                    && string.Equals(Path.GetFullPath(PathFor(backup.Id)), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                    result.Add(backup);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return result.OrderByDescending(b => b.Date).ToList();
    }

    public void Delete(Guid id)
    {
        try { File.Delete(PathFor(id)); }
        catch (DirectoryNotFoundException) { }
    }
}
