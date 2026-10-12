using System.Runtime.InteropServices;

namespace Cleanup.App.Services;

/// <summary>
/// The notification-area icon (design §5.2 Tray): Shell_NotifyIcon on a message-only window. Left click opens the
/// panel, right click shows a small menu. The Windows App SDK has no tray API, so this is plain Win32.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    public event Action? Clicked;
    public event Action<int>? MenuChosen;

    private readonly IntPtr window;
    private readonly WndProc proc;
    private readonly uint taskbarCreated;
    private readonly IntPtr icon;
    private readonly (int Id, string Text)[] menu;
    private string tooltip = "Cleanup";
    private bool added;

    private const int CallbackMessage = 0x8000 + 77; // WM_APP + 77
    private const uint Id = 1;

    public TrayIcon(string iconPath, (int Id, string Text)[] menuItems)
    {
        menu = menuItems;
        proc = WindowProc;
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "CleanupTray" + Environment.ProcessId,
        };
        RegisterClassEx(ref cls);
        // A hidden top-level window (not HWND_MESSAGE) so it receives the TaskbarCreated broadcast after Explorer restarts.
        window = CreateWindowEx(0, cls.lpszClassName, "CleanupTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        icon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, GetSystemMetrics(49), GetSystemMetrics(50), 0x10 /* LR_LOADFROMFILE */);
        Add();
    }

    public string Tooltip
    {
        get => tooltip;
        set
        {
            tooltip = value.Length > 120 ? value[..120] : value;
            if (added) Shell_NotifyIcon(1 /* NIM_MODIFY */, Data(0x4 /* NIF_TIP */));
        }
    }

    private void Add()
    {
        var data = Data(0x1 | 0x2 | 0x4); // NIF_MESSAGE | NIF_ICON | NIF_TIP
        added = Shell_NotifyIcon(0 /* NIM_ADD */, data);
        if (added)
        {
            data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4
            Shell_NotifyIcon(4 /* NIM_SETVERSION */, data);
        }
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = window,
        uID = Id,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tooltip,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            var evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt is 0x0202 /* WM_LBUTTONUP */ or 0x0400 /* NIN_SELECT */ or 0x0401 /* NIN_KEYSELECT */) Clicked?.Invoke();
            else if (evt is 0x0205 /* WM_RBUTTONUP */ or 0x007B /* WM_CONTEXTMENU */) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == taskbarCreated) Add();
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var popup = CreatePopupMenu();
        foreach (var (id, text) in menu)
        {
            if (text == "-") AppendMenu(popup, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
            else AppendMenu(popup, 0, (UIntPtr)id, text);
        }
        GetCursorPos(out var point);
        SetForegroundWindow(window); // so the menu closes when clicking elsewhere
        var chosen = TrackPopupMenu(popup, 0x100 /* TPM_RETURNCMD */ | 0x20 /* TPM_BOTTOMALIGN */, point.X, point.Y, 0, window, IntPtr.Zero);
        DestroyMenu(popup);
        if (chosen > 0) MenuChosen?.Invoke(chosen);
    }

    public void Dispose()
    {
        if (added) Shell_NotifyIcon(2 /* NIM_DELETE */, Data(0));
        added = false;
        if (window != IntPtr.Zero) DestroyWindow(window);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
