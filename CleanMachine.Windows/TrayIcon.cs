using System.Runtime.InteropServices;

namespace CleanMachine.Windows;

/// <summary>
/// Minimal system tray icon for WinUI 3 (no native support) implemented via the
/// Win32 Shell_NotifyIcon API. A hidden top-level window receives the icon's callback
/// messages (click to restore) and re-adds the icon when the taskbar is recreated
/// (e.g. after an Explorer restart, which broadcasts "TaskbarCreated" to top-level
/// windows only - a message-only window would never hear it).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const int WM_TRAYICON = 0x0400; // WM_USER + 100
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly uint _id;
    private readonly string _tip;
    private readonly TrayWndProc _wndProc;
    private string _className = string.Empty;
    private bool _added;

    public event Action? Clicked;

    public TrayIcon(IntPtr icon, string tip, uint id = 1)
    {
        _icon = icon;
        _tip = tip;
        _id = id;
        _wndProc = WndProc;
        _hwnd = CreateMessageWindow();
    }

    public void Show()
    {
        if (_added) return;
        var data = CreateData();
        if (Shell_NotifyIcon(NIM_ADD, ref data)) _added = true;
    }

    public void Hide()
    {
        if (!_added) return;
        var data = CreateData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            UnregisterClass(_className, GetModuleHandle(null));
        }
        GC.SuppressFinalize(this);
    }

    private NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = _id,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _icon,
        szTip = _tip,
        Anonymous = new NOTIFYICONDATAW_UNION { uVersion = 5 }
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TaskbarCreated)
        {
            if (_added)
            {
                var data = CreateData();
                Shell_NotifyIcon(NIM_ADD, ref data);
            }
        }
        else if (msg == WM_TRAYICON)
        {
            var action = lParam.ToInt64();
            if (action == WM_LBUTTONUP || action == WM_RBUTTONUP)
                Clicked?.Invoke();
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr CreateMessageWindow()
    {
        // Unique class name per instance so multiple icons never collide.
        var className = $"CleanMachineTrayIcon_{Guid.NewGuid():N}";
        _className = className;
        var instance = GetModuleHandle(null);
        var wndClass = new WNDCLASSW
        {
            lpfnWndProc = _wndProc,
            lpszClassName = className,
            hInstance = instance
        };
        RegisterClass(ref wndClass);
        // An invisible top-level (message-only-free) window: it must be a real
        // top-level window so it receives the "TaskbarCreated" broadcast.
        return CreateWindowEx(0, className, className, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr TrayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public TrayWndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NOTIFYICONDATAW_UNION
    {
        [FieldOffset(0)] public uint uTimeout;
        [FieldOffset(0)] public uint uVersion;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
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
        public NOTIFYICONDATAW_UNION Anonymous;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
