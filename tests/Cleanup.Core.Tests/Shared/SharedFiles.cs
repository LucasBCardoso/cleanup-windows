using System.Text.Json;

namespace Cleanup.Core.Tests.Shared;

/// <summary>Finds the repo root (the folder with Cleanup.sln) and reads the vendored <c>shared/</c> files.</summary>
internal static class SharedFiles
{
    public static readonly string RepoRoot = FindRepoRoot();
    public static readonly string SharedRoot = Path.Combine(RepoRoot, "shared");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    public static string Read(string relative) => File.ReadAllText(Path.Combine(SharedRoot, relative));

    public static T Load<T>(string relative) =>
        JsonSerializer.Deserialize<T>(Read(relative), Json) ?? throw new InvalidDataException($"{relative} is null");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cleanup.sln"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("Cleanup.sln not found above " + AppContext.BaseDirectory);
    }
}
