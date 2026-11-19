#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;

namespace KillerFamily.Controls
{
    /// <summary>One quick button on a card strip: an MDL2 glyph over a short caption. Tip is the
    /// full action name; Gesture, when the action has a shortcut, is added to the tooltip (the
    /// family shortcut rule).</summary>
    public sealed record CardAction(string Glyph, string Caption, string Tip, string? Gesture, Action Run, bool Enabled = true);

    /// <summary>One chip or segment on a card row. On is its current state.</summary>
    public sealed record CardChoice(string Text, string Tip, string? Gesture, bool On, Action Run, bool Enabled = true);

    /// <summary>
    /// One tile of a card strip (TileStrip): an MDL2 glyph over a caption, the family card style. Caption is the
    /// full name (tooltip and accessible name); Shown, when set, is the shorter text drawn under the glyph.
    /// Keys is the shortcut as it reads ("Ctrl+C"), drawn on the tile's second line while shortcuts show in menus.
    /// Mirror flips the glyph left to right. Flyout: the tile opens these rows beside itself instead of acting.
    /// Checked: an on/off command, filled while on. KeepsOpen: the tile flips in place and the card stays open.
    /// </summary>
    public sealed record CardTile(string Glyph, string Caption, string? Keys, Action? Run, bool Mirror = false, bool Enabled = true)
    {
        public string? Shown { get; init; }
        /// <summary>The tooltip, when the app words its shortcut hints its own way; otherwise "Caption (Keys)".</summary>
        public string? Hint { get; init; }
        public IList<object>? Flyout { get; init; }
        public bool? Checked { get; init; }
        public bool KeepsOpen { get; init; }
    }

    /// <summary>
    /// Builds card-menu rows in code, for the menus that are made at open time rather than in
    /// XAML (KillerNotes 1.7.0: the graph, sketch pad, dictation and Tags dialog cards). Every
    /// row is a MenuItem on the kit's card styles (CardSlot, CardQuickButton, CardToggle,
    /// CardSegment, CardLabel in Controls.xaml), so a card built here and one written in XAML
    /// look and behave the same. Ordinary MenuItems still go in as they are, after a Separator,
    /// for the short list at the bottom of a card.
    ///
    /// A control closes its card before it runs its action, the way a menu row does, so a
    /// dialog the action opens never sits under a card that is still open.
    /// </summary>
    public static class CardBuilder
    {
        /// <summary>"Name (Ctrl+X)", or the name alone when there is no shortcut.</summary>
        public static string Tip(string name, string? gesture) =>
            string.IsNullOrEmpty(gesture) ? name : name + " (" + gesture + ")";

        /// <summary>A ContextMenu on the CardMenu style. width is the card's own width; the
        /// template's 22px shadow halo on each side is added here.</summary>
        public static ContextMenu Menu(double width = 360)
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(FrameworkElement.StyleProperty, "CardMenu");
            menu.MinWidth = width + 44;
            menu.MaxWidth = width + 44;
            return menu;
        }

        /// <summary>The card's title, with an optional dim line under it.</summary>
        public static MenuItem Header(string title, string? meta = null)
        {
            var stack = new StackPanel { Margin = new Thickness(4, 2, 4, 6) };
            var head = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            head.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            stack.Children.Add(head);
            if (!string.IsNullOrEmpty(meta))
            {
                var sub = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
                stack.Children.Add(sub);
            }
            var slot = Slot(stack);
            AutomationProperties.SetName(slot, title);
            return slot;
        }

        /// <summary>The strip of quick buttons, ruled above and below like the note card's.</summary>
        public static MenuItem QuickStrip(string name, IEnumerable<CardAction> actions)
        {
            var grid = new UniformGrid { Rows = 1 };
            foreach (var a in actions)
            {
                var b = new Button { Content = a.Glyph, Tag = a.Caption, IsEnabled = a.Enabled, ToolTip = Tip(a.Tip, a.Gesture) };
                b.SetResourceReference(FrameworkElement.StyleProperty, "CardQuickButton");
                AutomationProperties.SetName(b, a.Tip);
                ToolTipService.SetShowOnDisabled(b, true);
                if (!string.IsNullOrEmpty(a.Gesture)) AutomationProperties.SetAcceleratorKey(b, a.Gesture);
                var run = a.Run;
                b.Click += (_, _) => { Close(b); run(); };
                grid.Children.Add(b);
            }
            // No inset: with the card's 7px padding and border, a tile's hover sits 8px from the edge (family rule).
            var rule = new Border { Margin = new Thickness(0), BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(0, 4, 0, 4), Child = grid };
            rule.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            var slot = Slot(rule);
            slot.Padding = new Thickness(0);
            AutomationProperties.SetName(slot, name);
            // The rule below only separates the strip from rows under it; a card that ends at the
            // strip drew a stray line at its bottom edge.
            slot.Loaded += (_, _) => rule.BorderThickness = new Thickness(0, 1, 0, EndsCard(slot) ? 0 : 1);
            return slot;
        }

