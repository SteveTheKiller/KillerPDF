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
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Window proc / Win32 interop (custom chrome, resize, DPI)
        // ============================================================

        private const int  WM_GETMINMAXINFO   = 0x0024;
        private const int  WM_DPICHANGED      = 0x02E0;
        private const int  WM_MOUSEHWHEEL     = 0x020E;
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

            if (msg == WM_NCCALCSIZE)
            {
                handled = WmNcCalcSize(hwnd, wParam, lParam);
                return IntPtr.Zero;
            }
            if (msg == WM_NCHITTEST)
            {
                handled = true;
                return new IntPtr(WmNcHitTest(hwnd, msg, wParam, lParam));
            }
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
            else if (msg == 0x0231) _inWindowSizeMove = true;    // WM_ENTERSIZEMOVE
            else if (msg == 0x0232) _inWindowSizeMove = false;   // WM_EXITSIZEMOVE
            else if (msg == WM_MOUSEHWHEEL)
            {
                // #196: WPF has no MouseHWheel event, so a precision touchpad's two-finger
                // horizontal scroll (and a mouse's tilt wheel) died at the HwndSource and the
                // document never panned sideways. Positive delta scrolls right.
                int hDelta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                ActiveViewer.ScrollHorizontalExt(hDelta);
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
                            ActiveViewer.RenderPage(0);
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                                (Action)(() => ActiveViewer.ReapplyGridOrFit()));
                            return;
                        }
                        int idx = PageList.SelectedIndex;
                        if (idx >= 0) ActiveViewer.RenderPage(idx);
                    }));
            }
            return IntPtr.Zero;
        }

        // ------------------------------------------------------------
        // Frame: real OS borders, custom caption
        // ------------------------------------------------------------
        // No WindowChrome. Windows owns the left, right and bottom borders; WM_NCCALCSIZE strips
        // only the caption so the title bar row sits at the top of the client area. Owning every
        // edge ourselves made a top or left drag jitter.

        private int _osFrameWidth;   // width of the OS border at the current DPI, from WM_NCCALCSIZE

        [StructLayout(LayoutKind.Sequential)]
        private struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }

        // Handles the wParam == TRUE form only.
        private bool WmNcCalcSize(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
        {
            if (wParam == IntPtr.Zero) return false;
            var p    = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lParam);
            var orig = p.rgrc0;                                  // proposed window rect
            if (WindowState == WindowState.Maximized || _fullScreen)
            {
                // Maximized: the OS hangs the border off screen, so clip the client to the work area.
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return true;
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                RECT b = _fullScreen ? info.rcMonitor : info.rcWork;
                p.rgrc0.left   = Math.Max(orig.left,   b.left);
                p.rgrc0.top    = Math.Max(orig.top,    b.top);
                p.rgrc0.right  = Math.Min(orig.right,  b.right);
                p.rgrc0.bottom = Math.Min(orig.bottom, b.bottom);
                Marshal.StructureToPtr(p, lParam, false);
                return true;
            }
            DefWindowProc(hwnd, WM_NCCALCSIZE, wParam, lParam);  // OS lays out the full frame
            p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(lParam);
            _osFrameWidth = p.rgrc0.left - orig.left;
            p.rgrc0.top   = orig.top;                            // drop the caption, keep the sides
            Marshal.StructureToPtr(p, lParam, false);
            return true;
        }

        private int WmNcHitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            // Screen coords; short cast keeps the sign on monitors left of or above the primary.
            long lp = lParam.ToInt64();
            int  mx = unchecked((short)(lp & 0xFFFF));
            int  my = unchecked((short)((lp >> 16) & 0xFFFF));

            // The top edge lost its OS border with the caption, so give it a grip of the same width.
            if (WindowState != WindowState.Maximized && !_fullScreen && _osFrameWidth > 0
                && GetWindowRect(hwnd, out RECT rc) && my < rc.top + _osFrameWidth)
            {
                if (mx <  rc.left  + _osFrameWidth) return HTTOPLEFT;
                if (mx >= rc.right - _osFrameWidth) return HTTOPRIGHT;
                return HTTOP;
            }

            // The native frame still has an invisible caption. Classify the visible custom title
            // bar first so those stale native button and caption zones cannot override it.
            if (TryGetTitleBarHit(mx, my, out int titleBarHit)) return titleBarHit;

            // Outside the custom title bar, let Windows answer for its real side and bottom borders.
            return DefWindowProc(hwnd, msg, wParam, lParam).ToInt32();
        }

        // Title-bar row is the caption, except over a control or the logo (scroll-wheel scaling).
        private bool TryGetTitleBarHit(int screenX, int screenY, out int hitTest)
        {
            hitTest = HTCLIENT;
            try
            {
                var pt  = PointFromScreen(new Point(screenX, screenY));
                // WM_NCHITTEST fires on every mouse move, so only walk the tree inside the bar.
                var bar = TitleBarBorder.TransformToAncestor(this)
                                        .TransformBounds(new Rect(TitleBarBorder.RenderSize));
                if (!bar.Contains(pt)) return false;
                hitTest = HTCAPTION;
                var res = VisualTreeHelper.HitTest(this, pt);
                DependencyObject? hit = res?.VisualHit;
                while (hit != null)
                {
                    if (hit is Control && !ReferenceEquals(hit, this)) hitTest = HTCLIENT;
                    if (ReferenceEquals(hit, LogoBar)) hitTest = HTCLIENT;
                    hit = VisualTreeHelper.GetParent(hit);
                }
                return true;
            }
            catch { return false; }   // not laid out yet; treat as client
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref info);
                RECT work = info.rcWork;
                RECT mon = info.rcMonitor;
                // Normal maximize respects the taskbar; F11 uses the whole monitor.
                RECT bounds = _fullScreen ? mon : work;
                mmi.ptMaxPosition.x = Math.Abs(bounds.left - mon.left);
                mmi.ptMaxPosition.y = Math.Abs(bounds.top - mon.top);
                mmi.ptMaxSize.x = Math.Abs(bounds.right - bounds.left);
                mmi.ptMaxSize.y = Math.Abs(bounds.bottom - bounds.top);
                // Windows supplies desktop-wide tracking limits. Do not shrink them to the
                // source monitor: a drag can maximize onto a larger monitor in the same move.
                mmi.ptMaxTrackSize.x = Math.Max(mmi.ptMaxTrackSize.x, mmi.ptMaxSize.x);
                mmi.ptMaxTrackSize.y = Math.Max(mmi.ptMaxTrackSize.y, mmi.ptMaxSize.y);
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

        [LibraryImport("user32.dll", EntryPoint = "MonitorFromWindow")]
        private static partial IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
        private static partial IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static partial IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCHITTEST     = 0x0084;
        private const int WM_NCCALCSIZE    = 0x0083;
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
        private const int ResizeBorder     = 12;

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
            Process.Start(new ProcessStartInfo(
                "https://github.com/SteveTheKiller/KillerPDF/releases/latest")
                { UseShellExecute = true });
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
        // the gap from the anchored edge is honored when there's room, otherwise reduced so the bar
        // never crosses the opposite edge. No-op until the bar has a measured width (PlaceAnnotationBar's
        // deferred pass positions it once laid out).
        private void PositionAnnotationBar(Border bar, Grid area)
        {
            // 98SE: classic toolbar band - flush, truly full width, no floating gaps and no slide
            // parking. The document scroller is inset by the band's height (SyncSe98BarInset), so
            // the vertical scrollbar starts BELOW the band instead of poking up beside its right
            // end. Restores the theme's own edge thickness and padding: SetBarDockedBorder below
            // writes hardcoded 1px borders and 4px padding straight over the BarEdgeThickness /
            // BarPadding resource references, which is what flattened the classic 2px light bevel
            // into a thin misplaced line.
            if (ThemeManager.Current == Theme.SE98)
            {
                bar.HorizontalAlignment = HorizontalAlignment.Stretch;
                bar.Margin = new Thickness(0);
                bar.SetResourceReference(Border.BorderThicknessProperty, "BarEdgeThickness");
                bar.SetResourceReference(Border.PaddingProperty, "BarPadding");
                bar.SetResourceReference(Border.CornerRadiusProperty, "AnnotationBarCornerRadius");
                // First pass runs before layout has measured the band; re-run once it has a height
                // so the scroller inset below lands on the real value.
                if (bar.ActualHeight <= 0)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        (Action)(() => { if (bar.Parent is Grid a) PositionAnnotationBar(bar, a); }));
                SyncSe98BarInset(bar, area);
                return;
            }
            SyncSe98BarInset(bar, area);   // clears a leftover 98SE inset after a theme switch
            double w = bar.ActualWidth;
            // The document's vertical scrollbar lives on the right edge of the area. Keep the bar clear
            // of it when it's showing; when it isn't, the bar can use the full edge.
            double sb = VerticalScrollBarInset();
            double maxLeft = Math.Max(0, area.ActualWidth - w);
            if (_annotBarCenterFrac is double frac)
            {
                // Center-parking needs a real measured width to place; edge anchors below don't, so they
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
                // otherwise honor the plain gap right up to the pane edge.
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

        // 98SE reserves the docked band's height as top margin on the pane's document scroller, so
        // the page and its vertical scrollbar start below the band (a classic toolbar strip) instead
        // of the bar floating over them. Every other theme (and a removed bar) resolves to 0, which
        // also clears a leftover inset after a theme switch. The scroller is looked up from the
        // bar's own area so the inset always lands on the pane the bar actually lives in.
        private static void SyncSe98BarInset(Border bar, Grid area, bool removing = false)
        {
            ScrollViewer? sv = null;
            foreach (object child in area.Children)
                if (child is ScrollViewer s) { sv = s; break; }
            if (sv is null) return;
            double inset = !removing && ThemeManager.Current == Theme.SE98 ? bar.ActualHeight : 0;
            if (Math.Abs(sv.Margin.Top - inset) > 0.5)
                sv.Margin = new Thickness(0, inset, 0, 0);
        }

        // Re-anchor a bar when its own size settles or changes (first measure, or the WrapPanel
        // dropping to a second row on a narrow pane) - the 98SE scroller inset must track the
        // band's real height, and the floating themes re-clamp against the new width.
        private void AnnotBarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Border b && b.Parent is Grid a) PositionAnnotationBar(b, a);
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
            // Only Windows 11 rounds the HWND; on Windows 10 rounded content would show a notch.
            bool squared = max || IsSnapped() || ThemeManager.Current == Theme.SE98 || !OsRoundsCorners();
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
                if (squared)
                    RootBorder.CornerRadius = new CornerRadius(0);
                else
                    RootBorder.SetResourceReference(Border.CornerRadiusProperty, "WindowCornerRadius");
                RootBorder.Margin         = new Thickness(0);
                // Only a maximized window drops the 1px frame (it's flush to every screen edge); a
                // snapped window keeps it so it still reads against the window beside it.
                RootBorder.BorderThickness = new Thickness(max || ThemeManager.Current == Theme.SE98 ? 0 : 1);
            }
            if (TitleBarBorder != null)
            {
                if (squared) TitleBarBorder.CornerRadius = new CornerRadius(0);
                else TitleBarBorder.SetResourceReference(Border.CornerRadiusProperty, "TitleBarCornerRadius");
            }
            if (FooterBorder != null)
            {
                if (squared) FooterBorder.CornerRadius = new CornerRadius(0);
                else FooterBorder.SetResourceReference(Border.CornerRadiusProperty, "FooterCornerRadius");
            }
            // The close tile owns only the window's top-right corner. Reusing the title bar's
            // full (top-left + top-right) radius rounded the tile's interior left edge too,
            // making its hover state look like a detached red cap instead of the window corner.
            var titleCorners = TryFindResource("TitleBarCornerRadius") is CornerRadius tc
                ? tc : new CornerRadius(0, 7, 0, 0);
            Resources["ChromeCloseCorner"] = squared
                ? new CornerRadius(0)
                : new CornerRadius(0, titleCorners.TopRight, 0, 0);

            // Retired: native OS shadow replaces the hand-cast one.
            if (WindowShadowBorder != null)
            {
                WindowShadowBorder.Visibility = Visibility.Collapsed;
                WindowShadowBorder.Effect     = null;
            }

            // Ask Windows 11 to round the HWND when floating, square when maximized/snapped. No-op
            // (caught) on Windows 10 and earlier, which simply keep square corners.
            ApplyWindowCorners(rounded: !squared);

            // This is the permanent resize hit target. Its child canvases choose dots or the
            // Win98 hatch; collapsing the parent also hid the hatch.
            ResizeGripDots?.Visibility = Visibility.Visible;
            UpdateRootClip();
        }

        // Windows 11 native rounded-corner toggle (DWMWA_WINDOW_CORNER_PREFERENCE = 33).
        private void ApplyWindowCorners(bool rounded)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int pref = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
                _osRoundsCorners = hr == 0;
            }
            catch { _osRoundsCorners = false; }   // pre-Win11 DWM: attribute unsupported, square corners
        }

        // Probed once; Windows 10 rejects the corner attribute.
        private bool? _osRoundsCorners;
        private bool OsRoundsCorners()
        {
            if (_osRoundsCorners is bool known) return known;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return true;            // no HWND yet; decide on the first real pass
            int pref = DWMWCP_DEFAULT;
            try { _osRoundsCorners = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0; }
            catch { _osRoundsCorners = false; }
            return _osRoundsCorners.Value;
        }

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DEFAULT    = 0;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND      = 2;

        [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const double ShadowMargin = 10;
        private bool _chromeSquared;   // true when maximized/snapped

        // Under WindowChrome the OS rounds the HWND itself, so content fills a square client rect and
        // needs no internal rounded clip. (A rounded clip here would expose dark corner triangles
        // against the now-square frame.) Kept as a no-op hook so existing call sites stay valid.
        private void UpdateRootClip()
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
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
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
