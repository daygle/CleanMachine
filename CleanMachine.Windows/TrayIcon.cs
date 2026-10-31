using System.Runtime.InteropServices;

namespace CleanMachine.Windows;

/// <summary>One row of the tray context menu, in exactly the shape
/// <see cref="TrayIcon.BuildMenu"/> produces: what the real Win32 popup draws.
/// Separators carry no command and are never enabled. Internal (rather than
/// private) so tests can pin the menu's command ids, labels, ordering and the
/// greyed-while-busy behaviour without driving Win32.</summary>
internal sealed record TrayMenuItem(uint CommandId, string Label, bool IsSeparator, bool Enabled)
{
    internal static TrayMenuItem Item(uint commandId, string label, bool enabled)
        => new(commandId, label, IsSeparator: false, Enabled: enabled);

    internal static TrayMenuItem SeparatorItem { get; } = new(0, string.Empty, IsSeparator: true, Enabled: false);
}

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
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const int WM_TRAYICON = 0x0400; // WM_USER + 100
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const uint WM_NULL = 0x0000;

    // Right-click context menu.
    private const uint MF_STRING = 0x0000;
    private const uint MF_GRAYED = 0x00000004;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint ID_OPEN = 1;
    private const uint ID_EXIT = 2;
    private const uint ID_QUICKCLEAN_FIRST = 3; // four consecutive ids, one per QuickCleanArea
    private const uint ID_CLEAN_EVERYTHING = 7; // runs all four areas in sequence

    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private readonly uint _id;
    private string _tip;
    private readonly TrayWndProc _wndProc;
    private string _className = string.Empty;
    private bool _added;
    private bool _disposed;

    public bool IsVisible => _added;

    /// <summary>Left-click, or the "Open" context-menu item: restore the window.</summary>
    public event Action? Clicked;
    /// <summary>The "Exit" context-menu item: quit the application.</summary>
    public event Action? ExitRequested;
    /// <summary>One of the "Quick Clean" context-menu items: run that area's quick
    /// clean immediately. Raised on the tray window's thread (the one that created
    /// this icon, the UI thread); subscribers should offload the run.</summary>
    public event Action<QuickCleanArea>? QuickCleanRequested;
    /// <summary>The "Clean Everything" context-menu item: run all four quick cleans
    /// in sequence. Same thread contract as <see cref="QuickCleanRequested"/>; the
    /// subscriber combines and reports the totals.</summary>
    public event Action? CleanEverythingRequested;
    /// <summary>Set while our own <see cref="CleaningActivity"/> scope is open, so the
    /// menu can grey the Quick Clean items out while a run is in progress. Cleanups
    /// started outside the tray still grey the menu through the same flag: it tracks
    /// <see cref="CleaningActivity.IsActive"/>, which every path flips. The internal
    /// test hook <see cref="QuickCleanInFlightForTest"/> exposes it.</summary>
    private volatile bool _quickCleanInFlight;

    /// <summary>The flag that decides whether the menu's five clean items are greyed
    /// right now. Internal so tests can verify the constructor's initial read and the
    /// <see cref="CleaningActivity.Changed"/> subscription end-to-end without having
    /// to right-click a real tray icon.</summary>
    internal bool QuickCleanInFlightForTest => _quickCleanInFlight;

    public TrayIcon(IntPtr icon, string tip, uint id = 1)
    {
        _icon = icon;
        _tip = tip;
        _id = id;
        _wndProc = WndProc;
        _hwnd = CreateMessageWindow();
        // The four Quick Clean items stay greyed while any clean is running - ours
        // or one started elsewhere (the Overview cards, a schedule, the agent).
        _quickCleanInFlight = CleaningActivity.IsActive;
        CleaningActivity.Changed += OnCleaningActivityChanged;
    }

    public void Show()
    {
        if (_added || _disposed || _hwnd == IntPtr.Zero) return;
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

    /// <summary>Swaps the displayed icon in place (NIM_MODIFY) - the cleaning
    /// animation calls this once per spinner frame. If the icon is currently
    /// hidden the new value is remembered for the next Show(), and a TaskbarCreated
    /// re-add uses it too (CreateData reads the field).</summary>
    public void SetIcon(IntPtr icon)
    {
        _icon = icon;
        if (!_added) return;
        var data = CreateData();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>Swaps the tooltip in place (NIM_MODIFY): "CleanMachine - cleaning..."
    /// while a clean runs, the plain name otherwise.</summary>
    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        var data = CreateData();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleaningActivity.Changed -= OnCleaningActivityChanged;
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

    private void OnCleaningActivityChanged(bool active) => _quickCleanInFlight = active;

    /// <summary>Every row of the right-click menu, in drawn order, with each item's
    /// command id and enabled state - a pure model of exactly the menu
    /// <see cref="ShowContextMenu"/> draws, kept separate from the Win32 calls so
    /// tests can pin ids, labels, ordering and the greyed-while-busy behaviour.
    /// All five clean items (Clean Everything plus one per Overview area) are greyed
    /// while any clean is running: a queued clean would only pile up behind the
    /// running one (every service shares the cleanup gate), and the spinner already
    /// tells the user it is busy.</summary>
    internal static IReadOnlyList<TrayMenuItem> BuildMenu(bool quickCleanInFlight)
    {
        List<TrayMenuItem> items =
        [
            TrayMenuItem.Item(ID_OPEN, "Open CleanMachine", enabled: true),
            TrayMenuItem.SeparatorItem,
        ];
        var enabled = !quickCleanInFlight;
        items.Add(TrayMenuItem.Item(ID_CLEAN_EVERYTHING, "Clean Everything", enabled));
        foreach (var area in new[] { QuickCleanArea.Browsers, QuickCleanArea.Windows, QuickCleanArea.Registry, QuickCleanArea.Apps })
            items.Add(TrayMenuItem.Item(QuickCleanCommand(area), $"Quick Clean - {QuickCleanService.Title(area)}", enabled));
        items.Add(TrayMenuItem.SeparatorItem);
        items.Add(TrayMenuItem.Item(ID_EXIT, "Exit CleanMachine", enabled: true));
        return items;
    }

    /// <summary>The menu command a quick clean on <paramref name="area"/> carries.
    /// The four ids are consecutive so a single range check maps them back.</summary>
    internal static uint QuickCleanCommand(QuickCleanArea area) => ID_QUICKCLEAN_FIRST + (uint)area;

    /// <summary>Reverse of <see cref="QuickCleanCommand"/>: the area the returned
    /// menu command selected, or null for anything else (Open, Exit, Clean
    /// Everything, a dismissed menu, or a stale id). Used by the dispatch ahead of
    /// every <see cref="QuickCleanRequested"/> raise, so the id arithmetic lives in
    /// one tested place.</summary>
    internal static QuickCleanArea? QuickCleanAreaFromCommand(uint command)
        => command >= ID_QUICKCLEAN_FIRST && command < ID_QUICKCLEAN_FIRST + 4
            ? (QuickCleanArea)(command - ID_QUICKCLEAN_FIRST)
            : null;

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TaskbarCreated)
        {
            if (_added)
            {
                var data = CreateData();
                _added = Shell_NotifyIcon(NIM_ADD, ref data);
            }
        }
        else if (msg == WM_TRAYICON)
        {
            var action = lParam.ToInt64();
            if (action == WM_LBUTTONUP)
                Clicked?.Invoke();
            else if (action == WM_RBUTTONUP)
                ShowContextMenu();
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Shows the right-click menu ("Open" / the five clean items / "Exit")
    /// at the cursor. Uses
    /// TPM_RETURNCMD so the chosen command comes back here rather than as a
    /// WM_COMMAND, and the SetForegroundWindow + WM_NULL dance MSDN requires so the
    /// menu dismisses correctly when the user clicks away from it.</summary>
    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            // Draw exactly what BuildMenu describes, so the tested model and the
            // real menu can never drift apart.
            foreach (var item in BuildMenu(_quickCleanInFlight))
            {
                if (item.IsSeparator)
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                else
                    AppendMenu(menu, MF_STRING | (item.Enabled ? 0u : MF_GRAYED), (UIntPtr)item.CommandId, item.Label);
            }

            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                point.X, point.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command == ID_OPEN) Clicked?.Invoke();
            else if (command == ID_EXIT) ExitRequested?.Invoke();
            else if (command == ID_CLEAN_EVERYTHING) CleanEverythingRequested?.Invoke();
            else if (QuickCleanAreaFromCommand(command) is { } area)
                QuickCleanRequested?.Invoke(area);
        }
        finally
        {
            DestroyMenu(menu);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
