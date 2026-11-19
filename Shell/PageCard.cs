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
            CardTile[] pageRow =
            [
                new("\uE8C8", Loc("Str_Ctx_DuplicatePage"), null, () => DuplicatePage(page), false, true),
                new("\uE8B1", Loc("Str_Ctx_ExtractPages"), "AltShift:X", () => Split_Click(this, new RoutedEventArgs()), false, true),
                new("\uE7C3", Loc("Str_Ctx_InsertBlank"), null, () => InsertBlankPage(page), false, true),
            ];
            // Rotation is a second row of the same tiles: the outline buttons stuck out (Steve 2026-11-14).
            CardTile[] turnRow =
            [
                new(GlyphText(0xE7AD), Loc("Str_Ctx_RotateCCWShort"), "Ctrl:L", () => RotatePages_Click(-90), true, true),
                new(GlyphText(0xE895), Loc("Str_Ctx_UpsideDown"), null, () => RotatePages_Click(180), false, true),
                new(GlyphText(0xE7AD), Loc("Str_Ctx_RotateCWShort"), "Ctrl:R", () => RotatePages_Click(90), false, true),
            ];

            // The page tools are tiles too (Steve: go all out on the icons). Merge names no shortcut: Alt+Shift+B merges
            // at the end, this tile after the clicked page.
            CardTile[] toolRow =
            [
                new("\uE710", Loc("Str_Lbl_Merge"), null, async () => await MergeAtIndex(page + 1), false, true),
                new("\uE7C2", Loc("Str_Tf_Suffix"), "R", () => OpenTransformWindow(), false, true),
                new("\uE790", Loc("Str_ColorCorrection"), "E", () => OpenTransformWindow(TransformWindowMode.ColorCorrection), false, true),
                // The thumbnails' own Ctrl+Shift+O: every selected page, not only the clicked one.
                new("\uEE6F", Loc("Str_Ctx_OcrPage"), "CtrlShift:O", OcrSelectedPagesToClipboard, false, true),
                // #207: the shared export dialog, scoped to the clicked selection (still editable).
                new("\uEB9F", Loc("Str_Ctx_ExportPageImage"), null, () =>
                {
                    var nums = PageList.SelectedItems.Cast<PageThumbnailVm>().Select(vm => vm.PageIndex + 1).OrderBy(n => n);
                    _ = ExportImagesFlow(string.Join(",", nums));
                }, false, true),
                new("\uE932", Loc("Str_Ctx_StampPages"), "S", () => OpenStampTool(), false, true),
            ];

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
            CardTile[] groupRow =
            [
                FlyoutTile("\uE771", Loc("Str_Card_CleanUp"), null, cleanUp),
                FlyoutTile("\uE8B9", Loc("Str_Card_FromPage"), null, fromPage),
                // Not the Move row's old arrows: those are the Transform tile's, two tiles up.
                FlyoutTile("\uE8AB", Loc("Str_Card_Move"), null, move),
            ];
            // Spares in the Hidden drawer: the submenus' rows as tiles of their own, plus Add Blank Page and Compare.
            CardTile[] spares =
            [
                Spare("fixorientation", new("\uE7AD", Loc("Str_Pages_FixOrientation"), "AltShift:O", FixPageOrientation, false, true)),
                Spare("straighten", new("\uE809", Loc("Str_Pages_Straighten"), "AltShift:S", StraightenScannedPages, false, true)),
                Spare("dividespreads", new("\uE89F", Loc("Str_Pages_DivideSpreads"), "AltShift:D", DivideSpreads, false, true)),
                Spare("rasterize", new("\uE91B", Loc("Str_Pages_RasterizeRegion"), null, () => RasterizeRegion(page), false, true)),
                Spare("extractimage", new("\uEB9F", Loc("Str_ExtractImage_Menu"), null, () => ExtractImage(page), false, true)),
                Spare("exporttable", new("\uE80A", Loc("Str_TableExport_Title"), null, () => ExportTable(page), false, true)),
                Spare("paragraph", new("\uE8E4", Loc("Str_Paragraph_Title"), "AltShift:P", () => EditParagraph(page), false, true)),
                Spare("moveup", new("\uE74A", Loc("Str_Lbl_MoveUp"), null, () => MoveUp_Click(this, new RoutedEventArgs()), false, true)),
                Spare("movedown", new("\uE74B", Loc("Str_Lbl_MoveDown"), null, () => MoveDown_Click(this, new RoutedEventArgs()), false, true)),
                Spare("reverse", new("\uE8CB", Loc("Str_Pages_Reverse"), "AltShift:R", ReversePageOrder, false, true)),
                Spare("addblank", new("\uE7C3", Loc("Str_Ctx_AddBlankPage"), "AltShift:A", AddBlankPageAtEnd, false, true)),
                Spare("compare", FlyoutTile("\uE7C4", Loc("Str_TT_ComparePDFs"), "AltShift:V", BuildComparisonContextItem())),
            ];
            // One customizable grid (KillerUI CardEdit): Duplicate, Extract and Delete always show, every other tile
            // starts on the card in five full rows, and the spares start in the drawer.
            var strip = EditableTileGroups(menu, "Thumbnail", ["\uE8C8", "\uE8B1", "\uE7C3"], [], pageRow, turnRow, toolRow, groupRow, spares);
            menu.Items.Add(CardSlot(strip, new Thickness(0)));
            FocusFirstActionFromKeyboard(menu, strip);
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
            // Delete sits at the header's right, the same trash button the mark cards carry there (Steve 2026-11-19), and
            // Insert Blank Page After took its tile.
            var delete = NavRowButton(menu, "\uE74D", "Str_Ctx_DeletePages", "Del", () => Delete_Click(this, new RoutedEventArgs()), true);
            delete.VerticalAlignment = VerticalAlignment.Center;
            delete.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(delete, Dock.Right);
            header.Children.Add(delete);
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
        private static MenuItem CardSlot(object content, Thickness padding) => KillerFamily.Controls.CardBuilder.Slot(content, padding);

        // The card's strip of glyph-over-caption tiles (KillerUI CardBuilder.TileStrip). Each closes the card, then
        // acts; the tooltip carries the full name and its shortcut (the family shortcut rule).
        private FrameworkElement QuickStrip(ContextMenu menu, params (string Glyph, string Caption, string? Gesture, Action Act, bool Mirror, bool Enabled)[] buttons)
            => QuickStrip(menu, 0, buttons);

        // columns > 0 wraps the strip into rows of that many, which keeps a long strip from widening the menu.
        private FrameworkElement QuickStrip(ContextMenu menu, int columns, params (string Glyph, string Caption, string? Gesture, Action Act, bool Mirror, bool Enabled)[] buttons)
            => QuickStrip(menu, columns, [.. buttons.Select(b => new CardTile(b.Glyph, b.Caption, b.Gesture, b.Act, b.Mirror, b.Enabled))]);

        // One tile as KillerPDF writes it: Gesture is a shortcut cap name, turned into keys and hint text here.
        private sealed record CardTile(string Glyph, string Caption, string? Gesture, Action? Act, bool Mirror, bool Enabled)
        {
            public MenuItem? Flyout { get; init; }
            public bool? Checked { get; init; }
            public bool KeepsOpen { get; init; }
            /// <summary>A spare tile's stable name: it starts in the Hidden drawer, is never locked, and its glyph may
            /// repeat one already on the card.</summary>
            public string? Id { get; init; }
        }

        // A spare tile for a customizable card: an action that has its home elsewhere and starts in the Hidden drawer,
        // for anyone who wants it on the card.
        private static CardTile Spare(string id, CardTile tile) => tile with { Id = "spare." + id };

        // A tile that opens the rows of a submenu built for a menu row (Compare, Fillable Field Preset, Clean up...).
        private static CardTile FlyoutTile(string glyph, string caption, string? gesture, MenuItem rows)
            => new(glyph, caption, gesture, null, false, rows.IsEnabled) { Flyout = rows };

        private FrameworkElement QuickStrip(ContextMenu menu, int columns, params CardTile[] tiles)
            => KillerFamily.Controls.CardBuilder.TileStrip(menu, Loc("Str_Card_Actions"), columns, tiles.Select(KitTile));

        // The family card grid (KillerUI CardBuilder.TileGroups): three across, one group of related tiles per row.
        private FrameworkElement TileGroups(ContextMenu menu, params IEnumerable<CardTile>[] groups)
            => KillerFamily.Controls.CardBuilder.TileGroups(menu, Loc("Str_Card_Actions"), groups.Select(g => g.Select(KitTile)));

        // A big card the user can customize (KillerUI CardEdit, Steve 2026-11-19): a pencil in its corner hides and
        // shows tiles; the locked glyphs are the card's everyday actions and always show, and the hidden-by-default glyphs
        // start in the Hidden column (Reset returns there). Saved per card in settings.
        // Spare tiles (Spare) start there too and are never locked.
        private FrameworkElement EditableTileGroups(ContextMenu menu, string card, string[] locked, string[] hiddenByDefault, params IEnumerable<CardTile>[] groups)
            => KillerFamily.Controls.CardBuilder.TileGroups(menu, Loc("Str_Card_Actions"), groups.Select(g => g.Select(KitTile)), CardEditFor(card, locked, hiddenByDefault));

        private static KillerFamily.Controls.CardEdit CardEditFor(string card, string[] locked, string[] hiddenByDefault)
            => new(() => App.GetSetting($"Card.{card}.Hidden"), value => App.SetSetting($"Card.{card}.Hidden", value ?? ""))
            {
                Locked = t => t.Id is null && locked.Contains(t.Glyph),
                DefaultHidden = t => t.Id is not null || hiddenByDefault.Contains(t.Glyph),
                EditTip = Loc("Str_Card_Edit"),
                DoneTip = Loc("Str_Card_EditDone"),
                ResetTip = Loc("Str_Card_EditReset"),
                ResetLabel = Loc("Str_Card_EditResetShort"),
                HiddenLabel = Loc("Str_Card_Hidden"),
                HiddenHint = Loc("Str_Card_HiddenHint"),
                LockedTip = Loc("Str_Card_AlwaysShown"),
            };

        private KillerFamily.Controls.CardTile KitTile(CardTile t)
        {
            string caption = t.Caption.TrimEnd('.', '…').TrimEnd();
            return new KillerFamily.Controls.CardTile(t.Glyph, caption, t.Gesture is null ? null : GesturesFor(t.Gesture), t.Act, t.Mirror, t.Enabled)
            {
                // A name too long for two lines of the tile shows its short caption; tooltip and name keep it whole.
                Shown = ShortTileCaption(caption),
                Hint = t.Gesture is null ? caption : WithGesture(caption, t.Gesture),
                Flyout = t.Flyout?.Items.Cast<object>().ToList(),
                Checked = t.Checked,
                KeepsOpen = t.KeepsOpen,
                Id = t.Id,
            };
        }

        // The short caption (TileCaptions.ShortKeys) of a tile whose full name is this caption, or the caption itself.
        private string ShortTileCaption(string caption)
        {
            foreach (var (full, shortKey) in Services.TileCaptions.ShortKeys)
                if (string.Equals(Loc(full).TrimEnd('.', '…').TrimEnd(), caption, StringComparison.Ordinal)) return Loc(shortKey);
            return caption;
        }
    }
}
