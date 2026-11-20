#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;

namespace KillerFamily.Controls
{
    /// <summary>
    /// A tile's shortcut line: the keys in brackets, and a long chord ("Ctrl+Alt+Shift+Left") split after the
    /// plus nearest its middle onto two lines, so the shortcut never sets the tile's width or gets cut off
    /// (Steve 2026-11-19). Short chords like "Ctrl+Shift+G" stay on one line.
    /// </summary>
    public sealed class TileKeysConverter : IValueConverter
    {
        public static readonly TileKeysConverter Instance = new();
        private const int OneLine = 14;

        public static string Format(string? keys)
        {
            if (string.IsNullOrEmpty(keys)) return string.Empty;
            if (keys!.Length <= OneLine) return "(" + keys + ")";
            int best = -1;
            for (int i = 0; i < keys.Length - 1; i++)
                if (keys[i] == '+' && (best < 0 || Math.Abs(i + 1 - keys.Length / 2.0) < Math.Abs(best + 1 - keys.Length / 2.0))) best = i;
            return best < 0 ? "(" + keys + ")" : "(" + keys.Substring(0, best + 1) + "\n" + keys.Substring(best + 1) + ")";
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Format(value as string);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

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
        /// <summary>The tile's stable name for a customized card (CardEdit). Defaults to the glyph, plus "~" when
        /// mirrored, which is unique on a card because two tiles never share a glyph unless one is mirrored.</summary>
        public string? Id { get; init; }
        internal string Key => Id ?? (Glyph + (Mirror ? "~" : ""));
    }

    /// <summary>
    /// Makes a card's tile grid customizable (Steve 2026-11-19). A small pencil in the card's top-right corner turns
    /// on edit mode: the card stays open with an accent border, a Hidden drawer two tiles wide slides out on the right,
    /// and tiles move by dragging: a tile under the pointer shows an open hand, the hand closes as the tile is picked
    /// up, the tile follows the pointer, and the other tiles slide aside to show where it will land, on the card in any
    /// spot or in the drawer. A click without a drag moves a tile to the other side, which also works from the
    /// keyboard. The card's tiles flow in the user's order with no gaps; taking one away slides the rest up.
    /// Locked tiles, the card's most important actions, can't be hidden or dragged and are drawn dimmed. The drawer's
    /// close X ends editing, and Reset to default returns to the starting layout. Only big cards get this; a small
    /// card stays fixed.
    /// DefaultHidden names the tiles that start in the drawer (an action that rarely belongs on that card, like Paste
    /// on a picture, or a spare the app offers); the user can drag them out, and Reset to default returns to exactly
    /// this set.
    /// Load and Save carry the user's layout in the app's own settings: "v2:", the card's tile ids in order joined by
    /// "|", then "/" and the drawer's tile ids, once the user has changed anything, and null (store it as empty) for
    /// the defaults. A "v1:" layout (the hidden tile ids alone) still loads.
    /// </summary>
    public sealed class CardEdit
    {
        public CardEdit(Func<string?> load, Action<string?> save) { Load = load; Save = save; }
        public Func<string?> Load { get; }
        public Action<string?> Save { get; }
        public Func<CardTile, bool> Locked { get; init; } = _ => false;
        public Func<CardTile, bool> DefaultHidden { get; init; } = _ => false;
        public string EditTip { get; init; } = "Customize this card";
        public string DoneTip { get; init; } = "Done";
        public string ResetTip { get; init; } = "Reset to default";
        /// <summary>The drawer's Reset link text: a word, because a circular arrow reads as Refresh.</summary>
        public string ResetLabel { get; init; } = "Reset";
        public string HiddenLabel { get; init; } = "Hidden";
        public string HiddenHint { get; init; } = "Drag a tile here to hide it";
        public string LockedTip { get; init; } = "Always shown";
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
            => TileGroups(card, name, groups, null, columns);

        /// <summary>TileGroups with edit mode (CardEdit) when edit is set; see CardEdit for how it behaves.</summary>
        public static Border TileGroups(ContextMenu card, string name, IEnumerable<IEnumerable<CardTile>> groups, CardEdit? edit, int columns = CardColumns)
        {
            var lists = groups.Select(g => g.ToList()).Where(g => g.Count > 0).ToList();
            if (edit is not null) return EditableGroups(card, name, lists, edit, columns);
            return GroupGrid(card, name, lists, columns);
        }

        private static Border GroupGrid(ContextMenu card, string name, List<List<CardTile>> lists, int columns, TileGrid? measureFrom = null)
        {
            var cells = new List<CardTile?>();
            foreach (var list in lists)
            {
                if (list.Count == 0) continue;
                cells.AddRange(list);
                for (int pad = (columns - list.Count % columns) % columns; pad > 0; pad--) cells.Add(null);
            }
            // Equal columns, but each row only as tall as its own tiles: a UniformGrid gave every row the height of the
            // tallest (a tile with a shortcut line), which left a blank line under the rows without one.
            var grid = new TileGrid { MeasureFrom = measureFrom };
            for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < (cells.Count + columns - 1) / columns; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var strip = Strip(card, name, grid, cells);
            for (int i = 0; i < grid.Children.Count; i++)
            {
                Grid.SetRow(grid.Children[i], i / columns);
                Grid.SetColumn(grid.Children[i], i % columns);
            }
            if (measureFrom is not null)
                for (int c = 0; c < columns; c++)
                {
                    var spacer = new FrameworkElement { Height = 0, IsHitTestVisible = false, Focusable = false };
                    Grid.SetColumn(spacer, c);
                    grid.Children.Add(spacer);
                    grid.Spacers.Add(spacer);
                }
            return strip;
        }

