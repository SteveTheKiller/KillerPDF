using System.Linq;

namespace KillerPDF
{
    // ============================================================
    // THE shortcut table. One source of truth for both views of the shortcuts overlay: the list
    // (Shell/ShortcutsOverlay.cs) and the visual keyboard (Shell/KeyboardMapOverlay.cs).
    //
    // This was two hand maintained tables until 1.7.5 and they had drifted apart: Home and End read
    // "first / last page" on the list but carried separate captions on the map, Alt+M was missing
    // from the map entirely, and Ctrl+B was documented as bold in one section and as the sidebar in
    // another while the code did neither consistently. Deriving both views from one array means a
    // binding cannot be described two ways, and ShortcutTableTests holds that.
    //
    // Deliberately free of WPF and of MainWindow, so KillerPDF.Tests can link the file the way it
    // links OcrCatalog.cs. Anything that needs a Brush or a Control belongs in the overlay files.
    // ============================================================

    /// <summary>Which modifier layer of the keyboard map a cap sits on.</summary>
    internal enum KbLayer { Base, Ctrl, CtrlShift, Shift, Alt, AltShift }

    /// <summary>
    /// One physical key the binding lights on the map. LabelKey overrides the row's description for
    /// this cap's hover caption, because a row and a cap are different granularities: the row reads
    /// "Home / End" while hovering Home should say "first page". Empty means inherit the row's.
    /// </summary>
    internal readonly record struct KsCap(KbLayer Layer, string Id, string LabelKey);

    /// <summary>One row of the list, plus the caps it lights on the map.</summary>
    internal readonly record struct KsBinding(string Keys, string LabelKey, string Cat, KsCap[] Caps);

    internal static class ShortcutTable
    {
        // ── Key-name tokens (#230) ─────────────────────────────────────────────────────────────
        // The key column used to be raw English: "Delete", "Home / End", "Wheel on logo". Italian
        // users want Canc, Inizio and Fine, and they were right to ask - a keycap name is as much
        // interface text as the description beside it.
        //
        // Only the NAMES are tokens. The chord syntax ("Ctrl+", "/", the F-numbers, letters and
        // digits) stays in the table, so translators never hand-maintain "Ctrl+Shift+" across 69
        // rows; they translate about twenty words and the composition takes care of itself.
        //
        // Ctrl and Alt are in here because German writes Strg. The gesture phrases are whole
        // tokens rather than assembled from "Wheel" plus "on" plus "view", because word order in
        // that phrase is not English's to dictate.
        internal static readonly (string Token, string Key)[] KeyTokens =
        [
            ("%ctrl%",       "Str_Key_Ctrl"),
            ("%alt%",        "Str_Key_Alt"),
            ("%shift%",      "Str_Key_Shift"),
            ("%del%",        "Str_Key_Delete"),
            ("%enter%",      "Str_Key_Enter"),
            ("%esc%",        "Str_Key_Esc"),
            ("%menu%",       "Str_Key_Menu"),
            ("%home%",       "Str_Key_Home"),
            ("%end%",        "Str_Key_End"),
            ("%pgup%",       "Str_Key_PgUp"),
            ("%pgdn%",       "Str_Key_PgDn"),
            ("%tab%",        "Str_Key_Tab"),
            ("%back%",       "Str_Key_Backspace"),
            ("%scroll%",     "Str_Key_Scroll"),
            ("%click%",      "Str_Key_Click"),
            ("%or%",         "Str_Key_Or"),
            ("%wheelview%",  "Str_Key_WheelView"),
            ("%wheellogo%",  "Str_Key_WheelLogo"),
            ("%middledrag%", "Str_Key_MiddleDrag"),
            ("%spacedrag%",  "Str_Key_SpaceDrag"),
        ];

        /// <summary>The character the website prints for zoom in. The app prints what the reader's
        /// keyboard can type without Shift (Services/KeyLayout.cs); a web page only knows the
        /// language, so this is the usual layout for each.</summary>
        internal static string SiteZoomInChar(string locale)
            => locale is "de" or "es" or "it" or "nb" or "hu" or "sl" or "tr" or "ja" ? "+" : "=";

