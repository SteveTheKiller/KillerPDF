using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Shell
{
    /// <summary>
    /// APP-OWNED: KillerPDF's side of the kit app size (Shell/AppScale.cs). Only the chrome scales:
    /// the toolbar row, the sidebar, the footer document controls and both panes' tab strips. The
    /// document pane is deliberately untouched, so the app size and the page zoom stay two separate
    /// controls. The sidebar column keeps its logical width across a change (SbPx), and the
    /// readout is held against the fit pipeline's status.
    /// </summary>
    public partial class MainWindow
    {
        // The kit's text-mode switch targets ScaleHost. KillerPDF has no single scaled element, so
        // ScaleHost is the toolbar row, one of the five hosts; ApplyAppScaleTo below gives the
        // others the same text mode and transform.
        private FrameworkElement ScaleHost => ToolbarRowBorder;

        /// <summary>The current app size, for dialogs that open at the window's scale.</summary>
        internal double AppScale => _appScale;

        // The sidebar column lives in the UNSCALED grid (screen px) while its content lays
        // out at screen/scale logical px. Every site that pushes a logical sidebar width
        // (SidebarMinOpen, SidebarMaxPages, the 24px collapse strip...) into the column
        // converts through this, so the sidebar's LOGICAL width holds steady across scales
        // and the thumbnails grow with the rest of the chrome instead of being squeezed.
        internal double SbPx(double logical) => logical * _appScale;

        private double _appScaleApplied = 1.0;

        partial void ApplyAppScaleTo(double scale, ref bool handled)
        {
            handled = true;
            var t = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);
            // BOTH panes: each carries its own strip, and scaling only the focused one would leave
            // the other pane's tabs at the previous size.
            foreach (FrameworkElement host in new FrameworkElement[]
                     { ToolbarRowBorder, SidebarOuterGrid, FooterDocumentControls,
                       Viewer.TabStripBorderCtl, ViewerB.TabStripBorderCtl })
            {
                host.LayoutTransform = t;
                if (scale == 1.0)
                {
                    host.ClearValue(TextOptions.TextFormattingModeProperty);
                    host.ClearValue(TextOptions.TextRenderingModeProperty);
                }
                else
                {
                    TextOptions.SetTextFormattingMode(host, TextFormattingMode.Ideal);
                    TextOptions.SetTextRenderingMode(host, TextRenderingMode.Grayscale);
                }
            }
            Viewer.RescaleRestrictionNotice();
            ViewerB.RescaleRestrictionNotice();
            FitRailToColumn();

            // Keep the sidebar's LOGICAL width constant across the change: the column and
            // the saved widths are screen px, so grow them with the scale (see SbPx above).
            double prev = _appScaleApplied;
            _appScaleApplied = scale;
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
        }

        // The readout goes out through SetStatusHeld, because the chrome resize re-runs the fit
        // pipeline and its page/zoom status would otherwise stomp it the same frame; it also lands
        // in the crash breadcrumb. The restore assigns directly: it puts a line back rather than
        // reporting something new.
        private System.Windows.Threading.DispatcherTimer? _pdfScaleHide;
        private string _pdfScaleStatusWas = string.Empty;
        private string _pdfScaleReadout   = string.Empty;

        partial void ShowAppScaleReadout(double scale, ref bool handled)
        {
            handled = true;
            if (_pdfScaleHide is null)
            {
                _pdfScaleHide = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal)
                    { Interval = TimeSpan.FromSeconds(5) };
                _pdfScaleHide.Tick += (_, _) =>
                {
                    _pdfScaleHide!.Stop();
                    if (StatusText.Text == _pdfScaleReadout) StatusText.Text = _pdfScaleStatusWas;
                };
            }

            if (!_pdfScaleHide.IsEnabled) _pdfScaleStatusWas = StatusText.Text;
            _pdfScaleHide.Stop();

            _pdfScaleReadout = string.Format(CultureInfo.CurrentCulture, Loc("Str_St_AppSize"), (int)Math.Round(scale * 100));
            SetStatusHeld(_pdfScaleReadout);
            _pdfScaleHide.Start();
        }
    }
}