        // A customizable card lays out every tile it has, spares included, in a hidden, zero-height copy of its grid. Any
        // tile can end up in any column once others hide, so the shown grid holds every column at least as wide as the
        // widest tile in that copy, through a zero-height spacer per column. Hiding or showing tiles never collapses the
        // card to two across, shifts a column, or changes the card's width.
        private sealed class TileGrid : Grid
        {
            public TileGrid? MeasureFrom { get; init; }
            public List<FrameworkElement> Spacers { get; } = [];
            public double[] ColumnWidths { get; private set; } = [];

            protected override Size MeasureOverride(Size constraint)
            {
                if (MeasureFrom is not null && MeasureFrom.ColumnWidths.Length == 0) MeasureFrom.Measure(new Size(constraint.Width, double.PositiveInfinity));
                if (MeasureFrom is not null)
                    for (int c = 0; c < Spacers.Count; c++)
                    {
                        double w = MeasureFrom.ColumnWidths.DefaultIfEmpty(0).Max();
                        if (Spacers[c].Width is double old && (double.IsNaN(old) || old != w)) Spacers[c].Width = w;
                    }
                var size = base.MeasureOverride(constraint);
                ColumnWidths = new double[ColumnDefinitions.Count];
                foreach (UIElement child in Children)
                    if (GetColumn(child) is int c && c < ColumnWidths.Length) ColumnWidths[c] = Math.Max(ColumnWidths[c], child.DesiredSize.Width);
                return size;
            }
        }

