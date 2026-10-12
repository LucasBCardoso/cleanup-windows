using System.Text.Json;
using Cleanup.Core.Models;

namespace Cleanup.Core.Developer;

public sealed record ToolResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs a program with a fixed argument list (never through a shell). Faked in tests.</summary>
public interface IToolRunner
{
    ToolResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout);
}

public sealed class DeveloperToolException(string message) : Exception(message)
{
    public static DeveloperToolException DockerNotRunning() =>
        new("O Docker não está em execução. Abra o Docker Desktop para ver o uso de espaço.");
}

public sealed record DockerUsage(string Type, string Size, string Reclaimable, int Count);

public static class DockerParser
{
    /// <summary><c>docker system df --format "{{json .}}"</c>: one JSON object per line.</summary>
    public static IReadOnlyList<DockerUsage> Parse(string text)
    {
        var result = new List<DockerUsage>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Type", out var type) || type.GetString() is not { } typeName)
                    continue;
                string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "?" : "?";
                result.Add(new DockerUsage(typeName, Text("Size"), Text("Reclaimable"), int.TryParse(Text("TotalCount"), out var n) ? n : 0));
            }
            catch (JsonException) { }
        }
        return result;
    }
}

/// <summary>A WSL (or Docker Desktop) virtual disk. Report only: Cleanup never touches these.</summary>
public sealed record WslDisk(string Name, string Path, long Size);

/// <summary>
/// Docker housekeeping through the <c>docker</c> CLI. Every command is a fixed argument list (no shell). Volumes are
/// never pruned: they hold data.
/// </summary>
public sealed class DeveloperTools(IToolRunner tools, string? dockerPath)
{
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PruneTimeout = TimeSpan.FromMinutes(10);

    public string? DockerPath => dockerPath;

    /// <summary>docker.exe on PATH, else Docker Desktop's own copy.</summary>
    public static string? DetectDocker(ScanEnvironment environment, Func<string, bool> fileExists)
    {
        var candidates = new List<string>();
        foreach (var dir in (environment.Variables.GetValueOrDefault("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Path.IsPathFullyQualified(dir)) candidates.Add(Path.Combine(dir, "docker.exe"));
        }
        foreach (var programFiles in (string[])["ProgramW6432", "ProgramFiles"])
        {
            if (environment.Variable(programFiles) is { } pf) candidates.Add(Path.Combine(pf, @"Docker\Docker\resources\bin\docker.exe"));
        }
        return candidates.FirstOrDefault(fileExists);
    }

    public IReadOnlyList<DockerUsage> DockerUsage()
    {
        if (dockerPath is null) return [];
        var result = tools.Run(dockerPath, ["system", "df", "--format", "{{json .}}"], QueryTimeout);
        if (!result.Succeeded)
        {
            var message = result.Stderr.Trim();
            var lower = message.ToLowerInvariant();
            // Windows: "error during connect: … open //./pipe/docker_engine: The system cannot find the file specified."
            if (lower.Contains("cannot connect") || lower.Contains("failed to connect") || lower.Contains("daemon running")
                || lower.Contains("error during connect") || lower.Contains("docker_engine"))
                throw DeveloperToolException.DockerNotRunning();
            throw new DeveloperToolException(message.Length > 0 ? message : "docker system df falhou");
        }
        return DockerParser.Parse(result.Stdout);
    }

    public void PruneDockerBuildCache() => Run(["builder", "prune", "-f"]);

    /// <summary>Dangling images only (<c>-a</c> would also remove images that are merely unused).</summary>
    public void PruneDanglingImages() => Run(["image", "prune", "-f"]);

    private void Run(IReadOnlyList<string> arguments)
    {
        if (dockerPath is null) return;
        var result = tools.Run(dockerPath, arguments, PruneTimeout);
        if (!result.Succeeded)
        {
            var message = result.Stderr.Trim();
            throw new DeveloperToolException(message.Length > 0 ? message : $"docker {string.Join(" ", arguments)} falhou");
        }
    }

    /// <summary>
    /// Virtual disks to report: each registered WSL distribution's <c>ext4.vhdx</c> (from the registry, read by the
    /// platform layer) plus Docker Desktop's data disks.
    /// </summary>
    public static IReadOnlyList<WslDisk> WslDisks(ScanEnvironment environment, IEnumerable<(string Name, string BasePath)> distributions)
    {
        var disks = new List<WslDisk>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && seen.Add(info.FullName)) disks.Add(new WslDisk(name, info.FullName, info.Length));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        foreach (var (name, basePath) in distributions)
        {
            if (Path.IsPathFullyQualified(basePath)) Add(name, Path.Combine(basePath, "ext4.vhdx"));
        }
        var docker = Path.Combine(environment.LocalAppData, "Docker", "wsl");
        Add("Docker Desktop (dados)", Path.Combine(docker, "disk", "docker_data.vhdx"));
        Add("Docker Desktop (dados)", Path.Combine(docker, "data", "ext4.vhdx"));
        Add("Docker Desktop", Path.Combine(docker, "main", "ext4.vhdx"));
        return disks.OrderByDescending(d => d.Size).ToList();
    }
}
