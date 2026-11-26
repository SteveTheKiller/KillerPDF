using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerFamily.Controls;

namespace KillerPDF.Shell
{
    // The right-click card for markup on the page (KillerUI card menu). The page card's nav row comes first, then a
    // header that names what was clicked with Delete at its end. The first tile row holds the three actions that matter
    // most for that kind of mark, the next rows reorder, group and pair it, and the labeled rows below set what that
    // kind of mark has: color, a shape's fill, opacity, a line's width, a highlight's style, or a text box's ink and
    // size. The bars stay the place to set up the next mark: the card changes only the marks it was opened on and
    // never the bar's settings. A multi-selection gets the controls that apply to all of it.
    public partial class MainWindow
    {
        private void AddAnnotationCard(PageAnnotation hit)
        {
            if (TryFindResource("CardMenu") is Style card) _ctxMenu.Style = card;
            bool multi = SelectionCount() > 1;
            var targets = CardTargets(hit);

            // Every object card opens with the same nav row as the page card: Back, Forward, Undo, Redo, Print and the page.
            AddNavRow(_ctxMenu, hit.PageIndex);
            _ctxMenu.Items.Add(CardSlot(WithHeaderDelete(AnnotationCardHeader(hit, multi, targets.Count), true), new Thickness(4, 2, 4, 6)));

            _annotations.TryGetValue(hit.PageIndex, out var pageList);
            int idx = pageList?.IndexOf(hit) ?? -1;
            bool canRaise = pageList is not null && idx >= 0 && OverlapNeighbor(pageList, idx, hit, +1) >= 0;
            bool canLower = pageList is not null && idx >= 0 && OverlapNeighbor(pageList, idx, hit, -1) >= 0;
            // Family card grid (KillerUI CardBuilder.TileGroups): three across, one group per row, most used first.
            // Delete sits in the header; Paste keeps its spot, dimmed with nothing to paste. The first row holds the
            // three actions that matter most for the kind of mark that was clicked.
            var clipboard = MarkCardTopRow(hit, multi);
            var layers = new List<CardTile>
            {
                new("\uE74A", Loc("Str_Ctx_BringToFront"), null, () => MoveAnnotationToEdge(hit, front: true), false, canRaise),
                new("\uE70E", Loc("Str_Ctx_Raise"), null, () => MoveAnnotationLayer(hit, +1), false, canRaise),
                new("\uE70D", Loc("Str_Ctx_Lower"), null, () => MoveAnnotationLayer(hit, -1), false, canLower),
                new("\uE74B", Loc("Str_Ctx_SendToBack"), null, () => MoveAnnotationToEdge(hit, front: false), false, canLower),
            };
            // A text box's Paste moves off its first row (Edit, Duplicate, Copy) and leads the order row instead.
            if (!clipboard.Any(t => t.Glyph == "\uE77F")) layers.Insert(0, PasteTile(hit));
            var grouping = new List<CardTile>();
            if (hit.GroupId.Length > 0)
            {
                grouping.Add(new("\uF168", Loc("Str_Ctx_Ungroup"), null, () => UngroupAnnotation(hit), false, true));
                grouping.Add(new("\uE738", Loc("Str_Ctx_RemoveFromGroup"), null, () => RemoveFromGroup(hit), false, true));
            }
            else if (multi) grouping.Add(new("\uF168", Loc("Str_Ctx_Group"), null, GroupSelected, false, true));
            // Several marks get one Arrange tile whose rows line them up, space them and size them (#504).
            // A single text box gets it too, for the rows that fit its frame to its text.
            if (multi || hit is TextAnnotation) grouping.Add(FlyoutTile("\uE8E4", Loc("Str_Ctx_Arrange"), null, ArrangeRows(targets.Count)));
            var pairing = new List<CardTile>();
            if (hit.PairId.Length > 0 && !multi && PairPartner(hit) is { } partner)
                pairing.Add(new("\uE8B3", Loc(partner is CoverAnnotation ? "Str_Ctx_SelectCover" : "Str_Ctx_SelectText"), null, () => SelectPartner(hit), false, true));
            if (SelectedPaired() is not null) pairing.Add(new("\uE738", Loc("Str_Ctx_Unpair"), null, UnpairSelected, false, true));
            var strip = TileGroups(_ctxMenu, clipboard, layers, grouping, pairing);
            _ctxMenu.Items.Add(CardSlot(strip, new Thickness(0)));
            FocusFirstActionFromKeyboard(_ctxMenu, strip);

            // Labeled rows start on the icon column, as the kit's CardLabel slots do (KillerUI CardBuilder).
            var colored = targets.Where(HasCardColor).ToList();
            if (colored.Count > 0)
                _ctxMenu.Items.Add(CardSlot(CardColorRow(colored), new Thickness(8, 8, 4, 2)));
            // A closed shape's inside: any color, or none.
            var filled = targets.Where(IsClosedShape).ToList();
            if (filled.Count > 0)
                _ctxMenu.Items.Add(CardSlot(CardFillRow(filled), new Thickness(8, 2, 4, 2)));
            // A cover stays opaque, so it has no opacity to set.
            var translucent = colored.Where(a => a is not CoverAnnotation).ToList();
            if (translucent.Count > 0)
                _ctxMenu.Items.Add(CardSlot(CardSliderRow(Loc("Str_Bar_Opacity"), 10, 255, CardAlphaOf(translucent[0]),
                    value => $"{(int)Math.Round(value / 255.0 * 100)}%", (value, gesture) => ApplyCardStyle(translucent, a => SetCardOpacity(a, (byte)value), gesture)),
                    new Thickness(8, 2, 4, 2)));
            var lines = targets.Where(a => a is InkAnnotation or MeasurementAnnotation).ToList();
            if (lines.Count > 0)
                _ctxMenu.Items.Add(CardSlot(CardSliderRow(Loc("Str_Bar_Size"), 1, 60, CardWidthOf(lines[0]), value => $"{value:F0}px",
                    (value, gesture) => ApplyCardStyle(lines, a => SetCardWidth(a, Math.Round(value)), gesture)), new Thickness(8, 2, 4, 2)));
            var marks = targets.OfType<HighlightAnnotation>().Where(h => h is not CoverAnnotation && !h.IsShape).ToList();
            if (marks.Count > 0)
                _ctxMenu.Items.Add(CardSlot(CardStyleRow(marks), new Thickness(8, 4, 4, 6)));
            // Text boxes get their ink and size here; the text bar that opened with the selection keeps the font.
            var texts = targets.OfType<TextAnnotation>().Cast<PageAnnotation>().ToList();
            if (texts.Count > 0)
            {
                _ctxMenu.Items.Add(CardSlot(CardTextColorRow(texts), new Thickness(8, 8, 4, 2)));
                _ctxMenu.Items.Add(CardSlot(CardSliderRow(Loc("Str_Bar_Size"), 6, 96, TextPointSize((TextAnnotation)texts[0]), value => $"{value:F0} pt",
                    (value, gesture) => ApplyTextCardStyle(texts, a => SetTextPointSize((TextAnnotation)a, Math.Round(value)), gesture)), new Thickness(8, 2, 4, 6)));
            }
        }

