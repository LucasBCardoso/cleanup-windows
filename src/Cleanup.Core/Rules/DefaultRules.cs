using System.Reflection;
using Cleanup.Core.Models;

namespace Cleanup.Core.Rules;

/// <summary>The built-in Smart Scan rules: <c>shared/rules/windows.json</c>, embedded at build time.</summary>
public static class DefaultRules
{
    /// <summary>Rules whose paths are only representative; a dedicated task scans them (or none yet).</summary>
    public static readonly IReadOnlySet<string> CodeBasedRuleIds = new HashSet<string>
    {
        "recycle-bin",          // RecycleBinTask: SHQueryRecycleBin / SHEmptyRecycleBin
        "chromium-app-caches",  // ChromiumCacheTask: proven by folder structure
        "windows-old",          // removed through DISM / Storage Sense, never deleted directly
    };

    private static readonly Lazy<IReadOnlyList<CleanupRule>> Rules = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Cleanup.Core.rules.windows.json")
                           ?? throw new InvalidOperationException("Embedded rules missing");
        using var reader = new StreamReader(stream);
        var rules = RuleSet.Parse(reader.ReadToEnd());
        RuleSet.Validate(rules);
        return rules;
    });

    public static IReadOnlyList<CleanupRule> All => Rules.Value;

    public static string OverridesPath => Path.Combine(AppPaths.SupportDirectory, "rules.json");

    /// <summary>Built-in rules merged with <c>%LOCALAPPDATA%\Cleanup\rules.json</c> when it exists.</summary>
    public static IReadOnlyList<CleanupRule> Load(string? overridesPath = null)
    {
        overridesPath ??= OverridesPath;
        if (!File.Exists(overridesPath)) return All;
        string json;
        try { json = File.ReadAllText(overridesPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new RuleSetException($"Não foi possível ler as regras em {overridesPath}: {e.Message}");
        }
        IReadOnlyList<CleanupRule> custom;
        try { custom = RuleSet.Parse(json); }
        catch (System.Text.Json.JsonException e) { throw new RuleSetException($"Regras inválidas em {overridesPath}: {e.Message}"); }
        return RuleSet.Merge(All, custom);
    }
}
