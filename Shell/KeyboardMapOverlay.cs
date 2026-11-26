using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace KillerPDF.Shell
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
        // KbLayer, the binding table and its types live in Services/ShortcutTable.cs, which is free
        // of WPF so KillerPDF.Tests can link it.
        private KbLayer _kbLayer = KbLayer.Base;
        private bool _kbBuilt;
        private TextBlock? _kbDetail;
        private TextBlock? _kbHoverAct;   // caption of the key under the mouse (marquee restart on layer switch)
        private string? _kbHoverId;
        private readonly Dictionary<string, (Border Cap, TextBlock Act, Rectangle Bar)> _kbKeys = [];
        private readonly Dictionary<KbLayer, Button> _kbLayerBtns = [];

        private const string KsViewSetting = "ShortcutView";   // "list" (default) | "keyboard"

        // ── Binding table ──────────────────────────────────────────────────────────────────────
        // DERIVED from ShortcutTable.KsAll, the same array the list is built from, so the two views
        // cannot describe a binding differently. Adding a shortcut is a one-line edit there.
        // A double claim on one cap would silently let the last writer win, which is how Ctrl+B
        // meant two things for so long; ShortcutTableTests asserts that never happens.
        // Rebuilt through the user's custom keys (#190) when the overlay is built and after they change.
        private Dictionary<KbLayer, Dictionary<string, (string Cat, string Label)>> KbMap =
            ShortcutTable.BuildMap();

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

        private static readonly KbLayer[] KbLayerButtons =
        [
            KbLayer.Base, KbLayer.Ctrl, KbLayer.CtrlShift, KbLayer.Shift, KbLayer.Alt, KbLayer.AltShift, KbLayer.CtrlAltShift,
        ];

        private static string KbLayerCaption(KbLayer layer) => layer switch
        {
            KbLayer.Ctrl => Loc("Str_Key_Ctrl").ToUpperInvariant(),
            KbLayer.CtrlShift => $"{Loc("Str_Key_Ctrl").ToUpperInvariant()}+{Loc("Str_Key_Shift").ToUpperInvariant()}",
            KbLayer.Shift => Loc("Str_Key_Shift").ToUpperInvariant(),
            KbLayer.Alt => Loc("Str_Key_Alt").ToUpperInvariant(),
            KbLayer.AltShift => $"{Loc("Str_Key_Alt").ToUpperInvariant()}+{Loc("Str_Key_Shift").ToUpperInvariant()}",
            KbLayer.CtrlAltShift => $"{Loc("Str_Key_Ctrl").ToUpperInvariant()}+{Loc("Str_Key_Alt").ToUpperInvariant()}+{Loc("Str_Key_Shift").ToUpperInvariant()}",
            _ => Loc("Str_KS_LayerBase").ToUpperInvariant(),
        };

        private static string? KbCapResource(string id) => id switch
        {
            "Ctrl" or "RCtrl" => "Str_Key_Ctrl",
            "Alt" or "RAlt" => "Str_Key_Alt",
            "Shift" or "RShift" => "Str_Key_Shift",
            "Del" => "Str_Key_Delete",
            "Enter" => "Str_Key_Enter",
            "Esc" => "Str_Key_Esc",
            "Menu" => "Str_Key_Menu",
            "Home" => "Str_Key_Home",
            "End" => "Str_Key_End",
            "PgUp" => "Str_Key_PgUp",
            "PgDn" => "Str_Key_PgDn",
            "Tab" => "Str_Key_Tab",
            _ => null,
        };

        // Modifier keycaps that light up per layer (they define it rather than carry a binding).
        private static readonly Dictionary<KbLayer, string[]> KbLayerMods = new()
        {
            [KbLayer.Base] = [], [KbLayer.Ctrl] = ["Ctrl", "RCtrl"],
            [KbLayer.CtrlShift] = ["Ctrl", "RCtrl", "Shift", "RShift"],
            [KbLayer.Shift] = ["Shift", "RShift"], [KbLayer.Alt] = ["Alt", "RAlt"],
            [KbLayer.AltShift] = ["Alt", "RAlt", "Shift", "RShift"],
            [KbLayer.CtrlAltShift] = ["Ctrl", "RCtrl", "Alt", "RAlt", "Shift", "RShift"],
        };

        private static string KbSectionKeyFor(string cat) => ShortcutTable.SectionTitleKey(cat);

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
            // Both views use the full card width, and the card grows with the window. A fixed card left the
            // list's German descriptions wrapping to one or two words per line (#508).
            SizeShortcutCard();
            KsViewListBtn.SetResourceReference(ForegroundProperty, keyboard ? "MutedTextBrush" : "PrimaryBrush");
            KsViewKeyboardBtn.SetResourceReference(ForegroundProperty, keyboard ? "PrimaryBrush" : "MutedTextBrush");
            if (keyboard) SetKbLayer(KbLayer.Base);
            if (persist) App.SetSetting(KsViewSetting, keyboard ? "keyboard" : "list");
        }

        private void SizeShortcutCard()
        {
            var (w, h) = Services.ShortcutCardSize.Caps(ShortcutsOverlay.ActualWidth, ShortcutsOverlay.ActualHeight);
            ShortcutCardGrid.MaxWidth  = w;
            ShortcutCardGrid.MaxHeight = h;
        }

        private void ShortcutsOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => SizeShortcutCard();

        // The card's content clips to the card's rounded inside (its corner less the 1px border), so the
        // list's scroll fade follows the corners. A square theme clips square.
        private void ShortcutCardContent_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not FrameworkElement content) return;
            double radius = TryFindResource("PanelCornerRadius") is CornerRadius corner ? Math.Max(0, corner.TopLeft - 1) : 0;
            content.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
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

            // Layer captions row. It wraps, so seven layers with long key names (Strg+Alt+Umschalt) never run
            // past the card.
            var layerRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var layer in KbLayerButtons)
            {
                // Use the shared command-button factory. In 98SE this supplies the raised two-tone
                // face and inverted pressed bevel; the old hand-built flat template bypassed it.
                var b = UiKit.Make(KbLayerCaption(layer), accent: false);
                b.SetResourceReference(FontFamilyProperty, "UiFont");
                b.FontSize = 11;
                b.Padding = new Thickness(10, 4, 10, 4);
                b.Margin = new Thickness(0, 0, 8, 0);
                b.BorderThickness = new Thickness(1);
                b.FocusVisualStyle = null;
                b.SetResourceReference(ForegroundProperty, "MutedTextBrush");
                b.SetResourceReference(BorderBrushProperty, "CardBorderBrush");
                var l = layer;
                b.Click += (_, _2) => SetKbLayer(l);
                _kbLayerBtns[layer] = b;
                layerRow.Children.Add(b);
            }
            host.Children.Add(layerRow);
            // The hint has its own wrapping row under the layer buttons. At the end of the button row it could
            // not wrap, so longer translations ran off the card (#508).
            var hint = new TextBlock
            {
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            };
            hint.SetResourceReference(TextBlock.TextProperty, "Str_KS_HoldHint");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            host.Children.Add(hint);

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
                        Text = cap, // symbols render via font fallback
                        FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0),
                    };
                    string? capResource = KbCapResource(id);
                    if (capResource is not null)
                        capText.SetResourceReference(TextBlock.TextProperty, capResource);
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
                        Width = U * w - 4, Height = 44, CornerRadius = new CornerRadius(0),
                        BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 4, 0),
                        Child = inner,
                    };
                    key.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
                    key.SetResourceReference(Border.BackgroundProperty, "KeyboardKeyBrush");
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
                        _kbDetail?.Text = " ";
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
                Text = " ", FontSize = 12.5,
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
        private static void KbMarqueeStart(TextBlock act)
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

        private static void KbMarqueeStop(TextBlock act)
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
            foreach (var kv in _kbKeys)
            {
                var (Cap, Act, Bar) = kv.Value;
                if (map.TryGetValue(kv.Key, out var b))
                {
                    Cap.SetResourceReference(Border.BorderBrushProperty, "KsCat" + b.Cat);
                    Bar.SetResourceReference(Shape.FillProperty, "KsCat" + b.Cat);
                    Bar.Visibility = Visibility.Visible;
                    Act.SetResourceReference(TextBlock.TextProperty, b.Label);
                    // The border and category bar carry the category color. Keep the action text on
                    // the theme's guaranteed readable foreground, since several neon families have
                    // one category color that is too dark against their keyboard face (#230).
                    Act.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    Act.Visibility = Visibility.Visible;
                }
                else
                {
                    Cap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    Bar.Visibility = Visibility.Collapsed;
                    Act.Visibility = Visibility.Collapsed;
                }
            }
            // Modifier caps that define the layer glow accent; the layer captions follow suit.
            string[] allMods = ["Ctrl", "RCtrl", "Shift", "RShift", "Alt", "RAlt"];
            foreach (var m in allMods)
                if (_kbKeys.TryGetValue(m, out var vis))
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty,
                        System.Array.IndexOf(KbLayerMods[layer], m) >= 0 ? "PrimaryBrush" : "CardBorderBrush");
            foreach (var kv in _kbLayerBtns)
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
            var layer = m.HasFlag(ModifierKeys.Control) && m.HasFlag(ModifierKeys.Alt) && m.HasFlag(ModifierKeys.Shift) ? KbLayer.CtrlAltShift
                      : m.HasFlag(ModifierKeys.Control) && m.HasFlag(ModifierKeys.Shift) ? KbLayer.CtrlShift
                      : m.HasFlag(ModifierKeys.Control) ? KbLayer.Ctrl
                      : m.HasFlag(ModifierKeys.Alt) && m.HasFlag(ModifierKeys.Shift) ? KbLayer.AltShift
                      : m.HasFlag(ModifierKeys.Alt) ? KbLayer.Alt
                      : m.HasFlag(ModifierKeys.Shift) ? KbLayer.Shift
                      : KbLayer.Base;
            if (layer != _kbLayer) SetKbLayer(layer);
        }
    }
}
