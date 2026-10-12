namespace Cleanup.Core.Updater;

/// <summary>Result of running a command-line tool. Output is decoded as UTF-8.</summary>
public sealed record ToolResult(int ExitCode, string Output, string Error, bool TimedOut = false);

/// <summary>The tool was not found or could not be started.</summary>
public sealed class ToolNotFoundException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs a program with an argument array, never a shell string, so no package id or file name can break the
/// quoting (invariant 4). Faked in tests; <c>ProcessToolRunner</c> in Cleanup.Platform.Windows is the real one.
/// </summary>
public interface IToolRunner
{
    Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
