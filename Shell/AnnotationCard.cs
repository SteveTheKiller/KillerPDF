using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerFamily.Controls;

namespace KillerPDF.Shell
{
    // The right-click card for markup on the page (KillerUI card menu). A header names what was clicked; quick buttons
    // edit, copy, raise, lower, group and delete it; a highlight, Shapes rectangle or drawing gets its color, opacity
    // (and a drawing its width) right on the card, and a highlight can turn into an underline or a strikethrough in
    // place. The Highlight bar stays the place to set up the next mark: the card changes only the marks it was opened
    // on and never the bar's settings. A multi-selection gets the controls that apply to all of it.
    public partial class MainWindow
    {
        private void AddAnnotationCard(PageAnnotation hit)
        {
            if (TryFindResource("CardMenu") is Style card) _ctxMenu.Style = card;
            bool multi = SelectionCount() > 1;
            var targets = CardTargets(hit);

            _ctxMenu.Items.Add(CardSlot(AnnotationCardHeader(hit, multi, targets.Count), new Thickness(4, 2, 4, 6)));

            _annotations.TryGetValue(hit.PageIndex, out var pageList);
            int idx = pageList?.IndexOf(hit) ?? -1;
            bool canRaise = pageList is not null && idx >= 0 && OverlapNeighbor(pageList, idx, hit, +1) >= 0;
            bool canLower = pageList is not null && idx >= 0 && OverlapNeighbor(pageList, idx, hit, -1) >= 0;
            var quick = new List<(string, string, string?, Action, bool, bool)>();
            if (!multi) quick.Add(("\uE70F", Loc("Str_Ctx_Edit"), null, () => EditAnnotation(hit), false, true));
            quick.Add(("\uE8C8", Loc("Str_Ctx_Copy"), "Ctrl:C", CopySelectedAnnotations, false, true));
            quick.Add(("\uE70E", Loc("Str_Ctx_Raise"), null, () => MoveAnnotationLayer(hit, +1), false, canRaise));
            quick.Add(("\uE70D", Loc("Str_Ctx_Lower"), null, () => MoveAnnotationLayer(hit, -1), false, canLower));
            if (hit.GroupId.Length > 0) quick.Add(("\uF168", Loc("Str_Ctx_Ungroup"), null, () => UngroupAnnotation(hit), false, true));
            else if (multi) quick.Add(("\uF168", Loc("Str_Ctx_Group"), null, GroupSelected, false, true));
            quick.Add(("\uE74D", Loc("Str_Ctx_DeleteSel"), "Del", DeleteSelected, false, true));
            var strip = QuickStrip(_ctxMenu, [.. quick]);
            _ctxMenu.Items.Add(CardSlot(strip, new Thickness(0)));
            FocusFirstActionFromKeyboard(_ctxMenu, strip);

            var colored = targets.Where(HasCardColor).ToList();
            if (colored.Count > 0)
            {
                // Labeled rows start on the icon column, as the kit's CardLabel slots do (KillerUI CardBuilder).
                _ctxMenu.Items.Add(CardSlot(CardColorRow(colored), new Thickness(8, 8, 4, 2)));
                _ctxMenu.Items.Add(CardSlot(CardSliderRow(Loc("Str_Bar_Opacity"), 10, 255, colored[0] is InkAnnotation ink ? ink.ColorA : ((HighlightAnnotation)colored[0]).ColorA,
                    value => $"{(int)Math.Round(value / 255.0 * 100)}%", (value, gesture) => ApplyCardStyle(colored, a => SetCardOpacity(a, (byte)value), gesture)),
                    new Thickness(8, 2, 4, 2)));
            }
            var inks = targets.Where(a => a is InkAnnotation).ToList();
            if (inks.Count > 0)
                _ctxMenu.Items.Add(CardSlot(CardSliderRow(Loc("Str_Bar_Size"), 1, 60, ((InkAnnotation)inks[0]).StrokeWidth, value => $"{value:F0}px",
                    (value, gesture) => ApplyCardStyle(inks, a => ((InkAnnotation)a).StrokeWidth = Math.Round(value), gesture)), new Thickness(8, 2, 4, 2)));
            var marks = targets.OfType<HighlightAnnotation>().Where(h => h is not CoverAnnotation && !h.IsShape).ToList();
            if (marks.Count > 0)
                _ctxMenu.Items.Add(CardSlot(CardStyleRow(marks), new Thickness(8, 4, 4, 6)));

            _ctxMenu.Items.Add(new Separator());
            if (MarkupToPaste())
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Paste"), (_, _) => PasteAnnotations(hit.PageIndex), GestureFor("Ctrl:V"), "\uE77F"));
            if (hit.PairId.Length > 0 && !multi && PairPartner(hit) is { } partner)
                _ctxMenu.Items.Add(MakeMenuItem(Loc(partner is CoverAnnotation ? "Str_Ctx_SelectCover" : "Str_Ctx_SelectText"),
                    (_, _) => SelectPartner(hit), glyph: "\uE8B3"));
            if (SelectedPaired() is not null)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Unpair"), (_, _) => UnpairSelected(), glyph: "\uE738"));
            if (hit.GroupId.Length > 0)
                _ctxMenu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RemoveFromGroup"), (_, _) => RemoveFromGroup(hit), glyph: "\uE738"));
            // A list with nothing in it after the rule reads as broken; the rule goes when it would end the card.
            if (_ctxMenu.Items[^1] is Separator) _ctxMenu.Items.RemoveAt(_ctxMenu.Items.Count - 1);
        }