        /// <summary>A card row that holds controls rather than being a command (CardSlot), with its own padding.</summary>
        public static MenuItem Slot(object content, Thickness padding)
        {
            var slot = Slot(content);
            slot.Padding = padding;
            return slot;
        }

        /// <summary>
        /// The family tile strip: tiles in one row, or wrapped into rows of columns when there are more. A tile closes
        /// its card, then acts. Each tile fills its cell, so with the card's 7px padding and 1px edge its hover sits
        /// 8px from the card's edge (family rule). Put the result in a card with Slot(strip, new Thickness(0)).
        /// </summary>
        public static Border TileStrip(ContextMenu card, string name, int columns, IEnumerable<CardTile> tiles)
        {
            var list = tiles.Cast<CardTile?>().ToList();
            var grid = columns > 0 && list.Count > columns ? new UniformGrid { Columns = columns } : new UniformGrid { Rows = 1 };
            return Strip(card, name, grid, list);
        }

        /// <summary>The family card width in tiles: three across, the page cards' width (Steve 2026-11-19).</summary>
        public const int CardColumns = 3;

        /// <summary>
        /// The family card grid. Rules (Steve 2026-11-19), which this method enforces so apps don't relearn them:
        /// 1. Three tiles across (CardColumns), so every card is the page cards' width. A long caption wraps inside its
        ///    tile; it never widens the card.
        /// 2. Related tiles are one group and share a row: Copy, Cut and Paste; the turns; Save and Replace; Raise and
        ///    Lower. A group never shares a row with another group, and a group longer than a row continues on the
        ///    next row; an unfilled row stays blank on the right instead of borrowing a tile from the next group.
        /// 3. Groups run most used first: the clipboard row leads, a destructive tile (Clear) goes last, and Delete on
        ///    a card for one thing sits in the header, not in the grid.
        /// 4. A tile that only sometimes applies keeps its spot dimmed (Paste with nothing to paste), so the rows
        ///    don't shift under the pointer from one right-click to the next.
        /// 5. Cards carry no text rows; everything a card does is a tile or a flyout tile.
        /// Empty groups are skipped.
        /// </summary>
        public static Border TileGroups(ContextMenu card, string name, IEnumerable<IEnumerable<CardTile>> groups, int columns = CardColumns)
        {
            var cells = new List<CardTile?>();
            foreach (var group in groups)
            {
                var list = group.ToList();
                if (list.Count == 0) continue;
                cells.AddRange(list);
                for (int pad = (columns - list.Count % columns) % columns; pad > 0; pad--) cells.Add(null);
            }
            return Strip(card, name, new UniformGrid { Columns = columns }, cells);
        }

        private static Border Strip(ContextMenu card, string name, UniformGrid grid, List<CardTile?> list)
        {
            foreach (var tile in list)
            {
                if (tile is null)
                {
                    // The blank end of a group's row: holds the column, takes no hover.
                    grid.Children.Add(new Border { Focusable = false });
                    continue;
                }
                // A trailing "..." on a tile reads as a cut-off caption, so tiles drop it.
                string caption = tile.Caption.TrimEnd('.', '\u2026').TrimEnd();
                string shown = (tile.Shown ?? caption).TrimEnd('.', '\u2026').TrimEnd();
                var button = new Button { Content = tile.Glyph, Tag = shown, ToolTip = tile.Hint ?? Tip(caption, tile.Keys), IsEnabled = tile.Enabled };
                AutomationProperties.SetName(button, caption);
                // The tile's second line shows these keys in brackets while shortcuts show in menus.
                if (!string.IsNullOrEmpty(tile.Keys)) AutomationProperties.SetAcceleratorKey(button, tile.Keys);
                ToolTipService.SetShowOnDisabled(button, true);
                button.SetResourceReference(FrameworkElement.StyleProperty, "CardQuickButton");
                bool on = tile.Checked == true;
                if (tile.Checked is not null) ShowTileState(button, on);
                if (tile.Flyout is { } rows)
                {
                    ContextMenu? flyout = null;
                    button.Click += (_, _) =>
                    {
                        flyout ??= TileFlyout(card, button, rows);
                        flyout.IsOpen = true;
                    };
                    grid.Children.Add(WithFlyoutChevron(button));
                    continue;
                }
                if (tile.Mirror)
                    button.Loaded += (_, _) =>
                    {
                        if (button.Template?.FindName("Glyph", button) is TextBlock g)
                        {
                            g.RenderTransformOrigin = new Point(0.5, 0.5);
                            g.RenderTransform = new ScaleTransform(-1, 1);
                        }
                    };
                var run = tile.Run;
                if (tile.KeepsOpen)
                    button.Click += (_, _) => { run?.Invoke(); on = !on; ShowTileState(button, on); };
                else
                    button.Click += (_, _) => { card.IsOpen = false; run?.Invoke(); };
                grid.Children.Add(button);
            }
            var strip = new Border { Padding = new Thickness(0), Child = grid };
            strip.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            AutomationProperties.SetName(strip, name);
            return strip;
        }

