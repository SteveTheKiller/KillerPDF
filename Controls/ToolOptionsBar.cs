using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// The docked options row of one tool (redesign section 3): 34 tall, one line that never
    /// wraps, the tool's groups in row order with a separator between them, and a "..." menu at
    /// the right end for the groups the pane is too narrow to hold. Which groups stay is decided by
    /// <see cref="ToolOptionsLayout.Assign"/> from widths measured once and frozen.
    /// </summary>
    public sealed class ToolOptionsBar : Border
    {
        /// <summary>Row height; every control in it is 24 tall and centered.</summary>
        public const double RowHeight = 34;

        private sealed class Group(string id, FrameworkElement content, ButtonBase? compact, bool overflowOnly)
        {
            public string Id { get; } = id;
            public FrameworkElement Content { get; } = content;
            public ButtonBase? Compact { get; } = compact;
            public bool OverflowOnly { get; } = overflowOnly;
            public double Width, CompactWidth;
        }

        private readonly List<Group> _groups = [];
        private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _menu = new() { Margin = new Thickness(10, 6, 10, 6) };
        private readonly Popup _menuPopup;
        private readonly Border _chipHost = new() { Padding = new Thickness(6) };
        private readonly Popup _chipPopup;
        private ToolOptionsAssignment? _fit;
        private double _fitWidth = double.NaN;
        private bool _measured, _remeasure;
        private IInputElement? _priorFocus;

        // The grain and 98SE lower edge reach out over the padding to the bar's sides. A floating bar widens
        // its left padding for the grip, so they follow every padding change.
        private Border[] _paddingLayers = [];

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == PaddingProperty)
                foreach (Border layer in _paddingLayers)
                    layer.Margin = new Thickness(-Padding.Left, 0, -Padding.Right, 0);
        }

        public ToolOptionsBar(EditTool tool, string name, string moreName)
        {
            Tool = tool;
            Height = RowHeight;
            Padding = new Thickness(ToolOptionsLayout.Padding / 2, 0, ToolOptionsLayout.Padding / 2, 0);
            BorderThickness = new Thickness(0, 0, 0, 1);
            SnapsToDevicePixels = true;
            SetResourceReference(BackgroundProperty, "AnnotationBarBrush");
            SetResourceReference(BorderBrushProperty, "AnnotationBarBorderBrush");
            AutomationProperties.SetName(this, name);
            // The row is a toolbar for screen readers; Tab walks through its controls one by one
            // (sliders and the size box use the arrow keys themselves, so arrows cannot be the way
            // between controls) and leaves to the document after the last one.
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);

            // A mouse press on a row button must not keep the keyboard: an open text box, or the
            // page, goes on taking the keys typed next, as it did when these were plain borders.
            // A press from the keyboard keeps focus on the control (ToolOptionsDock restores it
            // after a rebuild). Fields, sliders and dropdowns keep the focus a click gives them.
            AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) =>
            {
                if (!IsKeyboardFocusWithin) _priorFocus = Keyboard.FocusedElement;
            }), true);
            AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is not ButtonBase { TemplatedParent: null } || ReferenceEquals(e.OriginalSource, OverflowButton)
                    || InputManager.Current.MostRecentInputDevice is not MouseDevice) return;
                var prior = _priorFocus;
                _priorFocus = null;
                if (prior is UIElement { IsVisible: true } back)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => back.Focus()));
            }), true);

            OverflowButton = new ToggleButton
            {
                Width = 28, Height = ToolOptionsControls.ControlHeight,
                Margin = new Thickness(ToolOptionsLayout.OverflowButton - 28, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand, ToolTip = moreName,
                Content = MoreGlyph(),
                Template = (ControlTemplate)((Control)ToolOptionsControls.IconToggle(new Border(), false, moreName, () => { })).Template
            };
            AutomationProperties.SetName(OverflowButton, moreName);
            AutomationProperties.SetAutomationId(OverflowButton, "ToolOptionsMore");
            DockPanel.SetDock(OverflowButton, Dock.Right);

            _menuPopup = MakePopup(OverflowButton, _menu, rightAligned: true);
            _chipPopup = MakePopup(null, _chipHost, rightAligned: false);
            WirePopup(_menuPopup, OverflowButton);

            var dock = new DockPanel { LastChildFill = true };
            dock.Children.Add(OverflowButton);
            _row.ClipToBounds = true;
            dock.Children.Add(_row);

            // Grain behind the controls and the 98SE dark lower edge, both theme tokens.
            var grain = new Border { IsHitTestVisible = false, Margin = new Thickness(-Padding.Left, 0, -Padding.Right, 0) };
            grain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            grain.SetResourceReference(BackgroundProperty, "GrainBrushShared");
            var edge = new Border { IsHitTestVisible = false, Margin = grain.Margin };
            edge.SetResourceReference(BorderBrushProperty, "BarEdgeDarkBrush");
            edge.SetResourceReference(BorderThicknessProperty, "BarEdgeDarkThickness");
            var layers = new Grid();
            layers.Children.Add(grain);
            layers.Children.Add(edge);
            _paddingLayers = [grain, edge];
            layers.Children.Add(dock);
            // The popups render nothing in place; being in the tree lets their DynamicResources resolve.
            layers.Children.Add(_menuPopup);
            layers.Children.Add(_chipPopup);
            Child = layers;
        }

        /// <summary>The tool this row belongs to.</summary>
        public EditTool Tool { get; }

        /// <summary>The "..." button. Shown only while something is in its menu.</summary>
        public ToggleButton OverflowButton { get; }

        /// <summary>The groups in the row, in row order, as last laid out.</summary>
        public IReadOnlyList<string> InlineIds => _fit?.Inline ?? [.. _groups.Select(g => g.Id)];

        /// <summary>The groups in the "..." menu, in row order, as last laid out.</summary>
        public IReadOnlyList<string> OverflowIds => _fit?.Overflow ?? [];

        /// <summary>True while the color group shows as its one-chip form.</summary>
        public bool IsCompact => _fit?.Compact ?? false;

        /// <summary>True when a key pressed on <paramref name="source"/> belongs to a control in an
        /// options row, so the window's shortcuts leave it alone.</summary>
        public static bool OwnsKey(object? source, Key key, ModifierKeys modifiers)
        {
            if (modifiers != ModifierKeys.None) return false;
            // Esc in the "..." menu or the color chip's swatches closes that popup (spec 3.6).
            if (key == Key.Escape) return InPopup(source as DependencyObject);
            if (key is not (Key.Space or Key.Enter or Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Home or Key.End or Key.PageUp or Key.PageDown)) return false;
            return Owner(source as DependencyObject) is not null;
        }

        private static bool InPopup(DependencyObject? element)
        {
            for (DependencyObject? d = element; d is not null;
                 d = (d is Visual ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
            {
                if (d is Popup) return Owner(d) is not null;
                if (d is ToolOptionsBar) return false;
            }
            return false;
        }

        /// <summary>Measures the groups again after one of them changed its own content (the
        /// Scale and page size texts, the Redact count), so a longer text can never be clipped.</summary>
        public void Remeasure()
        {
            _remeasure = true;
            InvalidateMeasure();
        }

        /// <summary>Re-fits the row that holds <paramref name="element"/>, if it is in one.</summary>
        public static void RemeasureOwner(DependencyObject? element) => Owner(element)?.Remeasure();

        // The row a control lives in, through the visual tree and, for the "..." menu, the popup.
        private static ToolOptionsBar? Owner(DependencyObject? element)
        {
            for (DependencyObject? d = element; d is not null;
                 d = (d is Visual ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d))
                if (d is ToolOptionsBar bar) return bar;
            return null;
        }

        /// <summary>The element that holds a group's controls, wherever it currently lives.</summary>
        public FrameworkElement? GroupContent(string id) => _groups.FirstOrDefault(g => g.Id == id)?.Content;

        /// <summary>Adds a group at the end of the row. A compact form (the color chip) opens the full
        /// group below itself when the row is short of room; a menu-only group never sits in the row.</summary>
        public void Add(string id, FrameworkElement content, ButtonBase? compact = null, bool overflowOnly = false)
        {
            content.VerticalAlignment = VerticalAlignment.Center;
            var group = new Group(id, content, compact, overflowOnly);
            if (compact is not null)
            {
                compact.Click += (_, _) =>
                {
                    _chipPopup.PlacementTarget = compact;
                    _chipPopup.IsOpen = true;
                };
            }
            _groups.Add(group);
            _measured = false;
            _fitWidth = double.NaN;
            InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size constraint)
        {
            double width = constraint.Width;
            if (!_measured) MeasureGroups();
            else if (_remeasure && MeasureInPlace()) _fitWidth = double.NaN;
            _remeasure = false;
            if (!double.IsInfinity(width) && !double.IsNaN(width) && width != _fitWidth) Fit(width);
            return base.MeasureOverride(constraint);
        }

        // Measures every group where it is now, without moving it, so a focused control stays
        // focused. True when any width changed and the row has to be fitted again.
        private bool MeasureInPlace()
        {
            bool changed = false;
            foreach (var group in _groups)
            {
                group.Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                changed |= Math.Abs(group.Content.DesiredSize.Width - group.Width) > 0.5;
                group.Width = group.Content.DesiredSize.Width;
                if (group.Compact is null) continue;
                group.Compact.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                changed |= Math.Abs(group.Compact.DesiredSize.Width - group.CompactWidth) > 0.5;
                group.CompactWidth = group.Compact.DesiredSize.Width;
            }
            return changed;
        }

        // Every group is measured once at full size in the row, with nothing else in it, and the
        // widths are frozen: moving groups in and out can then never change the answer and oscillate.
        private void MeasureGroups()
        {
            _row.Children.Clear();
            foreach (var group in _groups)
            {
                Detach(group.Content);
                _row.Children.Add(group.Content);
                if (group.Compact is not null) { Detach(group.Compact); _row.Children.Add(group.Compact); }
            }
            foreach (var group in _groups)
            {
                group.Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                group.Width = group.Content.DesiredSize.Width;
                if (group.Compact is not null)
                {
                    group.Compact.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    group.CompactWidth = group.Compact.DesiredSize.Width;
                }
            }
            _row.Children.Clear();
            _measured = true;
        }

        private void Fit(double width)
        {
            _fitWidth = width;
            var specs = _groups.Select(g => new ToolOptionsGroupSpec(g.Id, g.Width,
                g.Compact is null ? null : g.CompactWidth, g.OverflowOnly)).ToList();
            var fit = ToolOptionsLayout.Assign(width, specs, ToolOptionsCatalog.ShedOrder(Tool));
            if (_fit is not null && fit.Inline.SequenceEqual(_fit.Inline) && fit.Compact == _fit.Compact
                && _row.Children.Count > 0) return;
            _fit = fit;

            _row.Children.Clear();
            _menu.Children.Clear();
            _chipHost.Child = null;
            foreach (var group in _groups)
            {
                Detach(group.Content);
                if (group.Compact is not null) Detach(group.Compact);
            }
            bool first = true;
            foreach (var group in _groups.Where(g => fit.Inline.Contains(g.Id)))
            {
                if (!first) _row.Children.Add(Separator());
                first = false;
                if (fit.Compact && group.Compact is not null)
                {
                    _row.Children.Add(group.Compact);
                    _chipHost.Child = group.Content;
                }
                else _row.Children.Add(group.Content);
            }
            foreach (var group in _groups.Where(g => fit.Overflow.Contains(g.Id)))
                _menu.Children.Add(MenuRow(group.Content));
            bool shed = fit.Overflow.Count > 0;
            OverflowButton.Visibility = shed ? Visibility.Visible : Visibility.Collapsed;
            if (!shed) _menuPopup.IsOpen = false;
            if (_chipHost.Child is null) _chipPopup.IsOpen = false;
        }

        private static Border MenuRow(FrameworkElement content) => new()
        {
            MinHeight = 32, Child = content, Padding = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        private static Border Separator()
        {
            var line = new Border
            {
                Width = 1, Height = 16, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness((ToolOptionsLayout.Separator - 1) / 2, 0, (ToolOptionsLayout.Separator - 1) / 2, 0)
            };
            line.SetResourceReference(BackgroundProperty, "CardBorderBrush");
            return line;
        }

        private static void Detach(FrameworkElement element)
        {
            switch (element.Parent)
            {
                case Panel panel: panel.Children.Remove(element); break;
                case Decorator decorator: decorator.Child = null; break;
            }
        }

        /// <summary>True once the dock has let go of this bar.</summary>
        internal bool Retired { get; private set; }

        /// <summary>True while the More settings menu or the compact chip's menu is open.</summary>
        internal bool MenusOpen => _menuPopup.IsOpen || _chipPopup.IsOpen;

        /// <summary>
        /// Closes this bar's menus for good. A bar is rebuilt while its More settings menu is open (a layer toggle
        /// reselects its layer), and a menu left open with no bar under it keeps the mouse, so the app looks frozen
        /// until another window takes the mouse back (#467).
        /// </summary>
        internal void Retire()
        {
            Retired = true;
            _menuPopup.IsOpen = false;
            _chipPopup.IsOpen = false;
            OverflowButton.IsChecked = false;
        }

        private static TextBlock MoreGlyph()
        {
            var glyph = new TextBlock
            {
                Text = ((char)0xE712).ToString(), FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            return glyph;
        }

        // Opens below its own button, 4 below, with the family grain over the items.
        private static Popup MakePopup(UIElement? target, FrameworkElement content, bool rightAligned)
        {
            var grain = new Border { CornerRadius = new CornerRadius(4), IsHitTestVisible = false };
            grain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            grain.SetResourceReference(BackgroundProperty, "GrainBrushShared");
            var inner = new Grid();
            inner.Children.Add(content);
            inner.Children.Add(grain);
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = inner };
            frame.SetResourceReference(BackgroundProperty, "MenuBackgroundBrush");
            frame.SetResourceReference(BorderBrushProperty, "MenuBorderBrush");
            KeyboardNavigation.SetTabNavigation(frame, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(frame, KeyboardNavigationMode.Cycle);
            // Below its own button, 4 down. The "..." button sits at the row's right end, so the menu
            // lines up with the button's right edge and opens leftward into the pane, never past it.
            var popup = new Popup
            {
                PlacementTarget = target, Placement = PlacementMode.Custom, VerticalOffset = 0,
                StaysOpen = false, AllowsTransparency = true, Child = frame,
                CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
                [
                    new CustomPopupPlacement(new Point(rightAligned ? targetSize.Width - popupSize.Width : 0, targetSize.Height + 4),
                        PopupPrimaryAxis.Horizontal),
                ]
            };
            bool returnFocus = false;
            popup.Opened += (_, _) => frame.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => frame.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
            frame.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                returnFocus = true;
                popup.IsOpen = false;
                e.Handled = true;
            };
            // Focus goes back to the button once the popup has really closed; asked for earlier, the
            // closing popup hands the keyboard to the window instead.
            popup.Closed += (_, _) =>
            {
                if (!returnFocus) return;
                returnFocus = false;
                if (popup.PlacementTarget is UIElement back)
                    back.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => back.Focus()));
            };
            return popup;
        }

        // The button and its menu stay in step both ways. While the menu is open the button stops
        // taking clicks, so the click that closes a StaysOpen=false popup cannot reopen it at once.
        private static void WirePopup(Popup popup, ToggleButton button)
        {
            button.Checked += (_, _) => popup.IsOpen = true;
            button.Unchecked += (_, _) => popup.IsOpen = false;
            popup.Opened += (_, _) => { button.IsChecked = true; button.IsHitTestVisible = false; };
            popup.Closed += (_, _) =>
            {
                button.IsChecked = false;
                button.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => button.IsHitTestVisible = true));
            };
            button.PreviewKeyDown += (_, e) =>
            {
                if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    popup.IsOpen = true;
                    e.Handled = true;
                }
            };
        }
    }
}
