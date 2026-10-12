using System.Text.Json;
using System.Text.Json.Serialization;
using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Updater;

/// <summary>One skipped update. A null <see cref="Version"/> ignores every version of the app.</summary>
public sealed record IgnoredUpdate(
    [property: JsonPropertyName("id")] string AppId,
    [property: JsonPropertyName("version")] string? Version)
{
    public bool Equals(IgnoredUpdate? other) =>
        other is not null && string.Equals(AppId, other.AppId, StringComparison.OrdinalIgnoreCase) && Version == other.Version;

    public override int GetHashCode() => HashCode.Combine(AppId.ToLowerInvariant(), Version);
}

/// <summary>
/// Updates the user chose to skip, persisted as JSON in <c>%LOCALAPPDATA%\Cleanup\ignored-updates.json</c>.
/// Value type, like <see cref="IgnoreList"/>. Mirrors the Swift <c>IgnoredUpdates</c>.
/// </summary>
public sealed class IgnoredUpdates : IEquatable<IgnoredUpdates>
{
    public IReadOnlySet<IgnoredUpdate> Entries { get; }

    public IgnoredUpdates() : this([]) { }

    public IgnoredUpdates(IEnumerable<IgnoredUpdate> entries) => Entries = entries.ToHashSet();

    public static string DefaultPath => Path.Combine(AppPaths.SupportDirectory, "ignored-updates.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static IgnoredUpdates Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new IgnoredUpdates();
        try
        {
            var entries = JsonSerializer.Deserialize<List<IgnoredUpdate>>(File.ReadAllText(path), Json)
                ?? throw new JsonException("vazio");
            if (entries.Any(e => string.IsNullOrWhiteSpace(e.AppId))) throw new JsonException("entrada sem id");
            return new IgnoredUpdates(entries);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new IgnoreListException($"A lista de atualizações ignoradas em {path} não pôde ser lida: {e.Message}", e);
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sorted = Entries.OrderBy(e => e.AppId, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Version ?? "", StringComparer.Ordinal).ToList();
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(sorted, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IgnoreListException($"A lista de atualizações ignoradas não pôde ser salva em {path}: {e.Message}", e);
        }
    }

    public IgnoredUpdates Adding(string appId, string? version) => new(Entries.Append(new IgnoredUpdate(appId, version)));

    public IgnoredUpdates Removing(IgnoredUpdate entry) => new(Entries.Where(e => !e.Equals(entry)));

    public bool IsIgnored(AvailableUpdate update) =>
        Entries.Any(e => string.Equals(e.AppId, update.AppId, StringComparison.OrdinalIgnoreCase)
                         && (e.Version is null || e.Version == update.NewVersion));

    public bool Equals(IgnoredUpdates? other) => other is not null && Entries.SetEquals(other.Entries);
    public override bool Equals(object? obj) => Equals(obj as IgnoredUpdates);
    public override int GetHashCode() => Entries.Count;
}
