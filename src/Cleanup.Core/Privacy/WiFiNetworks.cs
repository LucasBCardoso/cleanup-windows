using System.Text;
using Cleanup.Core.Admin;

namespace Cleanup.Core.Privacy;

/// <summary>Saved Wi-Fi networks: the profile names, and the names of the network in use (never offered, never removed).</summary>
public sealed record WiFiState(IReadOnlyList<string> Networks, IReadOnlySet<string> Connected);

/// <summary>
/// Saved Wi-Fi networks through <c>netsh wlan</c>. Listing needs no elevation; removal runs in the elevated helper
/// (<see cref="Elevation.Ops.WifiDelete"/>), one UAC prompt for the whole selection. The profile name is always passed
/// as its own argument (an argument array, never a shell string), and the helper re-checks that it isn't the network
/// in use. The parsers are pure and work on any display language: labels are localized, the layout isn't.
/// </summary>
public static class WiFiNetworks
{
    public static readonly IReadOnlyList<string> ShowProfilesArguments = ["wlan", "show", "profiles"];
    public static readonly IReadOnlyList<string> ShowInterfacesArguments = ["wlan", "show", "interfaces"];

    /// <summary>The helper's answer for a network it kept because the PC is using it.</summary>
    public const string InUseMessage = "rede em uso, mantida";

    /// <summary>netsh's label for the profile line of <c>show interfaces</c>, per display language.</summary>
    private static readonly HashSet<string> ProfileLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Profile", "Perfil", "Profil", "Profilo", "Profiel", "Профиль",
    };

    /// <summary>
    /// Profile names from <c>netsh wlan show profiles</c>: the indented <c>label : name</c> lines (e.g.
    /// "    All User Profile     : Café 5G"). Labels never contain a colon, so the name is everything after the first
    /// one, without the single space netsh puts after it.
    /// </summary>
    public static IReadOnlyList<string> Profiles(string output)
    {
        var names = new List<string>();
        foreach (var line in Lines(output))
        {
            if (line.Length == 0 || !char.IsWhiteSpace(line[0])) continue;
            if (Field(line) is not { } field || field.Value.Length == 0) continue;
            if (!names.Contains(field.Value, StringComparer.Ordinal)) names.Add(field.Value);
        }
        return names;
    }

    /// <summary>
    /// The SSIDs and profile names of connected interfaces, from <c>netsh wlan show interfaces</c>. Only the exact
    /// "SSID" label counts (not "AP BSSID"); a disconnected interface has no SSID line.
    /// </summary>
    public static IReadOnlySet<string> Connected(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            if (Field(line) is not { } field || field.Value.Length == 0) continue;
            if (field.Label == "SSID" || ProfileLabels.Contains(field.Label)) names.Add(field.Value);
        }
        return names;
    }

    private static IEnumerable<string> Lines(string output) => output.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd('\r'));

    private static (string Label, string Value)? Field(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0) return null;
        var label = line[..colon].Trim();
        if (label.Length == 0) return null;
        var value = line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        return (label, value);
    }

    /// <summary>
    /// Why a name is never removed, or null. Control characters can only be a trick; <c>*</c> and <c>?</c> are netsh
    /// wildcards (<c>name=*</c> deletes every profile); a double quote or a trailing backslash could change how the
    /// command line is split.
    /// </summary>
    public static string? Refusal(string name)
    {
        if (name.Length == 0 || string.IsNullOrWhiteSpace(name)) return "nome vazio";
        if (name.Any(c => char.IsControl(c))) return "nome com caracteres de controle";
        if (name.IndexOfAny(['*', '?']) >= 0) return "nome com curinga (* ou ?)";
        if (name.Contains('"') || name.EndsWith('\\')) return "nome com aspas ou barra invertida no fim";
        return null;
    }

    /// <summary>The arguments of <c>netsh wlan delete profile name=…</c>: the name stays inside one argument.</summary>
    public static IReadOnlyList<string> DeleteArguments(string name) => ["wlan", "delete", "profile", "name=" + name];

    public sealed record RemovalPlan(IReadOnlyList<Elevation.Step> Steps, IReadOnlyList<string> Names, IReadOnlyList<string> Skipped);

    /// <summary>One helper step per network; the network in use and refused names are skipped (kept).</summary>
    public static RemovalPlan Plan(IEnumerable<string> names, IReadOnlySet<string> connected)
    {
        var steps = new List<Elevation.Step>();
        var planned = new List<string>();
        var skipped = new List<string>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (connected.Contains(name) || Refusal(name) is not null) { skipped.Add(name); continue; }
            steps.Add(new Elevation.Step(Elevation.Ops.WifiDelete, Id: name));
            planned.Add(name);
        }
        return new RemovalPlan(steps, planned, skipped);
    }

    /// <summary>
    /// netsh writes UTF-8 or the console's OEM code page depending on the setup: strict UTF-8 first, else
    /// <paramref name="fallback"/>.
    /// </summary>
    public static string Decode(byte[] bytes, Encoding fallback)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return fallback.GetString(bytes); }
    }
}
