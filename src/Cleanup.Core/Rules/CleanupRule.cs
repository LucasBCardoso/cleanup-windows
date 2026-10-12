using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cleanup.Core.Rules;

[JsonConverter(typeof(JsonStringEnumConverter<ScanCategory>))]
public enum ScanCategory
{
    [JsonStringEnumMemberName("systemJunk")] SystemJunk,
    [JsonStringEnumMemberName("developerJunk")] DeveloperJunk,
    [JsonStringEnumMemberName("trash")] Trash,
    [JsonStringEnumMemberName("largeOldFiles")] LargeOldFiles,
    [JsonStringEnumMemberName("appLeftovers")] AppLeftovers,
}

[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    [JsonStringEnumMemberName("safe")] Safe,
    [JsonStringEnumMemberName("review")] Review,
}

/// <summary>
/// A declarative description of junk, in the same JSON schema as the macOS <c>CleanupRule</c>. Paths are templates:
/// <c>%VAR%</c> expands to the environment, and any component may contain <c>*</c>, <c>?</c> and <c>[...]</c> globs.
/// </summary>
public sealed record CleanupRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ScanCategory Category { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    public required IReadOnlyList<string> Paths { get; init; }
    /// <summary>Glob patterns matched against the last path component of each candidate.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];
    /// <summary>Only report entries not modified for at least this many days.</summary>
    public int? MinAgeDays { get; init; }
    public string Description { get; init; } = "";
    /// <summary>System locations removed by the elevated helper, never moved to the Recycle Bin.</summary>
    public bool RequiresAdmin { get; init; }
}

public sealed class RuleSetException(string message) : Exception(message);

public static class RuleSet
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static IReadOnlyList<CleanupRule> Parse(string json) =>
        JsonSerializer.Deserialize<List<CleanupRule>>(json, JsonOptions)
        ?? throw new RuleSetException("Rules JSON is null");

    /// <summary>
    /// Merges user overrides (same <c>id</c> replaces a built-in). The rules file is writable by anything running as
    /// the user, so it must never choose what the elevated helper deletes (invariant 9).
    /// </summary>
    public static IReadOnlyList<CleanupRule> Merge(IReadOnlyList<CleanupRule> builtIn, IReadOnlyList<CleanupRule> custom)
    {
        Validate(custom);
        if (custom.FirstOrDefault(r => r.RequiresAdmin) is { } admin)
            throw new RuleSetException($"Rule '{admin.Id}' sets requiresAdmin; only built-in rules may remove files as administrator");
        var overridden = custom.Select(r => r.Id).ToHashSet();
        return [.. builtIn.Where(r => !overridden.Contains(r.Id)), .. custom];
    }

    public static void Validate(IEnumerable<CleanupRule> rules)
    {
        var seen = new HashSet<string>();
        foreach (var rule in rules)
        {
            if (!seen.Add(rule.Id)) throw new RuleSetException($"Duplicate rule id '{rule.Id}'");
            if (rule.Paths.Count == 0) throw new RuleSetException($"Rule '{rule.Id}' has no paths");
        }
    }
}
