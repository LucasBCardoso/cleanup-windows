using System.Globalization;
using System.Text;

namespace Cleanup.Core.Uninstaller;

/// <summary>
/// Which registry keys an uninstall or reset may delete: only an app's own key under
/// <c>HKEY_CURRENT_USER\Software</c> (<c>Software\Vendor\Product</c> or <c>Software\Product</c>), never a shared one
/// (<c>Microsoft</c>, <c>Classes</c>, <c>Policies</c>…). Re-checked right before every delete (invariant 2 for the registry).
/// </summary>
public static class RegistryPolicy
{
    public const string UserSoftware = @"HKEY_CURRENT_USER\Software";

    /// <summary>Top-level keys under <c>HKCU\Software</c> that belong to Windows or to many programs.</summary>
    internal static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "AppDataLow", "Windows",
        "Intel", "NVIDIA Corporation", "AMD", "Realtek", "Google", "Apple Inc.", "Apple Computer, Inc.", "ODBC", "Cleanup",
        "Mozilla", "MozillaPlugins", "Adobe", "Oracle", "JavaSoft", "Python", "Khronos", "Valve", "Chromium", "Wine",
    };

    public static bool IsAllowed(string key, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\0') || key.Contains('/'))
        {
            reason = "chave inválida";
            return false;
        }
        if (!key.StartsWith(UserSoftware + "\\", StringComparison.OrdinalIgnoreCase))
        {
            reason = "fora de HKEY_CURRENT_USER\\Software";
            return false;
        }
        var parts = key[(UserSoftware.Length + 1)..].Split('\\');
        if (parts.Any(p => p.Length == 0 || p is "." or ".."))
        {
            reason = "chave inválida";
            return false;
        }
        // A protected vendor key itself is never deleted; a product below it ("Software\Mozilla\Firefox") may be.
        if (parts.Length == 1 && Protected.Contains(parts[0]))
        {
            reason = "chave compartilhada do Windows ou de vários programas";
            return false;
        }
        if (parts[0].Equals("Microsoft", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Classes", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("Policies", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase))
        {
            reason = "chave do Windows";
            return false;
        }
        return true;
    }

    public static bool IsAllowed(string key) => IsAllowed(key, out _);
}

/// <summary>A key to export as a <c>.reg</c> file: its values and subkeys, as read from the registry.</summary>
public sealed record RegNode(string Path, IReadOnlyList<RegValue> Values, IReadOnlyList<RegNode> Children);

public enum RegKind { String, ExpandString, MultiString, DWord, QWord, Binary, None, Other }

/// <summary>A value: its name ("" for the default value), kind and raw data (as stored in the registry).</summary>
public sealed record RegValue(string Name, RegKind Kind, byte[] Data, int RawType = 0);

/// <summary>
/// Writes <c>.reg</c> files in regedit's format ("Windows Registry Editor Version 5.00", UTF-16), so a deleted key is
/// put back by double-clicking its backup (design §6: export before deleting any key).
/// </summary>
public static class RegExport
{
    public const string Header = "Windows Registry Editor Version 5.00";

    public static string Format(RegNode root)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        Append(sb, root);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, RegNode node)
    {
        sb.Append("\r\n[").Append(node.Path).Append("]\r\n");
        foreach (var value in node.Values)
        {
            sb.Append(value.Name.Length == 0 ? "@" : Quote(value.Name)).Append('=').Append(Data(value)).Append("\r\n");
        }
        foreach (var child in node.Children) Append(sb, child);
    }

    internal static string Data(RegValue value) => value.Kind switch
    {
        RegKind.String when IsPlainString(value.Data, out var text) => Quote(text),
        RegKind.String => Hex("hex(1)", value.Data),
        RegKind.DWord when value.Data.Length == 4 => "dword:" + BitConverter.ToUInt32(value.Data).ToString("x8", CultureInfo.InvariantCulture),
        RegKind.ExpandString => Hex("hex(2)", value.Data),
        RegKind.MultiString => Hex("hex(7)", value.Data),
        RegKind.QWord => Hex("hex(b)", value.Data),
        RegKind.Binary => Hex("hex", value.Data),
        RegKind.None => Hex("hex(0)", value.Data),
        RegKind.DWord => Hex("hex(4)", value.Data),
        _ => Hex($"hex({value.RawType:x})", value.Data),
    };

    /// <summary>A REG_SZ written as text when it is exactly one NUL-terminated UTF-16 string.</summary>
    private static bool IsPlainString(byte[] data, out string text)
    {
        text = "";
        if (data.Length % 2 != 0) return false;
        var s = Encoding.Unicode.GetString(data);
        if (s.EndsWith('\0')) s = s[..^1];
        if (s.Contains('\0') || s.Contains('\r') || s.Contains('\n')) return false;
        text = s;
        return true;
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Hex(string prefix, byte[] data) =>
        prefix + ":" + string.Join(',', data.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
}
