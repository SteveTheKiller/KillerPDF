using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using KillerFamily.Accessibility;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // ============================================================
    // Tool search in the title bar, left of Minimize. The magnifier, or Ctrl+K, opens a box that
    // grows out of it; typing lists matching tools and commands live in a flyout under the box,
    // with their shortcuts, and Enter or a click runs one.
    //
    // The list is the shortcut table (Services/ShortcutTable.cs) plus the Document tools and the
    // Save menu's commands that have no key, so a command added to the table is searchable without
    // touching this file. A command from the table runs by handing its built-in key to the window's
    // key handler (Shell/KeyboardShortcuts.cs), so it does exactly what its shortcut does, even
    // when the user has moved that shortcut or turned keyboard shortcuts off.
    // ============================================================
    public partial class MainWindow
    {
        private sealed record ToolSearchEntry(string Name, string English, string Keywords, string Cat,
            string Gesture, Action Run);

        private const double ToolSearchBoxWidth = 280;
        private const int ToolSearchPerGroup = 5;
        private const int ToolSearchMostResults = 15;

        private Button? _toolSearchBtn;
        private Border? _toolSearchBox;
        private TextBox? _toolSearchInput;
        private TextBlock? _toolSearchHint;
        private Popup? _toolSearchPopup;
        private Border? _toolSearchCard;
        private StackPanel? _toolSearchList;
        private List<ToolSearchEntry> _toolSearchEntries = [];
        private readonly List<(Border Row, TextBlock Name, TextBlock Keys, ToolSearchEntry Entry)> _toolSearchRows = [];
        private int _toolSearchSelected = -1;
        private bool _toolSearchOpen;

        // Set while a search result runs through the key handler: the built-in key it stands for.
        private (Key Key, ModifierKeys Modifiers, Key SystemKey)? _syntheticShortcut;

        private static ResourceDictionary? _englishStrings;

        // Rows whose action only makes sense on something already selected or being typed.
        private static readonly HashSet<string> ToolSearchSkippedLabels =
        [
            "Str_KS_TextStyle", "Str_Lbl_Bold", "Str_Lbl_Italic", "Str_Lbl_Underline",
            "Str_KS_DeleteAnnot", "Str_Ctx_BmRename", "Str_KS_ConfirmCancel", "Str_Kb_Confirm",
            "Str_Kb_Cancel", "Str_KS_ContextMenu", "Str_KS_CopyText", "Str_KS_Paste", "Str_Ctx_Cut",
            "Str_KS_ScrollView", "Str_KS_PanView",
        ];

        // Keys that confirm, cancel, scroll or pan rather than run a command.
        private static readonly HashSet<string> ToolSearchSkippedCaps =
            ["Base:Space", "Base:Esc", "Base:Enter", "Base:Menu", "Base:Up", "Base:Down", "Shift:F10", "Shift:Enter"];

        // Commands that work with no document open. Everything else is left out of the list until
        // a PDF is open, instead of offering commands that would only say "open a file first".
        private static readonly HashSet<string> ToolSearchWithoutDocument =
        [
            "Str_KS_Open", "Str_KS_NewBlank", "Str_KS_ThisList", "Str_KS_About", "Str_KS_Toggle",
            "Str_KS_FullScreen", "Str_Toolbar_Hide", "Str_KS_AppSize", "Str_Theme_FilmGrain",
            "Str_Toolbar_SmallIcons", "Str_Toolbar_LargeIcons", "Str_Toolbar_TextNone",
            "Str_Toolbar_TextBeside", "Str_Toolbar_TextUnder", "Str_Toolbar_TextOnly",
            "Str_KS_ToggleSidebar", "Str_KS_SidebarSide", "Str_KS_SplitPane", "Str_Toolbar_Customize",
            "Str_Watch_Title", "Str_DocumentTools_Macros", "Str_CertCatalog_Title",
            "Str_NavigationButtons_Show", "Str_Btn_Quit", "Str_Layout_TabsTop", "Str_Layout_TabsBottom",
        ];

        // Other words people search for, in English whatever the interface language, because the
        // English names are searched in every language too.
        private static readonly Dictionary<string, string> ToolSearchKeywords = new(StringComparer.Ordinal)
        {
            ["Str_Guided_Title"] = "prepare email clean scan sensitive information privacy redact create fillable form workflow task",
            ["Str_Lbl_Select"] = "pointer arrow cursor move",
            ["Str_Lbl_Text"] = "type typewriter font add text edit text",
            ["Str_Lbl_Highlight"] = "marker highlighter",
            ["Str_Lbl_Line"] = "arrow straight line",
            ["Str_Lbl_Shape"] = "rectangle box ellipse circle oval polygon",
            ["Str_KS_NextShape"] = "rectangle ellipse polygon",
            ["Str_Lbl_Draw"] = "pen pencil ink freehand brush sketch",
            ["Str_Lbl_Image"] = "picture photo logo insert",
            ["Str_Lbl_Signature"] = "sign initials autograph",
            ["Str_Lbl_Crop"] = "trim margins cut",
            ["Str_Lbl_Rotate"] = "transform resize scale rotate",
            ["Str_Ctx_RotateCW"] = "rotate turn page clockwise",
            ["Str_Ctx_RotateCCW"] = "rotate turn page counterclockwise anticlockwise",
            ["Str_Ctx_StampPages"] = "stamp watermark bates header footer page numbers",
            ["Str_KS_FormField"] = "form field fillable input textbox",
            ["Str_ColorCorrection"] = "color brightness contrast levels grayscale",
            ["Str_Lbl_Measure"] = "ruler distance area length perimeter angle scale",
            ["Str_KS_NextMeasure"] = "distance perimeter area angle",
            ["Str_DocumentTools_Measurements"] = "measurements report list export csv",
            ["Str_DocumentTools_FormData"] = "form fields comments import export fdf xfdf json recognize detect xfa convert flatten",
            ["Str_DocumentTools_Preflight"] = "repair language metadata output intent icc bleed trim art production",
            ["Str_DocumentTools_Accessibility"] = "repair descriptions alternate text reading order table header row column",
            ["Str_DocumentTools_DataMerge"] = "csv xlsx json data merge mapping profile condition image placeholder flatten",
            ["Str_DocumentTools_ProductionLayouts"] = "imposition booklet nup n-up poster tile step repeat cut stack manual bates batch production marks",
            ["Str_DocumentTools_BookmarkData"] = "bookmarks outline import export json",
            ["Str_Lbl_Redact"] = "black out censor hide remove sensitive",
            ["Str_KS_Open"] = "open file browse load",
            ["Str_Menu_Scan"] = "scanner scanning acquire twain paper create pdf",
            ["Str_Menu_ConvertOffice"] = "microsoft office word excel document spreadsheet doc docx xls xlsx convert create pdf",
            ["Str_KS_SaveAs"] = "save copy rename",
            ["Str_KS_Print"] = "printer paper",
            ["Str_KS_DocInfo"] = "properties metadata title author info",
            ["Str_KS_NewBlank"] = "new blank create document",
            ["Str_Menu_ExportDocument"] = "export convert word excel powerpoint html markdown docx xlsx pptx",
            ["Str_Protect_Name"] = "password encrypt protect lock security",
            ["Str_KS_Find"] = "find search",
            ["Str_KS_Replace"] = "find replace substitute",
            ["Str_DocInvertSetting"] = "night dark mode invert colors",
            ["Str_KS_FullScreen"] = "presentation fullscreen",
            ["Str_KS_SplitPane"] = "side by side two documents",
            ["Str_Ctx_OcrPage"] = "ocr recognize text copy scan",
            ["Str_Ocr_Region"] = "ocr region area capture",
            ["Str_Ocr_ScanToText"] = "ocr scan searchable convert",
            ["Str_KS_ThisList"] = "keyboard shortcuts hotkeys keys help",
            ["Str_KS_About"] = "about version",
            ["Str_Lbl_Merge"] = "combine join append",
            ["Str_Ctx_ExtractPages"] = "split extract pages",
            ["Str_TT_ComparePDFs"] = "compare diff difference",
            ["Str_Menu_ReduceSize"] = "compress smaller optimize shrink",
            ["Str_Lbl_DigitalSig"] = "certificate digital signature sign",
            ["Str_Toolbar_Customize"] = "customize toolbar buttons rearrange reorder hide more menu overflow",
        };

        // Built once at startup: the magnifier caption button and the box it opens, placed in the
        // title bar host left of Minimize. The flyout's rows are rebuilt as the user types.
        private void BuildToolSearch()
        {
            var fontGlyph = new Glyph
            {
                Text = "",   // Segoe MDL2 Search
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            fontGlyph.SetResourceReference(TextBlock.FontWeightProperty, "CaptionGlyphWeight");
            fontGlyph.SetResourceReference(VisibilityProperty, "CaptionFontGlyphVisibility");

            // The drawn-caption themes draw their glyphs instead of using the icon font.
            var lens = new Ellipse { Width = 6, Height = 6, StrokeThickness = 1 };
            lens.SetResourceReference(Shape.StrokeProperty, "CaptionGlyphBrush");
            var handle = new Line { X1 = 5, Y1 = 5, X2 = 8.5, Y2 = 8.5, StrokeThickness = 1.5 };
            handle.SetResourceReference(Shape.StrokeProperty, "CaptionGlyphBrush");
            var drawnGlyph = new Canvas
            {
                Width = 9,
                Height = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
            };
            drawnGlyph.Children.Add(lens);
            drawnGlyph.Children.Add(handle);
            drawnGlyph.SetResourceReference(VisibilityProperty, "CaptionDrawnGlyphVisibility");

            var magnifier = new Grid();
            magnifier.SetResourceReference(HeightProperty, "CaptionButtonHeight");
            magnifier.Children.Add(fontGlyph);
            magnifier.Children.Add(drawnGlyph);
            _toolSearchBtn = new Button { Style = (Style)FindResource("ChromeButton"), Content = magnifier };
            _toolSearchBtn.SetResourceReference(ForegroundProperty, "CaptionGlyphBrush");
            _toolSearchBtn.Click += (_, _) =>
            {
                if (_toolSearchOpen) CloseToolSearch();
                else OpenToolSearch();
            };

            _toolSearchInput = new TextBox
            {
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(4, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                FocusVisualStyle = null,
                Template = FlatSearchTextBoxTemplate(),
            };
            _toolSearchInput.SetResourceReference(ForegroundProperty, "TextBrush");
            _toolSearchInput.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextBrush");
            _toolSearchInput.SetResourceReference(AutomationProperties.NameProperty, "Str_ToolSearch");
            _toolSearchInput.TextChanged += (_, _) =>
            {
                _toolSearchHint!.Visibility = _toolSearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                RefreshToolSearchResults();
            };

            _toolSearchHint = new TextBlock
            {
                IsHitTestVisible = false,
                Margin = new Thickness(6, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _toolSearchHint.SetResourceReference(TextBlock.TextProperty, "Str_ToolSearch");
            _toolSearchHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            _toolSearchList = new StackPanel();
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 440,
                Focusable = false,
                Margin = new Thickness(4),
                Content = _toolSearchList,
            };
            // Grain goes over the rows, so a highlighted row keeps the texture.
            var grain = new Border { IsHitTestVisible = false, Background = (Brush)FindResource("GrainBrushShared") };
            grain.SetResourceReference(OpacityProperty, "GrainOpacity");
            grain.SetResourceReference(Border.CornerRadiusProperty, "FlyoutCornerRadius");
            var layers = new Grid();
            layers.Children.Add(scroller);
            layers.Children.Add(grain);
            _toolSearchCard = new Border
            {
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 8),
                MinWidth = ToolSearchBoxWidth,
                MaxWidth = 460,
                Child = layers,
            };
            _toolSearchCard.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
            _toolSearchCard.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            _toolSearchCard.SetResourceReference(Border.CornerRadiusProperty, "FlyoutCornerRadius");

            var field = new Grid();
            field.Children.Add(_toolSearchHint);
            field.Children.Add(_toolSearchInput);
            _toolSearchBox = new Border
            {
                Width = 0,
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 2, 0),
                ClipToBounds = true,
                Child = field,
            };
            _toolSearchBox.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
            _toolSearchBox.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
            _toolSearchBox.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");

            // The flyout hangs from the box, the control that opened it. It sits in the box's tree
            // so its theme and string resources resolve the way the window's do.
            _toolSearchPopup = new Popup
            {
                AllowsTransparency = true,
                StaysOpen = true,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 4,
                PlacementTarget = _toolSearchBox,
                Child = _toolSearchCard,
            };
            field.Children.Add(_toolSearchPopup);

            ToolSearchHost.Children.Add(_toolSearchBox);
            ToolSearchHost.Children.Add(_toolSearchBtn);

            Deactivated += (_, _) => CloseToolSearch();
            LocationChanged += (_, _) => CloseToolSearch();
            PreviewMouseDown += (_, _) =>
            {
                if (_toolSearchOpen && !ToolSearchHost.IsMouseOver && _toolSearchCard?.IsMouseOver != true)
                    CloseToolSearch();
            };
        }

        // The find box's flat template, so no default WPF focus border shows inside the box.
        private static ControlTemplate FlatSearchTextBoxTemplate()
        {
            var template = new ControlTemplate(typeof(TextBox));
            var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            host.SetValue(ScrollViewer.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            host.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            template.VisualTree = host;
            return template;
        }

        private void ToggleToolSearch()
        {
            if (_toolSearchOpen) CloseToolSearch();
            else OpenToolSearch();
        }

        private void OpenToolSearch()
        {
            if (_toolSearchBox is null || _toolSearchInput is null || _toolSearchPopup is null) return;
            if (_toolSearchOpen)
            {
                _toolSearchInput.Focus();
                _toolSearchInput.SelectAll();
                return;
            }
            _toolSearchOpen = true;
            _toolSearchEntries = BuildToolSearchEntries();
            double captionHeight = TryFindResource("CaptionButtonHeight") is double height ? height : 32;
            _toolSearchBox.Height = Math.Min(24, captionHeight);
            if (_toolSearchCard is not null)
                _toolSearchCard.Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 14,
                    ShadowDepth = 2,
                    Direction = 270,
                    Opacity = TryFindResource("FlyoutShadowOpacity") is double opacity ? opacity : 0.4,
                };
            _toolSearchInput.Text = "";
            RefreshToolSearchResults();
            _toolSearchBox.Visibility = Visibility.Visible;

            // The box grows out of the magnifier toward the file name, then the flyout drops from it.
            if (SystemParameters.ClientAreaAnimation)
            {
                var grow = new DoubleAnimation(0, ToolSearchBoxWidth, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                grow.Completed += (_, _) =>
                {
                    if (!_toolSearchOpen) return;
                    _toolSearchBox.BeginAnimation(WidthProperty, null);
                    _toolSearchBox.Width = ToolSearchBoxWidth;
                    _toolSearchPopup.IsOpen = true;
                };
                _toolSearchBox.BeginAnimation(WidthProperty, grow);
            }
            else
            {
                _toolSearchBox.Width = ToolSearchBoxWidth;
                _toolSearchPopup.IsOpen = true;
            }
            _toolSearchInput.Focus();
            Keyboard.Focus(_toolSearchInput);
        }

        private void CloseToolSearch()
        {
            if (!_toolSearchOpen || _toolSearchBox is null || _toolSearchPopup is null) return;
            _toolSearchOpen = false;
            _toolSearchPopup.IsOpen = false;
            _toolSearchBox.BeginAnimation(WidthProperty, null);
            _toolSearchBox.Width = 0;
            _toolSearchBox.Visibility = Visibility.Collapsed;
            if (_toolSearchInput?.IsKeyboardFocusWithin == true)
            {
                Keyboard.ClearFocus();
                Focus();
            }
        }

        // Keys typed into the search box. Arrows move through the results, Enter runs the
        // highlighted one, Esc and Tab close the box and Ctrl+K closes it again. Everything else
        // goes on to the box and the window as usual.
        private bool HandleToolSearchKey(KeyEventArgs e)
        {
            if (!_toolSearchOpen || _toolSearchInput?.IsKeyboardFocusWithin != true) return false;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys modifiers = Keyboard.Modifiers;
            switch (key)
            {
                case Key.Escape:
                case Key.Tab:
                    CloseToolSearch();
                    break;
                case Key.Down:
                    SelectToolSearchRow(_toolSearchSelected + 1);
                    break;
                case Key.Up:
                    SelectToolSearchRow(_toolSearchSelected - 1);
                    break;
                case Key.PageDown:
                    SelectToolSearchRow(_toolSearchSelected + ToolSearchPerGroup);
                    break;
                case Key.PageUp:
                    SelectToolSearchRow(_toolSearchSelected - ToolSearchPerGroup);
                    break;
                case Key.Enter:
                    if (_toolSearchSelected >= 0 && _toolSearchSelected < _toolSearchRows.Count)
                        RunToolSearchEntry(_toolSearchRows[_toolSearchSelected].Entry);
                    break;
                case Key.K when modifiers == ModifierKeys.Control:
                    CloseToolSearch();
                    break;
                default:
                    return false;
            }
            e.Handled = true;
            return true;
        }

        private void RunToolSearchEntry(ToolSearchEntry entry)
        {
            CloseToolSearch();
            // After the box has closed and focus is back on the window, as if the key were pressed.
            Dispatcher.BeginInvoke(entry.Run, DispatcherPriority.Input);
        }

        // Every searchable command, for the current language and whether a document is open.
        private List<ToolSearchEntry> BuildToolSearchEntries()
        {
            bool hasDocument = _doc is not null;
            var entries = new List<ToolSearchEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string labelKey, string cat, string gesture, Action run, bool? requiresDocument = null)
            {
                if (!seen.Add(labelKey)) return;
                if (!hasDocument && (requiresDocument ?? !ToolSearchWithoutDocument.Contains(labelKey))
                    && !ToolSearchReadingCommands.Admits(HasReadingDocument, labelKey)) return;
                entries.Add(new ToolSearchEntry(WithoutShortcut(Loc(labelKey)), EnglishText(labelKey),
                    ToolSearchKeywords.GetValueOrDefault(labelKey, ""), cat, gesture, run));
            }

            foreach (KsBinding binding in ShortcutTable.Rows(Controls.PdfViewer.TextEnterMode.EnterCommits))
            {
                // Document tools are listed below with both their keys and run through the tool list.
                if (ShortcutTable.DocumentToolKeys.Any(tool => tool.LabelKey == binding.LabelKey)) continue;
                // A cap with its own label is its own command ("Fit width" on Ctrl+2); caps that
                // share the row's label are one command with several keys ("Line" on L or U).
                var commands = binding.Caps
                    .Where(cap => !ToolSearchSkippedCaps.Contains(cap.Layer + ":" + cap.Id))
                    .GroupBy(cap => cap.LabelKey.Length > 0 ? cap.LabelKey : binding.LabelKey);
                foreach (var command in commands)
                {
                    if (ToolSearchSkippedLabels.Contains(command.Key)) continue;
                    KsCap first = command.First();
                    Add(command.Key, binding.Cat, GesturesFor([.. command.Select(CapName)]),
                        () => RunShortcut(first));
                }
            }

            foreach (var (letter, labelKey) in ShortcutTable.DocumentToolKeys)
                Add(labelKey, "Docs", DocumentToolGestureText(letter, labelKey), () => RunDocumentTool(letter));

            foreach (OpenMenuCommand command in DocumentCreationCommands())
                Add(command.LabelKey, "File", "", command.Run, command.RequiresDocument);

            Add("Str_Toolbar_Customize", "View", "", OpenCustomizeToolbar);
            return entries;
        }

        private static string CapName(KsCap cap) => cap.Layer == KbLayer.Base ? cap.Id : cap.Layer + ":" + cap.Id;

        // English names are searched in every language, so "print" finds Drucken.
        private static string EnglishText(string labelKey)
        {
            if (_englishStrings is null)
            {
                try
                {
                    _englishStrings = new ResourceDictionary { Source = new Uri("pack://application:,,,/Strings/en-US.xaml") };
                }
                catch (IOException)
                {
                    _englishStrings = new ResourceDictionary();
                }
            }
            return _englishStrings[labelKey] is string english ? WithoutShortcut(english) : "";
        }

        // Runs a command from the shortcut table by handing its built-in key to the key handler.
        private void RunShortcut(KsCap cap)
        {
            // The on/off chord is answered before the handler's command chain, so it runs directly.
            if (cap.Layer == KbLayer.CtrlShift && cap.Id == "K")
            {
                SetKeyboardShortcutsEnabled(!KeyboardShortcutsEnabled);
                return;
            }
            if (ToolSearchKeyFor(cap.Id) is not Key key || PresentationSource.FromVisual(this) is not PresentationSource source)
                return;
            ModifierKeys modifiers = ModifiersFor(cap.Layer);
            bool system = modifiers.HasFlag(ModifierKeys.Alt) || key == Key.F10;
            var press = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            _syntheticShortcut = (system ? Key.System : key, modifiers, system ? key : Key.None);
            try { OnPreviewKeyDown(press); }
            finally { _syntheticShortcut = null; }
        }

        private static Key? ToolSearchKeyFor(string id) => id switch
        {
            "Del" => Key.Delete,
            "Equals" => Key.OemPlus,
            "Minus" => Key.OemMinus,
            "Slash" => Key.OemQuestion,
            "PgUp" => Key.PageUp,
            "PgDn" => Key.PageDown,
            _ => Enum.TryParse(id, out Key key) ? key : null,
        };

        // A Document tool runs the way its letter does in the Document tools panel.
        private void RunDocumentTool(char letter)
        {
            DocumentTool? tool = DocumentToolsForState().FirstOrDefault(candidate => candidate.Key == letter);
            if (tool is { Enabled: true }) tool.Run();
        }

        // Rebuilds the flyout for the box's text. An empty box lists the tools (or, with no
        // document open, the commands that work without one); a query lists the best matches,
        // grouped under the shortcut overlay's section names and colors.
        private void RefreshToolSearchResults()
        {
            if (_toolSearchList is null || _toolSearchInput is null) return;
            string query = _toolSearchInput.Text.Trim();
            List<ToolSearchEntry> shown;
            if (query.Length == 0)
            {
                shown = _doc is null
                    ? _toolSearchEntries
                    : [.. _toolSearchEntries.Where(entry => entry.Cat == "Tools")];
            }
            else
            {
                // A tool wins a tie with a command, since tools are what the box is mostly for.
                var ranked = _toolSearchEntries
                    .Select(entry => (Entry: entry, Score: CommandSearch.Score(query,
                        (entry.Name, 100), (entry.English, 85), (entry.Keywords, 70))))
                    .Where(match => match.Score > 0)
                    .Select(match => match.Entry.Cat == "Tools" ? (match.Entry, Score: match.Score + 25) : match)
                    .OrderByDescending(match => match.Score)
                    .ThenBy(match => match.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                // Once something matches well, the long tail of loose matches is noise.
                int floor = ranked.Count > 0 ? ranked[0].Score / 3 : 0;
                ranked.RemoveAll(match => match.Score < floor);
                // Groups appear in the order of their best match, a few rows each.
                shown = [.. ranked
                    .GroupBy(match => match.Entry.Cat)
                    .SelectMany(group => group.Take(ToolSearchPerGroup))
                    .Take(ToolSearchMostResults)
                    .Select(match => match.Entry)];
            }

            _toolSearchList.Children.Clear();
            _toolSearchRows.Clear();
            _toolSearchSelected = -1;
            if (shown.Count == 0)
            {
                var none = new TextBlock
                {
                    Text = Loc("Str_ToolSearch_None"),
                    FontFamily = UiKit.UiFont,
                    FontSize = 12,
                    Margin = new Thickness(8, 6, 8, 6),
                };
                none.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                _toolSearchList.Children.Add(none);
                return;
            }
            string? currentCat = null;
            foreach (ToolSearchEntry entry in shown)
            {
                if (entry.Cat != currentCat)
                {
                    _toolSearchList.Children.Add(ToolSearchHeader(entry.Cat, first: currentCat is null));
                    currentCat = entry.Cat;
                }
                _toolSearchList.Children.Add(ToolSearchRow(entry, _toolSearchRows.Count));
            }
            SelectToolSearchRow(0);
        }

        private TextBlock ToolSearchHeader(string cat, bool first)
        {
            string? titleKey = ShortcutTable.KsGroups.Where(group => group.Cat == cat)
                .Select(group => group.TitleKey).FirstOrDefault();
            var header = new TextBlock
            {
                Text = titleKey is null ? cat : Loc(titleKey),
                FontFamily = UiKit.UiFont,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, first ? 2 : 8, 8, 2),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "KsCat" + cat);
            return header;
        }

        private Border ToolSearchRow(ToolSearchEntry entry, int index)
        {
            var name = new TextBlock
            {
                Text = entry.Name,
                FontFamily = UiKit.UiFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var keys = new TextBlock
            {
                Text = entry.Gesture,
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                Margin = new Thickness(24, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            keys.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            var line = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(keys, Dock.Right);
            line.Children.Add(keys);
            line.Children.Add(name);
            var row = new Border
            {
                Padding = new Thickness(8, 5, 8, 5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = line,
            };
            row.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            AutomationProperties.SetName(row, entry.Gesture.Length > 0 ? $"{entry.Name} ({entry.Gesture})" : entry.Name);
            row.MouseEnter += (_, _) => SelectToolSearchRow(index);
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                RunToolSearchEntry(entry);
            };
            _toolSearchRows.Add((row, name, keys, entry));
            return row;
        }

        // Highlights one row the way a menu highlights its item, and keeps it in view.
        private void SelectToolSearchRow(int index)
        {
            if (_toolSearchRows.Count == 0) return;
            index = Math.Clamp(index, 0, _toolSearchRows.Count - 1);
            if (_toolSearchSelected >= 0 && _toolSearchSelected < _toolSearchRows.Count)
            {
                var (oldRow, oldName, oldKeys, _) = _toolSearchRows[_toolSearchSelected];
                oldRow.Background = Brushes.Transparent;
                oldName.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                oldKeys.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            }
            _toolSearchSelected = index;
            var (row, name, keys, _) = _toolSearchRows[index];
            row.SetResourceReference(Border.BackgroundProperty, "MenuHoverBrush");
            name.SetResourceReference(TextBlock.ForegroundProperty, "MenuHoverTextBrush");
            // The shortcut follows the hover text too, as a highlighted menu item's gesture does.
            keys.SetResourceReference(TextBlock.ForegroundProperty, "MenuHoverTextBrush");
            row.BringIntoView();
        }
    }
}