        /// <summary>Authoring helper: "F3", "Shift:F3", or either with a per-cap hover label.
        /// Cap ids match KbRows in KeyboardMapOverlay.cs; bare ids are the base layer.</summary>
        internal static KsCap Cap(string id, string labelKey = "")
        {
            int colon = id.IndexOf(':');
            if (colon < 0) return new KsCap(KbLayer.Base, id, labelKey);
            string layer = id[..colon];
            return new KsCap(
                layer switch
                {
                    "Ctrl" => KbLayer.Ctrl,
                    "CtrlShift" => KbLayer.CtrlShift,
                    "Shift" => KbLayer.Shift,
                    "Alt" => KbLayer.Alt,
                    "AltShift" => KbLayer.AltShift,
                    _ => KbLayer.Base,
                },
                id[(colon + 1)..], labelKey);
        }

        private static KsBinding B(string keys, string labelKey, string cat, params KsCap[] caps)
            => new(keys, labelKey, cat, caps);

        // ── Document tools chord ───────────────────────────────────────────────────────────────
        // Ctrl+Shift+D opens the Document tools sidebar panel with the keyboard on it, and the letter
        // of each tool runs it, so "Ctrl+Shift+D, P" is the preflight report. One letter per tool,
        // shared by the panel (tooltips and the letter handler in Shell/DocumentTools.cs) and the
        // overlay rows below. The second key is a panel letter rather than a modifier layer, so these
        // rows light no cap on the keyboard map; the map shows the Ctrl+Shift+D cap that opens it.
        // Declared above KsAll on purpose: static initializers run in textual order.
        internal const string DocumentToolsChord = "Ctrl+Shift+D";

        internal static readonly (char Key, string LabelKey)[] DocumentToolKeys =
        [
            ('P', "Str_DocumentTools_Preflight"),
            ('R', "Str_DocumentTools_PrintProduction"),
            ('S', "Str_DocumentTools_Separations"),
            ('A', "Str_DocumentTools_Accessibility"),
            ('W', "Str_DocumentTools_Signatures"),
            ('Z', "Str_CertCatalog_Title"),
            ('L', "Str_DocumentTools_Layers"),
            ('E', "Str_DocumentTools_EditLayers"),
            ('F', "Str_DocumentTools_Portfolio"),
            ('O', "Str_DocumentTools_EditPortfolio"),
            ('U', "Str_DocumentTools_EditPortfolioStructure"),
            ('T', "Str_DocumentTools_EditAttachments"),
            ('K', "Str_DocumentTools_Comments"),
            ('Y', "Str_DocumentTools_Measurements"),
            ('N', "Str_DocumentTools_NavigationAudit"),
            ('B', "Str_DocumentTools_AutoBookmarks"),
            ('G', "Str_DocumentTools_PageLabels"),
            ('V', "Str_DocumentTools_InitialView"),
            ('C', "Str_DocumentTools_StructuralComparison"),
            ('X', "Str_Guided_Title"),
            ('M', "Str_DocumentTools_Macros"),
            ('D', "Str_DocumentTools_DataMerge"),
            ('I', "Str_DocumentTools_InsertToc"),
            ('H', "Str_DocumentTools_FormData"),
            ('J', "Str_DocumentTools_BookmarkData"),
            ('Q', "Str_DocumentTools_ProductionLayouts"),
            ('0', "Str_CM_Title"),
        ];

        /// <summary>
        /// The document tools used often enough to have a key of their own as well as their panel letter. Each is a
        /// free Ctrl+Shift letter matching its panel letter, so Ctrl+Shift+M and Ctrl+Shift+D, M are the same tool.
        /// The overlay shows both on one row, and only the direct key lights a cap on the map.
        /// </summary>
        internal static readonly (string LabelKey, string Cap)[] DocumentToolDirectKeys =
        [
            ("Str_Guided_Title", "CtrlShift:X"),
            ("Str_DocumentTools_Macros", "CtrlShift:M"),
        ];

        /// <summary>The document tool a Ctrl+Shift key (a map id such as "M") runs directly, or null.</summary>
        internal static string? DocumentToolForDirectKey(string id)
            => DocumentToolDirectKeys.FirstOrDefault(t => t.Cap == "CtrlShift:" + id).LabelKey;

