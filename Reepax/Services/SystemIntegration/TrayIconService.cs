using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Reepax.Services.Download;
using Reepax.Services.Localization;

namespace Reepax.Services.SystemIntegration;

public class TrayIconService : IDisposable
{
    private static readonly Lazy<TrayIconService> _instance = new(() => new TrayIconService());
    public static TrayIconService Instance => _instance.Value;

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1024;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int NIN_BALLOONUSERCLICK = WM_USER + 5; // 0x0405

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int ExtractIconEx(string szFileName, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private Window? _mainWindow;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _isCreated;
    private bool _isDisposed;
    private ContextMenu? _contextMenu;

    private uint _wmTaskbarCreated;

    public void Initialize(Window mainWindow)
    {
        _mainWindow = mainWindow;
        _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

        var helper = new WindowInteropHelper(mainWindow);
        _hwnd = helper.Handle;

        if (_hwnd == IntPtr.Zero)
        {
            mainWindow.SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(mainWindow).Handle;
                var source = HwndSource.FromHwnd(_hwnd);
                source?.AddHook(WndProc);
            };
        }
        else
        {
            var source = HwndSource.FromHwnd(_hwnd);
            source?.AddHook(WndProc);
        }
    }

    public void ShowTrayIcon()
    {
        if (_isCreated)
            return;

        if (_hwnd == IntPtr.Zero && _mainWindow != null)
        {
            _hwnd = new WindowInteropHelper(_mainWindow).Handle;
        }

        if (_hwnd == IntPtr.Zero)
            return;

        try
        {
            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = GetAppIconHandle();
            }

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = Loc.Get("Tray_Tooltip")
            };

            _isCreated = Shell_NotifyIcon(NIM_ADD, ref nid);
            if (!_isCreated)
            {
                // If NIM_ADD failed (e.g. icon already registered), try NIM_MODIFY
                _isCreated = Shell_NotifyIcon(NIM_MODIFY, ref nid);
            }

