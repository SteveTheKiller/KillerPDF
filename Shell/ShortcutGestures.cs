using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace KillerPDF.Shell
{
    // ============================================================
    // Keyboard shortcuts as the action surfaces show them: menu gesture text and tooltips.
    //
    // One source for every surface, so a tooltip, a menu item and the shortcuts overlay cannot
    // describe one command two ways. Keys follow the user's custom shortcuts (#190) and key names
    // follow the active language, so a German menu reads Strg+C beside Strg+R instead of mixing
    // Ctrl and Strg. Menus are built when they open and pick this up then; the long-lived
    // tooltips are rebuilt by ApplyShortcutTooltips after a language change or a remap.
    // ============================================================
    public partial class MainWindow
    {
        /// <summary>The key a command answers to now, as display text: "Ctrl+S", "Strg+Umschalt+O",
        /// "F11". <paramref name="cap"/> uses the keyboard map's names, the way ShortcutTable.Cap
        /// does: "Ctrl:S", "CtrlShift:O", "F11", "Del", "Alt:Left".</summary>
        internal string GestureFor(string cap)
            => KeyText(CapKeys(ShortcutRemaps.Effective(ShortcutTable.Cap(cap))));

        private string? MenuShortcutFor(string header)
        {
            foreach (KsBinding binding in ShortcutTable.KsAll)
                if (binding.Caps.Length > 0 && Loc(binding.LabelKey) == header)
                    return GestureFor(CapName(binding.Caps[0]));
            foreach (var (letter, label) in ShortcutTable.DocumentToolKeys)
                if (Loc(label) == header) return GestureFor("CtrlShift:D") + ", " + letter;
            return null;
        }

        /// <summary>A document tool's keys as display text: "Ctrl+Shift+D, P", or for a tool with a direct key
        /// "Ctrl+Shift+M or Ctrl+Shift+D, M".</summary>
        internal string DocumentToolGestureText(char letter, string labelKey)
        {
            string chord = GestureFor("CtrlShift:D") + ", " + letter;
            string? direct = ShortcutTable.DocumentToolDirectKeys.FirstOrDefault(t => t.LabelKey == labelKey).Cap;
            return direct is null ? chord : GestureFor(direct) + " " + Loc("Str_Key_Or") + " " + chord;
        }

        /// <summary>Several keys for one command, joined the way the overlay joins them: "1 or T".</summary>
        private string GesturesFor(params string[] caps)
            => string.Join(" " + Loc("Str_Key_Or") + " ", caps.Select(GestureFor));

        /// <summary>A cap written with the overlay's key-name tokens.</summary>
        private static string CapKeys(KsCap cap) => ShortcutRowText.CapKeys(cap);

        /// <summary>Overlay key text with its tokens replaced by the active language's key names.</summary>
        private static string KeyText(string keys)
        {
            string text = ResolveKeyLabel(keys);
            foreach (var (token, key) in ShortcutTable.KeyTokens)
                if (text.Contains(token, StringComparison.Ordinal))
                    text = text.Replace(token, Loc(key), StringComparison.Ordinal);
            return text;
        }

        // Every tooltip string was written with its shortcut in brackets at the end, in every
        // language. The bracket is dropped and rebuilt from the live binding, so a moved key or a
        // language that does not say Ctrl never shows a stale shortcut. A bracket counts as a
        // shortcut when it holds one key (a letter, a digit or an F key) or a chord with a plus.
        private static readonly Regex TrailingShortcut =
            new(@"[\s​]*\((?:[A-Z0-9]|F\d{1,2}|[^()]*\+[^()]*)\)\s*$", RegexOptions.CultureInvariant);

        internal static string WithoutShortcut(string text) => TrailingShortcut.Replace(text, "");

        /// <summary>A label with its current shortcut appended: "Save (Ctrl+S)".</summary>
        private string WithGesture(string label, params string[] caps)
            => caps.Length == 0 ? label : KillerFamily.Layout.ShortcutHints.WithHint(label, GesturesFor(caps));

        /// <summary>A tooltip resource with its current shortcut appended.</summary>
        private string ShortcutTip(string resourceKey, params string[] caps)
            => WithGesture(WithoutShortcut(Loc(resourceKey)), caps);

        /// <summary>
        /// Rebuilds every tooltip that names a shortcut. Called at startup, after a language
        /// change and after the user moves a shortcut. Tool buttons list their keys in the same
        /// order as the overlay rows.
        /// </summary>
        private void ApplyShortcutTooltips()
        {
            void Tip(FrameworkElement? element, string key, params string[] caps)
            {
                if (element is null) return;
                element.ToolTip = ShortcutTip(key, caps);
            }

            Tip(NewBlankBtn, "Str_TT_NewBlank", "Ctrl:N");
            Tip(Viewer.NewTabButtonCtl, "Str_TT_NewBlank", "Ctrl:N");
            Tip(ViewerB.NewTabButtonCtl, "Str_TT_NewBlank", "Ctrl:N");
            Tip(CloseFileBtn, "Str_TT_CloseFile", "Ctrl:W");
            Tip(OpenFileBtn, "Str_TT_Open", "Ctrl:O");
            Tip(SaveAsBtn, "Str_TT_Save", "Ctrl:S");
            Tip(OcrBtn, "Str_TT_Ocr", "CtrlShift:O");
            Tip(PrintBtn, "Str_TT_Print", "Ctrl:P");
            SetPageNavigationTip(PreviousPageBtn, "Str_Kb_PrevPage", "PgUp");
            SetPageNavigationTip(NextPageBtn, "Str_Kb_NextPage", "PgDn");

            Tip(ToolPanBtn, "Str_TT_PanTool", "P");
            Tip(ToolSelectBtn, "Str_TT_SelectTool", "V");
            Tip(ToolTextBtn, "Str_TT_TextTool", "T");
            Tip(ToolHighlightBtn, "Str_TT_HighlightTool", "H");
            Tip(ToolUnderlineBtn, "Str_TT_LineTool", "L", "U");   // repurposed to the Line tool
            Tip(ToolShapeBtn, "Str_TT_ShapeTool", "O");
            Tip(ToolDrawBtn, "Str_TT_DrawTool", "D");
            Tip(ToolImageBtn, "Str_TT_ImageTool", "I");
            Tip(ToolSignatureBtn, "Str_TT_SignatureTool", "G");
            Tip(ToolCropBtn, "Str_TT_CropTool", "C");
            Tip(_toolRotateBtn, "Str_TT_RotateTool", "R");
            Tip(ToolStampBtn, "Str_TT_StampTool", "S");
            Tip(ToolColorCorrectionBtn, "Str_ColorCorrection", "E");
            Tip(ToolMeasureBtn, "Str_TT_MeasureTool", "M");
            Tip(ToolRedactBtn, "Str_TT_RedactTool", "X");
            Tip(ToolFormFieldBtn, "Str_TT_FormFieldTool", "F");

            Tip(UndoBtn, "Str_TT_UndoLast", "Ctrl:Z");
            Tip(RedoBtn, "Str_Ctx_Redo", "Ctrl:Y");
            Tip(SearchBtn, "Str_TT_Search", "Ctrl:F");
            Tip(_toolSearchBtn, "Str_ToolSearch", "Ctrl:K");

            Tip(SplitPaneRailBtn, "Str_TT_SplitPane", "F10");
            Tip(SidebarPagesTab, "Str_Rail_Pages", "Alt:D1");
            Tip(SidebarOutlinesTab, "Str_Rail_Outline", "Alt:D2");
            Tip(SidebarCommentsTab, "Str_Rail_Comments", "Alt:D3");
            Tip(SidebarAttachmentsTab, "Str_Rail_Attachments", "Alt:D4");
            Tip(SidebarLayersTab, "Str_Rail_Layers", "Alt:D5");
            Tip(SidebarSignaturesTab, "Str_Rail_Signatures", "Alt:D6");
            Tip(SidebarDocumentToolsTab, "Str_Rail_DocumentTools", "Alt:D7", "CtrlShift:D");
            RefreshDocumentToolsPanel(force: true);   // its rows name the Ctrl+Shift+D chord in the current language
            Tip(DeletePagesBtn, "Str_TT_DeletePages", "Del");
            Tip(FlattenBtn, "Str_Lbl_Flatten", "AltShift:F");
            Tip(MergeBtn, "Str_Lbl_Merge", "AltShift:B");
            Tip(ExtractPagesBtn, "Str_Lbl_Extract", "AltShift:X");
            Tip(ComparePdfBtn, "Str_TT_ComparePDFs", "AltShift:V");
            Tip(FullScreenRailBtn, "Str_KS_FullScreen", "F11");
            Tip(DocInvertBtn, "Str_TT_DocInvert", "N");
            Tip(DocInfoRailBtn, "Str_TT_DocInfo", "F4");
            Tip(ShortcutHelpRailBtn, "Str_TT_KeyboardShortcuts", "F1");
            Tip(VersionLabel, "Str_TT_About", "F12");
            SyncSidebarToggleTip();

            Tip(ViewContinuousRadio, "Str_View_Continuous", "F5");
            Tip(ViewSingleRadio, "Str_View_Single", "F6");
            Tip(ViewTwoPageRadio, "Str_View_TwoPage", "F7");
            Tip(ViewGridRadio, "Str_View_Grid", "F8");

            Tip(ZoomOutBtn, "Str_TT_ZoomOut", "Ctrl:Minus");
            Tip(ZoomInBtn, "Str_TT_ZoomIn", "Ctrl:Equals");
            ZoomBox.ToolTip = KillerFamily.Layout.ShortcutHints.WithHint(WithoutShortcut(Loc("Str_TT_ZoomLevel")), KeyText("%ctrl%+%scroll%")
                + " · " + GestureFor("Ctrl:Equals") + " / " + GestureFor("Ctrl:Minus")
                + " · " + GestureFor("Ctrl:D0"));
            LogoBar.ToolTip = KillerFamily.Layout.ShortcutHints.WithHint(WithoutShortcut(Loc("Str_TT_AppSize")), GestureFor("CtrlShift:Equals")
                + " / " + GestureFor("CtrlShift:Minus") + " / " + GestureFor("CtrlShift:D0"));

            _replaceToggleBtn?.ToolTip = ShortcutTip("Str_Replace_ToggleTT", "Ctrl:H");

            ApplyOverflowGestures();
        }

        /// <summary>The sidebar chevron's tooltip for its current state.</summary>
        private void SyncSidebarToggleTip()
            => _sidebarToggleBtn.ToolTip = ShortcutTip(
                _sidebarCollapsed ? "Str_TT_ExpandSidebar" : "Str_TT_CollapseSidebar", "F9");

        /// <summary>
        /// The overflow chevron's items carry the same right-hand shortcut column as the context
        /// menus. Tools show their letter there, as the page menu does.
        /// </summary>
        private void ApplyOverflowGestures()
        {
            (Button Item, string Cap)[] items =
            [
                (MiText, "T"), (MiFormField, "F"), (MiUnderline, "L"), (MiHighlight, "H"),
                (MiDraw, "D"), (MiShape, "O"), (MiImage, "I"), (MiSignature, "G"), (MiCrop, "C"),
                (MiColorCorrection, "E"), (MiMeasure, "M"), (MiRedact, "X"), (MiStamp, "S"),
                (MiUndo, "Ctrl:Z"), (MiRedo, "Ctrl:Y"),
            ];
            foreach (var (item, cap) in items)
                SetOverflowGesture(item, GestureFor(cap));
            // The rows ToolbarLayout adds for buttons the XAML gives none (New, Open, Select, ...).
            foreach (var (item, cap) in ToolbarOverflowGestures().Where(row => !items.Any(fixedRow => ReferenceEquals(fixedRow.Item, row.Item))))
                SetOverflowGesture(item, GestureFor(cap));
        }

        private static void SetOverflowGesture(Button item, string gestureText)
        {
            if (item.Content is DockPanel dock && dock.Children.Count > 0
                && dock.Children[0] is TextBlock existing && Equals(existing.Tag, "gesture"))
            {
                existing.Text = gestureText;
                return;
            }
            if (item.Content is not UIElement content) return;

            // Docked right in a row that fills the item, so the keys line up down the right edge
            // whatever length each translated label is.
            item.Content = null;
            var gesture = new TextBlock
            {
                Tag = "gesture",
                Text = gestureText,
                Margin = new Thickness(24, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            gesture.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            DockPanel.SetDock(gesture, Dock.Right);
            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(gesture);
            row.Children.Add(content);
            item.Content = row;
        }
    }
}
