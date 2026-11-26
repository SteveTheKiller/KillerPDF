using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KillerPDF.Controls
{
    /// <summary>
    /// A pane's document area with the active tool's options bar. Docked, the bar takes its own height
    /// at the top and the document gets the rest below, so the bar never covers the page. Floating, the
    /// document keeps the whole area and the bar hangs from its top edge over the page, where a drag on
    /// empty bar space slides it and a double-click minimizes it to a thin strip (#485). With no bar the
    /// document fills the whole area.
    /// </summary>
    [ContentProperty(nameof(Document))]
    public sealed class ToolOptionsDock : FrameworkElement
    {
        /// <summary>The inactive pane of a split shows its own tool's bar at this opacity.</summary>
        public const double InactiveOpacity = 0.55;

        /// <summary>Gap between a floating bar that has not been moved and the area's right edge.</summary>
        public const double FloatingGap = 8;

        /// <summary>Height of the strip a minimized floating bar leaves behind.</summary>
        public const double MinimizedHeight = 10;

        /// <summary>Radius of a floating bar's two bottom corners.</summary>
        public const double FloatingCornerRadius = 6;

        /// <summary>Radius of a minimized strip's two bottom corners.</summary>
        public const double MinimizedCornerRadius = 3;

        /// <summary>Width of the grip strip at a floating bar's left edge.</summary>
        public const double GripWidth = 12;

        /// <summary>Rows the rounded top corner of a floating bar under the tab strip takes, and how far
        /// in from the bar's side the pane's top border line starts.</summary>
        public const int TopCornerInset = 4;

        private UIElement? _bar, _document;
        private Border? _strip, _grip;
        private TopCorners? _corners;
        private bool _sealsTop;
        // A floating bar's own border, corners and padding, put back when it docks or is replaced.
        private (Border Bar, Thickness Border, CornerRadius Corners, Thickness Padding)? _plainChrome;
        private bool _dimmed, _floating, _minimized;
        private double? _centerFraction;
        private readonly List<UIElement> _attached = [];

        // A drag in progress: where the pointer started and where the bar's left edge was.
        private (double PointerX, double BarLeft)? _drag;

        /// <summary>Raised when the user slides or minimizes the floating bar, so the choice can be kept.</summary>
        public event EventHandler? FloatingStateChanged;

        public ToolOptionsDock()
        {
            PreviewMouseLeftButtonDown += OnPressed;
            PreviewMouseMove += OnMoved;
            PreviewMouseLeftButtonUp += OnReleased;
            // Only the dock's own capture ends a drag. LostMouseCapture bubbles, so a menu popup losing the
            // mouse would otherwise clear the drag and leave the dock holding the mouse for good (#467).
            LostMouseCapture += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) _drag = null; };
        }

        /// <summary>The document area: the scroller and everything layered over it.</summary>
        public UIElement? Document
        {
            get => _document;
            set => Swap(ref _document, value);
        }

        /// <summary>The options bar of the pane's tool, or null for a tool without one.
        /// A bar rebuilt while the keyboard was in it gets the focus back on the control with the
        /// same automation id, so pressing a swatch or a mode with Space keeps the user in place.</summary>
        public UIElement? Bar
        {
            get => _bar;
            set
            {
                if (ReferenceEquals(_bar, value)) return;
                // Only after a keyboard press: a mouse press hands focus back to where it was.
                string? focusedId = _bar is not null && _bar.IsKeyboardFocusWithin
                    && InputManager.Current.MostRecentInputDevice is KeyboardDevice
                    && Keyboard.FocusedElement is DependencyObject focused
                    ? FocusIdFor(focused) : null;
                if (_bar is not null)
                {
                    _bar.Visibility = Visibility.Visible;
                    _bar.Clip = null;   // a bar shown again later starts uncut
                }
                // The bar going out closes its menus, or one left open keeps the mouse with nothing under it (#467).
                (_bar as ToolOptionsBar)?.Retire();
                Swap(ref _bar, value);
                // Floating, the bar sits over the page; a native page window cuts it out of itself (ViewportOcclusion).
                if (value is not null) KillerPDF.Presentation.ViewportOcclusion.SetOccludes(value, true);
                if (value is not null)
                {
                    // Visibility stays as SyncBarVisibility left it: a minimized bar is clipped to its strip,
                    // never collapsed, or it measures nothing and leaves no strip to click (review BF-002).
                    value.Opacity = _dimmed ? InactiveOpacity : 1;
                }
                if (!string.IsNullOrEmpty(focusedId) && value is FrameworkElement bar)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        new Action(() => (FindById(bar, focusedId!) ?? FindById(bar, OverflowId))?.Focus()));
            }
        }

        /// <summary>True for the pane that does not have focus while the window is split.</summary>
        public bool Dimmed
        {
            get => _dimmed;
            set
            {
                _dimmed = value;
                if (_bar is not null) _bar.Opacity = value ? InactiveOpacity : 1;
                if (_corners is not null) _corners.Opacity = value ? InactiveOpacity : 1;
            }
        }

        /// <summary>
        /// True while the pane's tab strip sits on its top edge. A floating bar then hangs straight
        /// below the active tab, which paints over the pane's top border to open into the card, so the
        /// bar would merge into the tab with no edge between them. The bar rounds its top corners and
        /// reports in <see cref="TopSeal"/> where the pane's border line has to be drawn back.
        /// </summary>
        public bool SealsTop
        {
            get => _sealsTop;
            set
            {
                if (_sealsTop == value) return;
                _sealsTop = value;
                SyncBarVisibility();
                ReattachInDrawingOrder();
                InvalidateMeasure();
            }
        }

        private (double From, double To)? _openSpan;

        /// <summary>
        /// The active tab's left and right edges in the dock's coordinates, where that tab paints over
        /// the pane's top border; null when no tab opens into the card. A floating bar's top corner
        /// rounds only while it is under this span. Under an inactive tab the pane's border runs above
        /// the bar already, so the corner meets it square.
        /// </summary>
        internal (double From, double To)? OpenSpan
        {
            get => _openSpan;
            set
            {
                if (_openSpan == value) return;
                _openSpan = value;
                InvalidateArrange();
            }
        }

        /// <summary>Where the pane's top border line runs above a floating bar, left to right in the
        /// dock's own coordinates, on the row just above it; null while there is none.</summary>
        internal (double From, double To)? TopSeal { get; private set; }

        /// <summary>Raised when <see cref="TopSeal"/> changes.</summary>
        public event EventHandler? TopSealChanged;

        private bool ShowsCorners => _floating && _bar is not null && _sealsTop && _cornerRadius > 0;

        private void SetTopSeal((double From, double To)? seal)
        {
            if (TopSeal == seal) return;
            TopSeal = seal;
            TopSealChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// True to float the bar over the top edge of the document instead of docking it above, so
        /// choosing a tool never moves the page. (#485)
        /// </summary>
        public bool Floating
        {
            get => _floating;
            set
            {
                if (_floating == value) return;
                _floating = value;
                SyncBarVisibility();
                ReattachInDrawingOrder();
                InvalidateMeasure();
            }
        }

        /// <summary>True while a floating bar is minimized to its strip. Docked, a bar is never minimized.</summary>
        public bool Minimized
        {
            get => _floating && _minimized;
            set
            {
                if (_minimized == value) return;
                _minimized = value;
                SyncBarVisibility();
                ReattachInDrawingOrder();
                InvalidateMeasure();
            }
        }

        /// <summary>Where a slid floating bar's center sits, as a share of the area's width; null until it
        /// is moved, which keeps it against the right edge.</summary>
        public double? CenterFraction
        {
            get => _centerFraction;
            set { _centerFraction = value is double f ? Math.Clamp(f, 0, 1) : null; InvalidateArrange(); }
        }

        private double _cornerRadius = FloatingCornerRadius;

        /// <summary>Radius of a floating bar's bottom corners; 0 for a square theme such as 98SE. The minimized
        /// strip scales its own radius with it.</summary>
        public double CornerRadius
        {
            get => _cornerRadius;
            set
            {
                if (_cornerRadius == value) return;
                _cornerRadius = value;
                if (_plainChrome is { Bar: var bar }) bar.CornerRadius = new CornerRadius(0, 0, value, value);
                SyncBarVisibility();
                ReattachInDrawingOrder();
                InvalidateMeasure();
            }
        }

        private double StripRadius => _cornerRadius <= 0 ? 0 : MinimizedCornerRadius;

        /// <summary>The strip a minimized bar leaves behind, or null while none is shown.</summary>
        internal UIElement? MinimizedStrip => ShowsStrip ? _strip : null;

        private bool ShowsStrip => _bar is not null && Minimized;

        /// <summary>Minimizes a floating bar to its strip, or restores it.</summary>
        internal void ToggleMinimized()
        {
            if (!_floating) return;
            Minimized = !_minimized;
            FloatingStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Slides a floating bar so its left edge sits at <paramref name="left"/>, kept inside the area.</summary>
        internal void SlideTo(double left)
        {
            if (!_floating || _bar is null) return;
            double width = BarWidth(ActualWidth);
            double area = ActualWidth;
            if (area <= 0) return;
            double clamped = Math.Clamp(left, 0, Math.Max(0, area - width));
            CenterFraction = (clamped + width / 2) / area;
        }

        // As in 1.9.2, a minimized bar stays the thing you click: its controls are hidden and it is cut
        // down to a strip, and the grip dots are drawn over it without taking the click.
        /// <summary>The grip at a floating bar's left edge, or null while none is shown.</summary>
        internal UIElement? Grip => ShowsGrip ? _grip : null;

        private bool ShowsGrip => _floating && _bar is not null && !Minimized;

        // Floating, the bar hangs like a tab: side and bottom borders, rounded bottom corners, and room on
        // its left for the grip. Docked, it gets its own chrome back.
        private void SyncChrome()
        {
            if (_plainChrome is { } plain && (!_floating || !ReferenceEquals(plain.Bar, _bar)))
            {
                plain.Bar.BorderThickness = plain.Border;
                plain.Bar.CornerRadius = plain.Corners;
                plain.Bar.Padding = plain.Padding;
                plain.Bar.Clip = null;
                _plainChrome = null;
            }
            if (_floating && _bar is Border bar && _plainChrome is null)
            {
                _plainChrome = (bar, bar.BorderThickness, bar.CornerRadius, bar.Padding);
                bar.BorderThickness = new Thickness(1, 0, 1, 1);
                bar.CornerRadius = new CornerRadius(0, 0, _cornerRadius, _cornerRadius);
                bar.Padding = new Thickness(bar.Padding.Left + GripWidth, bar.Padding.Top, bar.Padding.Right, bar.Padding.Bottom);
            }
            if (ShowsGrip && _grip is null)
            {
                _grip = NewGrip();
                KillerPDF.Presentation.ViewportOcclusion.SetOccludes(_grip, true);
            }
            if (ShowsCorners && _corners is null)
            {
                _corners = new TopCorners { IsHitTestVisible = false, Opacity = _dimmed ? InactiveOpacity : 1 };
                KillerPDF.Presentation.ViewportOcclusion.SetOccludes(_corners, true);
                _corners.SetResourceReference(TopCorners.EdgeProperty, "PaneBorderBrush");
            }
        }

        /// <summary>
        /// The rounded top corners of a floating bar under the tab strip, drawn pixel by pixel in the
        /// pane border's color like the tab's foot (RetroTabBorder.DrawFlare), mirrored so a corner turns
        /// the other way. Per side: two pixels on the bar's first row, then one on each of the next two,
        /// one column in, before the side border takes over; the pane's line runs on from four in.
        /// An anti-aliased arc this small reads as dim half pixels, a break in the edge.
        /// </summary>
        private sealed class TopCorners : FrameworkElement
        {
            public static readonly DependencyProperty EdgeProperty = DependencyProperty.Register(
                nameof(Edge), typeof(Brush), typeof(TopCorners),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

            public Brush? Edge
            {
                get => (Brush?)GetValue(EdgeProperty);
                set => SetValue(EdgeProperty, value);
            }

            private bool _left, _right;

            public void SetSides(bool left, bool right)
            {
                if (_left == left && _right == right) return;
                _left = left;
                _right = right;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (Edge is not Brush edge) return;
                int w = (int)Math.Round(RenderSize.Width);
                if (w < 2 * TopCornerInset + 2) return;
                void Px(int x, int y, int cw = 1) => dc.DrawRectangle(edge, null, new Rect(x, y, cw, 1));
                if (_left) { Px(2, 0, 2); Px(1, 1); Px(1, 2); }
                if (_right) { Px(w - 4, 0, 2); Px(w - 2, 1); Px(w - 2, 2); }
            }
        }

        // A bar's rectangle with the pixels outside its rounded top corners cut away, so the card shows
        // there and the corner pixels drawn over it read as the bar's edge.
        private static Geometry CornerCut(double width, double height, bool left, bool right)
        {
            var cut = new GeometryGroup();
            if (left)
            {
                cut.Children.Add(new RectangleGeometry(new Rect(0, 0, 2, 1)));
                cut.Children.Add(new RectangleGeometry(new Rect(0, 1, 1, 2)));
            }
            if (right)
            {
                cut.Children.Add(new RectangleGeometry(new Rect(width - 2, 0, 2, 1)));
                cut.Children.Add(new RectangleGeometry(new Rect(width - 1, 1, 1, 2)));
            }
            var shape = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, width, height)), cut);
            shape.Freeze();
            return shape;
        }

        // Three stacked dots, the same dots 1.9.2's floating bar and the sidebar splitter use.
        private static Border NewGrip()
        {
            var dots = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // Lifted a pixel: centered exactly, the dots read low against the labels' text.
                Margin = new Thickness(0, 0, 0, 2),
                IsHitTestVisible = false,
            };
            for (int i = 0; i < 3; i++)
            {
                var dot = new Ellipse { Width = 3, Height = 3, Margin = new Thickness(0, 1.5, 0, 1.5) };
                dot.SetResourceReference(Shape.FillProperty, "MutedTextBrush");
                dots.Children.Add(dot);
            }
            return new Border { Child = dots, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        }

        private void SyncBarVisibility()
        {
            SyncChrome();
            if (_bar is null) return;
            _bar.Visibility = Visibility.Visible;
            bool minimized = ShowsStrip;
            if (_bar is Decorator { Child: UIElement content })
            {
                content.Opacity = minimized ? 0 : 1;
                content.IsHitTestVisible = !minimized;
            }
            if (!minimized) _bar.Clip = null;
            if (minimized && _strip is null)
            {
                _strip = NewStrip();
                KillerPDF.Presentation.ViewportOcclusion.SetOccludes(_strip, true);
            }
        }

        // The minimized strip: the bar's own colors with a row of grip dots, as in 1.9.2.
        private Border NewStrip()
        {
            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            for (int i = 0; i < 6; i++)
            {
                var dot = new Ellipse { Width = 3, Height = 3, Margin = new Thickness(2, 0, 2, 0) };
                dot.SetResourceReference(Shape.FillProperty, "MutedTextBrush");
                dots.Children.Add(dot);
            }
            return new Border { Child = dots, IsHitTestVisible = false };
        }

        // A rectangle with square top corners and rounded bottom ones: the top half of a plain rectangle
        // joined to a fully rounded one. A side flush against the pane's edge keeps its bottom corner
        // square, so a half-width rectangle covers that side's round.
        private static Geometry BottomRounded(double width, double height, double radius,
                                              bool squareLeft = false, bool squareRight = false)
        {
            var shape = new GeometryGroup { FillRule = FillRule.Nonzero };
            shape.Children.Add(new RectangleGeometry(new Rect(0, 0, width, Math.Max(0, height - radius))));
            shape.Children.Add(new RectangleGeometry(new Rect(0, 0, width, height), radius, radius));
            if (squareLeft) shape.Children.Add(new RectangleGeometry(new Rect(0, 0, width / 2, height)));
            if (squareRight) shape.Children.Add(new RectangleGeometry(new Rect(width / 2, 0, width / 2, height)));
            shape.Freeze();
            return shape;
        }

        private static Geometry Frozen(Geometry shape)
        {
            shape.Freeze();
            return shape;
        }

        private double BarWidth(double area) => _bar is null ? 0 : Math.Min(_bar.DesiredSize.Width, area);

        private double FloatingLeft(double area)
        {
            double width = BarWidth(area);
            double max = Math.Max(0, area - width);
            return _centerFraction is double f
                ? Math.Clamp(f * area - width / 2, 0, max)
                : Math.Max(0, area - width - FloatingGap);
        }

        // The automation id of the "..." button of an options bar.
        private const string OverflowId = "ToolOptionsMore";

        /// <summary>The automation id to focus in a rebuilt bar. A control inside the color chip's or
        /// the "..." menu's popup is not in the bar's visual tree and the popup closes with the old
        /// bar, so the id is the one of the button the popup opens from.</summary>
        internal static string? FocusIdFor(DependencyObject focused)
        {
            for (DependencyObject? d = focused; d is not null;
                 d = (d is Visual ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
                if (d is Popup { PlacementTarget: { } target })
                    return AutomationProperties.GetAutomationId(target);
            return AutomationProperties.GetAutomationId(focused);
        }

        private static UIElement? FindById(DependencyObject root, string id)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is UIElement element && AutomationProperties.GetAutomationId(element) == id && element.Focusable)
                    return element;
                if (FindById(child, id) is { } found) return found;
            }
            return null;
        }

        // ---- sliding and minimizing a floating bar ------------------------------------------------

        // Only empty bar space or the strip starts a drag or a double-click; a press on a swatch, slider,
        // box or button belongs to that control.
        private bool IsHandle(object source)
        {
            if (!_floating || source is not DependencyObject hit) return false;
            for (DependencyObject? d = hit; d is not null;
                 d = (d is Visual ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
            {
                if (ReferenceEquals(d, _strip)) return ShowsStrip;
                if (ReferenceEquals(d, _grip)) return ShowsGrip;
                // A press in a bar's menu reaches the dock through the popup's parent; it belongs to the menu (#467).
                if (d is Popup) return false;
                if (ReferenceEquals(d, _bar)) return true;
                if (d is ButtonBase or RangeBase or Selector or TextBoxBase or Thumb) return false;
                if (ReferenceEquals(d, this)) return false;
            }
            return false;
        }

        private void OnPressed(object sender, MouseButtonEventArgs e)
        {
            if (!IsHandle(e.OriginalSource)) return;
            e.Handled = true;
            if (e.ClickCount == 2) { _drag = null; ToggleMinimized(); return; }
            _drag = (e.GetPosition(this).X, FloatingLeft(ActualWidth));
            CaptureMouse();
        }

        private void OnMoved(object sender, MouseEventArgs e)
        {
            if (_drag is not { } drag || !IsMouseCaptured) return;
            SlideTo(drag.BarLeft + e.GetPosition(this).X - drag.PointerX);
        }

        private void OnReleased(object sender, MouseButtonEventArgs e)
        {
            if (_drag is not { } drag) return;
            // A fast drag can end before any move is reported; the release still lands the bar there.
            SlideTo(drag.BarLeft + e.GetPosition(this).X - drag.PointerX);
            _drag = null;
            ReleaseMouseCapture();
            FloatingStateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---- children and layout ----------------------------------------------------------------

        private void Swap(ref UIElement? slot, UIElement? value)
        {
            if (ReferenceEquals(slot, value)) return;
            if (slot is not null) RemoveLogicalChild(slot);
            slot = value;
            if (value is not null) AddLogicalChild(value);
            SyncBarVisibility();
            ReattachInDrawingOrder();
            InvalidateMeasure();
        }

        // Docked, the bar comes first so Tab reaches it just before the document, and Shift+Tab from the
        // document comes back to it; the two never overlap. Floating, the bar and its strip overlap the
        // page, so they are drawn last to stay on top.
        private IEnumerable<UIElement> InDrawingOrder()
        {
            if (!_floating)
            {
                if (_bar is not null) yield return _bar;
                if (_document is not null) yield return _document;
                yield break;
            }
            if (_document is not null) yield return _document;
            if (_bar is not null) yield return _bar;
            if (ShowsStrip && _strip is not null) yield return _strip;
            if (ShowsGrip && _grip is not null) yield return _grip;
            if (ShowsCorners && _corners is not null) yield return _corners;
        }

        // WPF composes children in the order they were attached, not the order GetVisualChild returns
        // later, so every change re-attaches them in the current order.
        private void ReattachInDrawingOrder()
        {
            foreach (UIElement child in _attached) RemoveVisualChild(child);
            _attached.Clear();
            foreach (UIElement child in InDrawingOrder())
            {
                AddVisualChild(child);
                _attached.Add(child);
            }
        }

        protected override int VisualChildrenCount => _attached.Count;

        protected override Visual GetVisualChild(int index) =>
            index >= 0 && index < _attached.Count ? _attached[index] : throw new ArgumentOutOfRangeException(nameof(index));

        protected override System.Collections.IEnumerator LogicalChildren =>
            new UIElement?[] { _bar, _document }.Where(e => e is not null).GetEnumerator();

        protected override Size MeasureOverride(Size available)
        {
            double barHeight = 0;
            if (_bar is not null)
            {
                _bar.Measure(new Size(available.Width, double.PositiveInfinity));
                barHeight = _bar.DesiredSize.Height;
            }
            if (ShowsStrip) _strip!.Measure(new Size(available.Width, MinimizedHeight));
            if (ShowsGrip) _grip!.Measure(new Size(GripWidth, barHeight));
            if (ShowsCorners) _corners!.Measure(new Size(available.Width, TopCornerInset));
            if (_floating) barHeight = 0;   // a floating bar takes no room from the document
            double rest = double.IsInfinity(available.Height) ? available.Height : Math.Max(0, available.Height - barHeight);
            _document?.Measure(new Size(available.Width, rest));
            double width = double.IsInfinity(available.Width)
                ? Math.Max(_bar?.DesiredSize.Width ?? 0, _document?.DesiredSize.Width ?? 0) : available.Width;
            double height = double.IsInfinity(available.Height)
                ? barHeight + (_document?.DesiredSize.Height ?? 0) : available.Height;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            double barHeight = _bar?.DesiredSize.Height ?? 0;
            if (_floating)
            {
                _document?.Arrange(new Rect(0, 0, final.Width, final.Height));
                if (_bar is not null)
                {
                    double left = FloatingLeft(final.Width), width = BarWidth(final.Width);
                    // Slid flush against either side of the pane, the bar drops its own border on that side so
                    // the pane's border is its edge; kept, the two lines read as one 2px edge.
                    // A top corner under the active tab rounds off; under an inactive tab it meets the
                    // pane's border square, and on the pane's own edge the pane's border is the bar's side.
                    double right = left + width;
                    bool flushLeft = left < 0.5, flushRight = right > final.Width - 0.5;
                    // Against either side the bottom corner on that side goes square, so the bar meets
                    // the pane's edge like a docked one instead of curling away from it.
                    if (_plainChrome is { Bar: var chromed } && ReferenceEquals(chromed, _bar))
                    {
                        var sides = new Thickness(flushLeft ? 0 : 1, 0, flushRight ? 0 : 1, 1);
                        if (chromed.BorderThickness != sides) chromed.BorderThickness = sides;
                        var corners = new CornerRadius(0, 0, flushRight ? 0 : _cornerRadius, flushLeft ? 0 : _cornerRadius);
                        if (chromed.CornerRadius != corners) chromed.CornerRadius = corners;
                    }
                    bool UnderOpen(double x) => _openSpan is var (from, to) && x >= from && x <= to;
                    bool arcLeft = ShowsCorners && !flushLeft && UnderOpen(left);
                    bool arcRight = ShowsCorners && !flushRight && UnderOpen(right);
                    Geometry? cut = arcLeft || arcRight
                        ? CornerCut(width, ShowsStrip ? MinimizedHeight : barHeight, arcLeft, arcRight) : null;
                    if (ShowsStrip)
                    {
                        Geometry rounded = BottomRounded(width, MinimizedHeight, StripRadius, flushLeft, flushRight);
                        _bar.Clip = cut is null ? rounded : Frozen(new CombinedGeometry(GeometryCombineMode.Intersect, rounded, cut));
                        if (_bar is Border source)
                        {
                            _strip!.BorderBrush = source.BorderBrush;
                            _strip.BorderThickness = new Thickness(flushLeft ? 0 : 1, 0, flushRight ? 0 : 1, 1);
                            _strip.CornerRadius = new CornerRadius(0, 0, flushRight ? 0 : StripRadius, flushLeft ? 0 : StripRadius);
                        }
                        _strip!.Clip = cut;
                        _strip.Arrange(new Rect(left, 0, width, MinimizedHeight));
                    }
                    else _bar.Clip = cut;
                    // Arranged after its clip is set: WPF takes an element's clip up when it arranges it.
                    _bar.Arrange(new Rect(left, 0, width, barHeight));
                    if (ShowsGrip) _grip!.Arrange(new Rect(left + 1, 0, GripWidth, Math.Max(0, barHeight - 1)));
                    if (ShowsCorners)
                    {
                        _corners!.SetSides(arcLeft, arcRight);
                        _corners.Arrange(new Rect(left, 0, width, TopCornerInset));
                    }
                    SetTopSeal(_sealsTop
                        ? (left + (arcLeft ? TopCornerInset : 0), left + width - (arcRight ? TopCornerInset : 0))
                        : null);
                }
                else SetTopSeal(null);
                return final;
            }
            SetTopSeal(null);
            _bar?.Arrange(new Rect(0, 0, final.Width, barHeight));
            _document?.Arrange(new Rect(0, barHeight, final.Width, Math.Max(0, final.Height - barHeight)));
            return final;
        }
    }
}