        // The first row of a mark card: the three things done most with this kind of mark. Paste keeps its spot,
        // dimmed with nothing to paste; a text box or signature leads with Edit.
        private List<CardTile> MarkCardTopRow(PageAnnotation hit, bool multi)
        {
            CardTile copy = new("\uE8C8", Loc("Str_Ctx_Copy"), "Ctrl:C", CopySelectedAnnotations, false, true);
            CardTile duplicate = new("\uE710", Loc("Str_Ctx_Duplicate"), null, () => DuplicateSelectedAnnotations(hit.PageIndex), false, true);
            if (!multi && CanEditAnnotation(hit))
                return [new("\uE70F", Loc("Str_Ctx_Edit"), null, () => EditAnnotation(hit), false, true), duplicate, copy];
            if (multi) return [duplicate, copy, PasteTile(hit)];
            return hit switch
            {
                // A highlight or cover belongs to the text under it, so a second copy on top of it says nothing new.
                CoverAnnotation or HighlightAnnotation { IsShape: false } => [copy, PasteTile(hit)],
                MeasurementAnnotation m => [new("\uE8EF", Loc("Str_Ctx_CopyValue"), null, () => CopyMeasurementValue(m), false, true), copy, PasteTile(hit)],
                _ => [duplicate, copy, PasteTile(hit)],
            };
        }

        private CardTile PasteTile(PageAnnotation hit) =>
            new("\uE77F", Loc("Str_Ctx_Paste"), "Ctrl:V", () => PasteAnnotations(hit.PageIndex), false, MarkupToPaste());

        private void CopyMeasurementValue(MeasurementAnnotation measurement)
        {
            string value = ActiveViewer.MeasurementLabelExt(measurement);
            if (string.IsNullOrWhiteSpace(value)) return;
            try { Clipboard.SetText(value); } catch (System.Runtime.InteropServices.ExternalException) { return; }
            SetStatus(Loc("Str_St_Copied"));
        }

