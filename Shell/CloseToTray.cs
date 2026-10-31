#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

// ============================================================
// CLOSE TO SYSTEM TRAY
//
// An About card option, "Close to system tray". With it on, closing the main window hides it
// and leaves an icon in the notification area: a click on the icon brings the window back, and
// its menu has Open <App> and Exit. With it off, closing quits, as it always did. The icon is
// there only while the window is hidden. KillerShot, a tray app, keeps its own permanent icon
// and is on by default; every other app is off until the user turns it on.
//
// ---- Wiring it up -------------------------------------------------------------------------
//   1. Copy this file unchanged. The namespace is shared on purpose, like StartupRegistration.
//   2. Main window constructor: _tray = CloseToTray.Attach(this, "AppName", Loc,
//      () => GetSetting(CloseToTray.SettingName) == "1"). The callback is asked each time the
//      window closes, so the About checkbox takes effect at once.
//   2b. First line of the main window's OnClosing override:
//      if (_tray.HideInstead(e)) return;
//      It has to come before the close fade, a quit prompt or any save, so a hide runs none of
//      them and the window comes back exactly as it was. It never hides while the app is
//      shutting down (Exit, Application.Shutdown, sign-out), so those closes save as before.
//   3. About card: a checkbox under "Start with Windows", bound to the app's "CloseToTray"
//      setting (default off), with Str_About_CloseToTray and Str_About_CloseToTrayHint. The
//      icon's menu uses Str_Tray_Open ("Open {0}", the app name) and Str_Tray_Exit.
//   4. A second launch that asks the running copy to come forward calls CloseToTray.ShowWindow
//      on the main window, which also works while it is hidden in the tray.
//   5. The icon's Exit, and Exit from the app menu, go through CloseToTray.Exit: it brings the
//      window back and closes it the normal way, so the window's own quit and save prompts run
//      and can cancel. A confirmed close then closes the app's other main windows the same way,
//      one at a time, and shuts the app down after the last; a Cancel on any of them keeps that
//      window and the app open. An update or an uninstall calls Application.Shutdown, which
//      never hides to the tray.
//   6. Optional Attach arguments; apps that pass none behave exactly as before:
//      canExit   asked before a tray Exit and when Windows ends the session; false keeps the app
//                running (and cancels the session end), for unsaved work the user declined to lose.
//      notifyIcon a test seam standing in for Shell_NotifyIcon (NIM_ADD, NIM_DELETE), so tests
//                run without a real notification area icon.
//      items     the app's own rows in the icon's menu, above Open.
//      keepIcon  an app option that keeps the icon while the window is open too. Call
//                RefreshIcon after that option changes and once after attaching.
// ============================================================
namespace KillerFamily.Shell
{
    internal sealed class CloseToTray : IDisposable
    {
        /// <summary>The app setting that turns the option on, "1" or "0".</summary>
        internal const string SettingName = "CloseToTray";

        /// <summary>An app's own row in the icon's menu, above Open. Header is asked each time the
        /// menu opens, so a language switch shows at once.</summary>
        internal sealed record TrayItem(Func<string> Header, Action Run);

        private const int WM_APP = 0x8000;
        private const int CallbackMessage = WM_APP + 0x51;
        private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
        private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
        private const uint NIIF_INFO = 1, NIIF_NOSOUND = 0x10;
        private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
        private const int WM_ENDSESSION = 0x0016;

        private readonly Window _window;
        private readonly string _appName;
        private readonly Func<string, string> _loc;
        private readonly Func<bool> _isEnabled;
        private readonly HwndSource _source;
        private readonly uint _taskbarCreated;
        private readonly IntPtr _icon;
        private bool _iconShown, _exiting, _exitRequested, _disposed;

        // Every attached main window, so a confirmed Exit can close the others the normal way.
        private static readonly List<CloseToTray> s_open = [];