        // The customizable card grid (CardEdit).
        private static Border EditableGroups(ContextMenu card, string name, List<List<CardTile>> lists, CardEdit edit, int columns)
        {
            const string Version = "v2:";
            var all = lists.SelectMany(g => g).GroupBy(t => t.Key).Select(g => g.First()).ToList();
            var byKey = all.ToDictionary(t => t.Key);
            // The card's tiles in the order they show (Steve 2026-11-19: drag a tile anywhere, no gaps). Every other
            // tile is in the drawer. Locked tiles always show. The default order is the groups one after another.
            var defaultOrder = all.Where(t => edit.Locked(t) || !edit.DefaultHidden(t)).Select(t => t.Key).ToList();
            var order = LoadOrder(edit.Load());
            List<string> LoadOrder(string? saved)
            {
                var shown = new List<string>();
                var stashed = new HashSet<string>();
                if (saved is not null && saved.StartsWith(Version, StringComparison.Ordinal))
                {
                    var parts = saved[Version.Length..].Split('/');
                    shown.AddRange(parts[0].Split('|', StringSplitOptions.RemoveEmptyEntries).Where(byKey.ContainsKey).Distinct());
                    if (parts.Length > 1) stashed.UnionWith(parts[1].Split('|', StringSplitOptions.RemoveEmptyEntries));
                }
                else if (saved is not null && saved.StartsWith("v1:", StringComparison.Ordinal))
                {
                    stashed.UnionWith(saved[3..].Split('|', StringSplitOptions.RemoveEmptyEntries));
                    shown.AddRange(all.Select(t => t.Key).Where(k => !stashed.Contains(k)));
                }
                else return [.. defaultOrder];
                // A locked tile always shows, and one the saved layout doesn't name (the app added it since) takes its
                // default side, at the end of the card.
                foreach (var t in all)
                    if (!shown.Contains(t.Key) && (edit.Locked(t) || (!stashed.Contains(t.Key) && !edit.DefaultHidden(t)))) shown.Add(t.Key);
                return shown;
            }
            bool Hidden(string key) => !order.Contains(key);
            bool editing = false;
            // After editing ends the card stays open until the next click: one outside closes it, one inside acts as
            // usual. Without this the card went back to closing on an outside click in the middle of the X's own
            // click, which lands over the drawer, outside the card, and the whole menu shut with the drawer.
            bool lingering = false;
            var owners = new Dictionary<UIElement, CardTile>();

            // The card keeps its three columns; nothing on it changes size in edit mode.
            var root = new Grid();
            var fullStrip = GroupGrid(card, name, lists, columns);
            var full = (TileGrid)fullStrip.Child;
            var fullHost = new Border { Height = 0, ClipToBounds = true, Visibility = Visibility.Hidden, IsHitTestVisible = false, Focusable = false, VerticalAlignment = VerticalAlignment.Top, Child = fullStrip };
            root.Children.Add(fullHost);
            var gridHost = new Border();
            root.Children.Add(gridHost);
            var pencil = SmallButton("", edit.EditTip);
            // Top right, on the side the drawer slides out from (Steve 2026-11-19).
            pencil.HorizontalAlignment = HorizontalAlignment.Right;
            pencil.VerticalAlignment = VerticalAlignment.Top;
            root.Children.Add(pencil);

            // The drawer: its own panel beside the card, its own shape and size, sliding out from behind the card's
            // right edge. It holds the tiles stashed away, two across and scrolling, under Reset to default and the
            // red-hover close X that ends editing, the same X the windows close with.
            var drawerTiles = new Border { HorizontalAlignment = HorizontalAlignment.Left };
            var drawerHint = new TextBlock { Text = edit.HiddenHint, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(4, 6, 4, 6), TextAlignment = TextAlignment.Center };
            drawerHint.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            var drawerTitle = new TextBlock { Text = edit.HiddenLabel, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            drawerTitle.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            var reset = LinkButton(edit.ResetLabel, edit.ResetTip);
            reset.Margin = new Thickness(4, 0, 4, 0);
            DockPanel.SetDock(reset, Dock.Right);
            var done = CloseButton(edit.DoneTip);
            done.Margin = new Thickness(2, 0, 0, 0);
            DockPanel.SetDock(done, Dock.Right);
            var drawerHead = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            drawerHead.Children.Add(done);
            drawerHead.Children.Add(reset);
            drawerHead.Children.Add(drawerTitle);
            DockPanel.SetDock(drawerHead, Dock.Top);
            var drawerBody = new DockPanel();
            drawerBody.Children.Add(drawerHead);
            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = new StackPanel { Children = { drawerHint, drawerTiles } } };
            drawerBody.Children.Add(scroller);
            var slide = new TranslateTransform();
            // Open on the card side: no border and square corners where it joins the card, so the two read as one shape.
            // The same film grain as the card surface, so the drawer reads as the card's own material.
            var grain = new Border { IsHitTestVisible = false };
            grain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            grain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            drawerBody.Margin = new Thickness(7);
            var drawerFace = new Border { BorderThickness = new Thickness(0, 1, 1, 1), Child = new Grid { Children = { grain, drawerBody } } };
            drawerFace.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
            // The menu's own bevels on a flat theme (98SE), as the card template draws them, minus the joined side.
            var bevels = new (string Brush, string Thickness, bool Inner)[]
            {
                ("MenuBevelLightBrush", "MenuBevelLightThickness", true), ("MenuBevelDarkBrush", "MenuBevelDarkThickness", true),
                ("MenuBevel2LightBrush", "MenuBevel2LightThickness", false), ("MenuBevel2DarkBrush", "MenuBevel2DarkThickness", false),
            }.Select(b =>
            {
                var bevel = new Border { IsHitTestVisible = false, Tag = b };
                bevel.SetResourceReference(Border.BorderBrushProperty, b.Brush);
                return bevel;
            }).ToList();
            var slideHost = new Grid { RenderTransform = slide };
            slideHost.Children.Add(drawerFace);
            foreach (var bevel in bevels) slideHost.Children.Add(bevel);
            var drawerClip = new Border { ClipToBounds = true, Child = slideHost };
            var drawer = new Popup
            {
                AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Right, PlacementTarget = card,
                Child = drawerClip, Focusable = false,
            };
            TextOptions.SetTextFormattingMode(drawerClip, TextFormattingMode.Display);
            root.Children.Add(drawer);   // takes no room; keeps the drawer in the card's tree for resources and lookups

            // While a tile is dragged both surfaces show where it would land: the preview order, with the dragged tile
            // dimmed in its spot on the card or in the drawer.
            // The picked-up tile follows the pointer, drawn on the card's hover face on a layer over the card or over the
            // drawer, whichever the pointer is on. It is drawn inside the two surfaces, never in a window of its own.
            var ghost = new Border { IsHitTestVisible = false, Opacity = 0.95, BorderThickness = new Thickness(1), Tag = "DragGhost" };
            ghost.SetResourceReference(Border.BackgroundProperty, "MenuHoverBrush");
            ghost.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
            ghost.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            var cardLayer = new Canvas { IsHitTestVisible = false };
            root.Children.Add(cardLayer);
            var drawerLayer = new Canvas { IsHitTestVisible = false };
            slideHost.Children.Add(drawerLayer);
            List<string>? preview = null;
            string? draggedKey = null;
            // What each surface last drew, so a drag rebuilds only the surface whose tiles changed.
            List<string>? cardDrawn = null, drawerDrawn = null;
            string? cardDragged = null, drawerDragged = null;
            void Rebuild()
            {
                var before = Positions();
                var view = preview ?? order;
                // One run of tiles in the user's order: nothing leaves a gap, so taking a tile away slides the rest up.
                if (cardDrawn is null || !cardDrawn.SequenceEqual(view) || cardDragged != draggedKey || draggedKey is null)
                {
                    foreach (var el in owners.Keys.Where(gridHost.IsAncestorOf).ToList()) owners.Remove(el);
                    var shown = view.Select(k => byKey[k]).ToList();
                    var strip = GroupGrid(card, name, shown.Count > 0 ? [shown] : [], columns, full);
                    foreach (UIElement el in ((Panel)strip.Child).Children)
                        if (TileOf(el) is { } tile)
                        {
                            owners[el] = tile;
                            if (editing) PrepareForEdit(el, tile);
                        }
                    gridHost.Child = strip;
                    cardDrawn = view.ToList();
                    cardDragged = draggedKey;
                }
                // The drawer's own grid, two across, each row only as tall as its tiles, like the card's.
                // Alphabetical by the name the tile shows, so a stocked drawer reads like an index. A card tile held over
                // the drawer lights the drawer up and goes in on release; it isn't shuffled into the list meanwhile.
                var stashed = all.Where(t => !view.Contains(t.Key) && (t.Key != draggedKey || !order.Contains(t.Key)))
                    .OrderBy(t => t.Shown ?? t.Caption, StringComparer.CurrentCultureIgnoreCase).ToList();
                var stashedKeys = stashed.Select(t => t.Key).ToList();
                if (drawerDrawn is null || !drawerDrawn.SequenceEqual(stashedKeys) || drawerDragged != draggedKey || draggedKey is null)
                {
                    foreach (var el in owners.Keys.Where(drawerTiles.IsAncestorOf).ToList()) owners.Remove(el);
                    var drawerStrip = GroupGrid(card, name, stashed.Count > 0 ? [stashed] : [], 2);
                    foreach (UIElement el in ((Panel)drawerStrip.Child).Children)
                        if (TileOf(el) is { } tile)
                        {
                            owners[el] = tile;
                            PrepareForEdit(el, tile);
                        }
                    drawerTiles.Child = drawerStrip;
                    drawerDrawn = stashedKeys;
                    drawerDragged = draggedKey;
                    drawerHint.Visibility = stashed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                reset.IsEnabled = !order.SequenceEqual(defaultOrder);
                if (drawer.IsOpen && draggedKey is null) PlaceDrawer();
                Glide(before);
            }

            // Where each tile sits now, on the card or in the drawer, as drawn (a tile still gliding counts where it is).
            Dictionary<string, (bool InDrawer, Point At)> Positions()
            {
                var at = new Dictionary<string, (bool, Point)>();
                if (!editing) return at;
                foreach (var (el, t) in owners)
                {
                    bool inDrawer = drawerTiles.IsAncestorOf(el);
                    if (inDrawer && !drawer.IsOpen) continue;
                    at[t.Key] = (inDrawer, el.TranslatePoint(new Point(), inDrawer ? drawerTiles : gridHost));
                }
                return at;
            }

            // Every tile that stayed on the same side slides from where it was to its new spot.
            void Glide(Dictionary<string, (bool InDrawer, Point At)> before)
            {
                if (before.Count == 0) return;
                root.UpdateLayout();
                if (drawer.IsOpen) drawerClip.UpdateLayout();
                var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                foreach (var (el, t) in owners)
                {
                    bool inDrawer = drawerTiles.IsAncestorOf(el);
                    if (!before.TryGetValue(t.Key, out var was) || was.InDrawer != inDrawer) continue;
                    var now = el.TranslatePoint(new Point(), inDrawer ? drawerTiles : gridHost);
                    double dx = was.At.X - now.X, dy = was.At.Y - now.Y;
                    if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;
                    var shift = new TranslateTransform(dx, dy);
                    el.RenderTransform = shift;
                    shift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
                    shift.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
                }
            }

            double tileWidth = 0;
            bool doneHot = false;
            void PlaceDrawer()
            {
                // The card's visible face sits inside its shadow halo. The drawer is attached to that face's right edge,
                // covering the card's border where they meet. It starts just under the card's top corner and never runs
                // past its bottom one: a full drawer scrolls instead.
                card.UpdateLayout();
                var face = Descendants(card).OfType<Border>().FirstOrDefault(b => ReferenceEquals(b.BorderBrush, card.BorderBrush) && b.BorderThickness.Left > 0);
                Rect r = face is not null && card.IsAncestorOf(face) ? face.TransformToAncestor(card).TransformBounds(new Rect(face.RenderSize)) : new Rect(card.RenderSize);
                var radius = card.TryFindResource("PanelCornerRadius") is CornerRadius cr ? cr : new CornerRadius(0);
                drawerFace.CornerRadius = new CornerRadius(0, radius.TopRight, radius.BottomRight, 0);
                // A flat theme (98SE) has no accent edge on its menus: the drawer takes the menu's border and bevels.
                bool flat = card.TryFindResource("UseDialogCaption") is true;
                drawerFace.SetResourceReference(Border.BorderBrushProperty, flat ? "MenuBorderBrush" : "PrimaryBrush");
                var innerMargin = card.TryFindResource("MenuBevelInnerMargin") is Thickness im ? im : new Thickness(0);
                foreach (var bevel in bevels)
                {
                    var (_, thicknessKey, inner) = ((string, string, bool))bevel.Tag;
                    var t = card.TryFindResource(thicknessKey) is Thickness th ? th : new Thickness(0);
                    bevel.BorderThickness = new Thickness(0, t.Top, t.Right, t.Bottom);
                    bevel.Margin = inner ? new Thickness(0, innerMargin.Top, innerMargin.Right, innerMargin.Bottom) : new Thickness(0);
                }
                grain.CornerRadius = new CornerRadius(0, Math.Max(0, radius.TopRight - 1), Math.Max(0, radius.BottomRight - 1), 0);
                double inset = Math.Max(4, Math.Max(radius.TopRight, radius.BottomRight));
                double room = Math.Max(60, r.Height - 2 * inset);
                drawerTiles.Width = 2 * tileWidth;
                drawerHint.MaxWidth = 2 * tileWidth;
                drawerFace.MaxHeight = room;
                drawerFace.MinHeight = Math.Min(120, room);
                drawerFace.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double seam = face?.BorderThickness.Right ?? 0;
                drawer.HorizontalOffset = r.Right - card.ActualWidth - seam;
                // The drawer's first column of pixels lies over the card's border; it is clipped away so the card's border
                // shows unbroken between the card and the drawer (Steve 2026-11-19).
                drawerClip.Clip = new RectangleGeometry(new Rect(seam, 0, 100000, 100000));
                drawer.VerticalOffset = r.Top + inset;
            }

            void PrepareForEdit(UIElement el, CardTile t)
            {
                // While editing a tile is something to move, not a command: every tile takes the pointer (a dimmed Paste
                // too), and a locked one says it always shows.
                // An open hand says the tile can be picked up; a locked tile keeps the arrow.
                foreach (var b in Descendants(el).OfType<Button>()) { b.IsEnabled = true; b.Cursor = edit.Locked(t) ? Cursors.Arrow : GrabCursor; }
                if (el is FrameworkElement fe) fe.Cursor = edit.Locked(t) ? Cursors.Arrow : GrabCursor;
                if (edit.Locked(t)) { el.Opacity = 0.45; if (el is FrameworkElement locked) locked.ToolTip = edit.LockedTip; }
                // The held tile's landing spot shows the tile faintly under the one following the pointer.
                if (t.Key == draggedKey) el.Opacity = 0.5;
            }

            void Persist() => edit.Save(order.SequenceEqual(defaultOrder) ? null
                : Version + string.Join("|", order) + "/" + string.Join("|", all.Select(t => t.Key).Where(Hidden)));

            // A click or the keyboard: a card tile goes to the drawer, a drawer tile to the end of the card.
            void Move(CardTile t, bool hide)
            {
                if (edit.Locked(t) || hide == Hidden(t.Key)) return;
                if (hide) order.Remove(t.Key);
                else order.Add(t.Key);
                Persist();
                Rebuild();
            }

            void ResetLayout() { order.Clear(); order.AddRange(defaultOrder); Persist(); Rebuild(); }

            void SetEditing(bool on, bool animate = true)
            {
                EndDrag();
                editing = on;
                card.StaysOpen = true;
                lingering = !on;
                // The pencil opens editing; while editing it steps aside and the drawer's X is the one way out.
                pencil.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
                HotDone(false);
                Rebuild();
                if (on)
                {
                    card.SetResourceReference(Control.BorderBrushProperty, "PrimaryBrush");
                    // The drawer is two of the card's tiles wide and as tall as what it holds, up to the card's own
                    // height; it slides out from under the card.
                    card.UpdateLayout();
                    tileWidth = Math.Max(54, owners.Keys.OfType<FrameworkElement>().Where(e => gridHost.IsAncestorOf(e)).Select(e => e.ActualWidth).DefaultIfEmpty(0).Max());
                    PlaceDrawer();
                    double width = drawerFace.DesiredSize.Width;
                    drawer.IsOpen = true;
                    slide.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(-width, 0, TimeSpan.FromMilliseconds(180))
                    { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
                }
                else
                {
                    card.ClearValue(Control.BorderBrushProperty);
                    if (!animate || !drawer.IsOpen) { slide.BeginAnimation(TranslateTransform.XProperty, null); drawer.IsOpen = false; HoldMouse(); return; }
                    // Slides back in under the card, then closes.
                    var tuck = new System.Windows.Media.Animation.DoubleAnimation(-drawerFace.ActualWidth, TimeSpan.FromMilliseconds(150))
                    { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };
                    tuck.Completed += (_, _) => { if (!editing) { slide.BeginAnimation(TranslateTransform.XProperty, null); drawer.IsOpen = false; HoldMouse(); } };
                    slide.BeginAnimation(TranslateTransform.XProperty, tuck);
                }
            }

            pencil.Click += (_, _) => { if (!editing) SetEditing(true); };
            reset.Click += (_, _) => ResetLayout();
            done.Click += (_, _) => { if (editing) SetEditing(false); };
            drawerClip.MouseLeave += (_, _) => { HotDone(false); HotReset(false); };
            card.Closed += (_, _) =>
            {
                EndDrag();
                if (editing) SetEditing(false, animate: false);
                lingering = false;
                card.StaysOpen = false;
            };
            // The card keeps the mouse once the drawer is gone, so the next click anywhere reaches it.
            void HoldMouse()
            {
                if (!lingering || !card.IsOpen) return;
                if (Mouse.Captured is DependencyObject held && (held == card || card.IsAncestorOf(held))) return;
                Mouse.Capture(card, CaptureMode.SubTree);
            }
            card.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
            {
                if (!lingering) return;
                lingering = false;
                card.StaysOpen = false;
                if (card.InputHitTest(e.GetPosition(card)) is null) { card.IsOpen = false; e.Handled = true; }
            }), true);

            // The card holds the mouse while it is open, so the drawer's clicks arrive at the card: both surfaces are
            // hit-tested by position. Drag a tile between them; a click without a drag moves it to the other side.
            CardTile? pressed = null;
            UIElement? pressedEl = null;
            Point start = default, grab = default;
            bool dragging = false, scrubbing = false, drawerLit = false;
            // The card holds the mouse, so the X never sees the pointer arrive: its red hover follows the pointer here.
            // The Reset link underlines under the pointer, for the same reason.
            void HotReset(bool on)
            {
                if (reset.Content is TextBlock text) text.TextDecorations = on && reset.IsEnabled ? TextDecorations.Underline : null;
            }
            void HotDone(bool on)
            {
                if (on == doneHot) return;
                doneHot = on;
                if (on)
                {
                    done.SetResourceReference(Control.BackgroundProperty, "CaptionCloseHoverBrush");
                    done.SetResourceReference(Control.ForegroundProperty, "CaptionCloseHoverFgBrush");
                }
                else
                {
                    done.Background = Brushes.Transparent;
                    done.SetResourceReference(Control.ForegroundProperty, "DimTextBrush");
                }
            }
            bool Over(MouseEventArgs e, FrameworkElement el)
            {
                if (!drawer.IsOpen || !el.IsVisible) return false;
                var p = e.GetPosition(el);
                return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
            }
            ScrollBar? Bar() => Descendants(scroller).OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Vertical && b.IsVisible);
            void Scrub(MouseEventArgs e)
            {
                if (Bar() is not { } bar) return;
                double f = Math.Clamp(e.GetPosition(bar).Y / Math.Max(1, bar.ActualHeight), 0, 1);
                scroller.ScrollToVerticalOffset(f * scroller.ScrollableHeight);
            }
            bool OverDrawer(MouseEventArgs e)
            {
                if (!drawer.IsOpen) return false;
                var p = e.GetPosition(drawerFace);
                return p.X >= 0 && p.Y >= 0 && p.X <= drawerFace.ActualWidth && p.Y <= drawerFace.ActualHeight;
            }
            DependencyObject? HitAt(MouseEventArgs e)
            {
                if (OverDrawer(e)) return drawerFace.InputHitTest(e.GetPosition(drawerFace)) as DependencyObject;
                var p = e.GetPosition(root);
                return p.X >= 0 && p.Y >= 0 && p.X <= root.ActualWidth && p.Y <= root.ActualHeight ? root.InputHitTest(p) as DependencyObject : null;
            }
            CardTile? Owner(DependencyObject? d)
            {
                for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                    if (d is UIElement u && owners.TryGetValue(u, out var t)) { pressedEl = u; return t; }
                return null;
            }
            bool Within(DependencyObject? d, DependencyObject parent)
            {
                for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                    if (d == parent) return true;
                return false;
            }
            // A mouse event over the drawer reaches both the drawer and the card (the drawer sits in the card's tree), so
            // each event is handled once.
            RoutedEventArgs? seen = null;
            bool Fresh(RoutedEventArgs e) { if (ReferenceEquals(e, seen)) return false; seen = e; return true; }
            void Down(object? sender, MouseButtonEventArgs e)
            {
                if (!editing || e.ChangedButton != MouseButton.Left || !Fresh(e)) return;
                // The element under the click when the event reaches it directly, else the surface under the pointer.
                var src = e.OriginalSource as DependencyObject;
                var hit = Owner(src) is not null || Within(src, reset) || Within(src, done) ? src : HitAt(e);
                if (Within(hit, done) || Over(e, done)) { SetEditing(false); e.Handled = true; return; }
                if (Within(hit, reset)) { if (reset.IsEnabled) ResetLayout(); e.Handled = true; return; }
                // The drawer's scroll bar: the card holds the mouse, so a press on it scrolls to that spot and a drag
                // follows the pointer.
                if (Bar() is { } bar && Over(e, bar)) { scrubbing = true; Scrub(e); e.Handled = true; return; }
                if (!Press(Screen(e), hit)) { if (OverDrawer(e)) e.Handled = true; return; }
                e.Handled = true;
            }
            void MoveMouse(object? sender, MouseEventArgs e)
            {
                if (!editing || !Fresh(e)) return;
                HotDone(Over(e, done));
                HotReset(Over(e, reset));
                if (scrubbing) { if (e.LeftButton == MouseButtonState.Pressed) Scrub(e); else scrubbing = false; return; }
                if (pressed is null) return;
                if (e.LeftButton != MouseButtonState.Pressed) { Release(Screen(e)); return; }
                Drag(Screen(e));
            }
            void Up(object? sender, MouseButtonEventArgs e)
            {
                if (!Fresh(e)) return;
                if (scrubbing) { scrubbing = false; e.Handled = true; return; }
                if (pressed is null) return;
                Release(Screen(e));
                e.Handled = true;
            }