        private static KsBinding DocumentToolRow((char Key, string LabelKey) tool)
        {
            string chord = "%ctrl%+%shift%+D, " + tool.Key;
            string? cap = DocumentToolDirectKeys.FirstOrDefault(t => t.LabelKey == tool.LabelKey).Cap;
            return cap is null
                ? B(chord, tool.LabelKey, "Docs")
                : B("%ctrl%+%shift%+" + cap["CtrlShift:".Length..] + " / " + chord, tool.LabelKey, "Docs", Cap(cap));
        }

        /// <summary>The key that runs a document tool while the Document tools panel has the keyboard.</summary>
        internal static char DocumentToolKey(string labelKey)
            => DocumentToolKeys.First(t => t.LabelKey == labelKey).Key;

        /// <summary>Menu gesture text for a document tool, e.g. "Ctrl+Shift+D, P".</summary>
        internal static string DocumentToolGesture(string labelKey)
            => DocumentToolsChord + ", " + DocumentToolKey(labelKey);

        // ── Saving a text box (#486) ───────────────────────────────────────────────────────────
        // The text bar chooses between Enter adds a line (Ctrl+Enter saves) and Enter saves
        // (Shift+Enter adds a line). Ctrl+Enter saves in both, so the row lights the same cap either
        // way: base Enter already belongs to Confirm and Shift+Enter to the previous search result.
        // KsAll holds the default row, which is also what the website prints.
        internal const string CommitTextLabel = "Str_KS_CommitText";

        internal static KsBinding CommitTextRow(bool enterCommits) => enterCommits
            ? B("%enter% / %ctrl%+%enter%", "Str_KS_CommitTextEnter", "Edit", Cap("Ctrl:Enter"))
            : B("%ctrl%+%enter%", CommitTextLabel, "Edit", Cap("Ctrl:Enter"));

        /// <summary>The overlay's rows for the current Enter choice: KsAll with the save row swapped.</summary>
        internal static IEnumerable<KsBinding> Rows(bool enterCommits) =>
            KsAll.Select(binding => binding.LabelKey == CommitTextLabel ? CommitTextRow(enterCommits) : binding);

