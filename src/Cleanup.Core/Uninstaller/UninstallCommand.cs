using System.Text.RegularExpressions;

namespace Cleanup.Core.Uninstaller;

/// <summary>
/// A vendor uninstaller to start: an executable and its argument string, split from the registry's
/// <c>UninstallString</c>. It is started directly (never through <c>cmd /c</c>), so nothing in the string is
/// interpreted by a shell: no <c>&amp;</c>, <c>|</c>, <c>%VAR%</c> or redirection.
/// </summary>
public sealed partial record UninstallCommand(string FileName, string Arguments)
{
    /// <summary>
    /// Splits an <c>UninstallString</c>. Handles quoted paths, unquoted paths with spaces
    /// (<c>C:\Program Files\Foo\uninst.exe /S</c>) and MSI products (<c>MsiExec.exe /I{GUID}</c> becomes <c>/X{GUID}</c>,
    /// which removes instead of opening the "modify" wizard). Null when no executable can be found safely.
    /// </summary>
    public static UninstallCommand? Parse(string? command, string systemRoot)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.Trim();
        if (s.Contains('\0') || s.Contains('\n') || s.Contains('\r')) return null;

        string file, rest;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 0) return null;
            file = s[1..end];
            rest = s[(end + 1)..];
        }
        else
        {
            // The shortest prefix ending in ".exe" followed by a space or the end: "C:\Program Files\A B\u.exe /S".
            var match = UnquotedExe().Match(s);
            if (!match.Success) return null;
            file = match.Groups[1].Value;
            rest = s[match.Length..];
        }
        file = file.Trim();
        rest = rest.Trim();
        if (file.Length == 0 || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (file.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || file.Contains('%')) return null;

        var bare = Path.GetFileName(file);
        // Command interpreters would run whatever the rest of the string says.
        if (Interpreters.Contains(bare, StringComparer.OrdinalIgnoreCase)) return null;

        if (bare.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            var guid = MsiProduct().Match(rest);
            if (!guid.Success) return null;
            return new UninstallCommand(Path.Combine(systemRoot, "System32", "msiexec.exe"), "/X" + guid.Groups[1].Value);
        }
        if (!Path.IsPathFullyQualified(file)) return null;
        return new UninstallCommand(file, rest);
    }

    private static readonly string[] Interpreters = ["cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "bash.exe", "wsl.exe"];

    [GeneratedRegex(@"^(.+?\.exe)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UnquotedExe();

    [GeneratedRegex(@"^/[IXix]\s*(\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})(\s|$)")]
    private static partial Regex MsiProduct();
}
