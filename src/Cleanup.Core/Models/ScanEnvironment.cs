using System.Text.RegularExpressions;

namespace Cleanup.Core.Models;

/// <summary>
/// Everything a scan needs to know about the machine: the profile folder and the environment variables rule paths
/// use (<c>%TEMP%</c>, <c>%LOCALAPPDATA%</c>…). Injected so tests can point the whole engine at a temporary folder.
/// </summary>
public sealed partial class ScanEnvironment
{
    public string Home { get; }
    public IReadOnlyDictionary<string, string> Variables { get; }

    public ScanEnvironment(string home, IReadOnlyDictionary<string, string> variables)
    {
        Home = PathUtil.Normalize(home);
        Variables = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
    }

    public static ScanEnvironment Current
    {
        get
        {
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                if (e.Key is string k && e.Value is string v && v.Length > 0) variables[k] = v;
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            variables.TryAdd("USERPROFILE", home);
            variables.TryAdd("LOCALAPPDATA", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            variables.TryAdd("APPDATA", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            variables.TryAdd("PROGRAMDATA", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            variables.TryAdd("SystemRoot", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            variables["TEMP"] = Path.GetTempPath();
            return new ScanEnvironment(home, variables);
        }
    }

    public string? Variable(string name) => Variables.TryGetValue(name, out var v) && v.Length > 0 ? PathUtil.Normalize(v) : null;

    public string LocalAppData => Variable("LOCALAPPDATA") ?? Path.Combine(Home, "AppData", "Local");
    public string RoamingAppData => Variable("APPDATA") ?? Path.Combine(Home, "AppData", "Roaming");
    public string SystemRoot => Variable("SystemRoot") ?? @"C:\Windows";
    public string ProgramData => Variable("PROGRAMDATA") ?? @"C:\ProgramData";
    public string Temp => Variable("TEMP") ?? Path.Combine(LocalAppData, "Temp");

    /// <summary>
    /// Expands <c>%VAR%</c> and a leading <c>~</c>. Returns null when a variable is unknown, so a rule never scans a
    /// half-expanded path like <c>%FOO%\*</c> relative to the current folder.
    /// </summary>
    public string? Expand(string template)
    {
        if (template == "~") return Home;
        if (template.StartsWith(@"~\", StringComparison.Ordinal)) template = Home + template[1..];
        var unknown = false;
        var result = VariablePattern().Replace(template, m =>
        {
            var value = Variable(m.Groups[1].Value);
            if (value is null) { unknown = true; return m.Value; }
            return value;
        });
        return unknown ? null : result;
    }

    [GeneratedRegex("%([A-Za-z0-9_()]+)%")]
    private static partial Regex VariablePattern();
}

/// <summary>Where Cleanup keeps its own state: <c>%LOCALAPPDATA%\Cleanup</c>.</summary>
public static class AppPaths
{
    public static string SupportDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cleanup");
}

public static class PathUtil
{
    /// <summary>Full path with <c>\</c> separators, no trailing separator except on a drive root.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Replace('/', '\\'));
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\') : full;
    }

    /// <summary>Absolute, and free of <c>.</c> / <c>..</c> components (history entries are user-writable).</summary>
    public static bool IsNormalizedAbsolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        return !path.Split('\\', '/').Any(c => c is "." or "..");
    }

    public static bool SameOrdinalIgnoreCase(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
