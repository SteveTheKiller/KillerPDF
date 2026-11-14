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
    // and markup count, the six things done most as quick buttons, then a short list in which the scanner cleanup,
    // the extract-from-page tools and the moves sit in submenus. Every action and shortcut is the one the old 21-row
    // list had. With several thumbnails selected the header says so, and the actions take the selection as before.
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
            menu.Items.Add(CardSlot(RotateRow(menu), new Thickness(6, 6, 6, 2)));

            menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_Merge"), async (_, _) => await MergeAtIndex(page + 1), gesture: "", glyph: "\uE710"));
            menu.Items.Add(MakeTransformMenuItem(Loc("Str_Tf_Suffix"), (_, _) => OpenTransformWindow(), GestureFor("R")));
            menu.Items.Add(MakeMenuItem(Loc("Str_ColorCorrection"), (_, _) =>
                OpenTransformWindow(TransformWindowMode.ColorCorrection), gesture: GestureFor("E"), glyph: "\uE790"));

            // #436: with two or more pages selected these act on the selection, otherwise on the whole document.
            var cleanUp = MakeMenuItem(Loc("Str_Card_CleanUp"), (_, _) => { }, glyph: "\uE771");
            cleanUp.Items.Add(MakeMenuItem(Loc("Str_Pages_FixOrientation"), (_, _) => FixPageOrientation(), GestureFor("AltShift:O"), "\uE7AD"));
            cleanUp.Items.Add(MakeMenuItem(Loc("Str_Pages_Straighten"), (_, _) => StraightenScannedPages(), glyph: "\uE809"));
            cleanUp.Items.Add(MakeMenuItem(Loc("Str_Pages_DivideSpreads"), (_, _) => DivideSpreads(), glyph: "\uE89F"));
            menu.Items.Add(cleanUp);

            var fromPage = MakeMenuItem(Loc("Str_Card_FromPage"), (_, _) => { }, glyph: "\uE8B9");
            fromPage.Items.Add(MakeMenuItem(Loc("Str_Pages_RasterizeRegion"), (_, _) => RasterizeRegion(page), glyph: "\uE91B"));
            fromPage.Items.Add(MakeMenuItem(Loc("Str_ExtractImage_Menu"), (_, _) => ExtractImage(page), glyph: "\uEB9F"));
            fromPage.Items.Add(MakeMenuItem(Loc("Str_TableExport_Title"), (_, _) => ExportTable(page), glyph: "\uE80A"));
            fromPage.Items.Add(MakeMenuItem(Loc("Str_Paragraph_Title"), (_, _) => EditParagraph(page), GestureFor("AltShift:P"), "\uE8E4"));
            menu.Items.Add(fromPage);

            var move = MakeMenuItem(Loc("Str_Card_Move"), (_, _) => { }, glyph: "\uE7C2");
            move.Items.Add(MakeMenuItem(Loc("Str_Lbl_MoveUp"), (s, e) => MoveUp_Click(s!, e), glyph: "\uE74A"));
            move.Items.Add(MakeMenuItem(Loc("Str_Lbl_MoveDown"), (s, e) => MoveDown_Click(s!, e), glyph: "\uE74B"));
            move.Items.Add(MakeMenuItem(Loc("Str_Pages_Reverse"), (_, _) => ReversePageOrder(), glyph: "\uE8CB"));
            menu.Items.Add(move);

            // The thumbnails' own Ctrl+Shift+O: every selected page, not only the clicked one.
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_OcrPage"), (_, _) => OcrSelectedPagesToClipboard(), GestureFor("CtrlShift:O"), "\uEE6F"));
            // #207: the shared export dialog, scoped to the clicked selection (still editable).
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_ExportPageImage"), (_, _) =>
            {
                var nums = PageList.SelectedItems.Cast<PageThumbnailVm>().Select(vm => vm.PageIndex + 1).OrderBy(n => n);
                _ = ExportImagesFlow(string.Join(",", nums));
            }, glyph: "\uEB9F"));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_StampPages"), (_, _) => OpenStampTool(), GestureFor("S"), "\uE932"));
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
            DockPanel.SetDock(insert, Dock.Right);
            header.Children.Add(insert);
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
            if (strip is not Border { Child: UniformGrid grid } || grid.Children.OfType<Button>().FirstOrDefault(b => b.IsEnabled) is not { } first) return;
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

        // Rotation sits under the four quick actions as two half-width buttons: six tiles made the strip too wide.
        private FrameworkElement RotateRow(ContextMenu menu)
        {
            var row = new UniformGrid { Rows = 1, Columns = 2 };
            row.Children.Add(CompactCardButton(menu, "\uE7AD", Loc("Str_Ctx_RotateCCWShort"), "CtrlShift:R", () => RotatePages_Click(-90), mirror: true));
            row.Children.Add(CompactCardButton(menu, "\uE7AD", Loc("Str_Ctx_RotateCWShort"), "Ctrl:R", () => RotatePages_Click(90), mirror: false));
            return row;
        }

        // A small outline button with its glyph beside the caption. It closes the card, then acts.
        private Button CompactCardButton(ContextMenu menu, string glyph, string caption, string gesture, Action act, bool mirror)
        {
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
            };
            if (mirror)
            {
                icon.RenderTransformOrigin = new Point(0.5, 0.5);
                icon.RenderTransform = new ScaleTransform(-1, 1);
            }
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Content = content, ToolTip = WithGesture(caption, gesture), Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(8, 4, 8, 4), HorizontalContentAlignment = HorizontalAlignment.Center
            };
            if (TryFindResource("OutlineButton") is Style style) button.Style = style;
            System.Windows.Automation.AutomationProperties.SetName(button, caption);
            button.Click += (_, _) => { menu.IsOpen = false; act(); };
            return button;
        }

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
        {
            var grid = columns > 0 && buttons.Length > columns ? new UniformGrid { Columns = columns } : new UniformGrid { Rows = 1 };
            foreach (var (glyph, caption, gesture, act, mirror, enabled) in buttons)
            {
                var button = new Button { Content = glyph, Tag = caption, ToolTip = gesture is null ? caption : WithGesture(caption, gesture), IsEnabled = enabled };
                ToolTipService.SetShowOnDisabled(button, true);
                if (TryFindResource("CardQuickButton") is Style style) button.Style = style;
                if (mirror)
                    button.Loaded += (_, _) =>
                    {
                        if (button.Template?.FindName("Glyph", button) is TextBlock g)
                        {
                            g.RenderTransformOrigin = new Point(0.5, 0.5);
                            g.RenderTransform = new ScaleTransform(-1, 1);
                        }
                    };
                button.Click += (_, _) => { menu.IsOpen = false; act(); };
                grid.Children.Add(button);
            }
            // Inset 8 like the kit's separators, so the strip's rules start on the icon column (KillerUI CardBuilder).
            var strip = new Border { Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(0, 4, 0, 4), Child = grid };
            strip.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            System.Windows.Automation.AutomationProperties.SetName(strip, Loc("Str_Card_Actions"));
            return strip;
        }
    }
}