            // The card and the drawer are separate popups, so a drag is tracked in screen points.
            Point Screen(MouseEventArgs e) => card.PointToScreen(e.GetPosition(card));
            bool Inside(FrameworkElement el, Point screen)
            {
                if (PresentationSource.FromVisual(el) is null) return false;
                var p = el.PointFromScreen(screen);
                return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
            }
            bool OnDrawer(Point screen) => drawer.IsOpen && Inside(drawerFace, screen);
            DependencyObject? HitAtScreen(Point screen) =>
                OnDrawer(screen) ? drawerFace.InputHitTest(drawerFace.PointFromScreen(screen)) as DependencyObject
                : Inside(root, screen) ? root.InputHitTest(root.PointFromScreen(screen)) as DependencyObject : null;

            bool Press(Point screen, DependencyObject? hit)
            {
                if (!editing) return false;
                pressed = Owner(hit ?? HitAtScreen(screen));
                if (pressed is null) return false;
                start = screen;
                dragging = false;
                return true;
            }

            void Drag(Point screen)
            {
                if (pressed is null || edit.Locked(pressed)) return;
                if (!dragging)
                {
                    if ((card.PointFromScreen(screen) - card.PointFromScreen(start)).Length < 4) return;
                    PickUp();
                }
                bool overDrawer = OnDrawer(screen);
                PlaceGhost(screen, overDrawer);
                if (overDrawer != drawerLit)
                {
                    drawerLit = overDrawer;
                    drawerFace.SetResourceReference(Border.BackgroundProperty, overDrawer ? "MenuHoverBrush" : "MenuBackgroundBrush");
                }
                var next = overDrawer ? order.Where(k => k != pressed.Key).ToList() : Landing(screen);
                if (preview is not null && next.SequenceEqual(preview)) return;
                preview = next;
                Rebuild();
            }

