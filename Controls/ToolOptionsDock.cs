using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// A pane's document area with the active tool's options bar docked above it. The bar takes
    /// its own height at the top and the document gets the rest below, so the bar never covers
    /// the page. With no bar the document fills the whole area.
    /// </summary>
    [ContentProperty(nameof(Document))]
    public sealed class ToolOptionsDock : FrameworkElement
    {
        /// <summary>The inactive pane of a split shows its own tool's bar at this opacity.</summary>
        public const double InactiveOpacity = 0.55;

        private UIElement? _bar, _document;
        private bool _dimmed;

        /// <summary>The document area: the scroller and everything layered over it.</summary>
        public UIElement? Document
        {
            get => _document;
            set => Swap(ref _document, value);
        }

        /// <summary>The docked options bar of the pane's tool, or null for a tool without one.
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
                Swap(ref _bar, value);
                if (value is not null) value.Opacity = _dimmed ? InactiveOpacity : 1;
                if (!string.IsNullOrEmpty(focusedId) && value is FrameworkElement bar)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        new Action(() => (FindById(bar, focusedId!) ?? FindById(bar, OverflowId))?.Focus()));
            }
        }

        /// <summary>Distance between a floating bar and the top of the document area.</summary>
        public const double FloatingInset = 8;

        private bool _floating;

        /// <summary>
        /// True to float the bar over the top of the document instead of docking it above, so choosing
        /// a tool never moves the page. (#485)
        /// </summary>
        public bool Floating
        {
            get => _floating;
            set
            {
                if (_floating == value) return;
                _floating = value;
                ReattachInDrawingOrder();
                InvalidateMeasure();
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
            }
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
                if (d is System.Windows.Controls.Primitives.Popup { PlacementTarget: { } target })
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

        private void Swap(ref UIElement? slot, UIElement? value)
        {
            if (ReferenceEquals(slot, value)) return;
            if (slot is not null) { RemoveVisualChild(slot); RemoveLogicalChild(slot); }
            slot = value;
            if (value is not null) AddLogicalChild(value);
            ReattachInDrawingOrder();
            InvalidateMeasure();
        }

        // WPF composes children in the order they were attached, not the order GetVisualChild returns
        // later, so a floating bar attached before the document was drawn under it and could not be
        // seen. Every change re-attaches both in the current order.
        private readonly List<UIElement> _attached = [];

        private void ReattachInDrawingOrder()
        {
            foreach (UIElement child in _attached) RemoveVisualChild(child);
            _attached.Clear();
            for (int i = 0; i < VisualChildrenCount; i++)
            {
                var child = (UIElement)GetVisualChild(i);
                AddVisualChild(child);
                _attached.Add(child);
            }
        }

        protected override int VisualChildrenCount => (_document is null ? 0 : 1) + (_bar is null ? 0 : 1);

        // Docked, the bar comes first so Tab reaches it just before the document, and Shift+Tab from the
        // document comes back to it; the two never overlap. Floating, the bar overlaps the page, so it is
        // drawn last to stay on top.
        protected override Visual GetVisualChild(int index)
        {
            UIElement? first = _floating ? _document ?? _bar : _bar ?? _document;
            UIElement? second = _floating ? (_document is null ? null : _bar) : (_bar is null ? null : _document);
            return index switch
            {
                0 when first is not null => first,
                1 when second is not null => second,
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };
        }

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
                    double width = Math.Min(_bar.DesiredSize.Width, final.Width);
                    _bar.Arrange(new Rect(Math.Max(0, (final.Width - width) / 2), FloatingInset, width, barHeight));
                }
                return final;
            }
            _bar?.Arrange(new Rect(0, 0, final.Width, barHeight));
            _document?.Arrange(new Rect(0, barHeight, final.Width, Math.Max(0, final.Height - barHeight)));
            return final;
        }
    }
}
