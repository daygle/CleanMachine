using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace CleanMachine.Windows;

public sealed partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_SIZE = 0x0005;
    private const long SC_MINIMIZE_VALUE = 0xF020;
    private const long SIZE_MINIMIZED_VALUE = 1;
    private const int GWL_WNDPROC = -4;

    private bool _showInTaskbar = true;
    private bool _inTray;
    private TrayIcon? _trayIcon;
    private WndProc? _baseWndProc;
    private IntPtr _hwnd;
    private IntPtr _originalWndProc;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public MainWindow()
    {
        InitializeComponent();
        SetWindowIcon();
        ApplyTitleBarTheme();
        Navigate<OverviewPage>();
        _ = LoadAgentStateAsync();
        _ = ApplyInitialTaskbarPreferenceAsync();
        SubclassForMinimizeToTray();
        Closed += (_, _) =>
        {
            _trayIcon?.Dispose();
            Unsubclass();
        };
    }

    private void SetWindowIcon()
    {
        var hIcon = GetAppIcon();
        if (hIcon != IntPtr.Zero)
        {
            // The IconId overload drives both the title-bar and taskbar icon for
            // unpackaged WinUI 3 apps.
            AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(hIcon));
            return;
        }

        // Fallback: load app.ico shipped as content next to the executable.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
    }

    /// <summary>Loads the app icon embedded in the executable (resource 32512),
    /// falling back to the loose Assets\app.ico next to the executable.</summary>
    private IntPtr GetAppIcon()
    {
        var hIcon = LoadIcon(GetModuleHandle(null), new IntPtr(32512));
        if (hIcon != IntPtr.Zero) return hIcon;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        return File.Exists(iconPath)
            ? LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE)
            : IntPtr.Zero;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// Themes the system title bar with the app's green palette. The bar uses the
    /// same deep green as the app icon (#204E42) so the two read as one brand mark;
    /// caption buttons blend into the bar with lighter-green hover states.
    /// Note: Windows honors these overrides on Windows 11; on Windows 10 the OS
    /// silently ignores them and keeps the default chrome.
    /// </summary>
    private void ApplyTitleBarTheme()
    {
        try
        {
            var bar = AppWindow.TitleBar;
            var brand = ColorFromHex("#204E42");     // deep green (icon background)
            var inactiveBg = ColorFromHex("#E5EBE7"); // sidebar border tone
            var inactiveFg = ColorFromHex("#89958F"); // muted green-gray text

            bar.BackgroundColor = brand;
            bar.ForegroundColor = ColorFromHex("#FFFFFF");
            bar.InactiveBackgroundColor = inactiveBg;
            bar.InactiveForegroundColor = inactiveFg;

            bar.ButtonBackgroundColor = brand;
            bar.ButtonForegroundColor = ColorFromHex("#FFFFFF");
            bar.ButtonHoverBackgroundColor = ColorFromHex("#2A6154");  // lighter green
            bar.ButtonHoverForegroundColor = ColorFromHex("#FFFFFF");
            bar.ButtonPressedBackgroundColor = ColorFromHex("#173B31"); // darker green
            bar.ButtonPressedForegroundColor = ColorFromHex("#FFFFFF");
            bar.ButtonInactiveBackgroundColor = inactiveBg;
            bar.ButtonInactiveForegroundColor = inactiveFg;
        }
        catch { /* title-bar theming is best-effort */ }
    }

    private static Windows.UI.Color ColorFromHex(string hex) => new()
    {
        A = 255,
        R = Convert.ToByte(hex.Substring(1, 2), 16),
        G = Convert.ToByte(hex.Substring(3, 2), 16),
        B = Convert.ToByte(hex.Substring(5, 2), 16),
    };

    [DllImport("user32.dll", EntryPoint = "LoadIconW", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hModule, IntPtr lpIconName);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInstance, string lpFileName, uint ulType, int cxDesired, int cyDesired, uint fuLoad);

    private async Task ApplyInitialTaskbarPreferenceAsync()
    {
        var settings = await AppSettings.LoadAsync();
        ApplyShowInTaskbar(settings.ShowInTaskbar);
    }

    public void ApplyShowInTaskbar(bool showInTaskbar)
    {
        _showInTaskbar = showInTaskbar;
        ApplyTaskbarStyle(showInTaskbar);
        if (showInTaskbar)
        {
            _inTray = false;
            HideTrayIcon();
            if (!AppWindow.IsVisible)
            {
                AppWindow.Show();
                if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
            }
        }
    }

    private void ApplyTaskbarStyle(bool showInTaskbar)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var wasVisible = AppWindow.IsVisible && !_inTray;
            // The shell only re-evaluates the taskbar button on a visibility change,
            // so briefly hide/reshow the window around the style change.
            if (wasVisible) AppWindow.Hide();
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (showInTaskbar)
            {
                exStyle &= ~(int)WS_EX_TOOLWINDOW;
                exStyle |= (int)WS_EX_APPWINDOW;
            }
            else
            {
                exStyle |= (int)WS_EX_TOOLWINDOW;
                exStyle &= ~(int)WS_EX_APPWINDOW;
            }
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
            if (wasVisible) AppWindow.Show();
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Intercepts the minimize command (title-bar button, taskbar, Win+D, etc.) via
    /// a window-proc subclass, because AppWindow.Changed does not report presenter
    /// state transitions. When "Show in taskbar" is off, minimizing sends the
    /// window to the system tray instead of leaving a taskbar button.
    /// </summary>
    private void SubclassForMinimizeToTray()
    {
        try
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // The field reference keeps the delegate (and its native thunk) alive
            // for the lifetime of the window — required while the OS holds the pointer.
            WndProc hook = WndProcHook;
            _baseWndProc = hook;
            _originalWndProc = SetWindowLongPtr(_hwnd, GWL_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(hook));
        }
        catch { /* subclassing is best-effort; without it minimize just minimizes */ }
    }

    private void Unsubclass()
    {
        if (_hwnd == IntPtr.Zero || _originalWndProc == IntPtr.Zero) return;
        try { SetWindowLongPtr(_hwnd, GWL_WNDPROC, _originalWndProc); }
        catch { /* the window may already be gone */ }
        _originalWndProc = IntPtr.Zero;
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_showInTaskbar && !_inTray)
        {
            // Swallow the minimize command and collapse to the tray instead.
            if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE_VALUE)
            {
                SendToTray();
                return IntPtr.Zero;
            }
            // Fallback for minimize paths that bypass WM_SYSCOMMAND.
            if (msg == WM_SIZE && wParam.ToInt64() == SIZE_MINIMIZED_VALUE)
            {
                SendToTray();
                return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
            }
        }
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private void SendToTray()
    {
        if (_inTray) return;
        // Show the tray icon BEFORE hiding the window: if the icon can't be shown,
        // the window must stay visible or it would be unreachable.
        if (!ShowTrayIcon()) return;
        _inTray = true;
        AppWindow.Hide();
    }

    private bool ShowTrayIcon()
    {
        if (_trayIcon is null)
        {
            var hIcon = GetAppIcon();
            if (hIcon == IntPtr.Zero) return false;
            _trayIcon = new TrayIcon(hIcon, "CleanMachine");
            _trayIcon.Clicked += OnTrayIconClicked;
        }
        _trayIcon.Show();
        return true;
    }

    private void HideTrayIcon()
    {
        _trayIcon?.Hide();
    }

    private void OnTrayIconClicked()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _inTray = false;
            HideTrayIcon();
            AppWindow.Show();
            if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
            Activate(); // bring the restored window to the foreground
        });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // SetWindowLongPtr only exists on 64-bit user32; route through SetWindowLong on 32-bit.
    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        Environment.Is64BitProcess
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public void Navigate<T>() where T : Page, new() => ContentFrame.Navigate(typeof(T));
    private async Task LoadAgentStateAsync() { var settings = await AppSettings.LoadAsync(); AgentStatusText.Text = settings.BackgroundAgentEnabled ? "●  Background Agent  ON" : "●  Background Agent  OFF"; }
    private void Overview_Click(object sender, RoutedEventArgs e) => Navigate<OverviewPage>();
    private void Cleaner_Click(object sender, RoutedEventArgs e) => Navigate<CleanerPage>();
    private void Registry_Click(object sender, RoutedEventArgs e) => Navigate<RegistryCarePage>();
    private void WindowsCleanup_Click(object sender, RoutedEventArgs e) => Navigate<WindowsCleanupPage>();
    private void SecureDeleteNav_Click(object sender, RoutedEventArgs e) => Navigate<SecureDeletePage>();
    private void Activity_Click(object sender, RoutedEventArgs e) => Navigate<ActivityPage>();
    private void Settings_Click(object sender, RoutedEventArgs e) => Navigate<SettingsPage>();
    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => Navigate<UpdatesPage>();
}
