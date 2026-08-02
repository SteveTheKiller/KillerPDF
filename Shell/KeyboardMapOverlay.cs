using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace KillerPDF
{
    // ============================================================
    // Visual keyboard for the shortcuts overlay.
    //
    // Same philosophy as ShortcutsOverlay.cs: the board is generated from the tables below (one
    // source of truth), and every brush and label is wired with SetResourceReference so theme and
    // language switches repaint live. Category colors are theme brushes (KsCat* in Themes/*.xaml),
    // keycap faces ride BgPanel / BorderDim / TextPrimary - the board always looks native to the
    // active theme. Holding a real Ctrl / Shift / Alt previews that layer, like the key will work.
    // ============================================================
    public partial class MainWindow
    {
        private enum KbLayer { Base, Ctrl, CtrlShift, Shift, Alt }

        private KbLayer _kbLayer = KbLayer.Base;
        private bool _kbBuilt;
        private TextBlock? _kbDetail;
        private TextBlock? _kbHoverAct;   // caption of the key under the mouse (marquee restart on layer switch)
        private string? _kbHoverId;
        private readonly Dictionary<string, (Border Cap, TextBlock Act, Rectangle Bar)> _kbKeys = new();
        private readonly Dictionary<KbLayer, Button> _kbLayerBtns = new();

        private const string KsViewSetting = "ShortcutView";   // "list" (default) | "keyboard"

        // ── Binding tables ─────────────────────────────────────────────────────────────────────
        // key id -> (category brush suffix, localized label resource key). Categories map 1:1 to
        // the KsCat* theme brushes and to the overlay's section title keys for the hover detail.
        private static readonly Dictionary<KbLayer, Dictionary<string, (string Cat, string Label)>> KbMap = new()
        {
            [KbLayer.Base] = new()
            {
                ["F1"] = ("Help", "Str_KS_ThisList"),   ["F2"] = ("Edit", "Str_Ctx_BmRename"),
                ["F3"] = ("Search", "Str_Kb_NextResult"), ["F4"] = ("File", "Str_KS_DocInfo"),
                ["F5"] = ("View", "Str_View_Continuous"), ["F6"] = ("View", "Str_View_Single"),
                ["F7"] = ("View", "Str_View_TwoPage"),  ["F8"] = ("View", "Str_View_Grid"),
                ["F9"] = ("View", "Str_KS_CycleView"),  ["F10"] = ("View", "Str_KS_SplitPane"),
                ["F11"] = ("View", "Str_KS_FullScreen"),
                ["F12"] = ("Help", "Str_KS_About"),
                ["D1"] = ("Tools", "Str_Lbl_Text"),     ["D2"] = ("Tools", "Str_Lbl_Highlight"),
                ["D3"] = ("Tools", "Str_Lbl_Line"),     ["D4"] = ("Tools", "Str_Lbl_Shape"),
                ["D5"] = ("Tools", "Str_Lbl_Draw"),     ["D6"] = ("Tools", "Str_Lbl_Image"),
                ["D7"] = ("Tools", "Str_Lbl_Signature"),["D8"] = ("Tools", "Str_Lbl_Crop"),
                ["D9"] = ("Tools", "Str_Lbl_Rotate"),   ["D0"] = ("Tools", "Str_TT_StampTool"),
                ["V"] = ("Tools", "Str_Lbl_Select"),    ["T"] = ("Tools", "Str_Lbl_Text"),
                ["L"] = ("Tools", "Str_Lbl_Line"),      ["U"] = ("Tools", "Str_Lbl_Line"),
                ["H"] = ("Tools", "Str_Lbl_Highlight"), ["D"] = ("Tools", "Str_Lbl_Draw"),
                ["I"] = ("Tools", "Str_Lbl_Image"),     ["G"] = ("Tools", "Str_Lbl_Signature"),
                ["C"] = ("Tools", "Str_Lbl_Crop"),      ["R"] = ("Tools", "Str_Lbl_Rotate"),
                ["S"] = ("Tools", "Str_TT_StampTool"),
                ["N"] = ("View", "Str_DocInvertSetting"),
                ["Home"] = ("Nav", "Str_Kb_FirstPage"), ["End"] = ("Nav", "Str_Kb_LastPage"),
                ["PgUp"] = ("Nav", "Str_Kb_PrevPage"),  ["PgDn"] = ("Nav", "Str_Kb_NextPage"),
                ["Left"] = ("Nav", "Str_Kb_PrevPage"),  ["Right"] = ("Nav", "Str_Kb_NextPage"),
                ["Up"] = ("Nav", "Str_KS_ScrollView"),  ["Down"] = ("Nav", "Str_KS_ScrollView"),
                ["Del"] = ("Edit", "Str_KS_DeleteAnnot"),
                ["Enter"] = ("Edit", "Str_Kb_Confirm"), ["Esc"] = ("Edit", "Str_Kb_Cancel"),
                ["Space"] = ("Nav", "Str_KS_PanView"),  ["Menu"] = ("Edit", "Str_KS_ContextMenu"),
            },
            [KbLayer.Ctrl] = new()
            {
                ["O"] = ("File", "Str_KS_Open"),        ["S"] = ("File", "Str_Lbl_Save"),
                ["W"] = ("File", "Str_KS_CloseFile"),   ["Q"] = ("File", "Str_KS_CloseAll"),
                ["N"] = ("File", "Str_KS_NewBlank"),    ["P"] = ("File", "Str_KS_Print"),
                ["D"] = ("File", "Str_KS_DocInfo"),
                ["F"] = ("Search", "Str_KS_Find"),      ["Z"] = ("Edit", "Str_KS_Undo"),
                ["Y"] = ("Edit", "Str_Ctx_Redo"),       ["C"] = ("Edit", "Str_KS_CopyText"),
                ["V"] = ("Edit", "Str_KS_Paste"),       ["A"] = ("Search", "Str_KS_SelectAll"),
                ["B"] = ("Nav", "Str_KS_ToggleSidebar"),["Tab"] = ("Nav", "Str_KS_NextTab"),
                ["Slash"] = ("Help", "Str_KS_ThisList"),
                ["D0"] = ("View", "Str_KS_ResetZoom"),  ["D1"] = ("View", "Str_Zoom_ActualSize"),
                ["D2"] = ("View", "Str_Zoom_FitWidth"), ["D3"] = ("View", "Str_Zoom_FitPage"),
                ["Equals"] = ("View", "Str_Lbl_ZoomIn"), ["Minus"] = ("View", "Str_Lbl_ZoomOut"),
                ["I"] = ("Edit", "Str_Lbl_Italic"),     ["U"] = ("Edit", "Str_Lbl_Underline"),
            },
            [KbLayer.CtrlShift] = new()
            {
                ["S"] = ("File", "Str_KS_SaveAs"),      ["O"] = ("Ocr", "Str_Ctx_OcrPage"),
                ["I"] = ("Ocr", "Str_Ocr_Region"),      ["Z"] = ("Edit", "Str_Ctx_Redo"),
                ["W"] = ("File", "Str_KS_CloseOthers"),
                ["Tab"] = ("Nav", "Str_KS_PrevTab"),    ["B"] = ("Nav", "Str_KS_SidebarSide"),
                ["Equals"] = ("View", "Str_KS_AppSize"), ["Minus"] = ("View", "Str_KS_AppSize"),
                ["D0"] = ("View", "Str_KS_AppSize"),
                // The toolbar appearance six, mirroring the bar's right-click menu top to bottom.
                ["D1"] = ("View", "Str_Toolbar_SmallIcons"), ["D2"] = ("View", "Str_Toolbar_LargeIcons"),
                ["D3"] = ("View", "Str_Toolbar_TextNone"),   ["D4"] = ("View", "Str_Toolbar_TextBeside"),
                ["D5"] = ("View", "Str_Toolbar_TextUnder"),  ["D6"] = ("View", "Str_Toolbar_TextOnly"),
            },
            [KbLayer.Shift] = new()
            {
                ["F3"] = ("Search", "Str_Kb_PrevResult"), ["F10"] = ("Edit", "Str_KS_ContextMenu"),
                ["Enter"] = ("Search", "Str_Kb_PrevResult"),
                ["N"] = ("View", "Str_InvertImagesToo"),
            },
            [KbLayer.Alt] = new()
            {
                ["Left"] = ("Nav", "Str_Kb_Back"), ["Right"] = ("Nav", "Str_Kb_Forward"),
            },
        };

        // ── Physical layout ────────────────────────────────────────────────────────────────────
        // (id, cap text, width units). id "" = spacer. Numpad omitted (digits mirror the number row).
        private static readonly (string Id, string Cap, double W)[][] KbRows =
        [
            [("Esc","Esc",1), ("","",0.8), ("F1","F1",1),("F2","F2",1),("F3","F3",1),("F4","F4",1), ("","",0.6),
             ("F5","F5",1),("F6","F6",1),("F7","F7",1),("F8","F8",1), ("","",0.6),
             ("F9","F9",1),("F10","F10",1),("F11","F11",1),("F12","F12",1)],
            [("Grave","`",1),("D1","1",1),("D2","2",1),("D3","3",1),("D4","4",1),("D5","5",1),("D6","6",1),
             ("D7","7",1),("D8","8",1),("D9","9",1),("D0","0",1),("Minus","-",1),("Equals","=",1),("Back","\u232B",2),
             ("","",0.6), ("Ins","Ins",1),("Home","Home",1),("PgUp","PgUp",1)],
            [("Tab","Tab",1.5),("Q","Q",1),("W","W",1),("E","E",1),("R","R",1),("T","T",1),("Y","Y",1),("U","U",1),
             ("I","I",1),("O","O",1),("P","P",1),("LBr","[",1),("RBr","]",1),("Bslash","\\",1.5),
             ("","",0.6), ("Del","Del",1),("End","End",1),("PgDn","PgDn",1)],
            [("Caps","Caps",1.8),("A","A",1),("S","S",1),("D","D",1),("F","F",1),("G","G",1),("H","H",1),("J","J",1),
             ("K","K",1),("L","L",1),("Semi",";",1),("Quote","'",1),("Enter","Enter",2.2)],
            [("Shift","Shift",2.3),("Z","Z",1),("X","X",1),("C","C",1),("V","V",1),("B","B",1),("N","N",1),("M","M",1),
             ("Comma",",",1),("Period",".",1),("Slash","/",1),("RShift","Shift",2.7),
             ("","",1.6), ("Up","\u2191",1)],
            [("Ctrl","Ctrl",1.5),("Win","Win",1.2),("Alt","Alt",1.5),("Space","",6.8),("RAlt","Alt",1.5),("Menu","\u2630",1),("RCtrl","Ctrl",1.5),
             ("","",0.6), ("Left","\u2190",1),("Down","\u2193",1),("Right","\u2192",1)],
        ];

        private static readonly (KbLayer Layer, string Caption)[] KbLayerButtons =
        [
            (KbLayer.Base, "BASE"), (KbLayer.Ctrl, "CTRL"), (KbLayer.CtrlShift, "CTRL+SHIFT"),
            (KbLayer.Shift, "SHIFT"), (KbLayer.Alt, "ALT"),
        ];

        // Modifier keycaps that light up per layer (they define it rather than carry a binding).
        private static readonly Dictionary<KbLayer, string[]> KbLayerMods = new()
        {
            [KbLayer.Base] = [], [KbLayer.Ctrl] = ["Ctrl", "RCtrl"],
            [KbLayer.CtrlShift] = ["Ctrl", "RCtrl", "Shift", "RShift"],
            [KbLayer.Shift] = ["Shift", "RShift"], [KbLayer.Alt] = ["Alt", "RAlt"],
        };

        private static string KbSectionKeyFor(string cat) => cat switch
        {
            "File" => "Str_KS_File", "Tools" => "Str_KS_Tools", "Edit" => "Str_KS_Editing",
            "Nav" => "Str_KS_Navigation", "View" => "Str_KS_View", "Search" => "Str_KS_SearchSelect",
            "Help" => "Str_KS_Help", _ => "Str_KS_Ocr",
        };

        // ── View toggle (LIST / KEYBOARD) ──────────────────────────────────────────────────────

        private void KsViewList_Click(object sender, RoutedEventArgs e) => ApplyShortcutView(keyboard: false, persist: true);
        private void KsViewKeyboard_Click(object sender, RoutedEventArgs e) => ApplyShortcutView(keyboard: true, persist: true);

        /// <summary>Shows the list or the keyboard inside the shortcuts overlay card. Called on
        /// every overlay open with the persisted choice, and by the two toggle captions.</summary>
        private void ApplyShortcutView(bool keyboard, bool persist = false)
        {
            if (keyboard && !_kbBuilt) BuildKeyboardView();
            ShortcutListHost.Visibility     = keyboard ? Visibility.Collapsed : Visibility.Visible;
            ShortcutKeyboardHost.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
            ShortcutCardGrid.MaxWidth       = keyboard ? 1080 : 640;
            KsViewListBtn.SetResourceReference(ForegroundProperty, keyboard ? "MutedTextBrush" : "PrimaryBrush");
            KsViewKeyboardBtn.SetResourceReference(ForegroundProperty, keyboard ? "PrimaryBrush" : "MutedTextBrush");
            if (keyboard) SetKbLayer(KbLayer.Base);
            if (persist) App.SetSetting(KsViewSetting, keyboard ? "keyboard" : "list");
        }

        private void ApplyPersistedShortcutView() =>
            ApplyShortcutView(App.GetSetting(KsViewSetting) == "keyboard");

        // ── Board construction (once, lazily) ──────────────────────────────────────────────────

        private void BuildKeyboardView()
        {
            _kbBuilt = true;
            var host = ShortcutKeyboardHost;
            host.Children.Clear();
            _kbKeys.Clear();
            _kbLayerBtns.Clear();

            // Layer captions row.
            var layerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var (layer, caption) in KbLayerButtons)
            {
                var b = new Button
                {
                    Content = caption, FontFamily = UiKit.MonoFont, FontSize = 11,
                    Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0),
                    BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                    FocusVisualStyle = null, Template = UiKit.ButtonTemplate(),   // no stock hover chrome
                };
                b.SetResourceReference(BackgroundProperty, "PaneBrush");
                b.SetResourceReference(ForegroundProperty, "MutedTextBrush");
                b.SetResourceReference(BorderBrushProperty, "CardBorderBrush");
                var l = layer;
                b.Click += (_, _2) => SetKbLayer(l);
                _kbLayerBtns[layer] = b;
                layerRow.Children.Add(b);
            }
            var hint = new TextBlock
            {
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            };
            hint.SetResourceReference(TextBlock.TextProperty, "Str_KS_HoldHint");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            layerRow.Children.Add(hint);
            host.Children.Add(layerRow);

            // The board. A DownOnly Viewbox keeps it fitting smaller windows without scrollbars.
            const double U = 46;   // one key unit incl. its 4px gap
            var board = new StackPanel();
            foreach (var row in KbRows)
            {
                var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                foreach (var (id, cap, w) in row)
                {
                    if (id.Length == 0) { r.Children.Add(new Border { Width = U * w }); continue; }
                    var capText = new TextBlock
                    {
                        Text = cap, FontFamily = UiKit.MonoFont,   // symbols render via font fallback
                        FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0),
                    };
                    capText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    var act = new TextBlock
                    {
                        FontSize = 8.5, HorizontalAlignment = HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed,
                        RenderTransform = new TranslateTransform(),
                    };
                    var actHost = new Border   // clips the caption so it can marquee on hover
                    {
                        ClipToBounds = true, VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(2, 0, 2, 5), Child = act,
                    };
                    var bar = new Rectangle
                    {
                        Height = 3, VerticalAlignment = VerticalAlignment.Bottom, RadiusX = 1.5, RadiusY = 1.5,
                        Margin = new Thickness(3, 0, 3, 0), Visibility = Visibility.Collapsed,
                    };
                    var inner = new Grid();
                    inner.Children.Add(capText);
                    inner.Children.Add(actHost);
                    inner.Children.Add(bar);
                    var key = new Border
                    {
                        Width = U * w - 4, Height = 44, CornerRadius = new CornerRadius(4),
                        BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 4, 0),
                        Child = inner,
                    };
                    key.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
                    key.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    // Hover: the keycap lifts a few pixels, like the cards on the killertools.net front page.
                    var lift = new TranslateTransform();
                    key.RenderTransform = lift;
                    string keyId = id;
                    key.MouseEnter += (_, _2) =>
                    {
                        _kbHoverAct = act; _kbHoverId = keyId;
                        KbShowDetail(keyId);
                        if (KbMap[_kbLayer].ContainsKey(keyId))   // only keys with a binding lift; dummies stay put
                        {
                            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, TimeSpan.FromMilliseconds(90)));
                            KbMarqueeStart(act);   // a cut-off caption scrolls, marquee-style
                        }
                    };
                    key.MouseLeave += (_, _2) =>
                    {
                        _kbHoverAct = null; _kbHoverId = null;
                        if (_kbDetail is not null) _kbDetail.Text = " ";
                        lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(130)));
                        KbMarqueeStop(act);
                    };
                    _kbKeys[id] = (key, act, bar);
                    r.Children.Add(key);
                }
                board.Children.Add(r);
            }
            host.Children.Add(new Viewbox
            {
                Child = board, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            _kbDetail = new TextBlock
            {
                Text = " ", FontFamily = UiKit.MonoFont, FontSize = 12.5,
                Margin = new Thickness(2, 10, 0, 0), Height = 18,
            };
            _kbDetail.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
            host.Children.Add(_kbDetail);
        }

        private void KbShowDetail(string id)
        {
            if (_kbDetail is null) return;
            if (KbMap[_kbLayer].TryGetValue(id, out var b))
            {
                string section = TryFindResource(KbSectionKeyFor(b.Cat)) as string ?? b.Cat;
                string label = TryFindResource(b.Label) as string ?? b.Label;
                _kbDetail.Text = $"{section} :: {label}";
            }
            else _kbDetail.Text = " ";
        }

        // ── Caption marquee (hover a lit key whose caption is cut off) ─────────────────────────

        /// <summary>Scrolls a truncated caption back and forth inside its clipped host while the
        /// key is hovered. No-op when the full text already fits.</summary>
        private void KbMarqueeStart(TextBlock act)
        {
            if (act.Visibility != Visibility.Visible || act.Parent is not Border host) return;
            // Measure with a probe TextBlock, NOT FormattedText: the probe inherits the same
            // text formatting mode as the live control, so its width matches what actually
            // renders. FormattedText measures Ideal-mode metrics and under-reports by a couple
            // of pixels, which made barely-trimmed captions ("Signature") never scroll.
            var probe = new TextBlock
            {
                Text = act.Text, FontFamily = act.FontFamily, FontSize = act.FontSize,
                FontStyle = act.FontStyle, FontWeight = act.FontWeight, FontStretch = act.FontStretch,
            };
            TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(act));
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double over = probe.DesiredSize.Width - host.ActualWidth;
            if (over <= 0.5) return;
            // Reparent the caption into a Canvas for the ride. A Canvas measures children with
            // INFINITE space, so the TextBlock escapes WPF's layout clip and renders the whole
            // caption; the host border clips the viewport. (Arranged directly in the too-small
            // host, the TextBlock is clipped to its slot BEFORE the transform runs, so the
            // animation just slides a pre-cut snapshot - the "tamp Pa" bug.)
            double h = act.ActualHeight;
            act.TextTrimming = TextTrimming.None;
            host.Child = null;
            var cv = new Canvas { Height = h };
            cv.Children.Add(act);
            Canvas.SetLeft(act, 0);
            Canvas.SetTop(act, 0);
            host.Child = cv;
            var tt = (TranslateTransform)act.RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, -over, TimeSpan.FromMilliseconds(System.Math.Max(600, over * 40)))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(350) });
        }

        private void KbMarqueeStop(TextBlock act)
        {
            var tt = (TranslateTransform)act.RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
            act.TextTrimming = TextTrimming.CharacterEllipsis;
            if (act.Parent is Canvas cv && cv.Parent is Border host)
            {
                cv.Children.Clear();
                host.Child = act;   // back to the plain centered, ellipsized layout
            }
        }

        // ── Layer painting ─────────────────────────────────────────────────────────────────────

        private void SetKbLayer(KbLayer layer)
        {
            _kbLayer = layer;
            if (!_kbBuilt) return;
            var map = KbMap[layer];
            foreach (var kv in _kbKeys)   // no KeyValuePair deconstruction on net48
            {
                var vis = kv.Value;
                if (map.TryGetValue(kv.Key, out var b))
                {
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty, "KsCat" + b.Cat);
                    vis.Bar.SetResourceReference(Shape.FillProperty, "KsCat" + b.Cat);
                    vis.Bar.Visibility = Visibility.Visible;
                    vis.Act.SetResourceReference(TextBlock.TextProperty, b.Label);
                    vis.Act.SetResourceReference(TextBlock.ForegroundProperty, "KsCat" + b.Cat);
                    vis.Act.Visibility = Visibility.Visible;
                }
                else
                {
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    vis.Bar.Visibility = Visibility.Collapsed;
                    vis.Act.Visibility = Visibility.Collapsed;
                }
            }
            // Modifier caps that define the layer glow accent; the layer captions follow suit.
            string[] allMods = ["Ctrl", "RCtrl", "Shift", "RShift", "Alt", "RAlt"];
            foreach (var m in allMods)
                if (_kbKeys.TryGetValue(m, out var vis))
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty,
                        System.Array.IndexOf(KbLayerMods[layer], m) >= 0 ? "PrimaryBrush" : "CardBorderBrush");
            foreach (var kv in _kbLayerBtns)   // no KeyValuePair deconstruction on net48
            {
                kv.Value.SetResourceReference(ForegroundProperty, kv.Key == layer ? "PrimaryBrush" : "MutedTextBrush");
                kv.Value.SetResourceReference(BorderBrushProperty, kv.Key == layer ? "PrimaryBrush" : "CardBorderBrush");
            }
            // Layer changed while a key is hovered (holding Ctrl / Shift / Alt): restart that key's
            // marquee for its NEW caption - MouseEnter alone never re-fires. Deferred one layout
            // pass so the caption text and size reflect the new layer before measuring.
            if (_kbHoverAct is not null && _kbHoverId is not null)
            {
                KbMarqueeStop(_kbHoverAct);
                KbShowDetail(_kbHoverId);
                var act = _kbHoverAct;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(act, _kbHoverAct)) KbMarqueeStart(act);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>Maps the live modifier state to a layer while the keyboard view is showing -
        /// called from OnPreviewKeyDown/Up so holding Ctrl / Shift / Alt previews that layer.</summary>
        private void KbSyncLayerFromModifiers()
        {
            if (!_kbBuilt || ShortcutKeyboardHost.Visibility != Visibility.Visible) return;
            var m = Keyboard.Modifiers;
            var layer = m.HasFlag(ModifierKeys.Control) && m.HasFlag(ModifierKeys.Shift) ? KbLayer.CtrlShift
                      : m.HasFlag(ModifierKeys.Control) ? KbLayer.Ctrl
                      : m.HasFlag(ModifierKeys.Alt) ? KbLayer.Alt
                      : m.HasFlag(ModifierKeys.Shift) ? KbLayer.Shift
                      : KbLayer.Base;
            if (layer != _kbLayer) SetKbLayer(layer);
        }
    }
}