        // A mark card's Delete sits at the right end of its header, a nav-row icon button with the Del shortcut in its
        // tooltip, so the strip below keeps its room for the other actions (Steve 2026-11-19). One entry: no Delete tile.
        private FrameworkElement WithHeaderDelete(FrameworkElement header, bool enabled) =>
            WithHeaderAction(header, NavRowButton(_ctxMenu!, "\uE74D", "Str_Ctx_DeleteSel", "Del", DeleteSelected, enabled));

        // Any object card's header button: it docks right, ahead of the title, so the title trims before the button goes.
        private static FrameworkElement WithHeaderAction(FrameworkElement header, FrameworkElement button)
        {
            if (header is not DockPanel dock) return header;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            dock.Children.Insert(dock.Children.Count - 1, button);
            return dock;
        }

        // The Arrange tile's rows. Distributing needs three marks, so with two its rows are off.
        private MenuItem ArrangeRows(int count)
        {
            var rows = new MenuItem { Header = Loc("Str_Ctx_Arrange") };
            void Row(string key, Services.AnnotationArrange.Action action)
            {
                var item = MakeMenuItem(Loc(key), (_, _) => ArrangeSelection(action));
                item.IsEnabled = Services.AnnotationArrange.Applies(action, count);
                rows.Items.Add(item);
            }
            Row("Str_Arrange_AlignLeft", Services.AnnotationArrange.Action.AlignLeft);
            Row("Str_Arrange_AlignCenter", Services.AnnotationArrange.Action.AlignCenter);
            Row("Str_Arrange_AlignRight", Services.AnnotationArrange.Action.AlignRight);
            Row("Str_Arrange_AlignTop", Services.AnnotationArrange.Action.AlignTop);
            Row("Str_Arrange_AlignMiddle", Services.AnnotationArrange.Action.AlignMiddle);
            Row("Str_Arrange_AlignBottom", Services.AnnotationArrange.Action.AlignBottom);
            rows.Items.Add(new Separator());
            Row("Str_Arrange_DistributeHorizontally", Services.AnnotationArrange.Action.DistributeHorizontally);
            Row("Str_Arrange_DistributeVertically", Services.AnnotationArrange.Action.DistributeVertically);
            Row("Str_Arrange_MoreHorizontalSpace", Services.AnnotationArrange.Action.MoreHorizontalSpace);
            Row("Str_Arrange_LessHorizontalSpace", Services.AnnotationArrange.Action.LessHorizontalSpace);
            Row("Str_Arrange_MoreVerticalSpace", Services.AnnotationArrange.Action.MoreVerticalSpace);
            Row("Str_Arrange_LessVerticalSpace", Services.AnnotationArrange.Action.LessVerticalSpace);
            rows.Items.Add(new Separator());
            Row("Str_Arrange_SameWidth", Services.AnnotationArrange.Action.SameWidth);
            Row("Str_Arrange_SameHeight", Services.AnnotationArrange.Action.SameHeight);
            Row("Str_Arrange_SameSize", Services.AnnotationArrange.Action.SameSize);
            rows.Items.Add(new Separator());
            Row("Str_Arrange_FitWidth", Services.AnnotationArrange.Action.FitWidth);
            Row("Str_Arrange_FitHeight", Services.AnnotationArrange.Action.FitHeight);
            Row("Str_Arrange_FitSize", Services.AnnotationArrange.Action.FitSize);
            return rows;
        }

        // The right-clicked annotation and the rest of the selection, the same set the bar restyles.
        private List<PageAnnotation> CardTargets(PageAnnotation hit)
        {
            var targets = new List<PageAnnotation> { hit };
            if (_selectedAnnotation is not null && !targets.Contains(_selectedAnnotation)) targets.Add(_selectedAnnotation);
            foreach (var a in _selectedSet) if (!targets.Contains(a)) targets.Add(a);
            return targets;
        }

        private static bool HasCardColor(PageAnnotation a) => a is InkAnnotation or HighlightAnnotation or MeasurementAnnotation;

        // The Shapes tool commits a closed outline: its last point repeats its first.
        private static bool IsClosedShape(PageAnnotation a) =>
            a is InkAnnotation ink && (ink.HasFill || (ink.Points.Count > 2 && ink.Points[0] == ink.Points[^1]));

