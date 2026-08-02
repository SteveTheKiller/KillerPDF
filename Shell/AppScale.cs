using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF
{
    // App-wide accessibility size, ported from KillerNotes: a LayoutTransform scale on the
    // chrome (toolbar row, sidebar, tab strip) grows or shrinks the UI crisply -
    // LayoutTransform reflows and re-rasterizes text rather than bitmap-stretching it. The
    // title bar and footer stay fixed, so the logo you scroll to drive this (MainWindow.xaml,
    // LogoBar) never moves. The document pane is deliberately NOT scaled: app size and page
    // zoom are two separate controls. Persisted app-wide ("AppScale").
    public partial class MainWindow
    {
        internal double _appScale = 1.0;
        private const double AppScaleMin = 0.7, AppScaleMax = 2.5, AppScaleStep = 0.02;

        // The sidebar column lives in the UNSCALED grid (screen px) while its content lays
        // out at screen/scale logical px. Every site that pushes a logical sidebar width
        // (SidebarMinOpen, SidebarMaxPages, the 24px collapse strip...) into the column
        // converts through this, so the sidebar's LOGICAL width holds steady across scales
        // and the thumbnails grow with the rest of the chrome instead of being squeezed.
        internal double SbPx(double logical) => logical * _appScale;

        private void InitAppScale()
        {
            if (double.TryParse(App.GetSetting("AppScale"), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double s))
                ApplyAppScale(s);
        }

        // Roll the wheel over the logo: one small step per notch (fine-grained, no big jumps).
        private void LogoBar_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ApplyAppScale(_appScale + (e.Delta > 0 ? AppScaleStep : -AppScaleStep), persist: true);
            e.Handled = true;
        }

        // The logo is marked IsHitTestVisibleInChrome (MainWindow.xaml) so the scroll wheel
        // reaches it for the zoom above - but that also takes it out of WindowChrome's native
        // caption, so window drag and double-click-maximize are restored here by hand.
        private void LogoBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeBtn_Click(this, new RoutedEventArgs());   // WindowChrome.cs
                e.Handled = true;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
                DragMove();
        }

        private void ApplyAppScale(double scale, bool persist = false)
        {
            double prev = _appScale;
            scale = Math.Round(Math.Max(AppScaleMin, Math.Min(AppScaleMax, scale)), 3);
            _appScale = scale;
            // CHROME ONLY: toolbar, sidebar, and tab strip scale; the document pane is
            // deliberately untouched, so the app size and the page zoom stay two separate
            // controls.
            var t = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);
            ToolbarRowBorder.LayoutTransform = t;
            SidebarOuterGrid.LayoutTransform = t;
            // BOTH panes: each carries its own strip, and scaling only the focused one would leave
            // the other pane's tabs at the previous size.
            Viewer.TabStripBorderCtl.LayoutTransform  = t;
            ViewerB.TabStripBorderCtl.LayoutTransform = t;
            // Keep the sidebar's LOGICAL width constant across the change: the column and
            // the saved widths are screen px, so grow them with the scale (see SbPx above).
            if (scale != prev && prev > 0)
            {
                double f = scale / prev;
                _savedPagesWidth    *= f;
                _savedOutlinesWidth *= f;
                if (_sidebarCol is { } col)
                {
                    if (col.Width.GridUnitType == GridUnitType.Pixel)
                        col.Width = new GridLength(col.Width.Value * f);
                    if (col.MinWidth > 0) col.MinWidth *= f;
                    if (!double.IsPositiveInfinity(col.MaxWidth)) col.MaxWidth *= f;
                }
            }
            if (persist)
            {
                App.SetSetting("AppScale", scale.ToString("0.###", CultureInfo.InvariantCulture));
                ShowScaleReadout(scale);
            }
        }

        // The readout is transient. Every wheel notch rewrites it and restarts the hold timer,
        // so the footer carries it while you are zooming and gives the line back a beat after
        // you stop. It still goes out through SetStatusHeld, because the chrome resize re-runs
        // the fit pipeline and its page/zoom status would otherwise stomp this the same frame
        // (MainWindow.xaml.cs SetStatus) - that hold is short and only covers the stomp.
        //
        // Whatever was showing before the first notch of a burst is snapshotted and put back,
        // but only if the readout is still the text on screen, so a status written after the
        // hold expired is never overwritten by a stale one. The restore assigns directly
        // rather than going through SetStatus: this is putting a line back, not reporting
        // something new, so it should not land in the crash breadcrumb a second time.
        //
        // Normal priority rather than the DispatcherTimer default of Background, so a busy
        // render cannot leave the readout parked on the footer.
        private System.Windows.Threading.DispatcherTimer? _appScaleHide;
        private string _appScaleStatusWas = string.Empty;
        private string _appScaleReadout   = string.Empty;

        private void ShowScaleReadout(double scale)
        {
            if (_appScaleHide is null)
            {
                _appScaleHide = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal)
                    { Interval = TimeSpan.FromSeconds(5) };
                _appScaleHide.Tick += (_, _) =>
                {
                    _appScaleHide!.Stop();
                    if (StatusText.Text == _appScaleReadout) StatusText.Text = _appScaleStatusWas;
                };
            }

            // Only the first notch of a burst snapshots; the rest are our own readout.
            if (!_appScaleHide.IsEnabled) _appScaleStatusWas = StatusText.Text;
            _appScaleHide.Stop();

            _appScaleReadout = string.Format(Loc("Str_St_AppSize"), (int)Math.Round(scale * 100));
            SetStatusHeld(_appScaleReadout);
            _appScaleHide.Start();
        }
    }
}
