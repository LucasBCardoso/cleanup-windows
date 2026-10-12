using System.Globalization;

namespace Cleanup.Cli;

public sealed class UsageException(string message) : Exception(message);

public abstract record Command
{
    public sealed record Help : Command;
    public sealed record Rules : Command;
    public sealed record Scan(bool IncludeReview) : Command;
    public sealed record Clean(IReadOnlyList<string> RuleIds, bool IncludeReview, bool Confirm, bool Permanent, bool Admin) : Command;
    public sealed record Large(long MinimumSize, int? OlderThanDays) : Command;
    public sealed record Lens(string? Path) : Command;
    public sealed record IgnoreList : Command;
    public sealed record IgnoreAdd(string Path) : Command;
    public sealed record IgnoreRemove(string Path) : Command;
    public sealed record History : Command;
    public sealed record Undo(string? Id, bool Confirm) : Command;
    public sealed record Disk : Command;
}

/// <summary>Parses arguments into a <see cref="Command"/>; kept apart from execution so it is easy to test.</summary>
public static class CommandLine
{
    public const string Usage = """
        cleanup — clean junk from Windows safely (dry run unless --yes)

          scan [--include-review]            what can be cleaned (read-only)
          clean [--rule ID]… [--include-review] [--yes] [--permanent] [--admin]
                                             move safe junk to the Recycle Bin (--admin: system junk, one UAC prompt)
          large [--min-size 500MB] [--older-than DAYS]
          lens [PATH]                        what is using space, folder by folder
          disk                               used and free space per drive
          ignore [list | add PATH | remove PATH]
          history                            past cleanups and what can still come back
          undo [ID] [--yes]                  put the last (or given) cleanup back
          rules                              built-in and custom rules
        """;

    public static Command Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new Command.Help();
        var rest = args.Skip(1).ToList();
        return args[0].ToLowerInvariant() switch
        {
            "help" or "--help" or "-h" => new Command.Help(),
            "rules" => new Command.Rules(),
            "scan" => new Command.Scan(TakeFlag(rest, "--include-review")).Done(rest),
            "clean" => ParseClean(rest),
            "large" => ParseLarge(rest),
            "lens" => new Command.Lens(rest.Count > 0 ? rest[0] : null),
            "disk" => new Command.Disk(),
            "ignore" => ParseIgnore(rest),
            "history" => new Command.History(),
            "undo" => ParseUndo(rest),
            _ => throw new UsageException($"unknown command '{args[0]}'"),
        };
    }

    private static T Done<T>(this T command, List<string> rest) where T : Command =>
        rest.Count == 0 ? command : throw new UsageException($"unexpected argument '{rest[0]}'");

    private static bool TakeFlag(List<string> rest, string flag) => rest.RemoveAll(a => a == flag) > 0;

    private static List<string> TakeValues(List<string> rest, string option)
    {
        var values = new List<string>();
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i] != option) continue;
            if (i + 1 >= rest.Count) throw new UsageException($"{option} needs a value");
            values.Add(rest[i + 1]);
            rest.RemoveRange(i, 2);
            i--;
        }
        return values;
    }

    private static Command ParseClean(List<string> rest)
    {
        var rules = TakeValues(rest, "--rule");
        return new Command.Clean(rules, TakeFlag(rest, "--include-review"), TakeFlag(rest, "--yes"), TakeFlag(rest, "--permanent"),
            TakeFlag(rest, "--admin")).Done(rest);
    }

    private static Command ParseLarge(List<string> rest)
    {
        var size = TakeValues(rest, "--min-size").LastOrDefault();
        var older = TakeValues(rest, "--older-than").LastOrDefault();
        int? days = older is null ? null : int.TryParse(older, out var d) && d >= 0 ? d : throw new UsageException("--older-than takes days");
        return new Command.Large(size is null ? Core.Scanning.LargeOldFilesTask.DefaultMinimumSize : ParseSize(size), days).Done(rest);
    }

    private static Command ParseIgnore(List<string> rest)
    {
        if (rest.Count == 0 || rest[0] == "list") return new Command.IgnoreList();
        if (rest.Count == 2 && rest[0] == "add") return new Command.IgnoreAdd(rest[1]);
        if (rest.Count == 2 && rest[0] == "remove") return new Command.IgnoreRemove(rest[1]);
        throw new UsageException("usage: cleanup ignore [list | add PATH | remove PATH]");
    }

    private static Command ParseUndo(List<string> rest)
    {
        var confirm = TakeFlag(rest, "--yes");
        var id = rest.Count > 0 ? rest[0] : null;
        if (rest.Count > 1) throw new UsageException($"unexpected argument '{rest[1]}'");
        return new Command.Undo(id, confirm);
    }

    /// <summary>"500MB", "1.5GB", "200K", "1024" (bytes). Decimal units, like the macOS CLI.</summary>
    public static long ParseSize(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        (string Suffix, long Factor)[] units = [("TB", 1_000_000_000_000), ("GB", 1_000_000_000), ("MB", 1_000_000), ("KB", 1_000),
            ("T", 1_000_000_000_000), ("G", 1_000_000_000), ("M", 1_000_000), ("K", 1_000), ("B", 1)];
        foreach (var (suffix, factor) in units)
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(t[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0)
                return (long)(v * factor);
        }
        return long.TryParse(t, out var bytes) && bytes >= 0 ? bytes : throw new UsageException($"invalid size '{text}'");
    }
}