        private static byte CardAlphaOf(PageAnnotation a) => a switch
        {
            InkAnnotation ink => ink.ColorA,
            HighlightAnnotation h => h.ColorA,
            MeasurementAnnotation m => m.ColorA,
            _ => 255
        };

        private static double CardWidthOf(PageAnnotation a) => a switch
        {
            InkAnnotation ink => ink.StrokeWidth,
            MeasurementAnnotation m => m.StrokeWidth,
            _ => 1
        };

        private static void SetCardWidth(PageAnnotation a, double width)
        {
            if (a is InkAnnotation ink) ink.StrokeWidth = width;
            else if (a is MeasurementAnnotation m) m.StrokeWidth = width;
        }

        // A shape's fill: a swatch fills it at the fill's own opacity (or the outline's, when it had none), and the
        // outline swatch empties it.
        private FrameworkElement CardFillRow(List<PageAnnotation> shapes)
        {
            var row = new SwatchRow { PickerName = Loc("Str_Bar_MoreColors") };
            var fills = shapes.Cast<InkAnnotation>().Select(s => s.HasFill ? SwatchRow.Hex(Color.FromRgb(s.FillR, s.FillG, s.FillB)) : "").Distinct().ToList();
            row.Show(SwatchColors, fills.Count == 1 ? fills[0] : null);
            void Fill(Color? chosen) => ApplyCardStyle(shapes, a =>
            {
                var ink = (InkAnnotation)a;
                if (chosen is not { } c) { ink.FillA = 0; return; }
                byte alpha = ink.FillA > 0 ? ink.FillA : ink.ColorA;
                ink.SetFillColor(Color.FromArgb(alpha, c.R, c.G, c.B));
            });
            row.ColorChosen += hex => Fill(hex.Length == 0 ? null : (Color)ColorConverter.ConvertFromString(hex));
            row.PickRequested += () =>
            {
                _ctxMenu.IsOpen = false;
                var first = (InkAnnotation)shapes[0];
                OpenColorPicker(first.HasFill ? first.GetFillColor() : first.GetColor(), chosen => Fill(chosen));
            };
            return LabelledCardRow(Loc("Str_Bar_ShapeFill"), row);
        }

        private FrameworkElement CardTextColorRow(List<PageAnnotation> texts)
        {
            var row = new SwatchRow { PickerName = Loc("Str_Bar_MoreColors") };
            var colors = texts.Cast<TextAnnotation>().Select(t => Color.FromRgb(t.ColorR, t.ColorG, t.ColorB)).Distinct().ToList();
            row.Show(SwatchColors, colors.Count == 1 ? SwatchRow.Hex(colors[0]) : null);
            // Text always has a color, so the kit's no-color outline has nothing to do here.
            if (row.Children.Count > 0) row.Children.RemoveAt(0);
            void Ink(Color c) => ApplyTextCardStyle(texts, a =>
            {
                var text = (TextAnnotation)a;
                text.SetColor(Color.FromArgb(text.ColorA, c.R, c.G, c.B));
            });
            row.ColorChosen += hex => { if (hex.Length > 0) Ink((Color)ColorConverter.ConvertFromString(hex)); };
            row.PickRequested += () =>
            {
                _ctxMenu.IsOpen = false;
                OpenColorPicker(colors.Count > 0 ? colors[0] : Colors.Black, Ink);
            };
            return LabelledCardRow(Loc("Str_Bar_Color"), row);
        }

        // A text box's size in points, the unit the text bar shows, from its size in canvas units.
        private double TextPointSize(TextAnnotation text)
        {
            double sy = _renderDims.TryGetValue(text.PageIndex, out var rd) && rd.h > 0 ? PointsPerCanvasUnit(text.PageIndex, rd.h) : 1;
            return Math.Clamp(Math.Round(text.FontSize * (sy > 0 ? sy : 1)), 6, 96);
        }

        private void SetTextPointSize(TextAnnotation text, double points)
        {
            double sy = _renderDims.TryGetValue(text.PageIndex, out var rd) && rd.h > 0 ? PointsPerCanvasUnit(text.PageIndex, rd.h) : 1;
            if (sy > 0) text.FontSize = points / sy;
        }

        // A text card change, then the text bar the selection opened catches up so its next change keeps the new look.
        private void ApplyTextCardStyle(List<PageAnnotation> texts, Action<PageAnnotation> change, Controls.SliderGesture? gesture = null)
        {
            ApplyCardStyle(texts, change, gesture);
            // A size drag catches the bar up once it settles, on the same timer as the draw bar.
            if (gesture is null) ActiveViewer.RefreshSelectedTextBar();
        }

