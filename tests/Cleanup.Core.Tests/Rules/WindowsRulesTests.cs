using Cleanup.Core.Rules;
using Cleanup.Core.Tests.Shared;

namespace Cleanup.Core.Tests.Rules;

/// <summary><c>shared/rules/windows.json</c> is the built-in Smart Scan rule set (design §5.1), in the macOS schema.</summary>
public class WindowsRulesTests
{
    private static IReadOnlyList<CleanupRule> Load() => RuleSet.Parse(SharedFiles.Read("rules/windows.json"));

    [Fact]
    public void ParsesAndValidates()
    {
        var rules = Load();
        Assert.NotEmpty(rules);
        RuleSet.Validate(rules);
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name), r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.Description), r.Id);
        });
    }

    [Fact]
    public void CoversEveryRuleInTheDesign()
    {
        string[] expected =
        [
            "user-temp", "crash-dumps", "wer-user", "thumbnail-cache", "browser-caches", "chromium-app-caches",
            "shader-caches", "old-installers", "recycle-bin", "windows-update-cache", "delivery-optimization",
            "wer-system", "windows-temp", "system-logs", "windows-old",
        ];
        Assert.Equal(expected.Order(), Load().Select(r => r.Id).Order());
    }

    [Fact]
    public void OnlySystemLocationsRequireAdmin()
    {
        var admin = Load().Where(r => r.RequiresAdmin).Select(r => r.Id).Order();
        Assert.Equal(["delivery-optimization", "system-logs", "wer-system", "windows-old", "windows-temp", "windows-update-cache"], admin);
    }

    [Fact]
    public void UserRuleCannotRequireAdmin()
    {
        var custom = RuleSet.Parse("""
            [{ "id": "x", "name": "X", "category": "systemJunk", "paths": ["C:\\Windows\\*"], "requiresAdmin": true }]
            """);
        var error = Assert.Throws<RuleSetException>(() => RuleSet.Merge(Load(), custom));
        Assert.Contains("requiresAdmin", error.Message);
    }

    [Fact]
    public void UserRuleReplacesBuiltInWithSameId()
    {
        var custom = RuleSet.Parse("""
            [{ "id": "user-temp", "name": "Temp", "category": "systemJunk", "risk": "review", "paths": ["%TEMP%\\*"] }]
            """);
        var merged = RuleSet.Merge(Load(), custom);
        var rule = Assert.Single(merged, r => r.Id == "user-temp");
        Assert.Equal(RiskLevel.Review, rule.Risk);
        Assert.Equal(Load().Count, merged.Count);
    }

    [Fact]
    public void DuplicateIdsAndEmptyPathsAreRejected()
    {
        Assert.Throws<RuleSetException>(() => RuleSet.Validate(RuleSet.Parse("""
            [{ "id": "a", "name": "A", "category": "trash", "paths": ["x"] },
             { "id": "a", "name": "A", "category": "trash", "paths": ["y"] }]
            """)));
        Assert.Throws<RuleSetException>(() => RuleSet.Validate(RuleSet.Parse("""
            [{ "id": "a", "name": "A", "category": "trash", "paths": [] }]
            """)));
    }
}
