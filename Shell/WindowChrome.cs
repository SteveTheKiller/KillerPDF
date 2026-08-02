using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Window proc / Win32 interop (custom chrome, resize, DPI)
        // ============================================================

        private const int  WM_GETMINMAXINFO   = 0x0024;
        private const int  WM_DPICHANGED      = 0x02E0;
        private const int  WM_ENTERSIZEMOVE   = 0x0231;
        private const int  WM_EXITSIZEMOVE    = 0x0232;
        private const int  WM_ERASEBKGND      = 0x0014;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const uint SWP_NOZORDER       = 0x0004;
        private const uint SWP_NOACTIVATE     = 0x0010;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Themed system menu (Shell/SystemMenu.cs): swallow the caption right-click and
            // Alt+Space before anything else, or Windows draws its stock white HMENU.
            if (TryHandleSystemMenu(msg, wParam, lParam)) { handled = true; return IntPtr.Zero; }

            if (msg == WM_ERASEBKGND)
            {
                // WPF paints the whole client area itself, so let nothing erase the background to a flat
                // fill underneath it during a resize - that erase is a flash that reads as part of the
                // edge "jitter". Claim the message as handled and report success (1) without painting.
                handled = true;
                return new IntPtr(1);
            }
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            else if (msg == WM_DPICHANGED)
            {
                // Apply Windows' suggested rect so the window's apparent size is preserved
                // on the new monitor. handled stays false so WPF's HwndSource also processes
                // the message - updating its internal DPI scale and firing Window.DpiChanged.
                var r = Marshal.PtrToStructure<RECT>(lParam);
                SetWindowPos(hwnd, IntPtr.Zero, r.left, r.top,
                             r.right - r.left, r.bottom - r.top,
                             SWP_NOZORDER | SWP_NOACTIVATE);
                // Re-render at the new DPI. DispatcherPriority.Loaded fires after WPF has
                // finished its own DPI update, so VisualTreeHelper.GetDpi already reflects
                // the new scale factor when RenderPage calls it.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() =>
                    {
                        if (_doc is null) return;
                        if (_viewMode == ViewMode.Grid)
                        {
                            // Grid's primary tile (and the page-width basis the column math uses) is
                            // ALWAYS page 0 - rendering the selected page here would corrupt that basis
                            // and could collapse the grid to one column. Re-render page 0, then re-fit the
                            // columns to the new DPI/size so the grid is preserved across the monitor move.
                            RenderPage(0);
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                                (Action)ReapplyGridOrFit);
                            return;
                        }
                        int idx = PageList.SelectedIndex;
                        if (idx >= 0) RenderPage(idx);
                    }));
            }
            // WM_NCHITTEST is handled natively by WindowChrome.ResizeBorderThickness. The document scrollbar
            // is kept grabbable over the right resize band via WindowChrome.IsHitTestVisibleInChrome on the
            // ScrollBar style (App.xaml), which is the WindowChrome-native way to exempt an element from the
            // resize border - no manual hit-test override (that fought WindowChrome and killed scrollbar clicks).
            return IntPtr.Zero;
        }

        private int WmNcHitTest(IntPtr hwnd, IntPtr lParam)
        {
            // lParam is screen coords: lo-word = X, hi-word = Y.
            // Cast through short to preserve sign (handles negative coords on left/above primary monitor).
            long lp  = lParam.ToInt64();
            int  mx  = unchecked((short)(lp & 0xFFFF));
            int  my  = unchecked((short)((lp >> 16) & 0xFFFF));

            if (!GetWindowRect(hwnd, out RECT rc)) return 0;

            // The floating window has a transparent ShadowMargin around the visible content, so the resize
            // grips must sit at the CONTENT edge (inset by the margin), not the window edge - otherwise you
            // have to reach out into the shadow to resize. Maximized/snapped has no margin.
            int sm = _chromeSquared ? 0 : (int)ShadowMargin;
            bool onLeft   = mx >= rc.left   + sm                 && mx <  rc.left   + sm + ResizeBorder;
            bool onRight  = mx <  rc.right  - sm                 && mx >= rc.right  - sm - ResizeBorder;
            bool onTop    = my >= rc.top    + sm                 && my <  rc.top    + sm + ResizeBorder;
            bool onBottom = my <  rc.bottom - sm                 && my >= rc.bottom - sm - ResizeBorder;

            // Never hijack a scrollbar for window resizing. The vertical scrollbar sits flush
            // against the window's right edge, so the resize border used to swallow it - the
            // cursor showed the resize arrow and dragging resized the window instead of moving
            // the thumb. If a ScrollBar is under the cursor, report client area so it stays grabbable.
            if ((onLeft || onRight || onTop || onBottom) && IsOverScrollBar(mx, my))
                return HTCLIENT;

            if (onTop    && onLeft)  return HTTOPLEFT;
            if (onTop    && onRight) return HTTOPRIGHT;
            if (onBottom && onLeft)  return HTBOTTOMLEFT;
            if (onBottom && onRight) return HTBOTTOMRIGHT;
            if (onLeft)              return HTLEFT;
            if (onRight)             return HTRIGHT;
            if (onTop)               return HTTOP;
            if (onBottom)            return HTBOTTOM;

            return 0;
        }

        // Hit-tests the visual tree at a screen point (physical pixels from WM_NCHITTEST)
        // and reports whether a ScrollBar sits under the cursor.
        private bool IsOverScrollBar(int screenX, int screenY)
        {
            try
            {
                var pt  = PointFromScreen(new Point(screenX, screenY));
                var res = VisualTreeHelper.HitTest(this, pt);
                DependencyObject? hit = res?.VisualHit;
                while (hit != null)
                {
                    if (hit is System.Windows.Controls.Primitives.ScrollBar) return true;
                    hit = VisualTreeHelper.GetParent(hit);
                }
            }
            catch { /* best-effort; fall through to normal resize handling */ }
            return false;
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(monitor, ref info);
                RECT work = info.rcWork;
                RECT mon = info.rcMonitor;
                // Normal maximize respects the taskbar (work area). F11 full screen needs the whole monitor:
                // ptMaxTrackSize caps how large the window can ever be sized, so without this the explicit
                // full-screen bounds get silently clamped back to the work area (taskbar stays visible).
                RECT bounds = _fullScreen ? mon : work;
                mmi.ptMaxPosition.x = Math.Abs(bounds.left - mon.left);
                mmi.ptMaxPosition.y = Math.Abs(bounds.top - mon.top);
                mmi.ptMaxSize.x = Math.Abs(bounds.right - bounds.left);
                mmi.ptMaxSize.y = Math.Abs(bounds.bottom - bounds.top);
                mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;
                // Enforce the window's MinWidth/MinHeight during user resize. The custom chrome
                // marks WM_GETMINMAXINFO handled, so WPF's own minimum enforcement is bypassed.
                try
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    if (MinWidth  > 0 && !double.IsInfinity(MinWidth))  mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWidth  * dpi.DpiScaleX);
                    if (MinHeight > 0 && !double.IsInfinity(MinHeight)) mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
                }
                catch { /* DPI not available yet; skip min enforcement for this pass */ }
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCHITTEST     = 0x0084;
        private const int HTCLIENT         = 1;
        private const int HTCAPTION        = 2;
        private const int HTLEFT           = 10;
        private const int HTRIGHT          = 11;
        private const int HTTOP            = 12;
        private const int HTTOPLEFT        = 13;
        private const int HTTOPRIGHT       = 14;
        private const int HTBOTTOM         = 15;
        private const int HTBOTTOMLEFT     = 16;
        private const int HTBOTTOMRIGHT    = 17;
        private const int ResizeBorder     = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ============================================================
        // Window chrome
        // ============================================================

        // internal: each pane's tab strip forwards its empty-space drag here.
        internal void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeBtn_Click(sender, e);
                return;
            }
            // Delegate drag to Windows via WM_NCLBUTTONDOWN(HTCAPTION).
            // This gives native restore-from-maximized-and-drag behavior:
            // if the window is maximized, Windows restores it and follows the cursor
            // exactly as a native title bar would.
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }

        // Custom bottom-right grip: forward a native bottom-right resize so it behaves exactly like the OS
        // border resize (and stays smooth). Only when floating; maximized/snapped don't resize.
        private void ResizeGrip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (WindowState != WindowState.Normal) return;
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTBOTTOMRIGHT), IntPtr.Zero);
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            // Two checkboxes, matching Killendar and KillerShell: the desktop shortcut (on by
            // default, as it always was) and the all-users install (off by default - it needs a
            // UAC prompt, so it has to be something the user deliberately asks for).
            var (confirmed, wantDesktop, allUsers) = KillerDialog.ShowTwoCheckPrompt(this,
                Loc("Str_Dlg_InstallMsg"),
                Loc("Str_Chk_Desktop"),  check1Initial: true,
                Loc("Str_Chk_AllUsers"), check2Initial: false,
                Loc("Str_Btn_DoInstall"),
                Loc("Str_Btn_Cancel"));
            if (!confirmed) return;

            // Hide the badge immediately so it doesn't flash if relaunch is slow
            _portableBadge.Visibility = Visibility.Collapsed;

            if (!App.InstallAndRelaunch(_currentFile, wantDesktop, allUsers))
            {
                // Elevation refused, or the copy failed - both already reported. Put the badge back
                // so the session carries on rather than silently looking installed.
                _portableBadge.Visibility = App.IsPortable() ? Visibility.Visible : Visibility.Collapsed;
                SetStatus(Loc("Str_Status_InstallFailed"));
            }
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // Rounded window corners look right only when floating; a maximized OR snapped window must
        // square off or the rounded corners reveal the desktop / adjacent window behind them.
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            UpdateWindowChrome();
            RepositionAnnotationBars();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateWindowChrome();
            RepositionAnnotationBars();
        }

        // Re-applies the saved placement to every visible annotation bar. Called synchronously from the
        // window events that resize/move the content area (resize, maximize/restore, move), so
        // the bar tracks its anchored edge and stays fully on-screen through all of them.
        internal void RepositionAnnotationBars()
        {
            if (PagePreviewPanel?.Parent is not Grid area) return;
            foreach (var bar in new[] { _drawSettingsBar, _textSettingsBar })
                if (bar is not null && bar.Visibility == Visibility.Visible)
                    PositionAnnotationBar(bar, area);
        }

        // Anchors a bar to whichever edge it sits nearer and clamps it fully inside the document area:
        // the gap from the anchored edge is honoured when there's room, otherwise reduced so the bar
        // never crosses the opposite edge. No-op until the bar has a measured width (PlaceAnnotationBar's
        // deferred pass positions it once laid out).
        private void PositionAnnotationBar(Border bar, Grid area)
        {
            double w = bar.ActualWidth;
            // The document's vertical scrollbar lives on the right edge of the area. Keep the bar clear
            // of it when it's showing; when it isn't, the bar can use the full edge.
            double sb = VerticalScrollBarInset();
            double maxLeft = Math.Max(0, area.ActualWidth - w);
            if (_annotBarCenterFrac is double frac)
            {
                // Centre-parking needs a real measured width to place; edge anchors below don't, so they
                // must still run on a freshly-rebuilt (unmeasured) bar - otherwise a same-tool refresh
                // (e.g. clicking Bold) reveals the new bar at the default right edge, over the scrollbar.
                if (w <= 0) return;
                // Parked away from both edges: keep the same fraction of the width so it scales smoothly
                // with the window instead of lurching toward an edge. Clamp so it never slides under the
                // scrollbar on the right.
                double maxLeftCentered = Math.Max(0, maxLeft - sb);
                double left = Math.Max(0, Math.Min(maxLeftCentered, frac * area.ActualWidth - w / 2));
                bar.HorizontalAlignment = HorizontalAlignment.Left;
                bar.Margin = new Thickness(left, bar.Margin.Top, 0, 0);
                SetBarDockedBorder(bar, dockedLeft: false, dockedRight: false);
            }
            else if (_annotBarAnchorRight)
            {
                // Sit the bar against the scrollbar's left edge when it's present (gap + scrollbar width),
                // otherwise honour the plain gap right up to the pane edge.
                double g = Math.Min(maxLeft, (_annotBarGap ?? 8) + sb);
                bar.HorizontalAlignment = HorizontalAlignment.Right;
                bar.Margin = new Thickness(0, bar.Margin.Top, g, 0);
                // Only merge with the pane's edge line when nothing (no scrollbar) sits between them.
                SetBarDockedBorder(bar, dockedLeft: false, dockedRight: sb <= 0 && g <= 0.5);
            }
            else
            {
                double g = Math.Min(maxLeft, _annotBarGap ?? 8);
                bar.HorizontalAlignment = HorizontalAlignment.Left;
                bar.Margin = new Thickness(g, bar.Margin.Top, 0, 0);
                SetBarDockedBorder(bar, dockedLeft: g <= 0.5, dockedRight: false);
            }
        }

        // Width reserved by the document pane's vertical scrollbar (matches the ScrollBar style's fixed
        // 12px in MainWindow.xaml). Zero when the scrollbar isn't currently shown, so a docked bar can
        // reach the pane edge; otherwise the bar stops at the scrollbar's left edge.
        private const double DocScrollBarWidth = 12;
        private double VerticalScrollBarInset() =>
            PagePreviewPanel?.ComputedVerticalScrollBarVisibility == Visibility.Visible ? DocScrollBarWidth : 0;

        // When the bar is docked flush against a side, drop its own 1px border on that side and swap it
        // for 1px of padding. The document pane's border (same brush) then serves as the single shared
        // edge line - no 2px double border, and no size or position change (so nothing jumps).
        private static void SetBarDockedBorder(Border bar, bool dockedLeft, bool dockedRight)
        {
            bar.BorderThickness = new Thickness(dockedLeft ? 0 : 1, 0, dockedRight ? 0 : 1, 1);
            bar.Padding = new Thickness(dockedLeft ? 5 : 4, 4, dockedRight ? 5 : 4, 4);
        }

        // Snapping changes the window's position/size but NOT its WindowState (it stays Normal), so
        // re-evaluate the chrome on move too - otherwise a window snapped to a screen half keeps its
        // rounded corners. (Hooked once in the constructor.)
        private void OnWindowLocationChanged(object? sender, EventArgs e)
        {
            UpdateWindowChrome();
            RepositionAnnotationBars();
        }

        // Applies the frame border and corner treatment for the current window layout.
        // Under WindowChrome the window is a real (opaque, GPU-composited) HWND: the OS draws the
        // drop shadow, and on Windows 11 the OS rounds the window corners (via DwmSetWindowAttribute
        // below). So the app content fills a SQUARE client rect - the old transparent shadow margin,
        // the fake WindowShadowBorder silhouette, and the internal rounded clip are all retired.
        private bool? _appliedSquared;   // last state pushed to the chrome; guards per-frame churn
        private void UpdateWindowChrome()
        {
            bool max     = WindowState == WindowState.Maximized || _fullScreen;
            bool squared = max || IsSnapped();
            _chromeSquared = squared;

            // The chrome treatment depends ONLY on the maximized/snapped state, not on the live size
            // (the size-dependent rounded clip was retired with the WindowChrome migration). So skip the
            // whole body - including the DwmSetWindowAttribute call and the property writes - while the
            // state is unchanged. This is what was firing a native DWM corner call on every resize frame
            // and making the toolbar/sidebar jump as content fell behind the window edge.
            if (_appliedSquared == squared) return;
            _appliedSquared = squared;

            // Content fills the window rectangle. Rounding is done by the OS on the HWND, not here,
            // so internal corners stay square to avoid dark nubs peeking past the rounded window edge.
            if (RootBorder != null)
            {
                RootBorder.CornerRadius   = new CornerRadius(0);
                RootBorder.Margin         = new Thickness(0);
                // Only a maximized window drops the 1px frame (it's flush to every screen edge); a
                // snapped window keeps it so it still reads against the window beside it.
                RootBorder.BorderThickness = new Thickness(max ? 0 : 1);
            }
            if (TitleBarBorder != null) TitleBarBorder.CornerRadius = new CornerRadius(0);
            if (FooterBorder   != null) FooterBorder.CornerRadius   = new CornerRadius(0);
            Resources["ChromeCloseCorner"] = new CornerRadius(0);

            // Retired: native OS shadow replaces the hand-cast one.
            if (WindowShadowBorder != null)
            {
                WindowShadowBorder.Visibility = Visibility.Collapsed;
                WindowShadowBorder.Effect     = null;
            }

            // Ask Windows 11 to round the HWND when floating, square when maximized/snapped. No-op
            // (caught) on Windows 10 and earlier, which simply keep square corners.
            ApplyWindowCorners(rounded: !squared);

            // The grip dots stay visible in every window state, matching the site statusbar (family
            // standard). ResizeGrip_MouseDown still no-ops unless the window is floating, so when
            // maximized/snapped they're purely decorative.
            if (ResizeGripDots != null) ResizeGripDots.Visibility = Visibility.Visible;
            UpdateRootClip(squared);
        }

        // Windows 11 native rounded-corner toggle (DWMWA_WINDOW_CORNER_PREFERENCE = 33).
        private void ApplyWindowCorners(bool rounded)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int pref = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* pre-Win11 DWM: attribute unsupported, square corners */ }
        }

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND      = 2;

        [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const double ShadowMargin = 10;
        private bool _chromeSquared;   // true when maximized/snapped

        // Under WindowChrome the OS rounds the HWND itself, so content fills a square client rect and
        // needs no internal rounded clip. (A rounded clip here would expose dark corner triangles
        // against the now-square frame.) Kept as a no-op hook so existing call sites stay valid.
        private void UpdateRootClip(bool squared)
        {
            if (RootClipGrid is null) return;
            RootClipGrid.Clip = null;
        }

        // True when the window is Aero-Snapped (half/quarter screen). Snapping leaves WindowState
        // == Normal, so it's detected by comparing the window rect to the monitor work area: a
        // snapped window is flush to a work-area edge and smaller than the full work area.
        private bool IsSnapped()
        {
            if (WindowState != WindowState.Normal) return false;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT w)) return false;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref info)) return false;
            RECT a = info.rcWork;

            const int tol = 2; // device-pixel tolerance for "flush to edge"
            bool flushLeft   = Math.Abs(w.left   - a.left)   <= tol;
            bool flushRight  = Math.Abs(w.right  - a.right)  <= tol;
            bool flushTop    = Math.Abs(w.top    - a.top)    <= tol;
            bool flushBottom = Math.Abs(w.bottom - a.bottom) <= tol;
            bool fillsWidth  = Math.Abs((w.right - w.left) - (a.right - a.left)) <= tol;
            bool fillsHeight = Math.Abs((w.bottom - w.top) - (a.bottom - a.top)) <= tol;

            // Exactly the work area (sized full but not maximized) is not a snap.
            if (fillsWidth && fillsHeight) return false;
            // Left/right half: full height, flush to one vertical edge, narrower than the work area.
            if (flushTop && flushBottom && (flushLeft || flushRight) && !fillsWidth) return true;
            // Quarter snap: flush into a corner and smaller than the work area in at least one axis.
            if ((flushLeft || flushRight) && (flushTop || flushBottom) && (!fillsWidth || !fillsHeight))
                return true;
            return false;
        }

        private bool _fadingOut;

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Second pass (our own Close after the fade): let it through.
            if (_fadingOut) { base.OnClosing(e); return; }

            // Fold the live (active-tab) dirty flag back into its session, then prompt once if
            // any open tab has unsaved changes.
            // Capture BOTH panes' live state, then ask across BOTH. `_sessions` is only the focused
            // pane, so testing it alone quits without a word while the other pane holds unsaved
            // edits - a data-loss bug.
            Viewer.CaptureActiveIfAny();
            ViewerB.CaptureActiveIfAny();
            bool anyDirty = _isDirty || AllSessions().Any(s => s.IsDirty);
            if (anyDirty)
            {
                // fadeClose:false so the prompt closes instantly instead of adding its own 150ms fade
                // before the app's fade-out starts - otherwise the two run back-to-back (300ms of waiting).
                // Default to No so a stray Enter can't silently discard unsaved work.
                var res = KillerDialog.Show(this,
                    Loc("Str_Dlg_UnsavedExit"),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, fadeClose: false,
                    defaultResult: MessageBoxResult.No);
                if (res != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            // #105, KillerFind-style (family standard): ONE quit prompt with two opt-out
            // checkboxes replaces the old Yes=forget / No=reopen question. Unchecked
            // "Close my open tabs" = the session reopens next launch; "Remember my choice"
            // locks the answer so we stop asking. Cancel keeps the app open.
            // Only asked when a document is actually open (loaded, or a lazy not-yet-loaded
            // restored tab) - with nothing open there are no tabs to close or reopen, so the
            // empty window just quits.
            bool anyOpenDoc = AllSessions().Any(s =>
                s.Doc != null || !string.IsNullOrEmpty(s.CurrentFile) || !string.IsNullOrEmpty(s.DeferredPath));
            // A confirmed "close without saving" already IS the quit confirmation - never stack
            // the quit prompt on top of it (one dialog max per close). The open-tabs / remember
            // preference just keeps its saved value for that close.
            if (!anyDirty && anyOpenDoc && App.GetSetting("RememberChoiceLocked") != "1")
            {
                var (confirmed, closeTabs, remember) = KillerDialog.ShowQuitPrompt(this,
                    Loc("Str_Dlg_QuitMsg"),
                    Loc("Str_Chk_CloseTabs"), App.GetSetting("RememberOpenFiles") == "0",
                    Loc("Str_Dlg_RememberChoice"),
                    Loc("Str_Btn_Quit"), Loc("Str_Btn_CancelDlg"));
                if (!confirmed)
                {
                    e.Cancel = true;
                    return;
                }
                App.SetSetting("RememberOpenFiles", closeTabs ? "0" : "1");
                if (remember) App.SetSetting("RememberChoiceLocked", "1");
            }
            SaveWindowSettings();
            // Fade the whole app out before it really closes (matches the dialog fade-out).
            e.Cancel = true;
            _fadingOut = true;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                Opacity, 0, new Duration(TimeSpan.FromMilliseconds(WindowFx.FadeMs)))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            anim.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, anim);
        }
    }
}
