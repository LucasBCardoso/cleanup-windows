using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Moves an item to the Recycle Bin the way the shell does internally, without the shell's cost: a same-volume rename
/// into <c>&lt;drive&gt;\$Recycle.Bin\&lt;SID&gt;\$R&lt;id&gt;&lt;ext&gt;</c> next to a version-2 <c>$I&lt;id&gt;&lt;ext&gt;</c> file
/// (original path, size, deletion time) — what Explorer reads to list and restore it. The rename is atomic: a folder
/// with a file another program holds open stays where it is, whole.
/// </summary>
public static class FastRecycle
{
    public abstract record Result;
    public sealed record Moved(string ItemPath) : Result;
    public sealed record Failed(string Reason) : Result;
    /// <summary>Not for the fast path (no Recycle Bin folder on that drive, network or removable drive): use the shell.</summary>
    public sealed record UseShell : Result;

    private const string IdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static Result TryMove(string path, long? knownSize)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return new UseShell(); }
        var root = Path.GetPathRoot(full);
        if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal)) return new UseShell();
        try
        {
            if (new DriveInfo(root).DriveType != DriveType.Fixed) return new UseShell();
        }
        catch (Exception e) when (e is ArgumentException or IOException) { return new UseShell(); }

        var folder = RecycleBin.FolderFor(root);
        if (!RecycleBin.IsOwnRecycleBinFolder(folder)) return new UseShell(); // missing or not a plain folder

        FileAttributes attributes;
        try { attributes = File.GetAttributes(full); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new Failed("não existe mais"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new UseShell(); }

        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        // The shell keeps the extension on the $R/$I names (files only), so Explorer shows the right icon.
        var extension = isDirectory ? "" : Path.GetExtension(full);
        if (extension.Length > 16) extension = "";
        var size = knownSize ?? (isDirectory ? 0 : new FileInfo(full).Length);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = RandomId();
            var info = Path.Combine(folder, "$I" + id + extension);
            var item = Path.Combine(folder, "$R" + id + extension);
            if (File.Exists(info) || File.Exists(item) || Directory.Exists(item)) continue;
            try
            {
                using (var stream = new FileStream(info, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(InfoBytes(full, size, DateTime.UtcNow));
                }
            }
            catch (IOException) when (File.Exists(info)) { continue; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new UseShell(); }

            // Rename only, never copy: MOVEFILE_COPY_ALLOWED is left out, so it can't turn into a slow cross-drive copy.
            if (Native.MoveFileEx(@"\\?\" + full, @"\\?\" + item, 0))
                return new Moved(item);

            var error = Marshal.GetLastWin32Error();
            try { File.Delete(info); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (error is 80 or 183) continue; // the $R name appeared meanwhile: try another id
            return error switch
            {
                2 or 3 => new Failed("não existe mais"),
                5 => new Failed("acesso negado ou em uso por outro programa"),
                32 or 33 => new Failed("em uso por outro programa"),
                17 => new UseShell(), // ERROR_NOT_SAME_DEVICE (a mount point inside the path)
                _ => new Failed(new Win32Exception(error).Message),
            };
        }
        return new UseShell();
    }

    /// <summary>The $I file, version 2 (Windows 10+): version, size, deletion FILETIME, name length (with the null), name.</summary>
    public static byte[] InfoBytes(string originalPath, long size, DateTime deletedUtc)
    {
        var name = Encoding.Unicode.GetBytes(originalPath + "\0");
        var bytes = new byte[28 + name.Length];
        BitConverter.GetBytes(2L).CopyTo(bytes, 0);
        BitConverter.GetBytes(size).CopyTo(bytes, 8);
        BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()).CopyTo(bytes, 16);
        BitConverter.GetBytes(originalPath.Length + 1).CopyTo(bytes, 24);
        name.CopyTo(bytes, 28);
        return bytes;
    }

    private static string RandomId()
    {
        Span<char> id = stackalloc char[6];
        for (var i = 0; i < id.Length; i++) id[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
        return new string(id);
    }

    /// <summary>Lets Explorer refresh the Recycle Bin (icon, open windows) after items arrived without the shell.</summary>
    public static void NotifyShell(IEnumerable<string> recycledItems)
    {
        try
        {
            foreach (var folder in recycledItems.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                SHChangeNotify(0x00001000 /* SHCNE_UPDATEDIR */, 0x0005 /* SHCNF_PATHW */, folder, IntPtr.Zero);
            SHUpdateRecycleBinIcon();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("shell32.dll")]
    private static extern void SHUpdateRecycleBinIcon();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
}
