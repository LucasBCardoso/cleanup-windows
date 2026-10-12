using Cleanup.Core.Models;
using Cleanup.Core.Safety;

namespace Cleanup.Core.Admin;

/// <summary>
/// What the elevated helper may delete (invariant 4): only direct children of a fixed set of system folders, named
/// here in code and never taken from rules or any user-writable file (invariant 9). Re-checked by the helper itself.
/// </summary>
public sealed class AdminPathPolicy
{
    /// <summary>Folder → optional file-name pattern its direct children must match.</summary>
    public IReadOnlyList<(string Root, string? Pattern)> Roots { get; }

    /// <summary><c>Logs\&lt;component&gt;\*.log.old</c>: rotated logs one level down.</summary>
    private readonly string logs;

    public AdminPathPolicy(string systemRoot, string programData)
    {
        systemRoot = PathUtil.Normalize(systemRoot);
        programData = PathUtil.Normalize(programData);
        logs = Path.Combine(systemRoot, "Logs");
        Roots =
        [
            (Path.Combine(systemRoot, "Temp"), null),
            (Path.Combine(systemRoot, @"SoftwareDistribution\Download"), null),
            (Path.Combine(systemRoot, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), null),
            (Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive"), null),
            (Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue"), null),
            (Path.Combine(systemRoot, @"Logs\CBS"), "*.cab"),
        ];
    }

    public static AdminPathPolicy For(ScanEnvironment environment) => new(environment.SystemRoot, environment.ProgramData);

    /// <summary>Null when allowed, else why not.</summary>
    public string? Reject(string path)
    {
        if (!PathUtil.IsNormalizedAbsolute(path)) return "caminho não normalizado";
        string normalized;
        try { normalized = PathUtil.Normalize(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "caminho inválido"; }
        if (!string.Equals(normalized, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "caminho não normalizado";
        var parent = Path.GetDirectoryName(normalized);
        var name = Path.GetFileName(normalized);
        if (parent is null || name.Length == 0) return "não é um item";
        foreach (var (root, pattern) in Roots)
        {
            if (!PathUtil.SameOrdinalIgnoreCase(parent, root)) continue;
            if (pattern is not null && !Rules.GlobExpander.Matches(name, pattern)) return $"só {pattern} em {root}";
            return null;
        }
        // Rotated logs: <SystemRoot>\Logs\<component>\<name>.log.old (files only).
        if (Path.GetDirectoryName(parent) is { } grand && PathUtil.SameOrdinalIgnoreCase(grand, logs)
            && name.EndsWith(".log.old", StringComparison.OrdinalIgnoreCase))
            return null;
        return "fora das pastas de sistema permitidas";
    }
}

/// <summary>The elevated helper's protocol: one request (a list of steps), one response (a result per step).</summary>
public static class Elevation
{
    public static class Ops
    {
        public const string Remove = "remove";
        public const string FlushDns = "flush-dns";
        public const string Run = "run";
    }

    /// <summary>
    /// <see cref="Ops.Run"/> only takes one of these maintenance ids; the helper maps each to a fixed command line.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedMaintenance = new HashSet<string>
    {
        "dism-cleanup", "sfc", "reset-search", "free-ram",
    };

    public sealed record Step(string Op, string? Path = null, string? Id = null);
    public sealed record Request(IReadOnlyList<Step> Steps);
    public sealed record StepResult(bool Ok, string Message);
    public sealed record Response(IReadOnlyList<StepResult> Results);

    public static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
}