        // Declaration order is display order. Column and section title come from KsGroups below.
        // A binding with no caps is a mouse gesture and correctly lights nothing on the board.
        internal static readonly KsBinding[] KsAll =
        [
            B("%alt%+%shift%+P", "Str_Paragraph_Title", "Edit", Cap("AltShift:P")),
            B("%ctrl%+%shift%+G", "Str_Lbl_DigitalSig", "Docs", Cap("CtrlShift:G")),
            B("%alt%+%shift%+T", "Str_TableExport_Title", "File", Cap("AltShift:T")),
            B("%alt%+%shift%+E", "Str_Menu_ExportImages", "File", Cap("AltShift:E")),
            B("%alt%+%shift%+Z", "Str_Menu_ReduceSize", "File", Cap("AltShift:Z")),
            B("%alt%+%shift%+C", "Str_Menu_CompressZip", "File", Cap("AltShift:C")),
            B("%alt%+%shift%+F", "Str_Lbl_Flatten", "File", Cap("AltShift:F")),
            B("%alt%+%shift%+B", "Str_Lbl_Merge", "File", Cap("AltShift:B")),
            B("%alt%+%shift%+X", "Str_Ctx_ExtractPages", "File", Cap("AltShift:X")),
            B("%alt%+%shift%+V", "Str_TT_ComparePDFs", "Docs", Cap("AltShift:V")),
            B("%alt%+%shift%+W", "Str_Watch_Title", "Docs", Cap("AltShift:W")),
            B("%alt%+%shift%+A", "Str_Ctx_AddBlankPage", "Docs", Cap("AltShift:A")),
            B("%alt%+%shift%+R", "Str_Pages_Reverse", "Docs", Cap("AltShift:R")),
            B("%alt%+%shift%+O", "Str_Pages_FixOrientation", "Docs", Cap("AltShift:O")),
            B("%alt%+%shift%+S", "Str_Pages_Straighten", "Docs", Cap("AltShift:S")),
            B("%alt%+%shift%+D", "Str_Pages_DivideSpreads", "Docs", Cap("AltShift:D")),
            B("%alt%+%shift%+L", "Str_Pages_RasterizeRegion", "Docs", Cap("AltShift:L")),
            B("%alt%+%shift%+1", "Str_Measure_Distance", "Tools", Cap("AltShift:D1")),
            B("%alt%+%shift%+2", "Str_Measure_Perimeter", "Tools", Cap("AltShift:D2")),
            B("%alt%+%shift%+3", "Str_Measure_Area", "Tools", Cap("AltShift:D3")),
            B("%alt%+%shift%+4", "Str_Measure_Angle", "Tools", Cap("AltShift:D4")),
            B("%alt%+%shift%+5", "Str_Measure_TT_Scale", "Tools", Cap("AltShift:D5")),
            B("%alt%+1", "Str_Rail_Pages", "Nav", Cap("Alt:D1")),
            B("%alt%+2", "Str_Rail_Outline", "Nav", Cap("Alt:D2")),
            B("%alt%+3", "Str_Rail_Comments", "Nav", Cap("Alt:D3")),
            B("%alt%+4", "Str_Rail_Attachments", "Nav", Cap("Alt:D4")),
            B("%alt%+5", "Str_Rail_Layers", "Nav", Cap("Alt:D5")),
            B("%alt%+6", "Str_Rail_Signatures", "Nav", Cap("Alt:D6")),
            B("%alt%+7", "Str_Rail_DocumentTools", "Nav", Cap("Alt:D7")),
            B("%ctrl%+O",             "Str_KS_Open",        "File", Cap("Ctrl:O")),
            B("%ctrl%+S",             "Str_Lbl_Save",       "File", Cap("Ctrl:S")),
            B("%ctrl%+%shift%+S",     "Str_KS_SaveAs",      "File", Cap("CtrlShift:S")),
            B("%ctrl%+W",             "Str_KS_CloseFile",   "File", Cap("Ctrl:W")),
            B("%ctrl%+%shift%+W",     "Str_KS_CloseOthers", "File", Cap("CtrlShift:W")),
            B("%ctrl%+%shift%+Q",     "Str_KS_CloseAll",    "File", Cap("CtrlShift:Q")),
            B("%ctrl%+Q",             "Str_Btn_Quit",       "File", Cap("Ctrl:Q")),
            B("%ctrl%+N",             "Str_KS_NewBlank",    "File", Cap("Ctrl:N")),
            B("%ctrl%+P",             "Str_KS_Print",       "File", Cap("Ctrl:P")),
            B("%ctrl%+D / F4",        "Str_KS_DocInfo",     "File", Cap("Ctrl:D"), Cap("F4")),
            B("%shift%+F4",           "Str_KS_FileSize",    "File", Cap("Shift:F4")),
            B("%ctrl%+%shift%+E",     "Str_Menu_ExportDocument", "File", Cap("CtrlShift:E")),
            B("%ctrl%+%shift%+P",     "Str_Protect_Name",   "File", Cap("CtrlShift:P")),

            B("V",               "Str_Lbl_Select",    "Tools", Cap("V")),
            // One letter per tool. Tools used to take the number row by toolbar position as well,
            // which renumbered every time a tool was added and could not reach past the tenth.
            B("T",               "Str_Lbl_Text",      "Tools", Cap("T")),
            B("H",               "Str_Lbl_Highlight", "Tools", Cap("H")),
            B("L (%or% U)",      "Str_Lbl_Line",      "Tools", Cap("L"), Cap("U")),
            B("O",               "Str_Lbl_Shape",     "Tools", Cap("O")),
            B("%shift%+O",       "Str_KS_NextShape",  "Tools", Cap("Shift:O")),
            B("D",               "Str_Lbl_Draw",      "Tools", Cap("D")),
            B("I",               "Str_Lbl_Image",     "Tools", Cap("I")),
            B("G",               "Str_Lbl_Signature", "Tools", Cap("G")),
            B("C",               "Str_Lbl_Crop",      "Tools", Cap("C")),
            B("R",               "Str_Lbl_Rotate",    "Tools", Cap("R")),
            B("%ctrl%+R",        "Str_Ctx_RotateCW", "Tools", Cap("Ctrl:R")),
            B("%ctrl%+L",        "Str_Ctx_RotateCCW", "Tools", Cap("Ctrl:L")),
            B("S",               "Str_Ctx_StampPages","Tools", Cap("S")),
            B("F",               "Str_KS_FormField",  "Tools", Cap("F")),
            B("E",               "Str_ColorCorrection", "Tools", Cap("E")),
            B("M",               "Str_Lbl_Measure",   "Tools", Cap("M")),
            B("%shift%+M",       "Str_KS_NextMeasure", "Tools", Cap("Shift:M")),
            B("X",               "Str_Lbl_Redact",    "Tools", Cap("X")),
            B("%back%",          "Str_KS_RemoveLastPoint", "Tools", Cap("Back")),

            B("%ctrl%+Z",         "Str_KS_Undo",        "Edit", Cap("Ctrl:Z")),
            B("%ctrl%+Y",         "Str_Ctx_Redo",       "Edit", Cap("Ctrl:Y")),
            B("%ctrl%+%shift%+Z", "Str_Ctx_Redo",       "Edit", Cap("CtrlShift:Z")),
            B("%ctrl%+C",         "Str_KS_CopyText",    "Edit", Cap("Ctrl:C")),
            B("%ctrl%+X",         "Str_Ctx_Cut",        "Edit", Cap("Ctrl:X")),
            B("%ctrl%+V",         "Str_KS_Paste",       "Edit", Cap("Ctrl:V")),
            // Real bindings as of 1.7.5. These were documented for years and never implemented;
            // Ctrl+B collapsed the sidebar instead, which is why the sidebar moved to F9.
            B("%ctrl%+B / I / U", "Str_KS_TextStyle",   "Edit", Cap("Ctrl:B", "Str_Lbl_Bold"),
                                                                Cap("Ctrl:I", "Str_Lbl_Italic"),
                                                                Cap("Ctrl:U", "Str_Lbl_Underline")),
            B("%del%",            "Str_KS_DeleteAnnot", "Edit", Cap("Del")),
            // Enter adds a line in a text box, so the box is saved with Ctrl+Enter. The text bar can
            // make Enter save instead (#486); CommitTextRow and Rows give the overlay that row.
            CommitTextRow(enterCommits: false),
            B("F2",               "Str_Ctx_BmRename",   "Edit", Cap("F2")),
            B("%enter% / %esc%",  "Str_KS_ConfirmCancel","Edit", Cap("Enter", "Str_Kb_Confirm"),
                                                                 Cap("Esc",   "Str_Kb_Cancel")),
            B("%menu% / %shift%+F10", "Str_KS_ContextMenu", "Edit", Cap("Menu"), Cap("Shift:F10")),

            B("F1 / %ctrl%+?", "Str_KS_ThisList", "Help", Cap("F1"), Cap("Ctrl:Slash")),
            B("F12",         "Str_KS_About",    "Help", Cap("F12")),
            B("%ctrl%+%shift%+K", "Str_KS_Toggle", "Help", Cap("CtrlShift:K")),

            B("← / → %or% %pgup%/%pgdn%", "Str_KS_PrevNext", "Nav", Cap("Left",  "Str_Kb_PrevPage"),
                                                                    Cap("Right", "Str_Kb_NextPage"),
                                                                    Cap("PgUp",  "Str_Kb_PrevPage"),
                                                                    Cap("PgDn",  "Str_Kb_NextPage")),
            B("%home% / %end%", "Str_KS_FirstLast",  "Nav", Cap("Home", "Str_Kb_FirstPage"),
                                                            Cap("End",  "Str_Kb_LastPage")),
            B("%alt%+← / %alt%+→", "Str_KS_BackForward", "Nav", Cap("Alt:Left",  "Str_Kb_Back"),
                                                                Cap("Alt:Right", "Str_Kb_Forward")),
            B("↑ / ↓",         "Str_KS_ScrollView",  "Nav", Cap("Up"), Cap("Down")),
            B("%ctrl%+%scroll%", "Str_KS_ZoomCursor", "Nav"),
            B("%ctrl%+%zin% / %ctrl%+%zout%", "Str_KS_ZoomInOut", "Nav", Cap("Ctrl:Equals", "Str_Lbl_ZoomIn"),
                                                                         Cap("Ctrl:Minus",  "Str_Lbl_ZoomOut")),
            B("%ctrl%+0",      "Str_KS_ResetZoom",   "Nav", Cap("Ctrl:D0")),
            B("%ctrl%+1 / 2 / 3",  "Str_KS_ZoomPresets", "Nav", Cap("Ctrl:D1", "Str_Zoom_ActualSize"),
                                                            Cap("Ctrl:D2", "Str_Zoom_FitWidth"),
                                                            Cap("Ctrl:D3", "Str_Zoom_FitPage")),
            B("%middledrag%",  "Str_KS_PanView",     "Nav"),
            B("%spacedrag%",   "Str_KS_PanView",     "Nav", Cap("Space")),
            B("F9",            "Str_KS_ToggleSidebar","Nav", Cap("F9")),
            B("%shift%+F9",    "Str_KS_SidebarSide", "Nav", Cap("Shift:F9")),
            B("%ctrl%+%tab%",  "Str_KS_NextTab",     "Nav", Cap("Ctrl:Tab")),
            B("%ctrl%+%shift%+%tab%", "Str_KS_PrevTab", "Nav", Cap("CtrlShift:Tab")),
            B("%alt%+%shift%+Up", "Str_Layout_TabsTop", "View", Cap("AltShift:Up")),
            B("%alt%+%shift%+Down", "Str_Layout_TabsBottom", "View", Cap("AltShift:Down")),
            B("%ctrl%+%shift%+N", "Str_Ctx_MoveToNewWindow", "Nav", Cap("CtrlShift:N")),
            B("%ctrl%+%shift%+T", "Str_Ctx_ReopenClosedTab", "Nav", Cap("CtrlShift:T")),

            B("F5",            "Str_View_Continuous", "View", Cap("F5")),
            B("F6",            "Str_View_Single",     "View", Cap("F6")),
            B("F7",            "Str_View_TwoPage",    "View", Cap("F7")),
            B("B",             "Str_View_BookMode",   "View", Cap("B")),   // #193: Two-Page only
            B("F8",            "Str_View_Grid",       "View", Cap("F8")),
            B("F10",           "Str_KS_SplitPane",    "View", Cap("F10")),
            // Esc belongs to Cancel on the base layer, so full screen only claims F11.
            B("F11 / %esc%",   "Str_KS_FullScreen",   "View", Cap("F11")),
            B("%alt%+M",       "Str_Toolbar_Hide",    "View", Cap("Alt:M")),
            B("%alt%+B",       "Str_KS_ToolMenu",     "View", Cap("Alt:B")),
            B("N",             "Str_DocInvertSetting","View", Cap("N")),
            B("%shift%+N",     "Str_InvertImagesToo", "View", Cap("Shift:N")),
            B("%ctrl%+%shift%+%zin% / %zout% / 0", "Str_KS_AppSize", "View", Cap("CtrlShift:Equals"),
                                                                             Cap("CtrlShift:Minus"),
                                                                             Cap("CtrlShift:D0")),
            B("%wheellogo%",   "Str_KS_AppSize",      "View"),
            B("%alt%+%shift%+G", "Str_Theme_FilmGrain", "View", Cap("AltShift:G")),
            B("%alt%+%shift%+N", "Str_NavigationButtons_Show", "View", Cap("AltShift:N")),
            // The toolbar appearance six, mirroring the bar's right-click menu top to bottom.
            B("%ctrl%+%shift%+1-6", "Str_KS_ToolbarStyle", "View", Cap("CtrlShift:D1", "Str_Toolbar_SmallIcons"),
                                                                Cap("CtrlShift:D2", "Str_Toolbar_LargeIcons"),
                                                                Cap("CtrlShift:D3", "Str_Toolbar_TextNone"),
                                                                Cap("CtrlShift:D4", "Str_Toolbar_TextBeside"),
                                                                Cap("CtrlShift:D5", "Str_Toolbar_TextUnder"),
                                                                Cap("CtrlShift:D6", "Str_Toolbar_TextOnly")),

            B("%ctrl%+%shift%+O", "Str_Ctx_OcrPage", "Ocr", Cap("CtrlShift:O")),
            B("%ctrl%+%shift%+I", "Str_Ocr_Region",  "Ocr", Cap("CtrlShift:I")),
            B("%ctrl%+%shift%+R", "Str_Ocr_ScanToText", "Ocr", Cap("CtrlShift:R")),

            B("%ctrl%+K",                  "Str_ToolSearch",        "Search", Cap("Ctrl:K")),
            B("%ctrl%+F",                  "Str_KS_Find",           "Search", Cap("Ctrl:F")),
            B("%ctrl%+H",                  "Str_KS_Replace",        "Search", Cap("Ctrl:H")),
            B("F3 / %shift%+F3",           "Str_KS_NextPrevResult", "Search", Cap("F3",       "Str_Kb_NextResult"),
                                                                              Cap("Shift:F3", "Str_Kb_PrevResult")),
            B("%enter% / %shift%+%enter%", "Str_KS_NextPrevResult", "Search", Cap("Shift:Enter", "Str_Kb_PrevResult")),
            B("%alt%+C / %alt%+W",         "Str_KS_SearchOptions",  "Search", Cap("Alt:C"), Cap("Alt:W")),
            B("%ctrl%+%alt%+%enter%",      "Str_Replace_All",       "Search"),
            B("%ctrl%+A",                  "Str_KS_SelectAll",      "Search", Cap("Ctrl:A")),
            B("%shift%+%click%",           "Str_KS_MultiSelect",    "Search"),

            B("%ctrl%+%shift%+D",          "Str_DocumentTools",     "Docs", Cap("CtrlShift:D")),
            .. DocumentToolKeys.Select(DocumentToolRow),
        ];