        private FrameworkElement AnnotationCardHeader(PageAnnotation hit, bool multi, int count)
        {
            Color? color = hit switch { InkAnnotation ink => ink.GetColor(), HighlightAnnotation h => h.GetColor(), TextAnnotation t => t.GetColor(), MeasurementAnnotation m => m.GetColor(), _ => null };
            string title = multi ? string.Format(Loc("Str_Card_ItemsSelected"), count) : AnnotationKindLabel(hit);
            string meta = string.Format(Loc("Str_PageLabel"), hit.PageIndex + 1);
            if (!multi && color is { } shade && hit is HighlightAnnotation or InkAnnotation or MeasurementAnnotation)
                meta += " · " + (int)Math.Round(shade.A / 255.0 * 100) + "%";
            return ObjectCardHeader(title, meta, color);
        }

        // An object card's header: an optional color dot, the title and a dim line under it.
        private static FrameworkElement ObjectCardHeader(string titleText, string meta, Color? color)
        {
            var dot = new Ellipse { Width = 10, Height = 10, StrokeThickness = 1, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shape.StrokeProperty, "MenuBorderBrush");
            if (color is { } c) dot.Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            else dot.Visibility = Visibility.Collapsed;

            var title = new TextBlock { Text = titleText, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var sub = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            var text = new StackPanel();
            text.Children.Add(title);
            text.Children.Add(sub);
            var header = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            header.Children.Add(dot);
            header.Children.Add(text);
            System.Windows.Automation.AutomationProperties.SetName(header, title.Text);
            return header;
        }

        private FrameworkElement CardColorRow(List<PageAnnotation> targets)
        {
            var row = new SwatchRow { PickerName = Loc("Str_Bar_MoreColors") };
            var colors = targets.Select(CardColorOf).Distinct().ToList();
            row.Show(SwatchColors, colors.Count == 1 ? SwatchRow.Hex(colors[0]) : null);
            // Markup always has a color, so the kit's no-color outline has nothing to do here.
            if (row.Children.Count > 0) row.Children.RemoveAt(0);
            row.ColorChosen += hex =>
            {
                if (hex.Length == 0) return;
                var chosen = (Color)ColorConverter.ConvertFromString(hex);
                ApplyCardStyle(targets, a => SetCardColor(a, chosen));
            };
            row.PickRequested += () =>
            {
                _ctxMenu.IsOpen = false;
                OpenColorPicker(colors.Count > 0 ? colors[0] : Colors.Yellow, chosen => ApplyCardStyle(targets, a => SetCardColor(a, chosen)));
            };
            return LabelledCardRow(Loc("Str_Bar_Color"), row);
        }

        private FrameworkElement CardSliderRow(string label, double min, double max, double value, Func<double, string> readout,
            Action<double, Controls.SliderGesture> apply)
        {
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 210, VerticalAlignment = VerticalAlignment.Center };
            if (TryFindResource("ThinSlider") is Style style) slider.Style = style;
            System.Windows.Automation.AutomationProperties.SetName(slider, label);
            var shown = new TextBlock { Text = readout(value), FontSize = 12, Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            shown.SetResourceReference(TextBlock.ForegroundProperty, "MenuTextBrush");
            var gesture = new Controls.SliderGesture(slider);
            slider.ValueChanged += (_, e) => { shown.Text = readout(e.NewValue); apply(e.NewValue, gesture); };
            var line = new DockPanel();
            DockPanel.SetDock(shown, Dock.Right);
            line.Children.Add(shown);
            line.Children.Add(slider);
            return LabelledCardRow(label, line);
        }

        // Highlight, Underline or Strikethrough: one mark becomes another in place, keeping its lines and color.
        private FrameworkElement CardStyleRow(List<HighlightAnnotation> marks)
        {
            var group = "CardStyle" + Guid.NewGuid().ToString("N");
            var segments = new StackPanel { Orientation = Orientation.Horizontal };
            HighlightStyle? current = marks.Select(m => m.Style).Distinct().Count() == 1 ? marks[0].Style : null;
            foreach (var (style, key) in new[] { (HighlightStyle.Fill, "Str_Lbl_Highlight"), (HighlightStyle.Underline, "Str_Lbl_Underline"), (HighlightStyle.Strikethrough, "Str_Lbl_Strike") })
            {
                var option = new RadioButton { Content = Loc(key), GroupName = group, IsChecked = current == style };
                if (TryFindResource("CardSegment") is Style segment) option.Style = segment;
                option.Checked += (_, _) => ApplyCardStyle([.. marks], a => ((HighlightAnnotation)a).Style = style);
                segments.Children.Add(option);
            }
            var track = new Border { Padding = new Thickness(2), Child = segments, HorizontalAlignment = HorizontalAlignment.Left };
            track.SetResourceReference(Border.BackgroundProperty, "MenuHoverBrush");
            track.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
            System.Windows.Automation.AutomationProperties.SetName(track, Loc("Str_Card_Style"));
            return LabelledCardRow(Loc("Str_Card_Style"), track);
        }

        private FrameworkElement LabelledCardRow(string label, UIElement content)
        {
            // The bar's labels end in a colon; a card's row labels do not (KillerUI CardLabel).
            var caption = new TextBlock { Text = label.TrimEnd(':', '：', ' ') };
            if (TryFindResource("CardLabel") is Style style) caption.Style = style;
            var row = new DockPanel();
            DockPanel.SetDock(caption, Dock.Left);
            row.Children.Add(caption);
            row.Children.Add(content);
            return row;
        }

        private static Color CardColorOf(PageAnnotation a) => a switch
        {
            InkAnnotation ink => Color.FromRgb(ink.ColorR, ink.ColorG, ink.ColorB),
            HighlightAnnotation h => Color.FromRgb(h.ColorR, h.ColorG, h.ColorB),
            MeasurementAnnotation m => Color.FromRgb(m.ColorR, m.ColorG, m.ColorB),
            _ => Colors.Transparent
        };

        // A cover's own SetColor keeps it opaque, so recoloring one never lets the old text show through.
        private static void SetCardColor(PageAnnotation a, Color c)
        {
            if (a is InkAnnotation ink) ink.SetColor(Color.FromArgb(ink.ColorA, c.R, c.G, c.B));
            else if (a is HighlightAnnotation h) h.SetColor(Color.FromArgb(h.ColorA, c.R, c.G, c.B));
            else if (a is MeasurementAnnotation m) m.SetColor(Color.FromArgb(m.ColorA, c.R, c.G, c.B));
        }

        private static void SetCardOpacity(PageAnnotation a, byte alpha)
        {
            if (a is InkAnnotation ink) ink.SetColor(Color.FromArgb(alpha, ink.ColorR, ink.ColorG, ink.ColorB));
            else if (a is HighlightAnnotation h) h.SetColor(Color.FromArgb(alpha, h.ColorR, h.ColorG, h.ColorB));
            else if (a is MeasurementAnnotation m) m.SetColor(Color.FromArgb(alpha, m.ColorR, m.ColorG, m.ColorB));
        }

        // Changes the card's marks directly, never the bar: one undo step per change (a slider drag is one gesture).
        private void ApplyCardStyle(List<PageAnnotation> targets, Action<PageAnnotation> change, Controls.SliderGesture? gesture = null)
        {
            if (targets.Count == 0) return;
            PushStyleUndo(targets, gesture);
            foreach (var target in targets) change(target);
            MarkDirty();
            int primary = _selectedAnnotation?.PageIndex ?? targets[0].PageIndex;
            foreach (int page in targets.Select(a => a.PageIndex).Distinct().Where(p => p != primary)) RenderAllAnnotations(page);
            RenderAllAnnotations(primary);
            ReattachSelectionVisuals();
            // The re-render wiped the canvases, so the rest of a multi-selection gets its outlines back (review BF-003),
            // and the bar the selection opened shows the new look so its next change does not write the old one back.
            ActiveViewer.ReattachMultiOutlinesExt();
            // A slider drag syncs the bar once it settles; rebuilding the docked bar on every tick is wasted work (r2 BF-006).
            if (gesture is null) ActiveViewer.RefreshSelectedDrawBar();
            else
            {
                // The pane the card changed, not whichever pane has focus when the timer fires (r3 BF-003).
                _cardBarRefreshPane = ActiveViewer;
                _cardBarRefresh ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200),
                    System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
                    {
                        _cardBarRefresh!.Stop();
                        _cardBarRefreshPane?.RefreshSelectedDrawBar();
                        _cardBarRefreshPane?.RefreshSelectedTextBar();
                    }, Dispatcher);
                _cardBarRefresh.Stop();
                _cardBarRefresh.Start();
            }
        }

        private System.Windows.Threading.DispatcherTimer? _cardBarRefresh;
        private Controls.PdfViewer? _cardBarRefreshPane;
    }
}
