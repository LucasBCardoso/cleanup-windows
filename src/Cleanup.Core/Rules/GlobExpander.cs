using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Cleanup.Core.Rules;

/// <summary>
/// Expands path templates whose components may contain <c>*</c>, <c>?</c> and <c>[...]</c> wildcards, one component
/// at a time so only folders that exist are listed. Never steps through a junction or symlink (invariant 3); a
/// matched entry may itself be one, and is then removed as a link.
/// </summary>
public static class GlobExpander
{
    public sealed record Result(IReadOnlyList<string> Matches, IReadOnlyList<string> Unreadable);

    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public static bool HasWildcard(string component) => component.IndexOfAny(['*', '?', '[']) >= 0;

    /// <summary>fnmatch-style match of a single name, ignoring case as NTFS does.</summary>
    public static bool Matches(string name, string pattern) =>
        Cache.GetOrAdd(pattern, p => new Regex(ToRegex(p), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).IsMatch(name);

    private static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                case '[':
                    var close = pattern.IndexOf(']', i + 2);
                    if (close < 0) { sb.Append(@"\["); break; }
                    var body = pattern[(i + 1)..close];
                    var negate = body.StartsWith('!');
                    if (negate) body = body[1..];
                    sb.Append('[').Append(negate ? "^" : "").Append(body.Replace(@"\", @"\\").Replace("[", @"\[")).Append(']');
                    i = close;
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.Append('$').ToString();
    }

    /// <summary>
    /// Existing paths matching an absolute pattern such as <c>C:\Users\me\AppData\Local\Temp\*</c>. Entries whose name
    /// starts with a dot are only matched when the pattern component does too.
    /// </summary>
    public static Result Expand(string pattern)
    {
        var normalized = pattern.Replace('/', '\\');
        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(normalized) || HasWildcard(root))
            return new([], []);
        var components = normalized[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        List<string> current = [root];
        var unreadable = new List<string>();
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component is "." or "..") return new([], unreadable);
            var next = new List<string>();
            foreach (var basePath in current)
            {
                // Every folder walked must be a real folder, never a link (invariants 3 and 13).
                if (!IsRealDirectory(basePath)) continue;
                Step(basePath, component, next, unreadable);
            }
            current = next;
            if (current.Count == 0) break;
        }
        current.Sort(StringComparer.OrdinalIgnoreCase);
        return new(current, unreadable);
    }

    internal static bool IsRealDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Directory)) return false;
            // A drive root is reported as a directory; other reparse points are links or mount points.
            return !attributes.HasFlag(FileAttributes.ReparsePoint) || Path.GetPathRoot(path) == path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Step(string basePath, string component, List<string> into, List<string> unreadable)
    {
        if (!HasWildcard(component))
        {
            var path = Path.Combine(basePath, component);
            if (File.Exists(path) || Directory.Exists(path)) into.Add(path);
            return;
        }
        IEnumerable<string> names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(basePath, "*", new EnumerationOptions
            {
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                MatchType = MatchType.Win32,
            }).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            unreadable.Add(basePath);
            return;
        }
        var includeDot = component.StartsWith('.');
        foreach (var name in names)
        {
            if (!includeDot && name.StartsWith('.')) continue;
            if (Matches(name, component)) into.Add(Path.Combine(basePath, name));
        }
    }
}