            if (_contextMenu == null)
            {
                CreateContextMenu();
            }
        }
        catch (Exception ex)
        {
            Storage.AppLogger.Error("Error showing Shell_NotifyIcon", ex);
        }
    }

    private static IntPtr GetAppIconHandle()
    {
        try
        {
            // 1. Try extracting embedded pack:// resource to AppData/Icons/app.ico
            var appDataIconPath = Path.Combine(Storage.SettingsService.IconsDirectory, "app.ico");
            try
            {
                var resUri = new Uri("pack://application:,,,/Reepax;component/Assets/app.ico");
                var resStream = Application.GetResourceStream(resUri) ?? Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
                if (resStream?.Stream != null)
                {
                    using var stream = resStream.Stream;
                    Directory.CreateDirectory(Storage.SettingsService.IconsDirectory);
                    using var fs = File.Create(appDataIconPath);
                    stream.CopyTo(fs);
                }
            }
            catch { }

            if (File.Exists(appDataIconPath))
            {
                var h = LoadImage(IntPtr.Zero, appDataIconPath, 1 /*IMAGE_ICON*/, 16, 16, 0x00000010 /*LR_LOADFROMFILE*/);
                if (h != IntPtr.Zero) return h;
            }

            // 2. Try Assets/app.ico next to executable
            var baseIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(baseIconPath))
            {
                var h = LoadImage(IntPtr.Zero, baseIconPath, 1, 16, 16, 0x00000010);
                if (h != IntPtr.Zero) return h;
            }

            // 3. Try ExtractIconEx from running executable module
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                if (ExtractIconEx(exePath, 0, out IntPtr hLarge, out IntPtr hSmall, 1) > 0)
                {
                    if (hLarge != IntPtr.Zero) DestroyIcon(hLarge);
                    if (hSmall != IntPtr.Zero) return hSmall;
                }
            }
        }
        catch (Exception ex)
        {
            Storage.AppLogger.Error("Error loading tray icon handle", ex);
        }

        return LoadIcon(IntPtr.Zero, (IntPtr)32512 /*IDI_APPLICATION*/);
    }

    public void HideTrayIcon()
    {
        if (!_isCreated || _hwnd == IntPtr.Zero)
            return;

        try
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _isCreated = false;
        }
        catch { }
    }

    private void CreateContextMenu()
    {
        _contextMenu = new ContextMenu();

        var openItem = new MenuItem { Header = Loc.Get("Tray_Menu_Open"), FontWeight = FontWeights.Bold };
        openItem.Click += (s, e) => RestoreMainWindow();

        var pauseAllItem = new MenuItem { Header = Loc.Get("Tray_Menu_PauseAll") };
        pauseAllItem.Click += (s, e) => QueueManager.Instance.StopQueue();

        var resumeAllItem = new MenuItem { Header = Loc.Get("Tray_Menu_ResumeAll") };
        resumeAllItem.Click += (s, e) => QueueManager.Instance.StartQueue();

        var exitItem = new MenuItem { Header = Loc.Get("Tray_Menu_Exit") };
        exitItem.Click += (s, e) =>
        {
            if (_mainWindow is MainWindow mw)
            {
                mw.ForceExit();
            }
            else
            {
                QueueManager.PerformSafeShutdown();
                Application.Current?.Shutdown();
            }
        };

        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(new Separator());
        _contextMenu.Items.Add(pauseAllItem);
        _contextMenu.Items.Add(resumeAllItem);
        _contextMenu.Items.Add(new Separator());
        _contextMenu.Items.Add(exitItem);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_wmTaskbarCreated != 0 && msg == _wmTaskbarCreated && _isCreated)
        {
            _isCreated = false;
            ShowTrayIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_TRAYICON)
        {
            int eventId = lParam.ToInt32();
            if (eventId == WM_LBUTTONDBLCLK || eventId == NIN_BALLOONUSERCLICK)
            {
                RestoreMainWindow();
                handled = true;
            }
            else if (eventId == WM_RBUTTONUP)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        if (_contextMenu == null || _hwnd == IntPtr.Zero) return;

        _contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        SetForegroundWindow(_hwnd);
        _contextMenu.IsOpen = true;
    }

    public void RestoreMainWindow()
    {
        if (_mainWindow == null)
            return;

        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(RestoreMainWindow);
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Focus();

        HideTrayIcon();
    }

    /// <summary>
    /// Checks whether the user is currently actively focused on an application window of Reepax.
    /// Returns true if an application window is the active foreground window and not minimized.
    /// </summary>
    public bool IsAppInForeground()
    {
        try
        {
            var fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero)
                return false;

            GetWindowThreadProcessId(fgHwnd, out uint fgPid);
            if (fgPid != (uint)Environment.ProcessId)
                return false;

            // User is currently focused on an application window. Check if main window is visible and not minimized.
            if (_mainWindow != null)
            {
                if (_mainWindow.Dispatcher.CheckAccess())
                {
                    if (_mainWindow.WindowState == WindowState.Minimized || !_mainWindow.IsVisible)
                        return false;
                }
                else
                {
                    bool isHiddenOrMin = false;
                    try
                    {
                        isHiddenOrMin = _mainWindow.Dispatcher.Invoke(() =>
                            _mainWindow.WindowState == WindowState.Minimized || !_mainWindow.IsVisible);
                    }
                    catch { }
                    if (isHiddenOrMin)
                        return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Displays a native Windows notification (toast / balloon).
    /// Creates the tray icon on demand if needed to show the notification in Windows 10/11.
    /// </summary>
    public void ShowNotification(string title, string message)
    {
        try
        {
            if (_hwnd == IntPtr.Zero && _mainWindow != null)
            {
                if (_mainWindow.Dispatcher.CheckAccess())
                {
                    _hwnd = new WindowInteropHelper(_mainWindow).Handle;
                }
                else
                {
                    _hwnd = _mainWindow.Dispatcher.Invoke(() => new WindowInteropHelper(_mainWindow).Handle);
                }
            }

            if (_hwnd == IntPtr.Zero)
                return;

            if (!_isCreated)
            {
                ShowTrayIcon();
            }

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001,
                uFlags = NIF_INFO,
                szInfo = message ?? string.Empty,
                szInfoTitle = title ?? string.Empty,
                dwInfoFlags = NIIF_INFO
            };

            Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }
        catch (Exception ex)
        {
            Storage.AppLogger.Error("Error showing completion notification", ex);
        }
    }

    public void ShowBalloon(string title, string message)
    {
        ShowNotification(title, message);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_isCreated && _hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _isCreated = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                var source = HwndSource.FromHwnd(_hwnd);
                source?.RemoveHook(WndProc);
            }
            catch { }
            _hwnd = IntPtr.Zero;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _mainWindow = null;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);
}