        // An on/off tile is filled with the selected-row color while it is on (the template paints its Background).
        private static void ShowTileState(Button button, bool on)
        {
            if (on) button.SetResourceReference(Control.BackgroundProperty, "RowSelectedBrush");
            else button.ClearValue(Control.BackgroundProperty);
        }

        // The quick-button template draws its content as one glyph, so the chevron is laid over the tile beside the
        // icon and lets clicks through to it.
        private static Grid WithFlyoutChevron(Button button)
        {
            var chevron = new TextBlock
            {
                Text = char.ConvertFromUtf32(0xE76C), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(30, 12, 0, 0)
            };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            var cell = new Grid();
            cell.Children.Add(button);
            cell.Children.Add(chevron);
            return cell;
        }

        // The rows a flyout tile opens, beside the tile, on the plain menu style. Picking a row closes the card as
        // well; Escape, Left or a click back on the card returns to the card, and a click anywhere else closes both.
        private static ContextMenu TileFlyout(ContextMenu card, Button tile, IList<object> rows)
        {
            var flyout = new ContextMenu();
            if (card.TryFindResource(typeof(ContextMenu)) is Style style) flyout.Style = style;
            TextOptions.SetTextFormattingMode(flyout, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(flyout, TextRenderingMode.Grayscale);
            foreach (object row in rows.ToList())
            {
                if (row is FrameworkElement { Parent: ItemsControl owner } fe) owner.Items.Remove(fe);
                flyout.Items.Add(row);
            }
            flyout.PlacementTarget = tile;
            flyout.Placement = PlacementMode.Right;
            bool picked = false, byKeyboard = false;
            flyout.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is not MenuItem { HasItems: false, StaysOpenOnClick: false }) return;
                picked = true;
                card.IsOpen = false;
            }));
            flyout.PreviewKeyDown += (_, e) => byKeyboard = e.Key is Key.Escape or Key.Left;
            flyout.Opened += (_, _) =>
            {
                picked = byKeyboard = false;
                if (InputManager.Current.MostRecentInputDevice is KeyboardDevice
                    && flyout.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsEnabled) is { } first)
                    first.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => first.Focus());
            };
            flyout.Closed += (_, _) =>
            {
                if (picked || !card.IsOpen) return;
                if (!byKeyboard && card.InputHitTest(Mouse.GetPosition(card)) is null) { card.IsOpen = false; return; }
                Mouse.Capture(card, CaptureMode.SubTree);
                if (byKeyboard) tile.Focus();
            };
            return flyout;
        }

        /// <summary>Whether this item is the last thing on its card.</summary>
        internal static bool EndsCard(MenuItem item)
        {
            if (ItemsControl.ItemsControlFromItemContainer(item) is not { } owner) owner = item.Parent as ItemsControl;
            return owner == null || owner.Items.IndexOf(item) == owner.Items.Count - 1;
        }

        /// <summary>A labeled row of independent on/off chips (CardToggle).</summary>
        public static MenuItem ToggleRow(string label, IEnumerable<CardChoice> choices)
        {
            var wrap = new WrapPanel();
            foreach (var c in choices)
            {
                var t = new ToggleButton { Content = c.Text, IsChecked = c.On, IsEnabled = c.Enabled, ToolTip = Tip(c.Tip, c.Gesture), Margin = new Thickness(0, 2, 6, 2) };
                t.SetResourceReference(FrameworkElement.StyleProperty, "CardToggle");
                AutomationProperties.SetName(t, c.Tip);
                ToolTipService.SetShowOnDisabled(t, true);
                if (!string.IsNullOrEmpty(c.Gesture)) AutomationProperties.SetAcceleratorKey(t, c.Gesture);
                var run = c.Run;
                t.Click += (_, _) => { Close(t); run(); };
                wrap.Children.Add(t);
            }
            return Labeled(label, wrap);
        }

        /// <summary>A labeled segmented toggle (CardSegment): one choice of several. A click on
        /// the choice that is already on just closes the card.</summary>
        public static MenuItem SegmentRow(string label, IEnumerable<CardChoice> choices)
        {
            var group = "CardSeg" + Guid.NewGuid().ToString("N");
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in choices)
            {
                var r = new RadioButton { Content = c.Text, GroupName = group, IsChecked = c.On, IsEnabled = c.Enabled, ToolTip = Tip(c.Tip, c.Gesture) };
                r.SetResourceReference(FrameworkElement.StyleProperty, "CardSegment");
                AutomationProperties.SetName(r, c.Tip);
                ToolTipService.SetShowOnDisabled(r, true);
                if (!string.IsNullOrEmpty(c.Gesture)) AutomationProperties.SetAcceleratorKey(r, c.Gesture);
                var run = c.Run;
                bool was = c.On;
                r.Click += (_, _) => { Close(r); if (!was) run(); };
                stack.Children.Add(r);
            }
            var track = new Border { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(2), Child = stack };
            track.SetResourceReference(Border.BackgroundProperty, "MenuHoverBrush");
            track.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            return Labeled(label, track);
        }

        /// <summary>A labeled row holding any control, its label on the card's label column.</summary>
        public static MenuItem Labeled(string label, UIElement content)
        {
            var dock = new DockPanel();
            var l = new TextBlock { Text = label };
            l.SetResourceReference(FrameworkElement.StyleProperty, "CardLabel");
            DockPanel.SetDock(l, Dock.Left);
            dock.Children.Add(l);
            dock.Children.Add(content);
            var slot = Slot(dock);
            slot.Padding = new Thickness(8, 4, 4, 4);   // the icon column (Controls.xaml CardLabel)
            AutomationProperties.SetName(slot, label);
            return slot;
        }

        /// <summary>An ordinary card row: header, shortcut, optional icon glyph.</summary>
        public static MenuItem Row(string header, string? gesture, string? glyph, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (!string.IsNullOrEmpty(gesture)) item.InputGestureText = gesture;
            if (!string.IsNullOrEmpty(glyph)) item.Icon = glyph;
            item.Click += (_, _) => run();
            return item;
        }

        /// <summary>Opens a pointer card or a keyboard card at a position in its target.</summary>
        public static void Open(ContextMenu menu, FrameworkElement target, bool keyboard, Rect keyboardPosition)
        {
            menu.PlacementTarget = target;
            menu.Placement = keyboard ? PlacementMode.RelativePoint : PlacementMode.MousePoint;
            if (keyboard)
            {
                menu.PlacementRectangle = keyboardPosition;
                bool dismissed = false;
                void Opened(object sender, RoutedEventArgs args)
                {
                    menu.Opened -= Opened;
                    menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        if (dismissed || !menu.IsOpen) return;
                        var first = menu.Items.OfType<MenuItem>()
                            .SelectMany(item => Elements(item.Header as DependencyObject))
                            .OfType<Control>().FirstOrDefault(control => control.IsEnabled && control.IsVisible && control.Focusable);
                        if (first != null) first.Focus();
                        else menu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    }));
                }
                void Closed(object sender, RoutedEventArgs args)
                {
                    dismissed = true;
                    menu.Opened -= Opened;
                    menu.Closed -= Closed;
                    // A card action may already have opened a dialog. Restore focus only when
                    // it is still in the dismissed card, or no element has taken it.
                    if (Keyboard.FocusedElement is not DependencyObject focused ||
                        ReferenceEquals(focused, menu) || menu.IsAncestorOf(focused)) target.Focus();
                }
                menu.Opened += Opened;
                menu.Closed += Closed;
            }
            menu.IsOpen = true;
        }

        private static IEnumerable<DependencyObject> Elements(DependencyObject? root)
        {
            if (root == null) yield break;
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var element in Elements(child)) yield return element;
        }

        /// <summary>Closes the card that holds this element.</summary>
        public static void Close(DependencyObject from)
        {
            for (DependencyObject? d = from; d != null; d = LogicalTreeHelper.GetParent(d) ?? (d is Visual ? VisualTreeHelper.GetParent(d) : null))
                if (d is ContextMenu menu) { menu.IsOpen = false; return; }
        }

        private static MenuItem Slot(object header)
        {
            var slot = new CardSlotItem { Header = header };
            slot.SetResourceReference(FrameworkElement.StyleProperty, "CardSlot");
            return slot;
        }

        // A header, quick strip or labeled row holds controls of its own, so it is a group to
        // assistive technology and not one more menu item to arrow through.
        private sealed class CardSlotItem : MenuItem
        {
            protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

            private sealed class Peer : System.Windows.Automation.Peers.MenuItemAutomationPeer
            {
                public Peer(MenuItem owner) : base(owner) { }
                protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
                    System.Windows.Automation.Peers.AutomationControlType.Group;
                protected override string GetClassNameCore() => "CardSlot";
            }
        }
    }
}
