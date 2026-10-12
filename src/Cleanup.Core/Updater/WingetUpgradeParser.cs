using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cleanup.Core.Updater;

/// <summary>One row of <c>winget upgrade</c>'s table.</summary>
public sealed record WingetPackage(string Name, string Id, string Version, string Available, string? Source,
    bool RequiresExplicitTargeting = false)
{
    /// <summary>winget shortens long ids with "…" when the table is too narrow.</summary>
    public bool IsIdTruncated => Id.EndsWith('…');
}

/// <summary>
/// Reads the human-readable table printed by <c>winget upgrade --include-unknown</c> (design §5.2, risk §12): column
/// positions come from the header line above the dashes, so localized headers ("Nome  ID  Versão  Disponível
/// Origem") work. Tolerates the progress spinner, ANSI escapes, truncated names and ids ("…"), the "N upgrades
/// available" footer and the "require explicit targeting" second table.
/// </summary>
public static partial class WingetUpgradeParser
{
    public static IReadOnlyList<WingetPackage> Parse(string output)
    {
        var lines = CleanLines(output);
        var packages = new List<WingetPackage>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (!IsDashLine(lines[i])) continue;
            var header = lines[i - 1];
            var columns = ColumnStarts(header);
            if (columns.Count < 4) continue;
            var explicitTargeting = PreviousNonBlank(lines, i - 2) is { } intro && intro.TrimEnd().EndsWith(':');
            var j = i + 1;
            for (; j < lines.Count && lines[j].Trim().Length > 0 && !IsDashLine(lines[j]); j++)
            {
                if (ParseRow(lines[j], columns) is { } row) packages.Add(row with { RequiresExplicitTargeting = explicitTargeting });
            }
            i = j - 1;
        }
        return packages;
    }

    private static string? PreviousNonBlank(List<string> lines, int index)
    {
        for (var k = index; k >= 0; k--)
        {
            if (lines[k].Trim().Length > 0) return lines[k];
        }
        return null;
    }

    /// <summary>
    /// Lines as a terminal would show them: carriage returns overwrite (the spinner), backspaces erase, ANSI escapes
    /// and progress-bar lines are dropped.
    /// </summary>
    internal static List<string> CleanLines(string output)
    {
        var result = new List<string>();
        foreach (var raw in AnsiEscape().Replace(output, "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var cut = line.LastIndexOf('\r');
            if (cut >= 0) line = line[(cut + 1)..];
            if (line.Contains('\b'))
            {
                var sb = new StringBuilder();
                foreach (var c in line)
                {
                    if (c == '\b') { if (sb.Length > 0) sb.Length--; }
                    else sb.Append(c);
                }
                line = sb.ToString();
            }
            if (SpinnerOnly().IsMatch(line) || line.Contains('█') || line.Contains('▒')) continue;
            result.Add(line);
        }
        return result;
    }

    internal static bool IsDashLine(string line)
    {
        var t = line.Trim();
        return t.Length >= 10 && t.All(c => c == '-' || c == '─');
    }

    /// <summary>Display column where each header word starts.</summary>
    internal static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        var column = 0;
        var previousIsSpace = true;
        foreach (var rune in header.EnumerateRunes())
        {
            var isSpace = Rune.IsWhiteSpace(rune);
            if (!isSpace && previousIsSpace) starts.Add(column);
            previousIsSpace = isSpace;
            column += Width(rune);
        }
        return starts;
    }

    private static WingetPackage? ParseRow(string line, List<int> columns)
    {
        var fields = SliceByColumns(line, columns);
        if (fields is not null) return Valid(fields, columns.Count) ? Build(fields, columns.Count) : null;
        // A name wider than its column (or measured differently) shifts everything: read the fixed fields from the right.
        var tokens = FromTheRight(line, columns.Count);
        return tokens is not null && Valid(tokens, columns.Count) && !tokens[^1].EndsWith('.') ? Build(tokens, columns.Count) : null;
    }

    private static WingetPackage Build(IReadOnlyList<string> f, int count) =>
        new(f[0], f[1], f[2], f[3], count >= 5 && f[4].Length > 0 ? f[4] : null);

    private static bool Valid(IReadOnlyList<string> f, int count)
    {
        if (f.Count < 4 || f.Take(4).Any(s => s.Length == 0)) return false;
        if (f[1].Any(char.IsWhiteSpace) || f[3].Any(char.IsWhiteSpace)) return false;
        if (!char.IsLetterOrDigit(f[1][0])) return false;
        if (!f[3].Any(char.IsAsciiDigit)) return false; // a version, not the words of a footer sentence
        if (count >= 5 && (f.Count < 5 || f[4].Length == 0 || f[4].Any(char.IsWhiteSpace))) return false;
        return true;
    }

    /// <summary>Fields cut at the header's display columns, or null when a field doesn't start at its column.</summary>
    private static List<string>? SliceByColumns(string line, List<int> columns)
    {
        var fields = new List<StringBuilder>();
        for (var k = 0; k < columns.Count; k++) fields.Add(new StringBuilder());
        var column = 0;
        var field = 0;
        Rune previous = new(' ');
        foreach (var rune in line.EnumerateRunes())
        {
            while (field + 1 < columns.Count && column >= columns[field + 1])
            {
                // The next column must begin after a space; otherwise this row isn't aligned with the header.
                if (!Rune.IsWhiteSpace(previous) && !Rune.IsWhiteSpace(rune)) return null;
                field++;
            }
            fields[field].Append(rune.ToString());
            previous = rune;
            column += Width(rune);
        }
        return fields.Select(f => f.ToString().Trim()).ToList();
    }

    /// <summary>Source and available are single words, a version may read "&lt; 1.2"; the id is the next word.</summary>
    private static List<string>? FromTheRight(string line, int count)
    {
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        string? Pop()
        {
            if (tokens.Count == 0) return null;
            var t = tokens[^1];
            tokens.RemoveAt(tokens.Count - 1);
            return t;
        }
        string? PopVersion()
        {
            var t = Pop();
            if (t is not null && tokens.Count > 0 && tokens[^1] is "<" or ">") t = Pop() + " " + t;
            return t;
        }
        var source = count >= 5 ? Pop() : "";
        var available = PopVersion();
        var version = PopVersion();
        var id = Pop();
        if (source is null || available is null || version is null || id is null || tokens.Count == 0) return null;
        List<string> fields = [string.Join(' ', tokens), id, version, available];
        if (count >= 5) fields.Add(source);
        return fields;
    }

    /// <summary>Terminal width of a character: East Asian wide and fullwidth characters take two columns.</summary>
    internal static int Width(Rune rune)
    {
        var v = rune.Value;
        if (CharUnicodeInfo.GetUnicodeCategory(v) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        var wide = v is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0x33FF or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF or >= 0xA000 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F900 and <= 0x1F9FF or >= 0x20000 and <= 0x3FFFD;
        return wide ? 2 : 1;
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"^\s*[-\\|/]\s*$")]
    private static partial Regex SpinnerOnly();
}