        // The right-clicked annotation and the rest of the selection, the same set the bar restyles.
        private List<PageAnnotation> CardTargets(PageAnnotation hit)
        {
            var targets = new List<PageAnnotation> { hit };
            if (_selectedAnnotation is not null && !targets.Contains(_selectedAnnotation)) targets.Add(_selectedAnnotation);
            foreach (var a in _selectedSet) if (!targets.Contains(a)) targets.Add(a);
            return targets;
        }

        private static bool HasCardColor(PageAnnotation a) => a is InkAnnotation || a is HighlightAnnotation and not CoverAnnotation;

        private FrameworkElement AnnotationCardHeader(PageAnnotation hit, bool multi, int count)
        {
            var dot = new Ellipse { Width = 10, Height = 10, StrokeThickness = 1, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shape.StrokeProperty, "MenuBorderBrush");
            Color? color = hit switch { InkAnnotation ink => ink.GetColor(), HighlightAnnotation h => h.GetColor(), TextAnnotation t => t.GetColor(), _ => null };
            if (color is { } c) dot.Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            else dot.Visibility = Visibility.Collapsed;

            var title = new TextBlock
            {
                Text = multi ? string.Format(Loc("Str_Card_ItemsSelected"), count) : AnnotationKindLabel(hit),
                FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            string meta = string.Format(Loc("Str_PageLabel"), hit.PageIndex + 1);
            if (!multi && color is { } shade && hit is HighlightAnnotation or InkAnnotation)
                meta += " · " + (int)Math.Round(shade.A / 255.0 * 100) + "%";
            var sub = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
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
            _ => Colors.Transparent
        };

        private static void SetCardColor(PageAnnotation a, Color c)
        {
            if (a is InkAnnotation ink) ink.SetColor(Color.FromArgb(ink.ColorA, c.R, c.G, c.B));
            else if (a is HighlightAnnotation h) h.SetColor(Color.FromArgb(h.ColorA, c.R, c.G, c.B));
        }

        private static void SetCardOpacity(PageAnnotation a, byte alpha)
        {
            if (a is InkAnnotation ink) ink.SetColor(Color.FromArgb(alpha, ink.ColorR, ink.ColorG, ink.ColorB));
            else if (a is HighlightAnnotation h) h.SetColor(Color.FromArgb(alpha, h.ColorR, h.ColorG, h.ColorB));
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
                    System.Windows.Threading.DispatcherPriority.Background, (_, _) => { _cardBarRefresh!.Stop(); _cardBarRefreshPane?.RefreshSelectedDrawBar(); }, Dispatcher);
                _cardBarRefresh.Stop();
                _cardBarRefresh.Start();
            }
        }

        private System.Windows.Threading.DispatcherTimer? _cardBarRefresh;
        private Controls.PdfViewer? _cardBarRefreshPane;
    }
}
