using System.Diagnostics;
using System.Text;
using Cleanup.Core.Developer;
using Microsoft.Win32;

namespace Cleanup.Platform.Windows;

/// <summary>Runs a tool with an argument array (no shell, no window) and a timeout after which it is killed.</summary>
public sealed class ProcessToolRunner : IToolRunner
{
    public ToolResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new DeveloperToolException($"{Path.GetFileName(executable)} não pôde ser iniciado");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new DeveloperToolException($"{Path.GetFileName(executable)} não respondeu em {(int)timeout.TotalSeconds} s; o Cleanup parou de esperar.");
        }
        process.WaitForExit();
        return new ToolResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}

/// <summary>WSL distributions registered for this user (<c>HKCU\…\Lxss</c>): name and folder of their <c>ext4.vhdx</c>.</summary>
public static class WslRegistry
{
    public static IReadOnlyList<(string Name, string BasePath)> Distributions()
    {
        var result = new List<(string, string)>();
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null) return result;
            foreach (var id in lxss.GetSubKeyNames())
            {
                using var key = lxss.OpenSubKey(id);
                if (key?.GetValue("DistributionName") is string name && key.GetValue("BasePath") is string basePath)
                {
                    // Store distributions use the \\?\ prefix.
                    result.Add((name, basePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? basePath[4..] : basePath));
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return result;
    }
}
