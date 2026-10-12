namespace Cleanup.Core.Updater;

/// <summary>
/// Compares app version strings the way people read them: <c>1.10 &gt; 1.9</c>, <c>1.2 == 1.2.0</c>,
/// <c>2.0-beta &lt; 2.0</c>, <c>2.0b3 &lt; 2.0rc1 &lt; 2.0</c>. Mirrors the Swift <c>VersionComparator</c>;
/// both run <c>shared/test-vectors/version-compare.json</c>.
/// </summary>
public static class VersionComparator
{
    internal abstract record Token
    {
        public sealed record Number(long Value) : Token;
        public sealed record Word(string Value) : Token;
    }

    /// <summary>Pre-release words in ascending order. Unknown words compare ordinally.</summary>
    internal static readonly string[] PrereleaseOrder = ["dev", "alpha", "a", "beta", "b", "pre", "preview", "rc"];
    private static readonly HashSet<char> Separators = ['.', '-', '_', '+', ' ', '(', ')'];

    /// <returns>Negative when <paramref name="lhs"/> is older, 0 when the same, positive when newer.</returns>
    public static int Compare(string lhs, string rhs)
    {
        var a = Tokens(SplitBuild(lhs).Version);
        var b = Tokens(SplitBuild(rhs).Version);
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : new Token.Number(0);
            var y = i < b.Count ? b[i] : new Token.Number(0);
            var result = Compare(x, y);
            if (result != 0) return result;
        }
        return 0;
    }

    /// <summary>True when <paramref name="candidate"/> is newer than <paramref name="installed"/>; build numbers break ties.</summary>
    public static bool IsNewer(string candidate, string? candidateBuild, string installed, string? installedBuild)
    {
        var result = Compare(candidate, installed);
        if (result != 0) return result > 0;
        var cb = candidateBuild ?? SplitBuild(candidate).Build;
        var ib = installedBuild ?? SplitBuild(installed).Build;
        if (cb is null || ib is null) return false;
        return Compare(cb, ib) > 0;
    }

    /// <summary>"1.2 (345)" → ("1.2", "345"): a trailing parenthesized part is a build number.</summary>
    internal static (string Version, string? Build) SplitBuild(string version)
    {
        var trimmed = version.Trim(' ');
        var open = trimmed.LastIndexOf('(');
        if (!trimmed.EndsWith(')') || open < 0) return (trimmed, null);
        var baseVersion = trimmed[..open].Trim(' ');
        var build = trimmed[(open + 1)..^1].Trim(' ');
        if (baseVersion.Length == 0) return (trimmed, null);
        return (baseVersion, build.Length == 0 ? null : build);
    }

    internal static List<Token> Tokens(string version)
    {
        var result = new List<Token>();
        var current = new System.Text.StringBuilder();
        bool? currentIsDigit = null;

        void Flush()
        {
            if (current.Length == 0) return;
            var text = current.ToString();
            result.Add(long.TryParse(text, out var n) ? new Token.Number(n) : new Token.Word(text));
            current.Clear();
            currentIsDigit = null;
        }

        foreach (var character in version.ToLowerInvariant())
        {
            if (Separators.Contains(character)) { Flush(); continue; }
            var isDigit = char.IsAsciiDigit(character);
            if (currentIsDigit is { } d && d != isDigit) Flush();
            current.Append(character);
            currentIsDigit = isDigit;
        }
        Flush();
        return result;
    }

    private static int Compare(Token x, Token y) => (x, y) switch
    {
        (Token.Number p, Token.Number q) => p.Value.CompareTo(q.Value),
        (Token.Number, Token.Word) => 1,   // 2.0.1 > 2.0b, 2.0 > 2.0-beta
        (Token.Word, Token.Number) => -1,
        (Token.Word p, Token.Word q) => CompareWords(p.Value, q.Value),
        _ => 0,
    };

    private static int CompareWords(string p, string q)
    {
        if (p == q) return 0;
        var rp = Array.IndexOf(PrereleaseOrder, p);
        var rq = Array.IndexOf(PrereleaseOrder, q);
        if (rp >= 0 && rq >= 0) return rp < rq ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(p, q));
    }
}
