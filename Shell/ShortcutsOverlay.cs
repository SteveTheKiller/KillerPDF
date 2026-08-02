using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF
{
    // ============================================================
    // Keyboard-shortcuts overlay: single source of truth.
    //
    // The overlay card (ShortcutOverlay in MainWindow.xaml) is generated from the tables below rather
    // than hand-authored row by row, so adding or changing a shortcut is a one-line edit here and can't
    // drift out of sync with a parallel block of XAML. The two empty hosts ShortcutLeftColumn /
    // ShortcutRightColumn are filled by BuildShortcutsOverlay(), called once from the constructor.
    //
    // Keys are literal text (shown in Consolas, like a real keycap); labels are Str_* resource keys so
    // they stay localized. Everything is wired with SetResourceReference so both the theme colours and
    // the active locale keep updating live, exactly as the old DynamicResource markup did.
    // ============================================================
    public partial class MainWindow
    {
        // One row: the literal key text and the resource key for its translated description.
        private readonly record struct KsRow(string Keys, string LabelKey);

        // A titled group of rows. TitleKey is a Str_* resource key rendered as the accent subheader.
        private sealed class KsSection
        {
            public string TitleKey = "";
            public KsRow[] Rows = [];
        }

        // Left column: File, Tools, Editing, Help.
        private static readonly KsSection[] KsLeftColumn =
        [
            new KsSection { TitleKey = "Str_KS_File", Rows =
            [
                new("Ctrl+O",       "Str_KS_Open"),
                new("Ctrl+S",       "Str_Lbl_Save"),
                new("Ctrl+Shift+S", "Str_KS_SaveAs"),
                new("Ctrl+W",       "Str_KS_CloseFile"),
                new("Ctrl+Shift+W", "Str_KS_CloseOthers"),
                new("Ctrl+Q",       "Str_KS_CloseAll"),
                new("Ctrl+N",       "Str_KS_NewBlank"),
                new("Ctrl+P",       "Str_KS_Print"),
                new("Ctrl+D / F4",  "Str_KS_DocInfo"),
            ]},
            new KsSection { TitleKey = "Str_KS_Tools", Rows =
            [
                new("V",        "Str_Lbl_Select"),
                new("1 (or T)", "Str_Lbl_Text"),
                new("2 (or H)", "Str_Lbl_Highlight"),
                new("3 (or L or U)", "Str_Lbl_Line"),
                new("4",        "Str_Lbl_Shape"),
                new("5 (or D)", "Str_Lbl_Draw"),
                new("6 (or I)", "Str_Lbl_Image"),
                new("7 (or G)", "Str_Lbl_Signature"),
                new("8 (or C)", "Str_Lbl_Crop"),
                new("9 (or R)", "Str_Lbl_Rotate"),
                new("0 (or S)", "Str_TT_StampTool"),
            ]},
            new KsSection { TitleKey = "Str_KS_Editing", Rows =
            [
                new("Ctrl+Z",         "Str_KS_Undo"),
                new("Ctrl+Y",         "Str_Ctx_Redo"),
                new("Ctrl+Shift+Z",   "Str_Ctx_Redo"),
                new("Ctrl+C",         "Str_KS_CopyText"),
                new("Ctrl+V",         "Str_KS_Paste"),
                new("Ctrl+B / I / U", "Str_KS_TextStyle"),
                new("Delete",         "Str_KS_DeleteAnnot"),
                new("F2",             "Str_Ctx_BmRename"),
                new("Enter / Escape", "Str_KS_ConfirmCancel"),
                new("Menu / Shift+F10","Str_KS_ContextMenu"),
            ]},
            new KsSection { TitleKey = "Str_KS_Help", Rows =
            [
                new("F1 / Ctrl+?", "Str_KS_ThisList"),
                new("F12",         "Str_KS_About"),
            ]},
        ];

        // Right column: Navigation, View, OCR, Search & Select.
        private static readonly KsSection[] KsRightColumn =
        [
            new KsSection { TitleKey = "Str_KS_Navigation", Rows =
            [
                // Tightened to fit the 120px key column with a visible gap before the label.
                new("← / → or PgUp/PgDn", "Str_KS_PrevNext"),
                new("Home / End",     "Str_KS_FirstLast"),
                new("Alt+← / Alt+→",  "Str_KS_BackForward"),
                new("↑ / ↓",          "Str_KS_ScrollView"),
                new("Ctrl+Scroll",    "Str_KS_ZoomCursor"),
                new("Ctrl+%zin% / Ctrl+%zout%", "Str_KS_ZoomInOut"),
                new("Ctrl+0",         "Str_KS_ResetZoom"),
                new("Ctrl+1/2/3",     "Str_KS_ZoomPresets"),
                new("Middle drag",    "Str_KS_PanView"),
                new("Space + drag",   "Str_KS_PanView"),
                new("Ctrl+B",         "Str_KS_ToggleSidebar"),
                new("Ctrl+Shift+B",   "Str_KS_SidebarSide"),
                new("Ctrl+Tab",       "Str_KS_NextTab"),
                new("Ctrl+Shift+Tab", "Str_KS_PrevTab"),
            ]},
            new KsSection { TitleKey = "Str_KS_View", Rows =
            [
                new("F5",        "Str_View_Continuous"),
                new("F6",        "Str_View_Single"),
                new("F7",        "Str_View_TwoPage"),
                new("F8",        "Str_View_Grid"),
                new("F9",        "Str_KS_CycleView"),
                // Untranslated gesture literals, same convention as "Middle drag" below Navigation.
                new("Wheel on view", "Str_KS_CycleView"),
                new("F10",       "Str_KS_SplitPane"),
                new("F11 / Esc", "Str_KS_FullScreen"),
                new("N",         "Str_DocInvertSetting"),
                new("Shift+N",   "Str_InvertImagesToo"),
                new("Ctrl+Shift+%zin% / %zout% / 0", "Str_KS_AppSize"),
                new("Wheel on logo",   "Str_KS_AppSize"),
                new("Ctrl+Shift+1..6", "Str_KS_ToolbarStyle"),
            ]},
            new KsSection { TitleKey = "Str_KS_Ocr", Rows =
            [
                new("Ctrl+Shift+O", "Str_Ctx_OcrPage"),
                new("Ctrl+Shift+I", "Str_Ocr_Region"),
            ]},
            new KsSection { TitleKey = "Str_KS_SearchSelect", Rows =
            [
                new("Ctrl+F",              "Str_KS_Find"),
                new("F3 / Shift+F3",       "Str_KS_NextPrevResult"),
                new("Enter / Shift+Enter", "Str_KS_NextPrevResult"),
                new("Ctrl+A",              "Str_KS_SelectAll"),
                new("Shift+Click",         "Str_KS_MultiSelect"),
            ]},
        ];

        // #153: the zoom keys are spelled differently per keyboard layout - "=" is a plain keypress
        // on US but needs Shift on German, where "+" is the unshifted one instead. The bindings
        // accept whichever key TYPES the character (Services/KeyLayout.cs), so these labels have to
        // follow suit, or the overlay advertises a chord that does not work on that machine.
        // %zin% / %zout% are substituted here; everything else passes through untouched. The token
        // markers avoid braces on purpose - a brace in a char or string literal makes a plain
        // brace-balance check on this file report a false mismatch.
        private static string ResolveKeyLabel(string keys)
            => keys.IndexOf('%') < 0
                ? keys
                : keys.Replace("%zin%", Services.KeyLayout.ZoomInChar())
                      .Replace("%zout%", Services.KeyLayout.ZoomOutChar());

        // Fill the two overlay columns from the tables above. Called once from the constructor; the
        // SetResourceReference calls keep every string and colour live across theme + language changes.
        private void BuildShortcutsOverlay()
        {
            BuildShortcutsColumn(ShortcutLeftColumn,  KsLeftColumn);
            BuildShortcutsColumn(ShortcutRightColumn, KsRightColumn);
        }

        private static void BuildShortcutsColumn(StackPanel host, KsSection[] sections)
        {
            host.Children.Clear();
            for (int s = 0; s < sections.Length; s++)
            {
                var section = sections[s];

                // Section subheader: accent, semibold, 12px top gap except for the first section.
                var header = new TextBlock
                {
                    FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                    FontSize   = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin     = new Thickness(0, s == 0 ? 0 : 12, 0, 4),
                };
                header.SetResourceReference(TextBlock.TextProperty, section.TitleKey);
                header.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                host.Children.Add(header);

                for (int r = 0; r < section.Rows.Length; r++)
                {
                    var row  = section.Rows[r];
                    bool last = r == section.Rows.Length - 1;
                    var dock = new DockPanel { Margin = new Thickness(0, 0, 0, last ? 0 : 4) };

                    // Keys: fixed 120px column, Consolas, dim.
                    var keys = new TextBlock
                    {
                        Text       = ResolveKeyLabel(row.Keys),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize   = 11,
                        Width      = 120,
                    };
                    keys.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    dock.Children.Add(keys);

                    // Description: fills the rest, localized, primary colour, shared KS font size.
                    var label = new TextBlock();
                    label.SetResourceReference(TextBlock.TextProperty, row.LabelKey);
                    label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    label.SetResourceReference(TextBlock.FontSizeProperty, "Str_KS_FontSize");
                    dock.Children.Add(label);

                    host.Children.Add(dock);
                }
            }
        }
    }
}