            // The card order with the dragged tile where the pointer is: in the spot of the tile under it, which slides
            // aside, or at the end past the last tile. Outside both surfaces the last spot holds.
            List<string> Landing(Point screen)
            {
                var view = preview ?? order;
                if (pressed is null || !Inside(gridHost, screen) || gridHost.Child is not Border { Child: Panel grid }) return view;
                var p = grid.PointFromScreen(screen);
                string? target = null;
                foreach (var (el, t) in owners)
                    if (el is FrameworkElement fe && grid.Children.Contains(fe) && new Rect((Point)VisualTreeHelper.GetOffset(fe), fe.RenderSize).Contains(p)) { target = t.Key; break; }
                var next = view.ToList();
                int from = next.IndexOf(pressed.Key);
                if (target is null)
                {
                    if (from >= 0) return view;   // between tiles on the card: the tile stays where it is shown
                    next.Add(pressed.Key);
                    return next;
                }
                if (target == pressed.Key) return view;
                int to = next.IndexOf(target);
                if (from >= 0) next.RemoveAt(from);
                next.Insert(to, pressed.Key);
                return next;
            }

            void PlaceGhost(Point screen, bool overDrawer)
            {
                if (ghost.Child is null) return;
                var layer = overDrawer ? drawerLayer : cardLayer;
                if (ghost.Parent != layer)
                {
                    (ghost.Parent as Panel)?.Children.Remove(ghost);
                    layer.Children.Add(ghost);
                }
                var p = layer.PointFromScreen(screen);
                Canvas.SetLeft(ghost, p.X - grab.X);
                Canvas.SetTop(ghost, p.Y - grab.Y);
            }

