using System.Text.RegularExpressions;

namespace Cleanup.Core.Updater;

/// <summary>The winget command lines Cleanup uses, and how it reads their exit codes.</summary>
public static partial class WingetCommands
{
    /// <summary>Microsoft Store › Downloads and updates: Store apps update there, one page for all of them.</summary>
    public const string StoreUpdatesUri = "ms-windows-store://downloadsandupdates";
    /// <summary>App Installer, which ships winget (missing on some Windows 10 installs).</summary>
    public const string AppInstallerStoreUri = "ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1";

    /// <summary>Read-only listing. Agreements accepted up front and no prompts, so it never waits for input.</summary>
    public static IReadOnlyList<string> ListArguments { get; } =
        ["upgrade", "--include-unknown", "--accept-source-agreements", "--disable-interactivity"];

    /// <summary>
    /// One package, by exact id, silently. winget downloads the installer from its manifest and verifies its SHA-256
    /// before running it.
    /// </summary>
    public static IReadOnlyList<string> InstallArguments(AvailableUpdate update)
    {
        if (!update.CanInstallWithWinget) throw new ArgumentException($"{update.Name} não pode ser atualizado pelo winget.", nameof(update));
        var args = new List<string> { "upgrade", "--id", update.AppId, "--exact" };
        if (update.SourceName is { } source && IsValidId(source)) args.AddRange(["--source", source]);
        if (IsUnknownVersion(update.CurrentVersion)) args.Add("--include-unknown");
        args.AddRange(["--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"]);
        return args;
    }

    /// <summary>
    /// A package id or source name as winget prints them: letters, digits and <c>. _ - +</c>, starting with a
    /// letter or digit, so it can never be read as an option.
    /// </summary>
    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public static bool IsUnknownVersion(string version) =>
        version.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || version.Equals("Desconhecido", StringComparison.OrdinalIgnoreCase)
        || version.Equals("Desconhecida", StringComparison.OrdinalIgnoreCase);

    /// <summary>A failed install's exit code in words (winget's APPINSTALLER_CLI_ERROR_* HRESULTs).</summary>
    public static string DescribeExitCode(int exitCode, string output)
    {
        var known = unchecked((uint)exitCode) switch
        {
            0x8A150011 => "o arquivo baixado não confere com o hash do manifesto; o winget não o instalou",
            0x8A150014 => "o pacote não foi encontrado nas fontes do winget",
            0x8A15002B => "nenhuma atualização aplicável a este app",
            0x8A150006 => "o instalador do app falhou",
            0x8A150101 => "o app está aberto; feche-o e tente de novo",
            0x8A150102 => "outra instalação está em andamento; tente de novo depois",
            0x8A150109 => "reinicie o computador para concluir a atualização",
            _ => null,
        };
        if (known is not null) return known;
        var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.Any(c => c is '█' or '▒'));
        var code = $"o winget terminou com o código 0x{unchecked((uint)exitCode):X8}";
        return last is null ? code : $"{code}: {last}";
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,199}$")]
    private static partial Regex IdPattern();
}

/// <summary>winget as an update source: <c>winget upgrade --include-unknown</c>, parsed.</summary>
public sealed class WingetSource(IToolRunner runner, string wingetPath) : IUpdateSource
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public UpdateSourceKind Kind => UpdateSourceKind.Winget;

    public async Task<IReadOnlyList<UpdateCheckOutcome>> UpdatesAsync(CancellationToken cancellationToken = default)
    {
        ToolResult result;
        try { result = await runner.RunAsync(wingetPath, WingetCommands.ListArguments, Timeout, cancellationToken); }
        catch (ToolNotFoundException e) { return [Failure(e.Message)]; }
        if (result.TimedOut) return [Failure("o winget não respondeu em 3 minutos")];
        var packages = WingetUpgradeParser.Parse(result.Output);
        if (packages.Count == 0 && result.ExitCode != 0 && !LooksEmpty(result.Output))
            return [Failure(WingetCommands.DescribeExitCode(result.ExitCode, result.Output + "\n" + result.Error))];
        return ToUpdates(packages).Select(u => (UpdateCheckOutcome)new UpdateCheckOutcome.Available(u)).ToList();
    }

    /// <summary>"No installed package found" / "Nenhum pacote instalado…": a normal, empty answer.</summary>
    private static bool LooksEmpty(string output) =>
        output.Contains("No installed package", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Nenhum pacote", StringComparison.OrdinalIgnoreCase)
        || output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Nenhuma atualização", StringComparison.OrdinalIgnoreCase);

    private static UpdateCheckOutcome Failure(string message) =>
        new UpdateCheckOutcome.Failed(new UpdateFailure("winget", "winget", message));

    /// <summary>Rows to updates. <c>msstore</c> rows are Store apps; two rows with one id are two copies.</summary>
    public static IReadOnlyList<AvailableUpdate> ToUpdates(IReadOnlyList<WingetPackage> packages)
    {
        var copies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var updates = new List<AvailableUpdate>();
        foreach (var p in packages)
        {
            var store = string.Equals(p.Source, "msstore", StringComparison.OrdinalIgnoreCase);
            var key = (store ? "s:" : "w:") + p.Id;
            var copy = copies.TryGetValue(key, out var n) ? n + 1 : 0;
            copies[key] = copy;
            updates.Add(new AvailableUpdate
            {
                AppId = p.Id,
                Name = p.Name,
                Source = store ? UpdateSourceKind.MicrosoftStore : UpdateSourceKind.Winget,
                CurrentVersion = p.Version,
                NewVersion = p.Available,
                SourceName = p.Source,
                RequiresExplicitTargeting = p.RequiresExplicitTargeting,
                IsIdTruncated = p.IsIdTruncated,
                Copy = copy,
            });
        }
        return updates;
    }
}

public sealed record InstallResult(bool Succeeded, string? Error)
{
    public static InstallResult Ok { get; } = new(true, null);
    public static InstallResult Fail(string error) => new(false, error);
}

/// <summary>
/// Installs one update with <c>winget upgrade --id … --exact --silent</c>. Installs never overlap: a second call
/// waits for the first (installers fight over the Windows Installer mutex and the UAC prompt).
/// </summary>
public sealed class WingetInstaller(IToolRunner runner, string wingetPath)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    public async Task<InstallResult> InstallAsync(AvailableUpdate update, CancellationToken cancellationToken = default)
    {
        if (update.Source == UpdateSourceKind.MicrosoftStore) return InstallResult.Fail("apps da Microsoft Store são atualizados pela Loja");
        if (update.IsIdTruncated) return InstallResult.Fail("o winget mostrou o id incompleto; atualize pelo Terminal com winget upgrade");
        if (!update.CanInstallWithWinget) return InstallResult.Fail("id de pacote inválido");
        var arguments = WingetCommands.InstallArguments(update);
        await oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var result = await runner.RunAsync(wingetPath, arguments, Timeout, cancellationToken);
            if (result.TimedOut) return InstallResult.Fail("a instalação passou de 30 minutos e foi interrompida");
            return result.ExitCode == 0 ? InstallResult.Ok : InstallResult.Fail(WingetCommands.DescribeExitCode(result.ExitCode, result.Output));
        }
        catch (ToolNotFoundException e) { return InstallResult.Fail(e.Message); }
        finally { oneAtATime.Release(); }
    }
}
