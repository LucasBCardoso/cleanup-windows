using System.Runtime.InteropServices;
using System.Text;
using Cleanup.Core.Scanning;
using Microsoft.Win32;

namespace Cleanup.Platform.Windows;

/// <summary>
/// What the Recycle Bin of a drive accepts, from the user's settings
/// (<c>HKCU\…\Explorer\BitBucket\Volume\{GUID}</c>: <c>NukeOnDelete</c>, <c>MaxCapacity</c> in MB). Used so unattended
/// cleaning never hits the shell's "too big for the Recycle Bin, delete permanently?" dialog.
/// </summary>
public static class RecycleBinLimits
{
    /// <summary>Null when the item can go to the Recycle Bin, else why not.</summary>
    public static string? WhyNot(string path)
    {
        var (nuke, maxBytes) = For(path);
        if (nuke) return "a Lixeira desta unidade apaga na hora; não limpo sem você";
        if (maxBytes is { } max)
        {
            var size = FileMetrics.Size(path);
            // A margin: the quota also holds what is already in the Recycle Bin.
            if (size > max * 0.8) return "grande demais para a Lixeira; limpe manualmente";
        }
        return null;
    }

    internal static (bool Nuke, long? MaxBytes) For(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null) return (false, null);
            var volume = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(root.EndsWith('\\') ? root : root + "\\", volume, volume.Capacity)) return (false, null);
            var guid = volume.ToString();
            var start = guid.IndexOf('{');
            var end = guid.IndexOf('}');
            if (start < 0 || end < start) return (false, null);
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid[start..(end + 1)]}");
            if (key is null) return (false, null);
            var nuke = key.GetValue("NukeOnDelete") is int n && n != 0;
            long? max = key.GetValue("MaxCapacity") is int mb && mb > 0 ? mb * 1024L * 1024L : null;
            return (nuke, max);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return (false, null);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
}
