using System.Text;
using Cleanup.Core.Privacy;
using Cleanup.Platform.Windows;

namespace Cleanup.Platform.Tests;

public sealed class WiFiTests
{
    /// <summary>
    /// The SSID is user-controlled (any nearby network name): it must reach netsh literally, as one argument. The real
    /// tool is swapped for a script that echoes the argv it received (parsed by the same Windows rules netsh's C
    /// runtime uses), so the test checks only what the process start passes along. Nothing touches Wi-Fi.
    /// </summary>
    [Fact]
    public void HostileSSIDsStayLiteral()
    {
        string[] hostile =
        [
            "it's", "$(touch pwned)", "`id`", "a b;c", "x & del /q C:\\", "%PATH%", "name=Casa", "a\\b", "a\\ b\\",
            "x\" name=\"Casa", "\"", "Café 5G ", "  ", "| more", "^&^", "-Command evil",
        ];
        var script = Path.Combine(Path.GetTempPath(), "cleanup-argv-" + Guid.NewGuid().ToString("N")[..8] + ".ps1");
        File.WriteAllText(script,
            "[Environment]::GetCommandLineArgs() | Select-Object -Skip 7 | ForEach-Object { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_)) }");
        try
        {
            foreach (var ssid in hostile)
            {
                var expected = WiFiNetworks.DeleteArguments(ssid);
                var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                var (code, output) = Netsh.Run(powershell,
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, .. expected], TimeSpan.FromSeconds(60));
                Assert.Equal(0, code);
                var received = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(b => Encoding.UTF8.GetString(Convert.FromBase64String(b))).ToList();
                Assert.Equal(expected, received);
            }
        }
        finally { File.Delete(script); }
    }

    [Fact]
    public void ListingWithoutWiFiHardwareIsNull()
    {
        // Read-only: `netsh wlan show profiles`. On a PC without Wi-Fi this is null; with Wi-Fi it lists names.
        var state = WiFi.Load();
        if (state is not null) Assert.All(state.Networks, n => Assert.DoesNotContain(n, state.Connected));
    }

    [Fact]
    public async Task AnEmptyPlanNeverAsksForElevation()
    {
        // Only the network in use and refused names: nothing to run, so the helper (and UAC) is never started.
        var outcome = await WiFi.ForgetAsync(["Casa", "*"], new HashSet<string> { "Casa" });
        Assert.False(outcome.Cancelled);
        Assert.Equal(["Casa", "*"], outcome.Kept);
        Assert.Empty(outcome.Removed);
        Assert.Empty(outcome.Failed);
    }
}
