using System.Xml.Linq;

namespace Cleanup.Core.Optimization;

/// <summary>Where a startup entry lives (design §5.2): the Windows counterpart of launchd's agent/daemon folders.</summary>
public enum StartupKind
{
    /// <summary>A value under <c>…\CurrentVersion\Run</c>.</summary>
    Run,
    /// <summary>A value under <c>…\CurrentVersion\RunOnce</c> (runs at the next logon, then Windows deletes it).</summary>
    RunOnce,
    /// <summary>A file in a Startup folder (usually a <c>.lnk</c>).</summary>
    StartupFolder,
    /// <summary>A Task Scheduler task with a logon or boot trigger. Report only.</summary>
    ScheduledTask,
    /// <summary>A third-party service set to start automatically. Report only, never deleted.</summary>
    Service,
}

public enum StartupScope
{
    /// <summary>The user's own (<c>HKCU</c>, the user's Startup folder): can be changed without administrator rights.</summary>
    CurrentUser,
    /// <summary>Every user's (<c>HKLM</c>, the common Startup folder, tasks, services): shown read-only.</summary>
    AllUsers,
}

/// <summary>One startup entry. Mirrors the Swift <c>LaunchItem</c>.</summary>
public sealed record StartupItem
{
    /// <summary>Value name, file name without <c>.lnk</c>, task name or service name.</summary>
    public required string Name { get; init; }
    public required StartupKind Kind { get; init; }
    public required StartupScope Scope { get; init; }
    /// <summary>
    /// Registry entries: the key path relative to its hive (<c>Software\Microsoft\Windows\CurrentVersion\Run</c>).
    /// Startup folder: the file's full path. Task: its path (<c>\Vendor\Task</c>). Service: its key name.
    /// </summary>
    public required string Location { get; init; }
    /// <summary>The registry value name, or the file name in the Startup folder (what <c>StartupApproved</c> keys on).</summary>
    public string? ValueName { get; init; }
    /// <summary>The command line as registered.</summary>
    public string? Command { get; init; }
    /// <summary>The executable the entry starts (resolved from the command or the shortcut), when known.</summary>
    public string? Program { get; init; }
    public bool IsDisabled { get; init; }
    /// <summary>Why the entry can't run: its program is missing, or the shortcut can't be read.</summary>
    public string? BrokenReason { get; init; }
    /// <summary>The program's file description or company (from its version resource), when it exists.</summary>
    public string? Publisher { get; init; }
    /// <summary>A <c>WOW6432Node</c> (32-bit) registry entry.</summary>
    public bool Is32Bit { get; init; }

    public string Id => $"{Kind}|{Scope}|{(Is32Bit ? "32" : "64")}|{Location}|{ValueName}".ToLowerInvariant();
    public bool IsBroken => BrokenReason is not null;

    /// <summary>Only the user's own Run/RunOnce values and Startup-folder files can be changed without administrator.</summary>
    public bool IsManageable => Scope == StartupScope.CurrentUser && Kind is StartupKind.Run or StartupKind.RunOnce or StartupKind.StartupFolder;

    /// <summary>Enable/disable goes through <c>StartupApproved</c>, which has no entry for RunOnce.</summary>
    public bool CanToggle => IsManageable && Kind != StartupKind.RunOnce;

    /// <summary>Where it is, for the row's subtitle ("HKCU\…\Run", "Pasta Inicializar"…).</summary>
    public string SourceLabel => Kind switch
    {
        StartupKind.Run or StartupKind.RunOnce => (Scope == StartupScope.CurrentUser ? @"HKCU\" : @"HKLM\") + Location,
        StartupKind.StartupFolder => Scope == StartupScope.CurrentUser ? "Pasta Inicializar" : "Pasta Inicializar (todos os usuários)",
        StartupKind.ScheduledTask => "Tarefa agendada " + Location,
        _ => "Serviço " + Location,
    };
}

/// <summary>The groups the list shows, in order (macOS: user agents, global agents, global daemons).</summary>
public enum StartupGroup { Mine, AllUsers, Tasks, Services }

public static class StartupGroups
{
    public static StartupGroup Of(StartupItem item) => item.Kind switch
    {
        StartupKind.ScheduledTask => StartupGroup.Tasks,
        StartupKind.Service => StartupGroup.Services,
        _ => item.Scope == StartupScope.CurrentUser ? StartupGroup.Mine : StartupGroup.AllUsers,
    };

    public static string Title(this StartupGroup group) => group switch
    {
        StartupGroup.Mine => "Seus itens de inicialização",
        StartupGroup.AllUsers => "Todos os usuários",
        StartupGroup.Tasks => "Tarefas agendadas ao iniciar",
        _ => "Serviços automáticos",
    };

    public static bool IsManageable(this StartupGroup group) => group == StartupGroup.Mine;
}

/// <summary>
/// The <c>Explorer\StartupApproved</c> values Task Manager uses to enable and disable startup entries: binary, the
/// first byte even (02, 06) = enabled, odd (03, 07) = disabled; a disabled entry also stores when, as a FILETIME.
/// </summary>
public static class StartupApproval
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string Run32Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    public const string StartupFolderKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    /// <summary>No value (never touched) or anything unreadable counts as enabled, as in Task Manager.</summary>
    public static bool IsDisabled(byte[]? data) => data is { Length: > 0 } && (data[0] & 1) == 1;

