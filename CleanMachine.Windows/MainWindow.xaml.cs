using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
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
    private const long SIZE_RESTORED_VALUE = 0;
    private const int GWL_WNDPROC = -4;

    private bool _showInTaskbar = true;
    private bool _minimizeToTray;
    private bool _startMinimizedToTray;
    private bool _closeToTray;
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
        LoadSidebarLogo();
        ApplyTitleBarTheme();
        Navigate<OverviewPage>();
        // Defer applying the taskbar preference until the window is first
        // activated: changing ex-styles while the shell is still registering the
        // window's button can race Explorer and permanently lose the button.
        // By the first Activated event the button is registered, and the default
        // preference (show) would be a no-op anyway.
        Activated += OnFirstActivated;
        SubclassForMinimizeToTray();
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            _trayIcon?.Dispose();
            Unsubclass();
            // Release the single-instance mutex and IPC events now (not at process
            // teardown) so a relaunch - e.g. right after an uninstall/reinstall -
            // can start immediately.
            (Microsoft.UI.Xaml.Application.Current as App)?.StopInstanceEvents();
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

    /// <summary>
    /// Loads the sidebar mark from the file next to the executable rather than via
    /// ms-appx: in unpackaged (installer) builds, ms-appx only resolves files that
    /// are indexed in the app's PRI resource map, and loose Content files silently
    /// fail there - which showed the sidebar logo as blank. A direct file path
    /// works the same in packaged and unpackaged builds.
    /// </summary>
    private async void LoadSidebarLogo()
    {
        try
        {
            // Prefer the copy embedded in the assembly: loose Content files can fail to
            // be laid down (or found) in unpackaged/installer builds, which showed the
            // sidebar mark blank. The embedded stream is always present. Fall back to the
            // loose Assets\AppLogo.png next to the executable if, for any reason, the
            // manifest resource is missing.
            var stream = OpenLogoStream();
            if (stream is null) return;
            await using (stream)
            {
                // Decode from a stream rather than a file:// Uri: in unpackaged (installer)
                // builds a BitmapImage built from a file Uri can silently fail to load,
                // leaving the sidebar mark blank. A stream decode is reliable in both
                // packaged and unpackaged builds.
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                SidebarLogo.Source = bitmap;
            }
        }
        catch { /* the logo is decorative; a blank image is an acceptable fallback */ }
    }

    /// <summary>Opens the sidebar logo bytes, preferring the embedded assembly resource
    /// and falling back to the loose file next to the executable. Returns null if
    /// neither source is available.</summary>
    private static Stream? OpenLogoStream()
    {
        var assembly = typeof(MainWindow).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith("AppLogo.png", StringComparison.OrdinalIgnoreCase));
        if (resourceName is not null)
        {
            var embedded = assembly.GetManifestResourceStream(resourceName);
            if (embedded is not null) return embedded;
        }

        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.png");
        return File.Exists(logoPath) ? File.OpenRead(logoPath) : null;
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

    private static global::Windows.UI.Color ColorFromHex(string hex) => new()
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

    private bool _taskbarPreferenceApplied;

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_taskbarPreferenceApplied || args.WindowActivationState == WindowActivationState.Deactivated) return;
        _taskbarPreferenceApplied = true;
        Activated -= OnFirstActivated;
        _ = ApplyInitialTaskbarPreferenceAsync();
    }

    /// <summary>Intercepts the window close: when CloseToTray is on, the close
    /// button minimizes to the tray instead of exiting.</summary>
    private void OnClosing(object sender, AppWindowClosingEventArgs args)
    {
        if (!_closeToTray || _inTray) return;
        // Cancel the close and minimize to tray instead.
        args.Cancel = true;
        SendToTray(hideWindow: true);
    }

    private async Task ApplyInitialTaskbarPreferenceAsync()
    {
        var settings = await AppSettings.LoadAsync();
        ApplyShowInTaskbar(settings.ShowInTaskbar);
        ApplyMinimizeToTray(settings.MinimizeToTray);
        // A logon autostart (--background) always opens to the tray, regardless of the
        // "minimize to tray on startup" preference.
        _startMinimizedToTray = settings.StartMinimizedToTray || App.LaunchedAtLogon;
        _closeToTray = settings.CloseToTray;

        // Create desktop shortcut on first launch for MSIX installs only
        // (the .exe installer already creates one via its desktopicon task).
        if (!settings.DesktopShortcutCreated && ScheduleService.IsMsix)
        {
            CreateDesktopShortcut();
            settings.DesktopShortcutCreated = true;
            await settings.SaveAsync();
        }

        // Start minimized to tray: hide the window immediately and show tray icon.
        if (_startMinimizedToTray)
        {
            SendToTray(hideWindow: true);
        }
    }

    /// <summary>Creates a desktop shortcut to the application. Works for both
    /// MSIX and .exe installs by using the current executable path.</summary>
    private void CreateDesktopShortcut()
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktopPath)) return;

            var shortcutPath = Path.Combine(desktopPath, "CleanMachine.lnk");
            if (File.Exists(shortcutPath)) return;

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            // Create a simple .lnk shortcut using COM Shell.
            var shell = (IShellLinkW)new CShellLink();
            shell.SetPath(exePath);
            shell.SetDescription("CleanMachine - Privacy cleanup utility");
            shell.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? exePath);

            var persistFile = (IPersistFile)shell;
            persistFile.Save(shortcutPath, false);
        }
        catch
        {
            // Shortcut creation is best-effort; missing icon is acceptable.
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetCurFile(out IntPtr ppszFileName);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
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
            // Self-heal: if a previous style change raced Explorer and lost the
            // taskbar button, force the tab back (idempotent, no-op if present).
            ForceTaskbarButton(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
    }

    /// <summary>Independent of "Show in taskbar": when on, minimizing shows a tray
    /// icon but keeps the taskbar button, so the window restores from either place.</summary>
    public void ApplyMinimizeToTray(bool minimizeToTray)
    {
        _minimizeToTray = minimizeToTray;
        // Turning the option off while a minimized window is showing its tray icon:
        // drop the icon - the taskbar button still restores the window.
        if (!minimizeToTray && _showInTaskbar && _inTray)
        {
            _inTray = false;
            HideTrayIcon();
        }
    }

    /// <summary>When true, the close button minimizes to tray instead of exiting.</summary>
    public void ApplyCloseToTray(bool closeToTray) => _closeToTray = closeToTray;

    private void ApplyTaskbarStyle(bool showInTaskbar)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            // Already in the requested state - do nothing. This matters at
            // startup: even a redundant hide/reshow can race Explorer's initial
            // taskbar-button registration and permanently lose the button.
            if (((exStyle & (int)WS_EX_TOOLWINDOW) != 0) == !showInTaskbar) return;

            var wasVisible = AppWindow.IsVisible && !_inTray;
            // The shell only re-evaluates the taskbar button on a visibility change,
            // so briefly hide/reshow the window around the style change.
            if (wasVisible) AppWindow.Hide();
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

    // Note: deliberately not sealed - the ComImport coclass is cast to
    // ITaskbarList, and a sealed-class-to-interface cast is a compile error
    // under classic conversion rules (COM coclasses cannot be derived from anyway).
    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarListClass { }

    [ComImport]
    [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    /// <summary>
    /// Re-asserts the window's taskbar button via the shell's ITaskbarList API.
    /// AddTab is idempotent for a window that already has a button, so this is
    /// safe to call as a self-heal whenever the button should be present.
    /// </summary>
    private static void ForceTaskbarButton(IntPtr hwnd)
    {
        try
        {
            var list = (ITaskbarList)new TaskbarListClass();
            list.HrInit();
            list.AddTab(hwnd);
            list.ActivateTab(hwnd);
        }
        catch { /* best-effort: the shell may be busy or the call unsupported */ }
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
            // for the lifetime of the window - required while the OS holds the pointer.
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
        var minimizeCommand = msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MINIMIZE_VALUE;
        var minimized = msg == WM_SIZE && wParam.ToInt64() == SIZE_MINIMIZED_VALUE;
        var restored = msg == WM_SIZE && wParam.ToInt64() == SIZE_RESTORED_VALUE;

        if (!_inTray && (minimizeCommand || minimized))
        {
            if (!_showInTaskbar)
            {
                // Tray-only mode: hide the window (no taskbar button). Swallow the
                // minimize command so it does not also appear minimized on the taskbar.
                SendToTray(hideWindow: true);
                if (minimizeCommand) return IntPtr.Zero;
            }
            else if (_minimizeToTray)
            {
                // Surface the tray icon but keep the taskbar button: let the normal
                // minimize proceed so the window can be restored from either place.
                SendToTray(hideWindow: false);
            }
        }
        else if (restored && _inTray && _showInTaskbar)
        {
            // Minimize-to-tray mode: restored from the taskbar or the window itself -
            // drop the tray icon. (In tray-only mode the window is hidden, not
            // minimized, so the icon must stay until the tray click restores it.)
            _inTray = false;
            HideTrayIcon();
        }
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>Shows the tray icon when the window minimizes. With
    /// <paramref name="hideWindow"/> the window hides entirely (tray-only mode);
    /// otherwise it stays minimized so its taskbar button remains.</summary>
    private void SendToTray(bool hideWindow)
    {
        if (_inTray) return;
        // Show the tray icon BEFORE hiding the window: if the icon can't be shown,
        // the window must stay visible or it would be unreachable.
        if (!ShowTrayIcon()) return;
        _inTray = true;
        if (hideWindow) AppWindow.Hide();
    }

    private bool ShowTrayIcon()
    {
        if (_trayIcon is null)
        {
            var hIcon = GetAppIcon();
            if (hIcon == IntPtr.Zero) return false;
            _trayIcon = new TrayIcon(hIcon, "CleanMachine");
            _trayIcon.Clicked += OnTrayIconClicked;
            _trayIcon.ExitRequested += OnTrayExitRequested;
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

    /// <summary>"Exit" chosen from the tray menu: really quit. The window is in the
    /// tray, so the close-to-tray interception is bypassed and the app shuts down;
    /// the Closed handler disposes the tray icon so it disappears immediately.</summary>
    private void OnTrayExitRequested() => RequestExit();

    /// <summary>Public entry point for a real quit (tray Exit, or the shutdown event
    /// the installer signals). Forces the close-to-tray interception off for one pass
    /// and exits; the Closed handler disposes the tray icon so it disappears immediately.</summary>
    public void RequestExit()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _inTray = true; // ensure OnClosing lets the close through instead of re-routing to tray
            Microsoft.UI.Xaml.Application.Current.Exit();
        });
    }

    /// <summary>Public entry point to show and foreground the window (tray click, tray
    /// "Open", or the activate event a second launch signals). Reused by App's IPC
    /// listener when a new process instance asks the running one to surface itself.</summary>
    public void RequestShowFromTray() => OnTrayIconClicked();

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

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush NavActiveBrush = new(ColorFromHex("#FFFFFF"));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush NavIdleBrush = new(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush NavHoverBrush = new(global::Windows.UI.Color.FromArgb(255, 243, 248, 245)); // #F3F8F5
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush NavPressedBrush = new(global::Windows.UI.Color.FromArgb(255, 234, 244, 238)); // #EAF4EE
    private bool _navPointerHandlersAttached;

    public void Navigate<T>() where T : Page, new()
    {
        ContentFrame.Navigate(typeof(T));
        HighlightNav(typeof(T));
    }

    /// <summary>Moves the white highlight to the nav item matching the open page,
    /// so the highlight always tracks whatever is on the right - however it got there
    /// (sidebar click or an in-page quick link).</summary>
    private void HighlightNav(Type pageType)
    {
        Button? active = pageType == typeof(OverviewPage) ? NavOverview
            : pageType == typeof(CleanerPage) ? NavCleaner
            : pageType == typeof(RegistryCarePage) ? NavRegistry
            : pageType == typeof(BackupsPage) ? NavBackups
            : pageType == typeof(WindowsCleanupPage) ? NavWindowsCleanup
            : pageType == typeof(AppCleanupPage) ? NavAppCleanup
            : pageType == typeof(InstalledAppsPage) ? NavInstalledApps
            : pageType == typeof(StartupAppsPage) ? NavStartupApps
            : pageType == typeof(SecureDeletePage) ? NavSecureDelete
            : pageType == typeof(DriveWiperPage) ? NavDriveWiper
            : pageType == typeof(ActivityPage) ? NavActivity
            : pageType == typeof(SchedulesPage) ? NavSchedules
            : pageType == typeof(AutomaticCleanupPage) ? NavAutomaticCleanup
            : pageType == typeof(SettingsPage) ? NavSettings
            : pageType == typeof(UpdatesPage) ? NavUpdates
            : null;
        foreach (var button in new[] { NavOverview, NavCleaner, NavRegistry, NavBackups, NavWindowsCleanup, NavAppCleanup, NavInstalledApps, NavStartupApps, NavSecureDelete, NavDriveWiper, NavActivity, NavSchedules, NavAutomaticCleanup, NavSettings, NavUpdates })
            button.Background = ReferenceEquals(button, active) ? NavActiveBrush : NavIdleBrush;

        AttachNavPointerFeedback();
    }

    private static bool IsNavActive(Button button) => ReferenceEquals(button.Background, NavActiveBrush);

    /// <summary>Hover/pressed tinting for nav items that are not the active one;
    /// the active item keeps its white highlight under the pointer.</summary>
    private void AttachNavPointerFeedback()
    {
        if (_navPointerHandlersAttached) return;
        _navPointerHandlersAttached = true;
        foreach (var button in new[] { NavOverview, NavCleaner, NavRegistry, NavBackups, NavWindowsCleanup, NavAppCleanup, NavInstalledApps, NavStartupApps, NavSecureDelete, NavDriveWiper, NavActivity, NavSchedules, NavAutomaticCleanup, NavSettings, NavUpdates })
        {
            button.PointerEntered += (s, _) => { var b = (Button)s; if (!IsNavActive(b)) b.Background = NavHoverBrush; };
            button.PointerExited += (s, _) => { var b = (Button)s; b.Background = IsNavActive(b) ? NavActiveBrush : NavIdleBrush; };
            button.PointerPressed += (s, _) => { var b = (Button)s; if (!IsNavActive(b)) b.Background = NavPressedBrush; };
            button.PointerReleased += (s, _) => { var b = (Button)s; b.Background = IsNavActive(b) ? NavActiveBrush : NavHoverBrush; };
        }
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => Navigate<OverviewPage>();
    private void Cleaner_Click(object sender, RoutedEventArgs e) => Navigate<CleanerPage>();
    private void Registry_Click(object sender, RoutedEventArgs e) => Navigate<RegistryCarePage>();
    private void Backups_Click(object sender, RoutedEventArgs e) => Navigate<BackupsPage>();
    private void WindowsCleanup_Click(object sender, RoutedEventArgs e) => Navigate<WindowsCleanupPage>();
    private void AppCleanup_Click(object sender, RoutedEventArgs e) => Navigate<AppCleanupPage>();
    private void StartupApps_Click(object sender, RoutedEventArgs e) => Navigate<StartupAppsPage>();
    private void InstalledApps_Click(object sender, RoutedEventArgs e) => Navigate<InstalledAppsPage>();
    private void SecureDeleteNav_Click(object sender, RoutedEventArgs e) => Navigate<SecureDeletePage>();
    private void DriveWiper_Click(object sender, RoutedEventArgs e) => Navigate<DriveWiperPage>();
    private void Activity_Click(object sender, RoutedEventArgs e) => Navigate<ActivityPage>();
    private void Schedules_Click(object sender, RoutedEventArgs e) => Navigate<SchedulesPage>();
    private void AutomaticCleanup_Click(object sender, RoutedEventArgs e) => Navigate<AutomaticCleanupPage>();
    private void Settings_Click(object sender, RoutedEventArgs e) => Navigate<SettingsPage>();
    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => Navigate<UpdatesPage>();
}
