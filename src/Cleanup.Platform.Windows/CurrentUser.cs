using System.Runtime.InteropServices;
using System.Text;

namespace Cleanup.Platform.Windows;

/// <summary>Who is signed in, for the Home greeting: the account's real name, not the logon name.</summary>
public static class CurrentUser
{
    /// <summary>
    /// First name from the Windows display name (domain account, then the local / Microsoft account's full name),
    /// falling back to the logon name. The domain lookup can wait on a controller, so call it off the UI thread.
    /// </summary>
    public static string FirstName() => FirstNameOf(DisplayName() ?? Environment.UserName);

    /// <summary>"Lucas Cardoso", "Cardoso, Lucas", "lucas.cardoso" → "Lucas"; blank → "".</summary>
    public static string FirstNameOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var comma = name.IndexOf(',');
        if (comma >= 0 && comma < name.Length - 1) name = name[(comma + 1)..];
        var first = name.Split([' ', '.', '_', ','],StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? "" : char.ToUpper(first[0]) + first[1..];
    }

    private static string? DisplayName()
    {
        try { return DomainDisplayName() ?? LocalFullName(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private static string? DomainDisplayName()
    {
        uint size = 256;
        var buffer = new StringBuilder((int)size);
        return GetUserNameEx(NameDisplay, buffer, ref size) && !string.IsNullOrWhiteSpace(buffer.ToString()) ? buffer.ToString().Trim() : null;
    }

    private static string? LocalFullName()
    {
        if (NetUserGetInfo(null, Environment.UserName, 10, out var info) != 0 || info == IntPtr.Zero) return null;
        try
        {
            var full = Marshal.PtrToStructure<USER_INFO_10>(info).usri10_full_name;
            return string.IsNullOrWhiteSpace(full) ? null : full.Trim();
        }
        finally { NetApiBufferFree(info); }
    }

    private const int NameDisplay = 3;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserNameExW")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetUserNameEx(int nameFormat, StringBuilder lpNameBuffer, ref uint nSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_10
    {
        public string usri10_name;
        public string usri10_comment;
        public string usri10_usr_comment;
        public string usri10_full_name;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? servername, string username, int level, out IntPtr bufptr);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
