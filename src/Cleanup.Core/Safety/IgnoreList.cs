using System.Text.Json;
using Cleanup.Core.Models;

namespace Cleanup.Core.Safety;

public sealed class IgnoreListException(string message, Exception inner) : Exception(message, inner);

/// <summary>Paths the user never wants touched, persisted as a JSON array of paths. Value type, like the Swift one.</summary>
public sealed record IgnoreList(IReadOnlySet<string> Paths)
{
    public IgnoreList() : this(new HashSet<string>(StringComparer.OrdinalIgnoreCase)) { }

    public static string DefaultPath => Path.Combine(AppPaths.SupportDirectory, "ignore.json");

    public static IgnoreList Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new IgnoreList();
        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
            // Every entry must be a usable absolute path; one bad entry makes the whole list unreadable (fail closed).
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)) throw new JsonException($"entrada inválida: \"{p}\"");
                _ = PathUtil.Normalize(p);
            }
            return new IgnoreList(new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new IgnoreListException($"A lista de ignorados em {path} não pôde ser lida: {e.Message}", e);
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Paths.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IgnoreListException($"A lista de ignorados não pôde ser salva em {path}: {e.Message}", e);
        }
    }

    public IgnoreList Adding(string path) =>
        new(new HashSet<string>(Paths, StringComparer.OrdinalIgnoreCase) { PathUtil.Normalize(path) });

    public IgnoreList Removing(string path)
    {
        var set = new HashSet<string>(Paths, StringComparer.OrdinalIgnoreCase);
        set.Remove(PathUtil.Normalize(path));
        return new(set);
    }
}

public static class AtomicFile
{
    /// <summary>Writes to a temporary file next to <paramref name="path"/>, then replaces it in one rename.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, contents);
        try { File.Move(temp, path, overwrite: true); }
        catch { File.Delete(temp); throw; }
    }
}