        // Section title and column for each category. Order here is the order sections appear.
        internal static readonly (string Cat, string TitleKey, bool Right)[] KsGroups =
        [
            ("File",   "Str_KS_File",         false),
            ("Tools",  "Str_KS_Tools",        false),
            ("Edit",   "Str_KS_Editing",      false),
            ("Help",   "Str_KS_Help",         false),
            ("Nav",    "Str_KS_Navigation",   true),
            ("View",   "Str_KS_View",         true),
            ("Ocr",    "Str_KS_Ocr",          true),
            ("Search", "Str_KS_SearchSelect", true),
            ("Docs",   "Str_KS_DocumentTools", true),
        ];

        /// <summary>The section title string key for a binding's category, from KsGroups.</summary>
        internal static string SectionTitleKey(string cat)
        {
            foreach (var group in KsGroups)
                if (group.Cat == cat) return group.TitleKey;
            return cat;
        }

        /// <summary>The map's view of the table: layer -> cap id -> (category, label key). A cap's
        /// own LabelKey wins over its row's, which is how one row serves two captions.
        /// <paramref name="place"/> moves a cap to the key the user chose for it (#190), and
        /// <paramref name="enterCommits"/> captions Ctrl+Enter for the text bar's Enter choice (#486).</summary>
        internal static Dictionary<KbLayer, Dictionary<string, (string Cat, string Label)>> BuildMap(
            Func<KsCap, KsCap>? place = null, bool enterCommits = false)
        {
            var map = new Dictionary<KbLayer, Dictionary<string, (string, string)>>();
            foreach (KbLayer layer in Enum.GetValues<KbLayer>())
                map[layer] = [];

            foreach (var binding in Rows(enterCommits))
                foreach (var cap in binding.Caps)
                {
                    KsCap placed = place?.Invoke(cap) ?? cap;
                    map[placed.Layer][placed.Id] =
                        (binding.Cat, cap.LabelKey.Length > 0 ? cap.LabelKey : binding.LabelKey);
                }
            return map;
        }

        /// <summary>Every (layer, cap) the table claims, as "Layer:Id", including duplicates.
        /// ShortcutTableTests uses this to prove no key is claimed twice, which is exactly how
        /// Ctrl+B managed to mean two things for so long.</summary>
        internal static IEnumerable<string> AllCapClaims() =>
            KsAll.SelectMany(b => b.Caps).Select(c => c.Layer + ":" + c.Id);
    }
}
