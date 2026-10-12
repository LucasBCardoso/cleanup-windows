using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Cleanup.Core.Uninstaller;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Cleanup.Platform.Windows.Apps;

/// <summary>
/// <c>HKEY_CURRENT_USER</c> for the Desinstalador: listing keys, <c>.reg</c> exports (raw value types and bytes, so the
/// backup re-imports exactly) and deleting a key tree, re-checked against <see cref="RegistryPolicy"/> every time.
/// </summary>
public sealed class WindowsRegistry : IRegistryBrowser, IRegistryStore
{
    private const string HkcuPrefix = "HKEY_CURRENT_USER\\";

    /// <summary>"HKEY_CURRENT_USER\Software\X" → "Software\X"; anything outside HKCU is refused.</summary>
    internal static string Relative(string key)
    {
        if (!key.StartsWith(HkcuPrefix, StringComparison.OrdinalIgnoreCase) || key.Length == HkcuPrefix.Length)
            throw new ArgumentException("só chaves de HKEY_CURRENT_USER", nameof(key));
        return key[HkcuPrefix.Length..];
    }

    public IReadOnlyList<string> SubKeyNames(string key)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Relative(key));
            return k?.GetSubKeyNames() ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return []; }
    }

    public long ApproximateSize(string key)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Relative(key));
            if (k is null) return 0;
            var budget = 5000;
            return Measure(k, ref budget);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return 0; }
    }

    private static long Measure(RegistryKey key, ref int budget)
    {
        long total = 0;
        foreach (var value in ReadValues(key))
        {
            total += (value.Name.Length * 2) + value.Data.Length;
            if (--budget <= 0) return total;
        }
        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is not null) total += (name.Length * 2) + Measure(child, ref budget);
            if (budget <= 0) break;
        }
        return total;
    }

    public bool Exists(string key)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Relative(key));
            return k is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return true; }
    }

    public void Export(string key, string file)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Relative(key)) ?? throw new IOException("a chave não existe mais");
        var node = Read(k, key);
        File.WriteAllText(file, RegExport.Format(node), Encoding.Unicode); // UTF-16 LE with BOM, like regedit
    }

    public void DeleteTree(string key)
    {
        if (!RegistryPolicy.IsAllowed(key, out var reason)) throw new UnauthorizedAccessException("bloqueado pela política de segurança: " + reason);
        Registry.CurrentUser.DeleteSubKeyTree(Relative(key), throwOnMissingSubKey: false);
    }

    internal static RegNode Read(RegistryKey key, string path)
    {
        var children = new List<RegNode>();
        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is not null) children.Add(Read(child, path + "\\" + name));
        }
        return new RegNode(path, ReadValues(key), children);
    }

    /// <summary>Every value with its raw type and bytes (RegQueryValueEx), including types .NET can't name.</summary>
    internal static List<RegValue> ReadValues(RegistryKey key)
    {
        var values = new List<RegValue>();
        foreach (var name in key.GetValueNames())
        {
            var size = 0;
            var status = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out var type, null, ref size);
            if (status != 0) continue;
            var data = new byte[size];
            status = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out type, data, ref size);
            if (status != 0) throw new Win32Exception(status);
            if (size < data.Length) Array.Resize(ref data, size);
            var kind = type switch
            {
                0 => RegKind.None,
                1 => RegKind.String,
                2 => RegKind.ExpandString,
                3 => RegKind.Binary,
                4 => RegKind.DWord,
                7 => RegKind.MultiString,
                11 => RegKind.QWord,
                _ => RegKind.Other,
            };
            values.Add(new RegValue(name, kind, data, type));
        }
        return values;
    }

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string? valueName, IntPtr reserved, out int type, byte[]? data, ref int dataSize);
}
