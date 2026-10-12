using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Cleanup.Core.Updater;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Runs a program with <see cref="ProcessStartInfo.ArgumentList"/> (never a shell string), no window, output read as
/// UTF-8. On timeout or cancellation the whole process tree is killed.
/// </summary>
public sealed class ProcessToolRunner : IToolRunner
{
    public async Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["NO_COLOR"] = "1"; // no ANSI colors from tools that honour it

        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Win32Exception e) { throw new ToolNotFoundException($"{Path.GetFileName(executable)} não pôde ser iniciado: {e.Message}", e); }
        process.StandardInput.Close(); // nothing to answer: a prompt fails instead of waiting forever

        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            return new ToolResult(-1, await SafeAsync(output), await SafeAsync(error), TimedOut: true);
        }
        return new ToolResult(process.ExitCode, await output, await error);
    }

    private static async Task<string> SafeAsync(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is TimeoutException or IOException or ObjectDisposedException) { return ""; }
    }
}

/// <summary>Finds winget.exe: the App Installer execution alias, else the PATH. Missing on some Windows 10 installs.</summary>
public static class WingetLocator
{
    public static string? Find()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), "winget.exe");
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
