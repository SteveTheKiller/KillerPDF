using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Layout shared by the page windows (Transform, Color correction, Stamp, and Print): a
    /// compact two-column options area, section headers that the keyboard reaches, one footer
    /// row, and the page fit used by the preview. Built without resource lookups that need a
    /// running application, so the rules can be checked headless.
    /// </summary>
    internal static class PageWindowLayout
    {
        /// <summary>16 inset + 206 + 12 gap + 206 + 4 + 12 reserved scrollbar gutter.</summary>
        public const double OptionsWidth = 456;
        /// <summary>The single-column fallback for a work area smaller than the minimum window.</summary>
        public const double NarrowOptionsWidth = 288;
        public const double ColumnWidth = 206;
        public const double NarrowColumnWidth = 256;
        public const double ColumnGap = 12;
        public const double OptionsInset = 16;
        /// <summary>Space around the page inside the preview frame.</summary>
        public const double PageMargin = 16;
        public const double ControlHeight = 26;
        public const double HeaderHeight = 28;
        /// <summary>The name of the options column's scroller, for tests and acceptance drivers.</summary>
        public const string OptionsScrollerName = "PageWindowOptions";

        /// <summary>
        /// The size of the page image on a print sheet, given the page's scaled size and the printable
        /// area. A page within a pixel or so of the area snaps to it plus one bleed pixel, so the white
        /// sheet does not peek through as a hairline. Any other size is kept and clipped by the sheet.
        /// </summary>
        public static (double Width, double Height) PrintedImageBox(double width, double height, double areaWidth, double areaHeight)
        {
            if (Math.Abs(width - areaWidth) <= 1.5) width = areaWidth + 1;
            if (Math.Abs(height - areaHeight) <= 1.5) height = areaHeight + 1;
            return (width, height);
        }

        /// <summary>The string key for the Print window's label over two shown pages. "Pages 3-4" is
        /// right only for two pages in a row; any other pair (a typed range, odd pages, reversed)
        /// is named as two pages.</summary>
        public static string PrintPagePairLabelKey(int firstPage, int secondPage)
            => secondPage == firstPage + 1 ? "Str_PagesOf" : "Str_Print_PagesAndOf";

        /// <summary>The fixed width of the options content: two columns and the gap, or one column.</summary>
        public static double ContentWidth(int columns)
            => columns >= 2 ? 2 * ColumnWidth + ColumnGap : NarrowColumnWidth;

        /// <summary>A theme resource as a brush. Any theme may state a surface as a gradient, so
        /// this never casts to SolidColorBrush; anything else becomes transparent.</summary>
        public static Brush ThemeBrush(object? resource) => resource as Brush ?? Brushes.Transparent;

        /// <summary>A theme resource as a raw color: a solid brush's color, a gradient's first stop,
        /// or the fallback.</summary>
        public static Color ThemeColor(object? resource, Color fallback) => resource switch
        {
            SolidColorBrush solid => solid.Color,
            GradientBrush { GradientStops.Count: > 0 } gradient => gradient.GradientStops[0].Color,
            _ => fallback,
        };

        /// <summary>The page's drawn size in the preview frame. The original page fits the frame
        /// less the margin on every side; the drawn bitmap (scaled, rotated) keeps that same factor,
        /// so a 50% page looks half as big, and is clamped so it never runs past the frame.</summary>
        public static Size FitPage(Size frame, Size originalPage, Size drawnPage)
        {
            double availW = Math.Max(1, frame.Width - 2 * PageMargin);
            double availH = Math.Max(1, frame.Height - 2 * PageMargin);
            if (originalPage.Width <= 0 || originalPage.Height <= 0 || drawnPage.Width <= 0 || drawnPage.Height <= 0)
                return new Size(availW, availH);
            double fit = Math.Min(availW / originalPage.Width, availH / originalPage.Height);
            double w = drawnPage.Width * fit, h = drawnPage.Height * fit;
            double clamp = Math.Min(1.0, Math.Min(availW / w, availH / h));
            return new Size(w * clamp, h * clamp);
        }

        /// <summary>How much wider the preview frame must be for the page, at the frame's height less
        /// the margins, to fit; zero when it already fits.</summary>
        public static double FittedExtraWidth(Size frame, Size page)
        {
            if (page.Width <= 0 || page.Height <= 0) return 0;
            double pageHeight = Math.Max(0, frame.Height - 2 * PageMargin);
            double needed = pageHeight * page.Width / page.Height + 2 * PageMargin;
            return Math.Max(0, needed - frame.Width);
        }

        /// <summary>A collapsible section: a keyboard-reachable header button over its body.
        /// Every section starts open.</summary>
        public static StackPanel Section(string title, UIElement body, out FrameworkElement header)
        {
            var button = new SectionHeader(title);
            void Sync() => body.Visibility = button.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            button.Checked += (_, _) => Sync();
            button.Unchecked += (_, _) => Sync();
            Sync();
            header = button;
            var panel = new StackPanel();
            panel.Children.Add(button);
            panel.Children.Add(body);
            return panel;
        }

        /// <summary>The chevron column and row height of a section header, for a header whose
        /// control is something else (Stamp's enable checkboxes).</summary>
        public static FrameworkElement HeaderRow(UIElement control)
        {
            var row = new DockPanel { Height = HeaderHeight, Margin = new Thickness(0, 8, 0, 4), LastChildFill = true };
            var chevron = new TextBlock { Text = "▾", Width = 16, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            DockPanel.SetDock(chevron, Dock.Left);
            row.Children.Add(chevron);
            row.Children.Add(control);
            return row;
        }

        /// <summary>A 1px line between sections, 8 above and 8 below.</summary>
        public static Border Divider()
        {
            var line = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            line.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            return line;
        }

        /// <summary>A label above a control (11 muted, 2 gap), with an optional readout on the
        /// right of the label row, and 8 below the cell.</summary>
        public static StackPanel Labeled(string label, UIElement control, FrameworkElement? readout = null)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), LastChildFill = true };
            if (readout != null)
            {
                DockPanel.SetDock(readout, Dock.Right);
                row.Children.Add(readout);
            }
            row.Children.Add(Label(label));
            cell.Children.Add(row);
            cell.Children.Add(control);
            return cell;
        }

        /// <summary>The 11 muted label used over fields and sliders.</summary>
        public static TextBlock Label(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            return label;
        }

        /// <summary>A fixed-width mono readout for a slider's label row.</summary>
        public static TextBlock Readout(string text, double width = 44)
        {
            var readout = new TextBlock { Text = text, Width = width, FontSize = 11, TextAlignment = TextAlignment.Right };
            readout.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            readout.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return readout;
        }

        /// <summary>One line of muted text that never wraps, so a changing value never changes the
        /// height measured at open; the full text rides in the tooltip.</summary>
        public static TextBlock StatusLine(bool mono = true)
        {
            var line = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            line.SetResourceReference(TextBlock.FontFamilyProperty, mono ? "MonoFont" : "UiFont");
            line.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var tip = new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = line };
            line.SetBinding(FrameworkElement.ToolTipProperty, tip);
            return line;
        }

        /// <summary>A muted hint that wraps across its cell.</summary>
        public static TextBlock Hint(string text)
        {
            var hint = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            hint.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            return hint;
        }

        /// <summary>A low-emphasis text action such as "Reset all", reached by Tab.</summary>
        public static FrameworkElement LinkButton(string text, Action onClick)
        {
            var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            var face = new FrameworkElementFactory(typeof(Border));
            face.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            face.SetValue(Border.PaddingProperty, new Thickness(0, 4, 0, 4));
            face.AppendChild(presenter);
            var link = new Button
            {
                Content = label,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = face },
            };
            AutomationProperties.SetName(link, text);
            void Lit(bool on) => label.SetResourceReference(TextBlock.ForegroundProperty, on ? "PrimaryBrush" : "MutedTextBrush");
            link.MouseEnter += (_, _) => Lit(true);
            link.MouseLeave += (_, _) => Lit(link.IsKeyboardFocused);
            link.GotKeyboardFocus += (_, _) => Lit(true);
            link.LostKeyboardFocus += (_, _) => Lit(link.IsMouseOver);
            link.Click += (_, _) => onClick();
            return link;
        }

        /// <summary>The footer row: an optional link on the left, then Cancel and the primary
        /// button on the right, 8 apart, 16 in from both sides, 12 above and below.</summary>
        public static FrameworkElement Footer(FrameworkElement? link, Button cancel, Button primary)
        {
            var row = new DockPanel { Margin = new Thickness(OptionsInset, 12, OptionsInset, 12), LastChildFill = false };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            cancel.Margin = new Thickness(0);
            primary.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(cancel);
            buttons.Children.Add(primary);
            if (link != null)
            {
                DockPanel.SetDock(link, Dock.Left);
                link.Margin = new Thickness(0, 0, 12, 0);
                row.Children.Add(link);
            }
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            return row;
        }

        /// <summary>The window body: the preview filling the left and the fixed-width options column
        /// on the right (footer pinned under the scrolling options). The preview never asks for
        /// height, so the body is exactly as tall as the open options and the footer.</summary>
        public static Grid Body(FrameworkElement options, FrameworkElement footer, FrameworkElement preview,
            out Border optionsColumn, int columns = 2, bool optionsLeft = false)
        {
            // Print keeps its options on the left; every other page window has them on the right.
            int previewColumn = optionsLeft ? 1 : 0, optionsColumnIndex = optionsLeft ? 0 : 1;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = optionsLeft ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = optionsLeft ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var previewHost = new NoDesiredSize { Child = preview };
            Grid.SetColumn(previewHost, previewColumn);
            grid.Children.Add(previewHost);

            // The content keeps its fixed width with 16 on the left; what is left on the right is
            // the reserved scrollbar gutter, so the scrollbar appearing never moves a control.
            options.Width = ContentWidth(columns);
            options.HorizontalAlignment = HorizontalAlignment.Left;
            options.Margin = new Thickness(OptionsInset, 0, 0, 0);
            TwoColumnPanel.SetColumns(options, columns);
            // Children are added in Tab order (spec 5): the options, then the footer pinned under
            // them, then the preview with its nav arrows. Placement comes from the rows and columns.
            var side = new Grid();
            side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.Children.Add(new ScrollViewer
            {
                Name = OptionsScrollerName,
                Content = options,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            });
            Grid.SetRow(footer, 1);
            side.Children.Add(footer);
            optionsColumn = new Border
            {
                Width = columns >= 2 ? OptionsWidth : NarrowOptionsWidth,
                Background = Brushes.Transparent,
                Child = side,
            };
            Grid.SetColumn(optionsColumn, optionsColumnIndex);
            grid.Children.Insert(0, optionsColumn);
            return grid;
        }

        // Measures its child but asks for nothing, so the preview fills whatever the options leave.
        private sealed class NoDesiredSize : Decorator
        {
            protected override Size MeasureOverride(Size constraint)
            {
                Child?.Measure(new Size(
                    double.IsInfinity(constraint.Width) ? 0 : constraint.Width,
                    double.IsInfinity(constraint.Height) ? 0 : constraint.Height));
                return new Size(0, 0);
            }
        }
    }

    /// <summary>
    /// A section header: chevron and title in a 28 high ToggleButton that Tab reaches, Space
    /// and Enter toggle, and UI Automation reports as expanded or collapsed.
    /// </summary>
    internal sealed class SectionHeader : ToggleButton
    {
        private readonly TextBlock _chevron;

        public SectionHeader(string title)
        {
            _chevron = new TextBlock { Width = 16, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            _chevron.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var text = new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            text.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_chevron);
            row.Children.Add(text);
            Content = row;
            Height = PageWindowLayout.HeaderHeight;
            Margin = new Thickness(0, 8, 0, 4);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Left;
            Cursor = Cursors.Hand;
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            var face = new FrameworkElementFactory(typeof(Border));
            face.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            face.AppendChild(presenter);
            Template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = face };
            AutomationProperties.SetName(this, title);
            IsChecked = true;
            Sync();
            Checked += (_, _) => Sync();
            Unchecked += (_, _) => Sync();
        }

        private ExpandCollapseState _announced = ExpandCollapseState.Collapsed;

        /// <summary>Raised after the header's expanded state changed, with the old and the new state,
        /// when UI Automation is told of the change.</summary>
        internal event Action<ExpandCollapseState, ExpandCollapseState>? ExpandCollapseChanged;

        private void Sync()
        {
            _chevron.Text = IsChecked == true ? "▾" : "▸";
            var now = IsChecked == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
            if (now == _announced) return;
            var before = _announced;
            _announced = now;
            ExpandCollapseChanged?.Invoke(before, now);
            if (UIElementAutomationPeer.FromElement(this) is Peer peer) peer.Announce(before, now);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(SectionHeader owner) : ToggleButtonAutomationPeer(owner), IExpandCollapseProvider
        {
            public override object GetPattern(PatternInterface patternInterface)
                => patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPattern(patternInterface);

            public ExpandCollapseState ExpandCollapseState
                => owner.IsChecked == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

            public void Announce(ExpandCollapseState before, ExpandCollapseState now)
                => RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, before, now);

            public void Expand() => owner.IsChecked = true;

            public void Collapse() => owner.IsChecked = false;
        }
    }

    /// <summary>
    /// Lays its children out row-major in two fixed columns (or one, on a narrow work area):
    /// left cell, right cell, next row. A child marked Span takes a whole row. A collapsed child
    /// keeps its slot, so swapping cells never reshuffles the grid. Tab order follows the
    /// children, so it is row-major too.
    /// </summary>
    internal sealed class TwoColumnPanel : Panel
    {
        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.RegisterAttached(
            "Columns", typeof(int), typeof(TwoColumnPanel),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached(
            "Span", typeof(bool), typeof(TwoColumnPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static int GetColumns(DependencyObject d) => (int)d.GetValue(ColumnsProperty);
        public static void SetColumns(DependencyObject d, int value) => d.SetValue(ColumnsProperty, value);
        public static bool GetSpan(DependencyObject d) => (bool)d.GetValue(SpanProperty);
        public static void SetSpan(DependencyObject d, bool value) => d.SetValue(SpanProperty, value);

        private int ColumnCount => GetColumns(this) >= 2 ? 2 : 1;

        private (int Row, int Column, bool Span)[] Slots()
        {
            int columns = ColumnCount;
            var slots = new (int, int, bool)[InternalChildren.Count];
            int row = 0, column = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                bool span = columns == 1 || GetSpan(InternalChildren[i]);
                if (span)
                {
                    if (column != 0) { row++; column = 0; }
                    slots[i] = (row, 0, true);
                    row++;
                    continue;
                }
                slots[i] = (row, column, false);
                if (++column == columns) { row++; column = 0; }
            }
            return slots;
        }

        private double RowWidth(Size available, out double column)
        {
            int columns = ColumnCount;
            double width = double.IsInfinity(available.Width) || double.IsNaN(available.Width)
                ? PageWindowLayout.ContentWidth(columns) : available.Width;
            column = columns == 2 ? (width - PageWindowLayout.ColumnGap) / 2 : width;
            return width;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = RowWidth(availableSize, out double column);
            var slots = Slots();
            var heights = new double[slots.Length == 0 ? 0 : slots[^1].Row + 1];
            for (int i = 0; i < slots.Length; i++)
            {
                UIElement child = InternalChildren[i];
                child.Measure(new Size(slots[i].Span ? width : column, double.PositiveInfinity));
                heights[slots[i].Row] = Math.Max(heights[slots[i].Row], child.DesiredSize.Height);
            }
            double total = 0;
            foreach (double h in heights) total += h;
            return new Size(width, total);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = RowWidth(finalSize, out double column);
            var slots = Slots();
            var heights = new double[slots.Length == 0 ? 0 : slots[^1].Row + 1];
            for (int i = 0; i < slots.Length; i++)
                heights[slots[i].Row] = Math.Max(heights[slots[i].Row], InternalChildren[i].DesiredSize.Height);
            var tops = new double[heights.Length];
            for (int r = 1; r < heights.Length; r++) tops[r] = tops[r - 1] + heights[r - 1];
            for (int i = 0; i < slots.Length; i++)
            {
                UIElement child = InternalChildren[i];
                double x = slots[i].Span ? 0 : slots[i].Column * (column + PageWindowLayout.ColumnGap);
                child.Arrange(new Rect(x, tops[slots[i].Row], slots[i].Span ? width : column, child.DesiredSize.Height));
            }
            return finalSize;
        }
    }
}