            void PickUp()
            {
                dragging = true;
                draggedKey = pressed!.Key;
                if (pressedEl is FrameworkElement fe && fe.ActualWidth > 0)
                {
                    grab = fe.PointFromScreen(start);
                    // A live copy of the tile, built the way the card builds its tiles, so its icon and label always show.
                    var copy = GroupGrid(card, name, [[pressed]], 1);
                    copy.Margin = new Thickness(0);
                    ghost.Child = copy;
                    ghost.Width = fe.ActualWidth;
                    ghost.Height = fe.ActualHeight;
                }
                // The hand closes on the tile for as long as it is held.
                Mouse.OverrideCursor = GrabbingCursor;
                if (Mouse.Captured is not DependencyObject held || (held != card && !card.IsAncestorOf(held))) Mouse.Capture(card, CaptureMode.SubTree);
            }

            void Release(Point screen)
            {
                if (pressed is null) return;
                var t = pressed;
                bool moved = dragging;
                if (moved) Drag(screen);
                var landed = preview;
                EndDrag();
                if (!moved) { Move(t, !Hidden(t.Key)); return; }
                if (landed is not null && !landed.SequenceEqual(order))
                {
                    order.Clear();
                    order.AddRange(landed);
                    Persist();
                }
                Rebuild();
            }

