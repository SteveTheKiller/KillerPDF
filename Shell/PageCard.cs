using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerPDF.Shell
{
    // The page thumbnail's right-click card (KillerUI card menu): a header naming the page with its size, orientation
    // and markup count, then rows of tiles: the page actions, the rotations, the page tools, and the scanner cleanup,
    // the extract-from-page tools and the moves as chevron tiles that open their rows beside them. Every action and
    // shortcut is the one the old 21-row list had. With several thumbnails selected the header says so, and the
    // actions take the selection as before.
    public partial class MainWindow
    {
        private ContextMenu BuildPageCard(int page)
        {
            var menu = MakeThemedMenu();
            if (TryFindResource("CardMenu") is Style card) menu.Style = card;
            // Three tiles and a header icon need less room than the kit card's 404 (Steve: make it less wide).
            menu.MinWidth = menu.MaxWidth = 320;
            int selected = PageList.SelectedItems.Count;
            var row = page < PageList.Items.Count ? PageList.Items[page] as PageThumbnailVm : null;

            menu.Items.Add(CardSlot(PageCardHeader(menu, page, selected, row), new Thickness(4, 2, 4, 6)));
            var strip = QuickStrip(menu,
                ("\uE8C8", Loc("Str_Ctx_DuplicatePage"), null, () => DuplicatePage(page), false, true),
                ("\uE8B1", Loc("Str_Ctx_ExtractPages"), "AltShift:X", () => Split_Click(this, new RoutedEventArgs()), false, true),
                ("\uE74D", Loc("Str_Ctx_DeletePages"), "Del", () => Delete_Click(this, new RoutedEventArgs()), false, true));
            menu.Items.Add(CardSlot(strip, new Thickness(0)));
            FocusFirstActionFromKeyboard(menu, strip);
            // Rotation is a second row of the same tiles: the outline buttons stuck out (Steve 2026-11-14).
            var turns = QuickStrip(menu,
                (GlyphText(0xE7AD), Loc("Str_Ctx_RotateCCWShort"), "Ctrl:L", () => RotatePages_Click(-90), true, true),
                (GlyphText(0xE895), Loc("Str_Ctx_UpsideDown"), null, () => RotatePages_Click(180), false, true),
                (GlyphText(0xE7AD), Loc("Str_Ctx_RotateCWShort"), "Ctrl:R", () => RotatePages_Click(90), false, true));
            menu.Items.Add(CardSlot(turns, new Thickness(0)));

            // The page tools are tiles too (Steve: go all out on the icons). Merge names no shortcut: Alt+Shift+B merges
            // at the end, this tile after the clicked page.
            var tools = QuickStrip(menu, 3,
                ("\uE710", Loc("Str_Lbl_Merge"), null, async () => await MergeAtIndex(page + 1), false, true),
                ("\uE7C2", Loc("Str_Tf_Suffix"), "R", () => OpenTransformWindow(), false, true),
                ("\uE790", Loc("Str_ColorCorrection"), "E", () => OpenTransformWindow(TransformWindowMode.ColorCorrection), false, true),
                // The thumbnails' own Ctrl+Shift+O: every selected page, not only the clicked one.
                ("\uEE6F", Loc("Str_Ctx_OcrPage"), "CtrlShift:O", OcrSelectedPagesToClipboard, false, true),
                // #207: the shared export dialog, scoped to the clicked selection (still editable).
                ("\uEB9F", Loc("Str_Ctx_ExportPageImage"), null, () =>
                {
                    var nums = PageList.SelectedItems.Cast<PageThumbnailVm>().Select(vm => vm.PageIndex + 1).OrderBy(n => n);
                    _ = ExportImagesFlow(string.Join(",", nums));
                }, false, true),
                ("\uE932", Loc("Str_Ctx_StampPages"), "S", () => OpenStampTool(), false, true));
            menu.Items.Add(CardSlot(tools, new Thickness(0)));

            // #436: with two or more pages selected these act on the selection, otherwise on the whole document.
            var cleanUp = MakeMenuItem(Loc("Str_Card_CleanUp"), (_, _) => { });
            cleanUp.Items.Add(MakeMenuItem(Loc("Str_Pages_FixOrientation"), (_, _) => FixPageOrientation(), GestureFor("AltShift:O"), "\uE7AD"));
            cleanUp.Items.Add(MakeMenuItem(Loc("Str_Pages_Straighten"), (_, _) => StraightenScannedPages(), glyph: "\uE809"));
            cleanUp.Items.Add(MakeMenuItem(Loc("Str_Pages_DivideSpreads"), (_, _) => DivideSpreads(), glyph: "\uE89F"));

            var fromPage = MakeMenuItem(Loc("Str_Card_FromPage"), (_, _) => { });
            fromPage.Items.Add(MakeMenuItem(Loc("Str_Pages_RasterizeRegion"), (_, _) => RasterizeRegion(page), glyph: "\uE91B"));
            fromPage.Items.Add(MakeMenuItem(Loc("Str_ExtractImage_Menu"), (_, _) => ExtractImage(page), glyph: "\uEB9F"));
            fromPage.Items.Add(MakeMenuItem(Loc("Str_TableExport_Title"), (_, _) => ExportTable(page), glyph: "\uE80A"));
            fromPage.Items.Add(MakeMenuItem(Loc("Str_Paragraph_Title"), (_, _) => EditParagraph(page), GestureFor("AltShift:P"), "\uE8E4"));

            var move = MakeMenuItem(Loc("Str_Card_Move"), (_, _) => { });
            move.Items.Add(MakeMenuItem(Loc("Str_Lbl_MoveUp"), (s, e) => MoveUp_Click(s!, e), glyph: "\uE74A"));
            move.Items.Add(MakeMenuItem(Loc("Str_Lbl_MoveDown"), (s, e) => MoveDown_Click(s!, e), glyph: "\uE74B"));
            move.Items.Add(MakeMenuItem(Loc("Str_Pages_Reverse"), (_, _) => ReversePageOrder(), glyph: "\uE8CB"));

            // The three submenus are tiles with a chevron; each opens its rows beside it.
            var groups = QuickStrip(menu, 3,
                FlyoutTile("\uE771", Loc("Str_Card_CleanUp"), null, cleanUp),
                FlyoutTile("\uE8B9", Loc("Str_Card_FromPage"), null, fromPage),
                // Not the Move row's old arrows: those are the Transform tile's, two tiles up.
                FlyoutTile("\uE8AB", Loc("Str_Card_Move"), null, move));
            menu.Items.Add(CardSlot(groups, new Thickness(0)));
            return menu;
        }

        private FrameworkElement PageCardHeader(ContextMenu menu, int page, int selected, PageThumbnailVm? row)
        {
            var thumb = new Border
            {
                Width = 26, Height = 34, Margin = new Thickness(0, 0, 12, 0), Background = Brushes.White,
                BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center
            };
            thumb.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            var image = new Image { Stretch = Stretch.Uniform };
            if (row is not null) image.SetBinding(Image.SourceProperty, new Binding(nameof(PageThumbnailVm.Thumbnail)) { Source = row });
            thumb.Child = image;

            int count = _doc?.PageCount ?? 0;
            var title = new TextBlock
            {
                Text = selected > 1 ? string.Format(Loc("Str_Card_PagesSelected"), selected) : string.Format(Loc("Str_PageOf"), page + 1, count),
                FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var meta = new TextBlock { Text = selected > 1 ? string.Format(Loc("Str_PageOf"), page + 1, count) : PageCardMeta(page), FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            meta.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(meta);
            var header = new DockPanel();
            DockPanel.SetDock(thumb, Dock.Left);
            header.Children.Add(thumb);
            // Insert a blank page after this one: an icon at the header's right, so the strip below keeps three tiles.
            var insert = new Button
            {
                Content = "\uE7C3", Width = 34, Height = 30, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc("Str_Ctx_InsertBlank")
            };
            if (TryFindResource("CardQuickButton") is Style quick) insert.Style = quick;
            insert.Padding = new Thickness(0, 6, 0, 0);
            System.Windows.Automation.AutomationProperties.SetName(insert, Loc("Str_Ctx_InsertBlank"));
            insert.Click += (_, _) => { menu.IsOpen = false; InsertBlankPage(page); };
            // A plus inside the page glyph, so the icon reads as "add page". The quick-button template draws its content
            // as one glyph, so the plus is laid over it and lets clicks through to the button.
            var insertPlus = new TextBlock
            {
                Text = GlyphText(0xE710), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 7, 0, 0)
            };
            insertPlus.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
            var insertIcon = new Grid { VerticalAlignment = VerticalAlignment.Center };
            insertIcon.Children.Add(insert);
            insertIcon.Children.Add(insertPlus);
            DockPanel.SetDock(insertIcon, Dock.Right);
            header.Children.Add(insertIcon);
            header.Children.Add(text);
            System.Windows.Automation.AutomationProperties.SetName(header, title.Text);
            return header;
        }

        // "8.5 × 11 in · Portrait · 2 markups": size in the Measure bar's units, then orientation, then the page's markup.
        private string PageCardMeta(int page)
        {
            var parts = new List<string>();
            string size = ActiveViewer.MeasurePageSizeText(page);
            if (size.Length > 0) parts.Add(size);
            if (_doc is not null)
            {
                var (width, height) = EnsureEngineDocumentSession().VisualPageSize(page, _pageRotations);
                parts.Add(Loc(width > height ? "Str_Print_Landscape" : "Str_Print_Portrait"));
            }
            int markups = _annotations.TryGetValue(page, out var list) ? list.Count : 0;
            if (markups > 0) parts.Add(markups == 1 ? Loc("Str_Card_Markups1") : string.Format(Loc("Str_Card_MarkupsN"), markups));
            return string.Join(" · ", parts);
        }

        // Quick actions sit in rows the arrow keys skip (KillerUI CardSlot), so a menu opened from the keyboard
        // (Shift+F10, the Menu key) puts focus on the first enabled action of its first strip; from there the arrow keys
        // move between the actions and on to the rows (1.9.3 review BF-012). A menu opened with the mouse leaves focus
        // alone. The shared page menu is refilled on every right-click, so only a strip still on the menu counts, and
        // each menu gets one Opened handler.
        private static readonly ConditionalWeakTable<ContextMenu, List<Button>> s_keyboardFocusTargets = new();

        private static void FocusFirstActionFromKeyboard(ContextMenu menu, FrameworkElement strip)
        {
            // A flyout tile sits in a cell with its chevron.
            if (strip is not Border { Child: UniformGrid grid }
                || grid.Children.Cast<UIElement>().Select(c => c as Button ?? (c as Panel)?.Children.OfType<Button>().FirstOrDefault())
                    .FirstOrDefault(b => b is { IsEnabled: true }) is not { } first) return;
            if (!s_keyboardFocusTargets.TryGetValue(menu, out var targets))
            {
                targets = [];
                s_keyboardFocusTargets.Add(menu, targets);
                menu.Opened += (_, _) =>
                {
                    var target = targets.FirstOrDefault(button => IsOnMenu(menu, button));
                    targets.Clear();
                    if (target is not null && InputManager.Current.MostRecentInputDevice is KeyboardDevice)
                        target.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => target.Focus());
                };
            }
            targets.Add(first);
        }

        private static bool IsOnMenu(ContextMenu menu, DependencyObject element)
        {
            for (DependencyObject? at = element; at is not null; at = LogicalTreeHelper.GetParent(at))
                if (ReferenceEquals(at, menu)) return true;
            return false;
        }

        // An icon-font glyph from its code point, so no private-use character is written into the source.
        private static string GlyphText(int codePoint) => char.ConvertFromUtf32(codePoint);

        // A card row that holds controls rather than being a command (KillerUI CardSlot).
        private MenuItem CardSlot(object content, Thickness padding)
        {
            var slot = new MenuItem { Header = content, Padding = padding };
            if (TryFindResource("CardSlot") is Style style) slot.Style = style;
            slot.Padding = padding;
            return slot;
        }

        // The card's strip of glyph-over-caption buttons between two rules. Each closes the card, then acts; the
        // tooltip carries the full name and its shortcut (the family shortcut rule).
        private FrameworkElement QuickStrip(ContextMenu menu, params (string Glyph, string Caption, string? Gesture, Action Act, bool Mirror, bool Enabled)[] buttons)
            => QuickStrip(menu, 0, buttons);

        // columns > 0 wraps the strip into rows of that many, which keeps a long strip from widening the menu.
        private FrameworkElement QuickStrip(ContextMenu menu, int columns, params (string Glyph, string Caption, string? Gesture, Action Act, bool Mirror, bool Enabled)[] buttons)
            => QuickStrip(menu, columns, [.. buttons.Select(b => new CardTile(b.Glyph, b.Caption, b.Gesture, b.Act, b.Mirror, b.Enabled))]);

        // One tile of a card strip. Flyout: the tile opens these rows next to itself instead of acting, and carries
        // a small chevron beside its icon, the way the toolbar's split buttons do. Checked: an on/off command whose
        // tile is filled while it is on. KeepsOpen: the tile flips in place and the card stays open.
        private sealed record CardTile(string Glyph, string Caption, string? Gesture, Action? Act, bool Mirror, bool Enabled)
        {
            public MenuItem? Flyout { get; init; }
            public bool? Checked { get; init; }
            public bool KeepsOpen { get; init; }
        }

        // A tile that opens the rows of a submenu built for a menu row (Compare, Fillable Field Preset, Clean up...).
        private static CardTile FlyoutTile(string glyph, string caption, string? gesture, MenuItem rows)
            => new(glyph, caption, gesture, null, false, rows.IsEnabled) { Flyout = rows };

        private FrameworkElement QuickStrip(ContextMenu menu, int columns, params CardTile[] tiles)
        {
            var grid = columns > 0 && tiles.Length > columns ? new UniformGrid { Columns = columns } : new UniformGrid { Rows = 1 };
            foreach (var tile in tiles)
            {
                var (glyph, caption, gesture, act, mirror, enabled) = tile;
                // A trailing "..." on a tile reads as a cut-off caption, so tiles drop it.
                caption = caption.TrimEnd('.', '…').TrimEnd();
                // A name too long for two lines of the tile ("Open Containing Folder" read "Open Containi...") shows
                // its short caption; the tooltip and the accessible name keep the full name.
                string shown = ShortTileCaption(caption);
                // The caption is capped at 80 in CardQuickButton, so one long caption ("Clear Page Annotations") wraps
                // instead of stretching the card, while the tile itself fills its column and its hover reaches the
                // card's 8px edge.
                var button = new Button { Content = glyph, Tag = shown, ToolTip = gesture is null ? caption : WithGesture(caption, gesture), IsEnabled = enabled };
                // The caption alone is the accessible name; the tooltip adds the shortcut.
                System.Windows.Automation.AutomationProperties.SetName(button, caption);
                // The tile's second line shows these keys in brackets while Show Shortcuts in Menus is on.
                if (gesture is not null) System.Windows.Automation.AutomationProperties.SetAcceleratorKey(button, GesturesFor(gesture));
                ToolTipService.SetShowOnDisabled(button, true);
                if (TryFindResource("CardQuickButton") is Style style) button.Style = style;
                bool on = tile.Checked == true;
                if (tile.Checked is not null) ShowTileState(button, on);
                if (tile.Flyout is { } rows)
                {
                    ContextMenu? flyout = null;
                    button.Click += (_, _) =>
                    {
                        flyout ??= BuildTileFlyout(menu, button, rows);
                        flyout.IsOpen = true;
                    };
                    grid.Children.Add(WithFlyoutChevron(button));
                    continue;
                }
                if (mirror)
                    button.Loaded += (_, _) =>
                    {
                        if (button.Template?.FindName("Glyph", button) is TextBlock g)
                        {
                            g.RenderTransformOrigin = new Point(0.5, 0.5);
                            g.RenderTransform = new ScaleTransform(-1, 1);
                        }
                    };
                if (tile.KeepsOpen)
                    button.Click += (_, _) => { act?.Invoke(); on = !on; ShowTileState(button, on); };
                else
                    button.Click += (_, _) => { menu.IsOpen = false; act?.Invoke(); };
                grid.Children.Add(button);
            }
            // KillerUI's CardBuilder insets its strip 8 so the strip's top and bottom rules start on the icon column.
            // This strip draws no rules, and the inset only took width: "Extract Page(s)" wrapped onto two lines.
            var strip = new Border { Padding = new Thickness(0), Child = grid };
            strip.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            System.Windows.Automation.AutomationProperties.SetName(strip, Loc("Str_Card_Actions"));
            return strip;
        }

        // The short caption (TileCaptions.ShortKeys) of a tile whose full name is this caption, or the caption itself.
        private string ShortTileCaption(string caption)
        {
            foreach (var (full, shortKey) in Services.TileCaptions.ShortKeys)
                if (string.Equals(Loc(full).TrimEnd('.', '…').TrimEnd(), caption, StringComparison.Ordinal)) return Loc(shortKey);
            return caption;
        }

        // An on/off tile is filled with the selected-row color while it is on (the template paints its Background).
        private static void ShowTileState(Button button, bool on)
        {
            if (on) button.SetResourceReference(Control.BackgroundProperty, "RowSelectedBrush");
            else button.ClearValue(Control.BackgroundProperty);
        }

        // The quick-button template draws its content as one glyph, so the chevron is laid over the tile beside the
        // icon and lets clicks through to it (the same trick as the page card's add-page plus).
        private static Grid WithFlyoutChevron(Button button)
        {
            var chevron = new TextBlock
            {
                Text = GlyphText(0xE76C), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(30, 12, 0, 0)
            };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            var cell = new Grid();
            cell.Children.Add(button);
            cell.Children.Add(chevron);
            return cell;
        }

        // The submenu a flyout tile opens, beside the tile. Its rows are moved off the menu row they were built for.
        // Picking a row closes the card as well; Escape, Left or a click back on the card returns to the card, and a
        // click anywhere else closes both.
        private ContextMenu BuildTileFlyout(ContextMenu card, Button tile, MenuItem rows)
        {
            var flyout = MakeThemedMenu();
            foreach (object row in rows.Items.Cast<object>().ToList())
            {
                rows.Items.Remove(row);
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
    }
}