    public static byte[] Enabled() => [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] Disabled(DateTime utcNow)
    {
        var data = new byte[12];
        data[0] = 0x03;
        BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }
}

/// <summary>Finds the executable in a registered command line, the way <c>CreateProcess</c> would.</summary>
public static class StartupCommand
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat", ".cmd", ".scr", ".pif"];

    /// <summary>
    /// <c>"C:\Program Files\X\x.exe" --flag</c> → <c>C:\Program Files\X\x.exe</c>. Unquoted paths with spaces are tried
    /// prefix by prefix (an existing file wins, else the first prefix ending in an executable extension).
    /// <c>%VAR%</c> is expanded with <paramref name="expand"/>. <c>rundll32</c> commands name the DLL they load.
    /// </summary>
    public static string? Program(string? command, Func<string, bool> fileExists, Func<string, string>? expand = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = (expand ?? (s => s))(command.Trim());
        string program;
        string rest;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            program = end < 0 ? text[1..] : text[1..end];
            rest = end < 0 ? "" : text[(end + 1)..].TrimStart();
        }
        else
        {
            var words = text.Split(' ');
            program = words[0];
            rest = string.Join(' ', words.Skip(1));
            string? firstWithExtension = null;
            for (var i = 1; i <= words.Length; i++)
            {
                var candidate = string.Join(' ', words.Take(i));
                if (Path.IsPathFullyQualified(candidate) && fileExists(candidate))
                {
                    program = candidate;
                    rest = string.Join(' ', words.Skip(i));
                    firstWithExtension = null;
                    break;
                }
                if (firstWithExtension is null && HasExecutableExtension(candidate))
                {
                    firstWithExtension = candidate;
                    rest = string.Join(' ', words.Skip(i));
                }
            }
            if (firstWithExtension is not null) program = firstWithExtension;
        }
        program = program.Trim();
        if (program.Length == 0) return null;

        // rundll32 <dll>,<entry>: the DLL is what can go missing.
        if (string.Equals(Path.GetFileNameWithoutExtension(program), "rundll32", StringComparison.OrdinalIgnoreCase) && rest.Length > 0)
        {
            var argument = rest.StartsWith('"') ? rest[1..].Split('"')[0] : rest.Split(',')[0].Split(' ')[0];
            argument = argument.Split(',')[0].Trim();
            if (Path.IsPathFullyQualified(argument)) return argument;
        }
        return program;
    }

    private static bool HasExecutableExtension(string path) =>
        ExecutableExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "programa não encontrado: …" when <paramref name="program"/> is a full local path that doesn't exist. A relative
    /// program (found through PATH) isn't judged, and neither is one on a drive that may just be unplugged or offline
    /// (<paramref name="isUnverifiableDrive"/>: removable, network, missing drive letter).
    /// </summary>
    public static string? BrokenReason(string? program, Func<string, bool> fileExists, Func<string, bool> isUnverifiableDrive)
    {
        if (program is null || !Path.IsPathFullyQualified(program)) return null;
        if (program.StartsWith(@"\\", StringComparison.Ordinal) || isUnverifiableDrive(program)) return null;
        return fileExists(program) ? null : MissingPrefix + program;
    }

    public const string MissingPrefix = "programa não encontrado: ";
}

/// <summary>One task from <c>schtasks /query /xml ONE</c>.</summary>
public sealed record ScheduledTaskEntry(string Path, string? Command, string? Arguments, bool Enabled, bool AtLogon, bool AtBoot, string? Author);

/// <summary>Parses <c>schtasks /query /xml ONE</c>: one <c>&lt;Tasks&gt;</c> document, each task preceded by a comment with its path.</summary>
public static class ScheduledTaskParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static IReadOnlyList<ScheduledTaskEntry> Parse(string xml)
    {
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return []; }
        var result = new List<ScheduledTaskEntry>();
        string? pendingPath = null;
        foreach (var node in document.Root?.Nodes() ?? [])
        {
            if (node is XComment comment) { pendingPath = comment.Value.Trim(); continue; }
            if (node is not XElement task || task.Name.LocalName != "Task") continue;
            var path = pendingPath ?? Text(task, "RegistrationInfo", "URI") ?? "";
            pendingPath = null;
            var triggers = task.Element(Ns + "Triggers")?.Elements().ToList() ?? [];
            bool Has(string name) => triggers.Any(t => t.Name.LocalName == name
                && !string.Equals(t.Element(Ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase));
            var exec = task.Element(Ns + "Actions")?.Elements(Ns + "Exec").FirstOrDefault();
            var enabled = !string.Equals(Text(task, "Settings", "Enabled"), "false", StringComparison.OrdinalIgnoreCase);
            result.Add(new ScheduledTaskEntry(path, exec?.Element(Ns + "Command")?.Value.Trim(), exec?.Element(Ns + "Arguments")?.Value.Trim(),
                enabled, Has("LogonTrigger"), Has("BootTrigger"), Text(task, "RegistrationInfo", "Author")));
        }
        return result;
    }

    /// <summary>What the startup list shows: third-party tasks that run at logon or boot (<c>\Microsoft\…</c> is Windows itself).</summary>
    public static IEnumerable<ScheduledTaskEntry> StartupTasks(IEnumerable<ScheduledTaskEntry> tasks) =>
        tasks.Where(t => (t.AtLogon || t.AtBoot) && !t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase));

    private static string? Text(XElement task, string parent, string child) => task.Element(Ns + parent)?.Element(Ns + child)?.Value.Trim();
}
