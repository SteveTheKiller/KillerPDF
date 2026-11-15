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
            // Inset 8 like the kit's separators, so the strip's rules start on the icon column.
            var rule = new Border { Margin = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(0, 4, 0, 4), Child = grid };
            rule.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            var slot = Slot(rule);
            slot.Padding = new Thickness(0);
            AutomationProperties.SetName(slot, name);
            return slot;
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