            void EndDrag()
            {
                pressed = null;
                dragging = false;
                preview = null;
                draggedKey = null;
                (ghost.Parent as Panel)?.Children.Remove(ghost);
                ghost.Child = null;
                if (ReferenceEquals(Mouse.OverrideCursor, GrabbingCursor)) Mouse.OverrideCursor = null;
                drawerLit = false;
                drawerFace.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
            }
            // Test hook: drives the drag in screen points the way the mouse does.
            root.SetValue(EditDriverProperty, new EditDriver(s => Press(s, null), Drag, Release, () => order.ToList(), on => { if (on != editing) SetEditing(on, animate: false); }));
            // Both surfaces listen: the card while it holds the mouse, the drawer when it gets the events itself.
            foreach (UIElement surface in new UIElement[] { card, drawerClip })
            {
                surface.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(Down), true);
                surface.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(MoveMouse), true);
                surface.AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(Up), true);
                // The wheel scrolls the drawer while the pointer is over it.
                surface.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) =>
                {
                    if (!editing || !OverDrawer(e)) return;
                    scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 3.0);
                    e.Handled = true;
                }), true);
            }
            // Keyboard: Enter or Space on a card tile moves it to the drawer while editing.
            root.PreviewKeyDown += (_, e) =>
            {
                if (!editing || e.Key is not (Key.Enter or Key.Space) || Owner(Keyboard.FocusedElement as DependencyObject) is not { } t) return;
                Move(t, !Hidden(t.Key));
                e.Handled = true;
            };

            Rebuild();
            var outer = new Border { Child = root };
            AutomationProperties.SetName(outer, name);
            return outer;
        }

        // A bare icon button for a card's corner control (the edit pencil): no face and no hover box, only the glyph,
        // dim at rest and in the accent color under the pointer (Steve 2026-11-19: the gray hover box sat off center).
        private static Button SmallButton(string glyph, string tip)
        {
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(content);
            var b = new Button
            {
                Content = glyph, ToolTip = tip, Width = 22, Height = 22, Padding = new Thickness(0), FontSize = 11,
                FontFamily = new FontFamily("Segoe MDL2 Assets"), Cursor = Cursors.Hand,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = bd },
            };
            b.SetResourceReference(Control.ForegroundProperty, "DimTextBrush");
            b.MouseEnter += (_, _) => b.SetResourceReference(Control.ForegroundProperty, "PrimaryBrush");
            b.MouseLeave += (_, _) => b.SetResourceReference(Control.ForegroundProperty, "DimTextBrush");
            AutomationProperties.SetName(b, tip);
            ToolTipService.SetShowOnDisabled(b, true);
            return b;
        }

        // A text link at caption size: the accent color, dim while there is nothing to do, underlined under the pointer
        // (the card drives that, since it holds the mouse). No face, no border.
        private static Button LinkButton(string text, string tip)
        {
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(content);
            var label = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            var b = new Button { Content = label, ToolTip = tip, Cursor = Cursors.Hand, Focusable = false, VerticalAlignment = VerticalAlignment.Center, Template = new ControlTemplate(typeof(Button)) { VisualTree = bd } };
            void Paint() => label.SetResourceReference(TextBlock.ForegroundProperty, b.IsEnabled ? "PrimaryBrush" : "DimTextBrush");
            Paint();
            b.IsEnabledChanged += (_, _) => { Paint(); if (!b.IsEnabled) label.TextDecorations = null; };
            AutomationProperties.SetName(b, tip);
            ToolTipService.SetShowOnDisabled(b, true);
            return b;
        }

        // The windows' close X at a small button's size: red on hover (CaptionCloseHoverBrush), the pixel-art X on the
        // flat 98SE theme. The card drives the hover itself while it holds the mouse.
        private static Button CloseButton(string tip)
        {
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            var glyph = new FrameworkElementFactory(typeof(ContentPresenter));
            glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            glyph.SetResourceReference(UIElement.VisibilityProperty, "CaptionFontGlyphVisibility");
            bd.AppendChild(glyph);
            var pixels = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            pixels.SetValue(FrameworkElement.WidthProperty, 10.0);
            pixels.SetValue(FrameworkElement.HeightProperty, 9.0);
            pixels.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            pixels.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            pixels.SetValue(UIElement.IsHitTestVisibleProperty, false);
            pixels.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            pixels.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);
            pixels.SetValue(System.Windows.Shapes.Shape.FillProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            pixels.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M0,0h2v1h-2z M8,0h2v1h-2z M1,1h2v1h-2z M7,1h2v1h-2z M2,2h2v1h-2z M6,2h2v1h-2z M3,3h4v1h-4z M4,4h2v1h-2z M3,5h4v1h-4z M2,6h2v1h-2z M6,6h2v1h-2z M1,7h2v1h-2z M7,7h2v1h-2z M0,8h2v1h-2z M8,8h2v1h-2z"));
            pixels.SetResourceReference(UIElement.VisibilityProperty, "CaptionDrawnGlyphVisibility");
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.AppendChild(bd);
            grid.AppendChild(pixels);
            var b = new Button
            {
                Content = "\uE8BB", ToolTip = tip, Width = 22, Height = 22, Padding = new Thickness(0), FontSize = 10,
                FontFamily = new FontFamily("Segoe MDL2 Assets"), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                Focusable = true, Template = new ControlTemplate(typeof(Button)) { VisualTree = grid },
            };
            b.SetResourceReference(Control.ForegroundProperty, "DimTextBrush");
            b.MouseEnter += (_, _) => { b.SetResourceReference(Control.BackgroundProperty, "CaptionCloseHoverBrush"); b.SetResourceReference(Control.ForegroundProperty, "CaptionCloseHoverFgBrush"); };
            b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; b.SetResourceReference(Control.ForegroundProperty, "DimTextBrush"); };
            AutomationProperties.SetName(b, tip);
            return b;
        }

        private static readonly DependencyProperty TileProperty = DependencyProperty.RegisterAttached("Tile", typeof(CardTile), typeof(CardBuilder));

        /// <summary>Test hook on an editable card's grid: press, drag and release in screen points, the card order, and
        /// edit mode on or off.</summary>
        internal sealed record EditDriver(Func<Point, bool> Press, Action<Point> Drag, Action<Point> Release, Func<List<string>> Order, Action<bool> Editing);
        internal static readonly DependencyProperty EditDriverProperty = DependencyProperty.RegisterAttached("EditDriver", typeof(EditDriver), typeof(CardBuilder));

        // The open hand over a tile that can be picked up and the closed hand while one is held: the family's own grab
        // cursors (Resources/open_hand.cur and closed_hand.cur, the art DragCursors loads), so a card tile is grabbed
        // with the same hand as everything else that moves. A missing resource costs the hand, not the app.
        private static Cursor? grabCursor, grabbingCursor;
        internal static Cursor GrabCursor => grabCursor ??= FamilyCursor("open_hand.cur");
        internal static Cursor GrabbingCursor => grabbingCursor ??= FamilyCursor("closed_hand.cur");

        private static Cursor FamilyCursor(string file)
        {
            try
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/" + file, UriKind.Absolute));
                if (info?.Stream is null) return Cursors.SizeAll;
                using var stream = info.Stream;
                return new Cursor(stream);
            }
            catch { return Cursors.SizeAll; }
        }
        private static CardTile? TileOf(UIElement el) => el.GetValue(TileProperty) as CardTile;

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            int n = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
            if (n == 0) foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var d in Descendants(c)) yield return d;
            else for (int i = 0; i < n; i++) foreach (var d in Descendants(VisualTreeHelper.GetChild(root, i))) yield return d;
        }

        private static Border Strip(ContextMenu card, string name, Panel grid, List<CardTile?> list)
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
                    var cell = WithFlyoutChevron(button);
                    cell.SetValue(TileProperty, tile);
                    grid.Children.Add(cell);
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
                button.SetValue(TileProperty, tile);
                grid.Children.Add(button);
            }
            var strip = new Border { Padding = new Thickness(0), Child = grid };
            strip.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            AutomationProperties.SetName(strip, name);
            return strip;
        }

        // An on/off tile is filled with the selected-row color while it is on (the template paints its Background).
        // Most themes make that color a faint wash, so the glyph and caption keep their colors. A theme that makes
        // it a solid fill states TileCheckedTextBrush (98SE: white on its navy), and the glyph, caption and
        // shortcut take it while the tile is on, or they would sit dark on dark. Public so an app that repaints an
        // open card's tiles in place shows them the same way.
        public static void ShowTileState(Button button, bool on)
        {
            if (on) button.SetResourceReference(Control.BackgroundProperty, "RowSelectedBrush");
            else button.ClearValue(Control.BackgroundProperty);
            bool stated = on && button.TryFindResource("TileCheckedTextBrush") is Brush;
            // A theme that derives the selected-row color from its accent (Light) also makes it a solid fill without
            // stating a text color, so any part that no longer reads on an opaque fill (4.5:1 for text, 3:1 for the
            // glyph) takes black or white, whichever reads better. Parts that already read keep their colors.
            // An opaque gradient (Blood's peach) reads as the average of its stops.
            var fill = !on || stated ? null : button.TryFindResource("RowSelectedBrush") switch
            {
                SolidColorBrush { Color.A: 255 } solid => solid.Color,
                GradientBrush { GradientStops.Count: > 0 } gradient when gradient.GradientStops.All(s => s.Color.A == 255) => Color.FromRgb(
                    (byte)gradient.GradientStops.Average(s => s.Color.R), (byte)gradient.GradientStops.Average(s => s.Color.G),
                    (byte)gradient.GradientStops.Average(s => s.Color.B)),
                _ => (Color?)null,
            };
            static double Luminance(Color c)
            {
                static double Channel(byte v) { double x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
                return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
            }
            static double Contrast(Color a, Color b)
            {
                double la = Luminance(a), lb = Luminance(b);
                return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
            }
            void Paint()
            {
                foreach (string part in new[] { "Glyph", "Caption", "Shortcut" })
                {
                    if (button.Template?.FindName(part, button) is not TextBlock text) continue;
                    if (stated) { text.SetResourceReference(TextBlock.ForegroundProperty, "TileCheckedTextBrush"); continue; }
                    text.ClearValue(TextBlock.ForegroundProperty);
                    if (fill is not { } back || text.Foreground is not SolidColorBrush { } current) continue;
                    if (Contrast(current.Color, back) >= (part == "Glyph" ? 3.0 : 4.5)) continue;
                    text.Foreground = Contrast(Colors.White, back) >= Contrast(Colors.Black, back) ? Brushes.White : Brushes.Black;
                }
            }
            button.ApplyTemplate();
            if (button.Template?.FindName("Glyph", button) is TextBlock) { Paint(); return; }
            // Not templated yet (a tile built before its card is in the tree): paint once it loads.
            void Once(object sender, RoutedEventArgs e) { button.Loaded -= Once; Paint(); }
            button.Loaded += Once;
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
