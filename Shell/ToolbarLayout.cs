using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // ============================================================
    // Toolbar layout: which buttons sit on the toolbar, in what order, on which side, and which
    // live in the More menu (the overflow chevron) instead.
    //
    // Every toolbar button is one unit here, with an id that is saved, the element on the bar (a
    // split button's main half and chevron together), and its row in the More menu. The layout is
    // a list of ids for each side, with "-" for a separator, plus the ids the user moved to the
    // More menu. ReflowToolbar still sheds buttons into the More menu when the window is too
    // narrow; a button the user put there stays there at any width.
    // ============================================================
    public partial class MainWindow
    {
        internal sealed record ToolbarUnit(string Id, FrameworkElement Bar, Button Main, Button Overflow,
            string Glyph, string LabelKey, string? Cap);

        /// <summary>A saved toolbar arrangement. Separator tokens are <see cref="ToolbarSeparatorToken"/>.</summary>
        internal sealed record ToolbarLayout(IReadOnlyList<string> Left, IReadOnlyList<string> Right,
            IReadOnlyList<string> More);

        internal const string ToolbarSeparatorToken = "-";
        private const string ToolbarLayoutSetting = "ToolbarLayout";

        internal static readonly ToolbarLayout DefaultToolbarLayout = new(
            ["new", "close", "open", "save", "ocr", "flatten", "print", "undo", "redo", "compare", "-", "merge", "extract",
             "-", "delete", "moveup", "movedown"],
            ["pan", "select", "text", "highlight", "line", "shape", "draw", "image", "signature", "crop", "transform",
             "colorcorrection", "measure", "redact", "stamp", "-", "formfield", "-", "clear",
             "-", "search"],
            []);

        // Buttons leave the bar for the More menu in this order as the window narrows, a group at a
        // time: the most occasional first, the text-markup tools last. File basics lose their
        // captions before the final fallback; Select stays visible.
        private static readonly string[][] ToolbarShedOrder =
        [
            ["compare"],
            ["delete", "moveup", "movedown"],
            ["merge", "extract"],
            ["stamp"],
            ["signature"],
            ["image"],
            ["crop"],
            ["colorcorrection"],
            ["measure"],
            ["redact"],
            ["undo", "redo", "clear", "search"],
            ["shape"],
            ["draw"],
            ["line"],
            ["highlight"],
            ["formfield"],
            ["text"],
        ];

        private static readonly string[] ToolbarLastResortShedOrder = ["flatten", "print", "close", "new", "transform", "pan"];

        private readonly List<ToolbarUnit> _toolbarUnits = [];
        private ToolbarLayout _toolbarLayout = DefaultToolbarLayout;
        private bool _toolbarAvailabilityQueued;

        internal IReadOnlyList<ToolbarUnit> ToolbarUnits => _toolbarUnits;
        internal ToolbarLayout CurrentToolbarLayout => _toolbarLayout;

        /// <summary>Registers every toolbar button once, creating the More menu rows the XAML
        /// does not declare, then arranges both bars from the saved layout.</summary>
        private void InitToolbarLayout()
        {
            _toolbarUnits.Clear();
            void Add(string id, FrameworkElement bar, Button main, Button? overflow, string glyph, string label,
                string? cap, RoutedEventHandler click)
            {
                overflow ??= MakeOverflowRow(glyph, label, click);
                overflow.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = main });
                _toolbarUnits.Add(new ToolbarUnit(id, bar, main, overflow, glyph, label, cap));
            }
            Add("new", NewBlankBtn, NewBlankBtn, null, "\uE7C3", "Str_Lbl_New", "Ctrl:N", New_Click);
            Add("close", CloseFileBtn, CloseFileBtn, null, "\uE711", "Str_Lbl_Close", "Ctrl:W", CloseFile_Click);
            Add("open", GrpOpen, OpenFileBtn, null, "\uE8DA", "Str_Lbl_Open", "Ctrl:O", Open_Click);
            Add("save", GrpSave, SaveAsBtn, null, "\uE74E", "Str_Lbl_Save", "Ctrl:S", Save_Click);
            Add("ocr", GrpOcr, OcrBtn, null, "\uEE6F", "Str_Lbl_Ocr", "CtrlShift:O", Ocr_Click);
            Add("flatten", FlattenBtn, FlattenBtn, null, "\uE8B9", "Str_Lbl_Flatten", "AltShift:F", SaveFlattened_Click);
            Add("print", PrintBtn, PrintBtn, null, "\uE749", "Str_Lbl_Print", "Ctrl:P", Print_Click);
            Add("compare", ComparePdfBtn, ComparePdfBtn, MiCompare, CompareGlyph, "Str_TT_ComparePDFs", "AltShift:V", ComparePdf_Click);
            Add("merge", MergeBtn, MergeBtn, MiMerge, "\uE710", "Str_Lbl_Merge", "AltShift:B", Merge_Click);
            Add("extract", ExtractPagesBtn, ExtractPagesBtn, MiExtract, "\uE8B1", "Str_Lbl_Extract", "AltShift:X", Split_Click);
            Add("delete", DeletePagesBtn, DeletePagesBtn, MiDelete, "\uE8C6", "Str_Lbl_Delete", null, Delete_Click);
            Add("moveup", MovePageUpBtn, MovePageUpBtn, MiMoveUp, "\uE74A", "Str_Lbl_MoveUp", null, MoveUp_Click);
            Add("movedown", MovePageDownBtn, MovePageDownBtn, MiMoveDown, "\uE74B", "Str_Lbl_MoveDown", null, MoveDown_Click);
            Add("pan", ToolPanBtn, ToolPanBtn, null, PanGlyph, "Str_Lbl_Pan", "P", ToolPan_Click);
            Add("select", ToolSelectBtn, ToolSelectBtn, null, "\uE8B0", "Str_Lbl_Select", "V", ToolSelect_Click);
            Add("text", ToolTextBtn, ToolTextBtn, MiText, "\uE8D2", "Str_Lbl_Text", "T", ToolText_Click);
            Add("highlight", ToolHighlightBtn, ToolHighlightBtn, MiHighlight, "\uE7E6", "Str_Lbl_Highlight", "H", ToolHighlight_Click);
            Add("line", ToolUnderlineBtn, ToolUnderlineBtn, MiUnderline, "\uE738", "Str_Lbl_Line", "L", ToolLine_Click);
            Add("shape", ToolShapeBtn, ToolShapeBtn, MiShape, "\uE739", "Str_Lbl_Shape", "O", ToolShape_Click);
            Add("draw", ToolDrawBtn, ToolDrawBtn, MiDraw, "\uED63", "Str_Lbl_Draw", "D", ToolDraw_Click);
            Add("image", ToolImageBtn, ToolImageBtn, MiImage, "\uEB9F", "Str_Lbl_Image", "I", ToolImage_Click);
            Add("signature", ToolSignatureBtn, ToolSignatureBtn, MiSignature, "\uEE56", "Str_Lbl_Signature", "G", ToolSignature_Click);
            Add("crop", ToolCropBtn, ToolCropBtn, MiCrop, "\uE7A8", "Str_Lbl_Crop", "C", ToolCrop_Click);
            Add("transform", ToolRotateBtn, ToolRotateBtn, null, TransformGlyph, "Str_Lbl_Rotate", "R", ToolRotate_Click);
            Add("colorcorrection", ToolColorCorrectionBtn, ToolColorCorrectionBtn, MiColorCorrection, "\uE790", "Str_ColorCorrection", "E", ToolColorCorrection_Click);
            Add("measure", ToolMeasureBtn, ToolMeasureBtn, MiMeasure, "\uED5E", "Str_Lbl_Measure", "M", ToolMeasure_Click);
            Add("redact", ToolRedactBtn, ToolRedactBtn, MiRedact, "\uED1A", "Str_Lbl_Redact", "X", ToolRedact_Click);
            Add("stamp", ToolStampBtn, ToolStampBtn, MiStamp, "\uE932", "Str_Lbl_Stamp", "S", ToolStamp_Click);
            Add("formfield", ToolFormFieldBtn, ToolFormFieldBtn, MiFormField, FormFieldGlyph, "Str_Lbl_FormField", "F", ToolFormField_Click);
            Add("undo", UndoBtn, UndoBtn, MiUndo, "\uE7A7", "Str_Lbl_Undo", "Ctrl:Z", Undo_Click);
            Add("redo", RedoBtn, RedoBtn, MiRedo, "\uE7A6", "Str_Ctx_Redo", "Ctrl:Y", Redo_Click);
            Add("clear", ClearAllBtn, ClearAllBtn, MiClear, "\uED62", "Str_Lbl_Clear", null, ClearAllAnnotations_Click);
            Add("search", SearchBtn, SearchBtn, null, "\uE721", "Str_Lbl_Search", "Ctrl:F", Search_Click);

            _toolbarLayout = NormalizeToolbarLayout(ParseToolbarLayout(App.GetSetting(ToolbarLayoutSetting)));
            ApplyToolbarLayout();
            PageList.SelectionChanged += (_, _) => ScheduleToolbarAvailabilityRefresh();
            foreach (var viewer in new[] { Viewer, ViewerB })
            {
                viewer.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, e) =>
                {
                    if (ReferenceEquals(e.OriginalSource, viewer.ActiveTextBoxRef))
                        ScheduleToolbarAvailabilityRefresh();
                }), handledEventsToo: true);
                viewer.AddHandler(Keyboard.LostKeyboardFocusEvent,
                    new KeyboardFocusChangedEventHandler((_, _) => ScheduleToolbarAvailabilityRefresh()),
                    handledEventsToo: true);
            }
            RefreshToolbarAvailability();
        }

        private void ScheduleToolbarAvailabilityRefresh()
        {
            if (_toolbarAvailabilityQueued || Dispatcher.HasShutdownStarted) return;
            _toolbarAvailabilityQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _toolbarAvailabilityQueued = false;
                RefreshToolbarAvailability();
            }));
        }

        private void RefreshToolbarAvailability()
        {
            bool hasDoc = _doc is not null;
            bool hasReadingDocument = ActiveViewer.HasReadDocumentExt;
            foreach (UIElement control in new UIElement[] { PageSizeLabel, ViewContinuousRadio, ViewSingleRadio,
                         ViewTwoPageRadio, ViewTwoPageContinuousRadio, ViewGridRadio, FooterDisplayControls, ZoomOutBtn, ZoomBox, ZoomInBtn })
            {
                control.IsEnabled = hasReadingDocument;
                control.Opacity = hasReadingDocument ? 1 : .4;
            }
            var edit = EditAvailability();
            var state = new ToolbarAvailability.State(hasDoc, edit.Undo, edit.Redo, edit.Clear,
                PageList.SelectedItems.Count, PageList.SelectedIndex, _doc?.PageCount ?? 0, hasReadingDocument);
            foreach (ToolbarUnit unit in _toolbarUnits)
            {
                unit.Bar.IsEnabled = ToolbarAvailability.GroupEnabled(unit.Id, state);
                unit.Main.IsEnabled = ToolbarAvailability.MainEnabled(unit.Id, state);
            }
        }

        /// <summary>What Undo, Redo and Clear can do right now, for the toolbar and the page menus. Read from the
        /// stacks, not from a button's IsEnabled, which the queued toolbar refresh can leave one step behind.</summary>
        private (bool Undo, bool Redo, bool Clear) EditAvailability()
        {
            bool pendingText = ActiveViewer.HasPendingTextEditExt;
            return (_undoStack.Count > 0 || pendingText,
                _redoStack.Count > 0 && !pendingText,
                _doc is not null && (_annotations.Values.Any(items => items.Count > 0) || ActiveViewer.HasPendingAnnotationExt));
        }

        /// <summary>A More menu row for a button the XAML gives no row of its own: its icon and
        /// caption, enabled whenever the toolbar button is.</summary>
        private Button MakeOverflowRow(string glyph, string labelKey, RoutedEventHandler click)
        {
            var icon = MakeToolbarGlyph(glyph, 14);
            var iconHost = new Grid { Width = 24 };
            iconHost.Children.Add(icon);
            var caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            caption.SetResourceReference(TextBlock.TextProperty, labelKey);
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(iconHost);
            row.Children.Add(caption);
            var button = new Button
            {
                Style = (Style)FindResource("OverflowMenuButton"),
                Visibility = Visibility.Collapsed,
                Content = row
            };
            button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            button.Click += click;
            OverflowMenuPanel.Children.Add(button);
            return button;
        }

        /// <summary>Reads the saved layout: "left;right;more", each a comma list of ids.</summary>
        internal static ToolbarLayout ParseToolbarLayout(string? saved)
        {
            if (string.IsNullOrWhiteSpace(saved)) return DefaultToolbarLayout;
            string[] parts = saved.Split(';');
            if (parts.Length != 3) return DefaultToolbarLayout;
            static List<string> Ids(string part) =>
                [.. part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            return new ToolbarLayout(Ids(parts[0]), Ids(parts[1]), Ids(parts[2]));
        }

        internal static string FormatToolbarLayout(ToolbarLayout layout) =>
            string.Join(",", layout.Left) + ";" + string.Join(",", layout.Right) + ";" + string.Join(",", layout.More);

        /// <summary>
        /// Drops unknown and repeated ids and puts back any button the saved layout does not
        /// mention (one a later version added) where the default layout has it. Separators never
        /// start or end a side or sit side by side.
        /// </summary>
        internal static ToolbarLayout NormalizeToolbarLayout(ToolbarLayout layout)
        {
            var known = DefaultToolbarLayout.Left.Concat(DefaultToolbarLayout.Right)
                .Where(id => id != ToolbarSeparatorToken).ToHashSet();
            var seen = new HashSet<string>();
            List<string> Clean(IEnumerable<string> ids, bool separators)
            {
                var result = new List<string>();
                foreach (string id in ids)
                {
                    if (id == ToolbarSeparatorToken)
                    {
                        if (separators && result.Count > 0 && result[^1] != ToolbarSeparatorToken) result.Add(id);
                    }
                    else if (known.Contains(id) && seen.Add(id)) result.Add(id);
                }
                while (result.Count > 0 && result[^1] == ToolbarSeparatorToken) result.RemoveAt(result.Count - 1);
                return result;
            }
            List<string> left = Clean(layout.Left, true), right = Clean(layout.Right, true), more = Clean(layout.More, false);
            foreach (string id in DefaultToolbarLayout.Left.Where(id => id != ToolbarSeparatorToken && !seen.Contains(id)))
                left.Add(id);
            foreach (string id in DefaultToolbarLayout.Right.Where(id => id != ToolbarSeparatorToken && !seen.Contains(id)))
                right.Add(id);
            return new ToolbarLayout(left, right, more);
        }

        /// <summary>Saves and applies a layout from Customize toolbar.</summary>
        internal void SetToolbarLayout(ToolbarLayout layout)
        {
            _toolbarLayout = NormalizeToolbarLayout(layout);
            string formatted = FormatToolbarLayout(_toolbarLayout);
            if (formatted == FormatToolbarLayout(DefaultToolbarLayout)) App.RemoveSetting(ToolbarLayoutSetting);
            else App.SetSetting(ToolbarLayoutSetting, formatted);
            ApplyToolbarLayout();
            InvalidateToolbarReflow();
            ReflowToolbar();
        }

        /// <summary>Puts every unit on its bar in layout order, with separators between groups,
        /// and orders the More menu: the user's choices first, then the rest in toolbar order.</summary>
        private void ApplyToolbarLayout()
        {
            var byId = _toolbarUnits.ToDictionary(unit => unit.Id);
            foreach (ToolbarUnit unit in _toolbarUnits)
                (unit.Bar.Parent as Panel)?.Children.Remove(unit.Bar);
            LeftBar.Children.Clear();
            RightBar.Children.Clear();

            void Fill(Panel bar, IEnumerable<string> ids)
            {
                foreach (string id in ids)
                {
                    if (id == ToolbarSeparatorToken) bar.Children.Add(MakeToolbarSeparator());
                    else if (byId.TryGetValue(id, out ToolbarUnit? unit) && !_toolbarLayout.More.Contains(id))
                        bar.Children.Add(unit.Bar);
                }
            }
            Fill(LeftBar, _toolbarLayout.Left);
            Fill(RightBar, _toolbarLayout.Right);
            // Buttons in the More menu stay in the tree, collapsed, so captions, shortcuts and the
            // active-tool highlight keep working on them; they sit at the end of their default side.
            foreach (string id in _toolbarLayout.More)
            {
                if (!byId.TryGetValue(id, out ToolbarUnit? unit)) continue;
                unit.Bar.Visibility = Visibility.Collapsed;
                (DefaultToolbarLayout.Left.Contains(id) ? LeftBar : RightBar).Children.Add(unit.Bar);
            }

            var rows = _toolbarLayout.More.Concat(_toolbarLayout.Left).Concat(_toolbarLayout.Right)
                .Where(byId.ContainsKey).Select(id => (UIElement)byId[id].Overflow).ToList();
            foreach (UIElement row in rows) OverflowMenuPanel.Children.Remove(row);
            for (int index = 0; index < rows.Count; index++) OverflowMenuPanel.Children.Insert(index, rows[index]);
        }

        private Rectangle MakeToolbarSeparator()
        {
            var separator = new Rectangle { Width = 1, Margin = new Thickness(4, 6, 4, 6), Tag = ToolbarSeparatorToken };
            separator.SetResourceReference(Shape.FillProperty, "CardBorderBrush");
            return separator;
        }

        /// <summary>Hides a separator with no visible button on one side of it, so shed or
        /// hidden buttons never leave a stray or doubled divider.</summary>
        private void SyncToolbarSeparators()
        {
            foreach (Panel bar in new Panel[] { LeftBar, RightBar })
            {
                Rectangle? pending = null;
                bool seenButton = false;
                foreach (UIElement child in bar.Children)
                {
                    if (child is Rectangle { Tag: ToolbarSeparatorToken } separator)
                    {
                        separator.Visibility = Visibility.Collapsed;
                        if (seenButton) pending ??= separator;
                        continue;
                    }
                    if (child.Visibility != Visibility.Visible) continue;
                    if (pending is not null) pending.Visibility = Visibility.Visible;
                    pending = null;
                    seenButton = true;
                }
            }
        }

        /// <summary>The More menu rows that show a shortcut, for ApplyOverflowGestures.</summary>
        private IEnumerable<(Button Item, string Cap)> ToolbarOverflowGestures() =>
            _toolbarUnits.Where(unit => unit.Cap is not null).Select(unit => (unit.Overflow, unit.Cap!));

        /// <summary>Customize toolbar, from the toolbar's right-click menu and Search Tools.</summary>
        private void OpenCustomizeToolbar()
        {
            var dialog = new Controls.CustomizeToolbarDialog(this, _toolbarUnits, _toolbarLayout,
                DefaultToolbarLayout, (glyph, size) => MakeToolbarGlyph(glyph, size),
                key => WithoutShortcut(Loc(key)));
            if (dialog.ShowDialog() == true && dialog.Result is { } layout) SetToolbarLayout(layout);
        }
    }
}