        // WPF's own shutdown flag. Application.Shutdown closes every window through OnClosing
        // and ignores a cancel, so without it a hide would skip the app's save and then close
        // anyway. Null only if a future WPF renames it; then a shutdown close hides first.
        private static readonly PropertyInfo? s_isShuttingDown =
            typeof(Application).GetProperty("IsShuttingDown", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>True when the WPF shutdown flag was found. Checked by FamilyContracts.</summary>
        internal static bool CanSeeShutdown => s_isShuttingDown?.PropertyType == typeof(bool);

        private static bool AppShuttingDown()
        {
            try { return s_isShuttingDown?.GetValue(null) is true; }
            catch { return false; }
        }

        /// <summary>Prepares the icon for <paramref name="window"/>, whose OnClosing override calls
        /// HideInstead. <paramref name="isEnabled"/> is asked on every close; <paramref name="loc"/>
        /// looks up Str_Tray_Open and Str_Tray_Exit.</summary>
        internal static CloseToTray Attach(Window window, string appName, Func<string, string> loc, Func<bool> isEnabled,
            Func<bool>? canExit = null, IReadOnlyList<TrayItem>? items = null, Func<bool>? keepIcon = null,
            Func<uint, bool>? notifyIcon = null)
            => new(window, appName, loc, isEnabled, canExit, items, keepIcon, notifyIcon);

        private readonly Func<bool>? _canExit;
        private readonly IReadOnlyList<TrayItem> _items;
        private readonly Func<bool>? _keepIcon;
        private readonly Func<uint, bool>? _notifyIcon;

        private CloseToTray(Window window, string appName, Func<string, string> loc, Func<bool> isEnabled,
            Func<bool>? canExit, IReadOnlyList<TrayItem>? items, Func<bool>? keepIcon, Func<uint, bool>? notifyIcon)
        {
            _canExit = canExit;
            _notifyIcon = notifyIcon;
            _items = items ?? [];
            _keepIcon = keepIcon;
            _window = window;
            _appName = appName;
            _loc = loc;
            _isEnabled = isEnabled;
            // WS_POPUP, never shown. A message-only window would miss TaskbarCreated, which is
            // how the icon comes back after Explorer restarts.
            _source = new HwndSource(new HwndSourceParameters(appName + "TrayIcon") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
            _source.AddHook(WndProc);
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            int size = GetSystemMetrics(49);   // SM_CXSMICON
            // The SDK embeds ApplicationIcon as icon group 32512.
            _icon = LoadImage(GetModuleHandle(null), new IntPtr(32512), 1, size, size, 0);
            window.Closed += WindowClosed;
            // The user's next click or key after Exit ends the exit. A prompt the user turned down
            // leaves the window open, and the next close hides to the tray again. A close that is
            // still running (a fade's second pass) arrives without input and still closes.
            window.PreviewMouseDown += EndExitRequest;
            window.PreviewKeyDown += EndExitRequest;
            Application.Current.SessionEnding += SessionEnding;
            Application.Current.Exit += ApplicationExit;
            s_open.Add(this);
        }

        private void WindowClosed(object? sender, EventArgs e)
        {
            bool quit = _exitRequested;
            Dispose();
            if (!quit) return;
            // The close Exit started went through. Close the next main window the normal way, so
            // its own save prompt runs and Cancel keeps it (and the app) open; Application.Shutdown
            // would ignore that Cancel. When the last one has closed, the app shuts down.
            if (s_open.Count > 0) s_open[0].CloseForExit();
            else Application.Current?.Shutdown();
        }

        private void CloseForExit()
        {
            ShowWindow();
            _exitRequested = true;
            _window.Close();
        }

        private void EndExitRequest(object sender, RoutedEventArgs e) => _exitRequested = false;

        private void SessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            if (e.Cancel) return;
            if (_canExit != null && !_canExit()) { e.Cancel = true; return; }
            _exiting = true;
            // Later SessionEnding work (an app's own unsaved-changes check) can still cancel the
            // session end. Windows then carries on, so the tray must too: once the request is over
            // and the app is not shutting down, Exit, the icon and hiding work again.
            Application.Current?.Dispatcher.InvokeAsync(() => { if (!_disposed && !AppShuttingDown()) _exiting = false; });
        }

        private void ApplicationExit(object sender, ExitEventArgs e) => Dispose();

        /// <summary>Quits the app from the icon or the app menu without hiding to the tray. The
        /// window comes back and closes the normal way, so its quit and save prompts run and can
        /// cancel; a confirmed close shuts the app down.</summary>
        internal void Exit()
        {
            if (_disposed || _exiting || _exitRequested) return;
            if (_canExit != null && !_canExit()) return;
            CloseForExit();
        }

        /// <summary>Brings the window back from the tray, the taskbar or behind other windows.</summary>
        internal void ShowWindow()
        {
            if (_disposed) return;
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            if (_keepIcon == null) HideIcon();
            else RefreshIcon();
        }

        /// <summary>Shows the icon while the app keeps it (keepIcon) or the window is in the tray,
        /// and hides it otherwise. Safe to call at any time.</summary>
        internal void RefreshIcon()
        {
            if (_disposed || _exiting) return;
            if (_keepIcon?.Invoke() == true || !_window.IsVisible) ShowIcon();
            else HideIcon();
        }

        /// <summary>Call first in the main window's OnClosing override. With the option on, cancels
        /// the close, hides the window, shows the icon and returns true, and the override returns
        /// at once. Returns false for a real close: the option off, Exit, or the app shutting down.
        /// If the icon cannot be added the close is still canceled but the window stays in view,
        /// so the app is never left running with no window and no icon.</summary>
        internal bool HideInstead(CancelEventArgs e)
        {
            if (_exiting || _exitRequested || _disposed || e.Cancel || AppShuttingDown() || !_isEnabled()) return false;
            e.Cancel = true;
            ShowIcon();
            if (_iconShown) _window.Hide();
            else ShowWindow();
            return true;
        }

        /// <summary>
        /// While the window is hidden in the tray, a notification from its icon - the way an app
        /// that is out of sight says something happened. Silent: the caller decides about sound.
        /// Clicking it brings the window back. False when the window is not in the tray.
        /// </summary>
        internal bool Notify(string title, string text)
        {
            if (!_iconShown || _disposed) return false;
            var d = Data(NIF_INFO);
            d.szInfoTitle = title.Length > 63 ? title[..63] : title;
            d.szInfo = text.Length > 255 ? text[..255] : text;
            d.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
            return Shell_NotifyIcon(NIM_MODIFY, ref d);
        }

        /// <summary>True while the window is hidden and its tray icon is showing.</summary>
        internal bool InTray => _iconShown && !_window.IsVisible;

        private void ShowIcon()
        {
            if (_iconShown) return;
            var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            _iconShown = _notifyIcon?.Invoke(NIM_ADD) ?? Shell_NotifyIcon(NIM_ADD, ref d);
        }

        private void HideIcon()
        {
            if (!_iconShown) return;
            var d = Data(0);
            _ = _notifyIcon?.Invoke(NIM_DELETE) ?? Shell_NotifyIcon(NIM_DELETE, ref d);
            _iconShown = false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == CallbackMessage)
            {
                int ev = (int)(lParam.ToInt64() & 0xFFFF);
                if (ev is WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT or NIN_BALLOONUSERCLICK) ShowWindow();
                else if (ev is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
                handled = true;
            }
            else if (msg == (int)_taskbarCreated && _iconShown)
            {
                _iconShown = false;   // Explorer restarted and dropped the icon
                ShowIcon();
                // The icon could not come back: bring the window back so the app is reachable.
                if (!_iconShown && !_window.IsVisible) ShowWindow();
            }
            // WM_ENDSESSION with wParam FALSE: the session end was canceled (by this app's own check
            // or another one), so the tray works again at once. The queued reset in SessionEnding
            // covers a cancel that arrives without this message.
            else if (msg == WM_ENDSESSION && wParam == IntPtr.Zero && !_disposed) _exiting = false;
            return IntPtr.Zero;
        }

        private void ShowMenu()
        {
            var menu = new ContextMenu();
            AnchorToPointer(menu);
            foreach (var item in _items)
            {
                var row = new MenuItem { Header = item.Header() };
                row.Click += (_, _) => item.Run();
                menu.Items.Add(row);
            }
            if (_items.Count > 0) menu.Items.Add(new Separator());
            var open = new MenuItem { Header = string.Format(_loc("Str_Tray_Open"), _appName), FontWeight = FontWeights.SemiBold };
            open.Click += (_, _) => ShowWindow();
            var exit = new MenuItem { Header = _loc("Str_Tray_Exit") };
            exit.Click += (_, _) => Exit();
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            // The hidden owner must be in front, or the menu does not close when clicking away.
            SetForegroundWindow(_source.Handle);
            menu.IsOpen = true;
        }

        /// <summary>
        /// The icon's menu opens the way Windows' own tray menus do: the card's bottom-left corner at
        /// the pointer, rising out of the notification area; to the pointer's left near the right
        /// edge of the screen, and downward under a taskbar at the top.
        ///
        /// MousePoint placement did not do this. It drops a menu below the pointer when it fits and
        /// flips it above when it does not, and the ContextMenu style's shadow-halo offsets are only
        /// right for the downward case, so a short menu covered the icons while a taller one floated
        /// about 40px above them: one placement, different in every app.
        ///
        /// With no PlacementTarget, a Custom placement is measured from the screen's top-left, in
        /// device pixels, so the candidates are the pointer itself less the card's halo.
        /// </summary>
        internal static void AnchorToPointer(ContextMenu menu)
        {
            if (!GetCursorPos(out POINT cursor)) { menu.Placement = PlacementMode.MousePoint; return; }
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = menu.VerticalOffset = 0;
            menu.CustomPopupPlacementCallback = (popupSize, _, _) =>
            {
                double scale = PresentationSource.FromVisual(menu)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                Thickness halo = menu.Template?.FindName("MenuRoot", menu) is FrameworkElement root ? root.Margin : new Thickness(22, 18, 22, 26);
                return Candidates(new Point(cursor.X, cursor.Y), popupSize, halo, scale);
            };
        }

        /// <summary>Where the menu may go, best first, in device pixels: its visible card two pixels
        /// from the pointer, above right, above left, below right, below left.</summary>
        internal static CustomPopupPlacement[] Candidates(Point pointer, Size popupSize, Thickness halo, double scale)
        {
            const double gap = 2;
            double rightX = pointer.X - (halo.Left - gap) * scale, leftX = pointer.X - popupSize.Width + (halo.Right - gap) * scale;
            double upY = pointer.Y - popupSize.Height + (halo.Bottom - gap) * scale, downY = pointer.Y - (halo.Top - gap) * scale;
            return
            [
                new CustomPopupPlacement(new Point(rightX, upY), PopupPrimaryAxis.None),
                new CustomPopupPlacement(new Point(leftX, upY), PopupPrimaryAxis.None),
                new CustomPopupPlacement(new Point(rightX, downY), PopupPrimaryAxis.None),
                new CustomPopupPlacement(new Point(leftX, downY), PopupPrimaryAxis.None),
            ];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);

        private NOTIFYICONDATA Data(uint flags) => new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _appName.Length > 127 ? _appName[..127] : _appName,
            szInfo = "",
            szInfoTitle = "",
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            s_open.Remove(this);
            HideIcon();
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _window.Closed -= WindowClosed;
            _window.PreviewMouseDown -= EndExitRequest;
            _window.PreviewKeyDown -= EndExitRequest;
            if (Application.Current is { } app)
            {
                app.SessionEnding -= SessionEnding;
                app.Exit -= ApplicationExit;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public int uCallbackMessage;
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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint load);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    }
}
