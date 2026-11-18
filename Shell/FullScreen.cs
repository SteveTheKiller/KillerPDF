using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        private bool _fullScreen;
        private GridLength _fsTitleRow, _fsFooterRow, _fsSidebarCol, _fsSplitterCol;
        private double _fsSidebarMin;
        private WindowState _fsPrevState;
        private bool _fsPrevTopmost;
        private ResizeMode _fsPrevResize;
        private double _fsPrevLeft, _fsPrevTop, _fsPrevW, _fsPrevH;
        private bool _fsAnimating;

        // The footer's full screen button. The footer hides with the rest of the chrome, so the
        // page menu's Full Screen item, F11 and Esc are the ways back.
        private void FullScreenRailBtn_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

        // F11 distraction-free mode: hides all chrome (title bar, toolbar, tab strip, sidebar, footer) and
        // grows the window over the whole monitor so just the document pane fills the screen on a dark-gray
        // backdrop. F11 or Esc exits. The switch happens under a black cross-fade so the resize never jumps.
        private void ToggleFullScreen()
        {
            if (_fsAnimating) return;   // ignore re-presses mid-transition
            _fsAnimating = true;
            bool entering = !_fullScreen;

            var cover = new Border { Background = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
            Grid.SetRow(cover, 0);
            Grid.SetRowSpan(cover, RootGrid.RowDefinitions.Count);
            Panel.SetZIndex(cover, 99998);
            RootGrid.Children.Add(cover);

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            fadeIn.Completed += (_, _2) =>
            {
                ApplyFullScreen(entering);
                // Reveal only after the resize/relayout has settled, so no edge of old layout flashes through.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                    fadeOut.Completed += (_, _3) =>
                    {
                        RootGrid.Children.Remove(cover);
                        _fsAnimating = false;
                        if (entering) ShowFullScreenHint();
                    };
                    cover.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }));
            };
            cover.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }

        private void ApplyFullScreen(bool entering)
        {
            // The split panes keep their RATIO across the size jump - OnSplitHostResized handles
            // every non-interactive resize (full screen included), so nothing extra is needed here.
            _fullScreen = entering;
            var v = entering ? Visibility.Collapsed : Visibility.Visible;

            TitleBarBorder.Visibility     = v;
            // Exit must respect the user's hide-toolbar setting (#215), not blanket-restore it.
            ToolbarRowBorder.Visibility   = entering || _toolbarHidden ? Visibility.Collapsed : Visibility.Visible;
            // BOTH panes - full screen hides every strip, not just the focused pane's.
            Viewer.TabStripBorderCtl.Visibility  = v;
            ViewerB.TabStripBorderCtl.Visibility = v;
            FooterBorder.Visibility       = v;
            SidebarOuterGrid.Visibility   = v;
            SidebarToggleStrip.Visibility = v;
            SidebarSplitter.Visibility    = v;

            // The document pane is a lifted card in normal use (RadCard corners, an 8px inset on
            // its outer side, PaneShadow). Full screen wants the canvas edge to edge, so the card
            // treatment comes off entirely and goes back on exit - otherwise a rounded corner and
            // an 8px strip of window background sit inside a "full screen" view.
            // The MARGIN is on the PdfViewer control, not on the card border inside it - the
            // control is what the layout positions.
            // ContentPane, not Viewer - the inset to the window edge belongs to the host that holds
            // both panes. See the note in ApplySidebarSide.
            ContentPane.Margin = entering ? new Thickness(0) : DocPaneInsetMargin();
            DocPaneBorder.CornerRadius = entering ? new CornerRadius(0)
                                                  : (CornerRadius)FindResource("RadCard");
            DocPaneBorder.BorderThickness = new Thickness(entering ? 0 : 1);
            // The shadow caster is inside the control now, so it needs no margin of its own -
            // hiding it is still right, so nothing casts onto a full-screen canvas.
            DocPaneShadow.Visibility = v;
            // A full-screen window can't be resized, so its corner grip goes too.
            ResizeGripDots.Visibility = v;

            if (entering)
            {
                _fsTitleRow   = RootGrid.RowDefinitions[0].Height;
                _fsFooterRow  = RootGrid.RowDefinitions[4].Height;
                _fsSidebarCol  = _sidebarCol.Width;
                _fsSidebarMin  = _sidebarCol.MinWidth;
                _fsSplitterCol = MainContentGrid.ColumnDefinitions[1].Width;
                RootGrid.RowDefinitions[0].Height = new GridLength(0);
                RootGrid.RowDefinitions[4].Height = new GridLength(0);
                // Collapse the ACTUAL sidebar column (_sidebarCol), which ApplySidebarSide repoints to DocCol
                // when the sidebar is on the right. MinWidth must drop to 0 too (it floors the width to 24
                // otherwise) and the splitter column (col 1) collapses, so the document fills the whole screen
                // with no leftover strip on either side, regardless of side or sidebar width.
                _sidebarCol.MinWidth = 0;
                _sidebarCol.Width = new GridLength(0);
                MainContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
                DocPaneBorder.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));   // dark-gray backdrop

                // Cover the whole monitor with explicit bounds. A maximized window is clamped to the work
                // area (taskbar stays visible), so instead we go Normal, size to the full monitor rect, and
                // set Topmost so the window sits above the always-on-top taskbar - true full screen.
                _fsPrevState = WindowState;
                _fsPrevTopmost = Topmost;
                _fsPrevResize = ResizeMode;
                _fsPrevLeft = Left; _fsPrevTop = Top; _fsPrevW = Width; _fsPrevH = Height;

                // Read the target monitor while still on it (before any WindowState change). Set the target
                // bounds FIRST, then drop to Normal: WPF restores to the just-set bounds, so the window lands
                // straight on this monitor instead of momentarily restoring to its old normal rect on another
                // screen (the "flash to another monitor"). Re-apply bounds after Normal to be certain.
                // Paint the window background black for the grow. When the window jumps from its small
                // rect to the full monitor, the newly-exposed area is filled by the window background
                // until the black cover re-lays-out over it. Black (instead of the gray BgDark) makes
                // that exposed edge match the cover, so entering no longer flashes a gray border the way
                // it did - shrinking on exit exposes nothing, which is why exit already looked clean.
                // Restored to BgDark on exit.
                Background = Brushes.Black;

                var b = CurrentMonitorBoundsPixels();
                Topmost = true;
                // #215: Topmost exists only to cover the always-on-top taskbar while KillerPDF is
                // the ACTIVE window. Held unconditionally, it sat over every other program the user
                // switched to. Yield it on deactivate, take it back on return - browser behavior.
                Deactivated += FsYieldTopmost;
                Activated   += FsReassertTopmost;
                ResizeMode = ResizeMode.NoResize;
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                ApplyFullScreenMonitorBounds(b);
                // Custom chrome and WPF both perform one more layout pass after the state change.
                // Reassert the native monitor rectangle after that pass so neither can leave the
                // border outside the physical display at fractional DPI scales.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        if (_fullScreen) ApplyFullScreenMonitorBounds(b);
                    }));
            }
            else
            {
                RootGrid.RowDefinitions[0].Height = _fsTitleRow;
                RootGrid.RowDefinitions[4].Height = _fsFooterRow;
                _sidebarCol.MinWidth = _fsSidebarMin;
                _sidebarCol.Width = _fsSidebarCol;
                MainContentGrid.ColumnDefinitions[1].Width = _fsSplitterCol;
                DocPaneBorder.SetResourceReference(Border.BackgroundProperty, "ContentPaneBrush");
                SetResourceReference(BackgroundProperty, "SurfaceBrush");   // undo the black grow-backdrop

                // Drop topmost and restore the pre-full-screen window placement. Restore the normal bounds
                // first (so WPF's remembered restore rect is correct) then re-maximize if it was maximized.
                Deactivated -= FsYieldTopmost;
                Activated   -= FsReassertTopmost;
                Topmost = _fsPrevTopmost;
                ResizeMode = _fsPrevResize;
                WindowState = WindowState.Normal;
                Left = _fsPrevLeft; Top = _fsPrevTop; Width = _fsPrevW; Height = _fsPrevH;
                if (_fsPrevState == WindowState.Maximized) WindowState = WindowState.Maximized;
            }

            // Re-apply the frame treatment: squared (no border, square corners, full clip) in full screen,
            // back to the floating border on exit. _fullScreen is already set, so UpdateWindowChrome reads it.
            UpdateWindowChrome();
        }

        // #215: full-screen topmost is active-window-only; see the enter branch above.
        private void FsYieldTopmost(object? sender, EventArgs e)    => Topmost = false;
        private void FsReassertTopmost(object? sender, EventArgs e) => Topmost = true;

        // Full native-pixel bounds of the monitor the window is currently on. Keeping this rectangle in
        // native coordinates avoids rounding errors between WPF units and per-monitor DPI scaling.
        private RECT CurrentMonitorBoundsPixels()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref info))
                throw new InvalidOperationException("Windows could not determine the current monitor bounds.");
            return info.rcMonitor;
        }

        private void ApplyFullScreenMonitorBounds(RECT bounds)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero,
                         bounds.left, bounds.top,
                         bounds.right - bounds.left, bounds.bottom - bounds.top,
                         SWP_NOZORDER | SWP_NOACTIVATE);
        }

        // Chrome-style toast: fades in near the top, holds, then fades out and removes itself.
        private void ShowFullScreenHint()
        {
            var toast = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1c, 0x1c, 0x1c)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(18, 9, 18, 9),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 44, 0, 0),
                Opacity = 0,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Opacity = 0.5 },
                Child = new TextBlock
                {
                    Text = string.Format(Loc("Str_FullScreen_Hint"), GestureFor("F11")), Foreground = Brushes.White, FontSize = 13,
                    FontFamily = UiKit.UiFont
                }
            };
            Grid.SetRow(toast, 0);
            Grid.SetRowSpan(toast, RootGrid.RowDefinitions.Count);
            Panel.SetZIndex(toast, 99999);
            RootGrid.Children.Add(toast);

            toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            t.Tick += (_, _2) =>
            {
                t.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                fade.Completed += (_, _3) => RootGrid.Children.Remove(toast);
                toast.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            t.Start();
        }
    }
}
