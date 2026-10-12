using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Cleanup.Platform.Windows;

/// <summary>
/// Permanent deletion that can't be redirected (invariants 3 and 13). Every entry is opened relative to its parent's
/// handle with FILE_OPEN_REPARSE_POINT, so a folder swapped for a junction mid-delete is removed as a link, never
/// entered; children are listed from the open handle, and each entry is deleted through its own handle (POSIX
/// semantics, read-only ignored). Path-based recursive deletes re-resolve every name and can be steered — which matters
/// when the elevated helper deletes inside user-writable system folders such as Windows\Temp.
/// </summary>
public static class HandleDelete
{
    public static void Delete(string path)
    {
        using var handle = Native.CreateFile(@"\\?\" + Path.GetFullPath(path), Native.Access, Native.ShareAll, IntPtr.Zero,
            Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid) throw Error(Marshal.GetLastWin32Error(), path);
        DeleteTree(handle, path);
    }

    private static void DeleteTree(SafeFileHandle handle, string display)
    {
        var attributes = Attributes(handle);
        if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var name in Children(handle))
            {
                using var child = OpenRelative(handle, name, out var status);
                if (child is null)
                {
                    if (status == unchecked((int)0xC0000034)) continue; // STATUS_OBJECT_NAME_NOT_FOUND: already gone
                    throw new IOException($"{Path.Combine(display, name)}: NTSTATUS 0x{status:X8}");
                }
                DeleteTree(child, Path.Combine(display, name));
            }
        }
        MarkForDeletion(handle, display);
    }

    private static FileAttributes Attributes(SafeFileHandle handle)
    {
        if (!Native.GetFileInformationByHandleEx(handle, Native.FileBasicInfo, out Native.FILE_BASIC_INFO info, Marshal.SizeOf<Native.FILE_BASIC_INFO>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (FileAttributes)info.FileAttributes;
    }

    /// <summary>Names in a directory, listed from its handle (FILE_ID_BOTH_DIR_INFO), without "." and "..".</summary>
    private static List<string> Children(SafeFileHandle directory)
    {
        var names = new List<string>();
        var buffer = new byte[64 * 1024];
        var infoClass = Native.FileIdBothDirectoryRestartInfo;
        while (true)
        {
            if (!Native.GetFileInformationByHandleEx(directory, infoClass, buffer, buffer.Length))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 18) break; // ERROR_NO_MORE_FILES
                throw new Win32Exception(error);
            }
            infoClass = Native.FileIdBothDirectoryInfo;
            var offset = 0;
            while (true)
            {
                var next = BitConverter.ToInt32(buffer, offset);
                var length = BitConverter.ToInt32(buffer, offset + 60);
                var name = Encoding.Unicode.GetString(buffer, offset + 104, length);
                if (name is not "." and not "..") names.Add(name);
                if (next == 0) break;
                offset += next;
            }
        }
        return names;
    }

    private static SafeFileHandle? OpenRelative(SafeFileHandle parent, string name, out int status)
    {
        var bytes = Encoding.Unicode.GetBytes(name);
        var nameBuffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, nameBuffer, bytes.Length);
            var unicode = new Native.UNICODE_STRING { Length = (ushort)bytes.Length, MaximumLength = (ushort)bytes.Length, Buffer = nameBuffer };
            var unicodePtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.UNICODE_STRING>());
            try
            {
                Marshal.StructureToPtr(unicode, unicodePtr, false);
                var attributes = new Native.OBJECT_ATTRIBUTES
                {
                    Length = Marshal.SizeOf<Native.OBJECT_ATTRIBUTES>(),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = unicodePtr,
                    Attributes = 0x40, // OBJ_CASE_INSENSITIVE
                };
                status = Native.NtCreateFile(out var handle, Native.Access, ref attributes, out _, IntPtr.Zero, 0, Native.ShareAll,
                    1 /* FILE_OPEN */, 0x00200000 /* FILE_OPEN_REPARSE_POINT */ | 0x20 /* FILE_SYNCHRONOUS_IO_NONALERT */ | 0x4000 /* FILE_OPEN_FOR_BACKUP_INTENT */,
                    IntPtr.Zero, 0);
                if (status < 0) { handle.Dispose(); return null; }
                return handle;
            }
            finally { Marshal.FreeHGlobal(unicodePtr); }
        }
        finally { Marshal.FreeHGlobal(nameBuffer); }
    }

    private static void MarkForDeletion(SafeFileHandle handle, string display)
    {
        var ex = new Native.FILE_DISPOSITION_INFO_EX { Flags = 0x1 | 0x2 | 0x10 }; // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE
        if (Native.SetFileInformationByHandle(handle, Native.FileDispositionInfoEx, ref ex, Marshal.SizeOf<Native.FILE_DISPOSITION_INFO_EX>())) return;
        var error = Marshal.GetLastWin32Error();
        // File systems without the extended class (FAT, some network drives): the classic disposition.
        var classic = new Native.FILE_DISPOSITION_INFO { DeleteFile = 1 };
        if (Native.SetFileInformationByHandle(handle, Native.FileDispositionInfo, ref classic, Marshal.SizeOf<Native.FILE_DISPOSITION_INFO>())) return;
        throw Error(error == 87 /* invalid parameter: Ex unsupported */ ? Marshal.GetLastWin32Error() : error, display);
    }

    private static Exception Error(int code, string path) => code switch
    {
        32 or 33 => new IOException($"{path}: em uso por outro programa", code),
        5 => new UnauthorizedAccessException($"{path}: acesso negado"),
        2 or 3 => new FileNotFoundException($"{path}: não existe mais"),
        145 => new IOException($"{path}: a pasta não ficou vazia", code),
        _ => new Win32Exception(code, $"{path}: {new Win32Exception(code).Message}"),
    };

    private static class Native
    {
        public const uint Access = 0x00010000 /* DELETE */ | 0x00100000 /* SYNCHRONIZE */ | 0x80 /* FILE_READ_ATTRIBUTES */ | 0x1 /* FILE_LIST_DIRECTORY */;
        public const uint ShareAll = 0x7;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        public const int FileBasicInfo = 0;
        public const int FileDispositionInfo = 4;
        public const int FileIdBothDirectoryInfo = 10;
        public const int FileIdBothDirectoryRestartInfo = 11;
        public const int FileDispositionInfoEx = 21;

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_BASIC_INFO
        {
            public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
            public uint FileAttributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_DISPOSITION_INFO_EX { public uint Flags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_DISPOSITION_INFO { public byte DeleteFile; }

        [StructLayout(LayoutKind.Sequential)]
        public struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_STATUS_BLOCK
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FILE_BASIC_INFO info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, byte[] buffer, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FILE_DISPOSITION_INFO_EX info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FILE_DISPOSITION_INFO info, int size);

        [DllImport("ntdll.dll")]
        public static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref OBJECT_ATTRIBUTES attributes, out IO_STATUS_BLOCK status,
            IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
    }
}
