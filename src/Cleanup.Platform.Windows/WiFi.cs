using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cleanup.Core.Admin;
using Cleanup.Core.Privacy;

namespace Cleanup.Platform.Windows;

/// <summary>Runs <c>%SystemRoot%\System32\netsh.exe</c> with an argument array (never through a shell).</summary>
public static class Netsh
{
    public static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

    static Netsh() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The console's OEM code page (850 on a Brazilian Windows), netsh's output when it isn't UTF-8.</summary>
    public static Encoding OemEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { return Encoding.Latin1; }
        }
    }

    public static (int ExitCode, string Output) Run(IReadOnlyList<string> arguments, TimeSpan? timeout = null) =>
        Run(Path, arguments, timeout);

    /// <summary>Starts <paramref name="file"/> with each argument passed as its own <c>ArgumentList</c> entry.</summary>
    public static (int ExitCode, string Output) Run(string file, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{file} não iniciou");
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromSeconds(20)))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{System.IO.Path.GetFileName(file)} não respondeu");
        }
        copy.Wait();
        _ = error.Result;
        return (process.ExitCode, WiFiNetworks.Decode(output.ToArray(), OemEncoding));
    }
}

public static class WiFi
{
    /// <summary>Saved networks without the one in use; null without Wi-Fi (or when netsh fails). Needs no elevation.</summary>
    public static WiFiState? Load()
    {
        try
        {
            var (code, profilesText) = Netsh.Run(WiFiNetworks.ShowProfilesArguments);
            if (code != 0) return null;
            var profiles = WiFiNetworks.Profiles(profilesText);
            if (profiles.Count == 0) return null;
            var (_, interfacesText) = Netsh.Run(WiFiNetworks.ShowInterfacesArguments);
            var connected = WiFiNetworks.Connected(interfacesText);
            return new WiFiState(profiles.Where(p => !connected.Contains(p)).ToList(), connected);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException or IOException)
        {
            return null;
        }
    }

    public sealed record Outcome(IReadOnlyList<string> Removed, IReadOnlyList<string> Kept, IReadOnlyList<string> Failed, bool Cancelled);

    /// <summary>
    /// Forgets the networks in one elevated session (one UAC prompt). The network in use and refused names are kept
    /// without asking; an empty plan never shows the prompt; a cancelled prompt removes nothing.
    /// </summary>
    public static async Task<Outcome> ForgetAsync(IReadOnlyList<string> names, IReadOnlySet<string> connected)
    {
        var plan = WiFiNetworks.Plan(names, connected);
        var kept = plan.Skipped.ToList();
        if (plan.Steps.Count == 0) return new Outcome([], kept, [], false);
        var outcome = await ElevatedRunner.RunAsync(plan.Steps);
        if (outcome.Cancelled) return new Outcome([], kept, [], true);
        if (outcome.Error is { } error) return new Outcome([], kept, plan.Names.Select(n => $"{n}: {error}").ToList(), false);
        var removed = new List<string>();
        var failed = new List<string>();
        for (var i = 0; i < plan.Names.Count; i++)
        {
            var result = outcome.Results[i];
            if (result.Ok) removed.Add(plan.Names[i]);
            else if (result.Message == WiFiNetworks.InUseMessage) kept.Add(plan.Names[i]);
            else failed.Add($"{plan.Names[i]}: {result.Message}");
        }
        return new Outcome(removed, kept, failed, false);
    }
}
