using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

// The family theme engine. Identical in every app: an app sets its defaults and its own tokens
// through SetDefaults, AppTokens and AppDerivedTokens at startup, and never edits this file.
// Promoted from KillerShell, the family reference for theme colors, grain and geometry.
namespace KillerPDF.Services
{
    // Thirteen palettes, in picker order. SE98 cannot be written "98SE" because an enum member
    // may not start with a digit; ThemeFileName below maps it back to the real file and accent
    // folder name. Order matches KillerNotes, which is the reference for the theme set.
    public enum Theme
    {
        Dark, Light, Black, SE98, Blood, Greed, Cyanotic, Ectoplasm, Decay,
        Mourning, Sepulchre, Delirium, Malaise
    }
    public enum Accent { Green, Red, Blue, Purple, Orange, Teal, Yellow, Magenta }

    /// <summary>
    /// Swaps the app-owned palette dictionary (MergedDictionaries[0]) in
    /// place at runtime; control styles bind brushes via DynamicResource so an in-place per-key
    /// update repaints everything. Persistence is pluggable (wire GetSetting/SetSetting at startup).
    /// Requires a complete Themes/{Theme}.xaml dictionary at [0].
    /// </summary>
    public static class ThemeManager
    {
        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>Killer Sans UI (Killer Sans scaled to Segoe UI proportions) with the Segoe fallback stack.</summary>
        private static readonly string AssemblyName = typeof(ThemeManager).Assembly.GetName().Name!;
        internal static readonly FontFamily KillerSansUiStack = new(new Uri($"pack://application:,,,/{AssemblyName};component/"), "./Fonts/#Killer Sans UI, Segoe UI, Microsoft JhengHei UI, Nirmala UI");
        internal static readonly FontFamily KillerSansFont = new(new Uri("pack://application:,,,/"), "./Fonts/#Killer Sans");
        internal static readonly FontFamily LacquerHeadingStack = new(new Uri("pack://application:,,,/"), "./Fonts/#Lacquer, ./Fonts/#Killer Sans UI, Segoe UI, Microsoft JhengHei UI, Nirmala UI");
        /// <summary>
        /// Read a corner radius out of the live theme. For code-built controls, which cannot use a
        /// DynamicResource and so had their radii as C# literals - the exact bug that kept the
        /// panes, tabs and status bar rounded on 98SE however many times the markup was zeroed.
        /// Accepts either a CornerRadius token or a bare Double, and falls back to the literal the
        /// caller used to carry so no theme moves if a key is missing.
        /// </summary>
        public static double Radius(string key, double fallback)
        {
            var v = System.Windows.Application.Current?.TryFindResource(key);
            if (v is System.Windows.CornerRadius cr) return cr.TopLeft;
            if (v is double d) return d;
            return fallback;
        }

        internal static Color LightTitleBarColor(Brush? titleBar, Color fallback)
        {
            if (titleBar is not LinearGradientBrush gradient || gradient.GradientStops.Count == 0)
                return fallback;

            GradientStop light = gradient.GradientStops[0];
            foreach (GradientStop stop in gradient.GradientStops)
                if (stop.Offset > light.Offset) light = stop;
            return light.Color;
        }

        public static Func<string, string?> GetSetting { get; set; } = _ => null;
        public static Action<string, string> SetSetting { get; set; } = (_, _) => { };

        /// <summary>
        /// The app's own tokens, for keys only that app's views use. Called with the palette and
        /// accent merged and BEFORE the family contract below, so an app value wins over a family
        /// default; a theme file's own value has already been read and should be left alone
        /// (use <see cref="SetIfAbsent"/>).
        /// </summary>
        public static Action<ResourceDictionary, Theme, Accent>? AppTokens { get; set; }

        /// <summary>The app's tokens that are DERIVED from family tokens (a terminal surface that
        /// follows ListPaneBrush). Called after the family contract, before publishing.</summary>
        public static Action<ResourceDictionary, Theme, Accent>? AppDerivedTokens { get; set; }

        /// <summary>Sets a key only when neither the theme nor anything earlier stated it.</summary>
        public static void SetIfAbsent(ResourceDictionary dictionary, string key, object? value)
        {
            if (!dictionary.Contains(key)) dictionary[key] = value;
        }

        /// <summary>Points a key at another key's value when the theme did not state it.</summary>
        public static void Mirror(ResourceDictionary dictionary, string key, string source)
        {
            if (!dictionary.Contains(key) && dictionary.Contains(source)) dictionary[key] = dictionary[source];
        }

        // The theme and accents an app opens on before anything is saved. Each app picks the
        // pair that matches its own branding through SetDefaults; each neutral family defines its
        // own shade of an accent, so a borrowed default can open in a color the app never uses.
        private static Theme _current = Theme.Dark;
        private static Accent _darkAccent  = Accent.Blue;
        private static Accent _lightAccent = Accent.Green;
        private static Accent _blackAccent = Accent.Orange;
        private static Accent _se98Accent  = Accent.Green;

        /// <summary>Call before <see cref="Initialize"/> to set the app's first-run theme and accents.</summary>
        public static void SetDefaults(Theme theme, Accent dark, Accent light, Accent black, Accent se98)
        {
            _current = theme; _darkAccent = dark; _lightAccent = light; _blackAccent = black; _se98Accent = se98;
        }

        public static Theme Current => _current;
        public static Accent AccentChoiceFor(Theme t) => AccentFor(t);

        private static Accent AccentFor(Theme t) =>
            t == Theme.Light ? _lightAccent
            : t == Theme.Black ? _blackAccent
            : t == Theme.SE98 ? _se98Accent
            : _darkAccent;

        private static bool HasAccents(Theme t) =>
            t == Theme.Dark || t == Theme.Light || t == Theme.Black || t == Theme.SE98;

        /// <summary>True for the themes that carry an Accents/ folder, so a picker can show or hide
        /// its accent dots without repeating the list.</summary>
        public static bool SupportsAccents(Theme t) => HasAccents(t);

        /// <summary>
        /// A foreground that is actually legible on <paramref name="fill"/>. Keeps the theme's
        /// own <paramref name="preferred"/> when that already clears WCAG AA (4.5:1), and
        /// otherwise falls back to whichever pole - near-black or white - scores higher.
        /// </summary>
        private static SolidColorBrush ReadableOn(SolidColorBrush? fill, SolidColorBrush? preferred)
        {
            var white = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            if (fill == null) return preferred ?? white;
            if (preferred != null && Contrast(fill.Color, preferred.Color) >= 4.5) return preferred;

            var ink = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
            var pick = Contrast(fill.Color, ink.Color) >= Contrast(fill.Color, white.Color) ? ink : white;
            pick.Freeze();
            return pick;
        }

        /// <summary>WCAG 2.1 contrast ratio between two opaque colors, 1.0 to 21.0.</summary>
        private static double Contrast(Color a, Color b)
        {
            double la = Relative(a), lb = Relative(b);
            double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        private static double Relative(Color c)
        {
            static double Ch(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        /// <summary>File stem for a theme, and the name of its accent folder. Only SE98 differs,
        /// because an enum member cannot start with a digit but both the palette file and the
        /// accent folder are named "98SE".</summary>
        private static string ThemeFileName(Theme theme) =>
            theme == Theme.SE98 ? "98SE" : theme.ToString();

        public static event Action? ThemeChanged;

        /// <summary>Set by App before <see cref="Initialize"/> when the process is running elevated.</summary>
        /// <remarks>
        /// An admin window gets Blood, and keeps its own theme under its own key. Two reasons
        /// for a separate key rather than just forcing the palette: switching theme inside an
        /// admin window must not rewrite the theme every ordinary window uses, and an elevated
        /// process shares the same HKCU as the unelevated one, so a single key would have them
        /// fighting over it. Blood is the default rather than a lock - the point is that an
        /// admin window never comes up looking like a user-level one.
        /// </remarks>
        public static bool Elevated { get; set; }

        private static string ThemeKey => Elevated ? "ThemeAdmin" : "Theme";

        public static void Initialize()
        {
            // "98SE" is what the setting has always been written as by the other apps in the
            // family, and it is what the file is called - but it does not parse as the enum
            // member, so it is mapped by hand before TryParse gets a look at it.
            string? saved = GetSetting(ThemeKey);
            _current = saved == "98SE" ? Theme.SE98
                     : Enum.TryParse<Theme>(saved, out var t) && Enum.IsDefined(typeof(Theme), t) ? t
                     : (Elevated ? Theme.Blood : _current);

            // TryParse also accepts any number ("99"), which names no theme file; IsDefined keeps
            // a damaged setting from failing startup.
            _darkAccent  = Enum.TryParse<Accent>(GetSetting("DarkAccent"),  out var da) && Enum.IsDefined(typeof(Accent), da) ? da : _darkAccent;
            _lightAccent = Enum.TryParse<Accent>(GetSetting("LightAccent"), out var la) && Enum.IsDefined(typeof(Accent), la) ? la : _lightAccent;
            _blackAccent = Enum.TryParse<Accent>(GetSetting("BlackAccent"), out var ba) && Enum.IsDefined(typeof(Accent), ba) ? ba : _blackAccent;
            // SE98Accent is the spelling some apps once wrote.
            _se98Accent  = Enum.TryParse<Accent>(GetSetting("98SEAccent") ?? GetSetting("SE98Accent"), out var wa) && Enum.IsDefined(typeof(Accent), wa) ? wa : _se98Accent;
            LoadDict(_current);
        }

        public static void Apply(Theme theme)
        {
            _current = theme;
            // Persist the FILE name, not the enum name, so the stored value reads "98SE" the
            // same way every other app in the family writes it.
            SetSetting(ThemeKey, ThemeFileName(theme));
            LoadDict(theme);
            ThemeChanged?.Invoke();
        }

        public static void ApplyAccent(Theme family, Accent accent)
        {
            if      (family == Theme.Light) { _lightAccent = accent; SetSetting("LightAccent", accent.ToString()); }
            else if (family == Theme.Black) { _blackAccent = accent; SetSetting("BlackAccent", accent.ToString()); }
            else if (family == Theme.SE98)  { _se98Accent  = accent; SetSetting("98SEAccent",  accent.ToString()); }
            else                            { _darkAccent  = accent; SetSetting("DarkAccent",  accent.ToString()); }

            if (_current == family)
            {
                LoadDict(_current);
                ThemeChanged?.Invoke();
            }
        }

        /// <summary>Publishes a theme as MergedDictionaries[0]. The accent defaults to the one saved
        /// for that family; a check or a standalone window can pass its own.</summary>
        internal static void LoadDict(Theme theme, Accent? accentOverride = null)
        {
            var app = Application.Current;
            if (app == null) return;
            var combined = CreateDictionary(theme, accentOverride ?? AccentFor(theme));
            var merged = app.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = combined;
            else merged.Add(combined);
        }

        /// <summary>The app's first-run theme, for a standalone installer window that saves nothing.</summary>
        internal static ResourceDictionary CreateInstallerTheme() => CreateDictionary(_current, AccentFor(_current));

        /// <summary>Publishes the first-run theme for a standalone installer window, without reading
        /// or writing any saved preference. Call <see cref="SetDefaults"/> first.</summary>
        internal static void InitializeInstaller() => LoadDict(_current);

        private static ResourceDictionary CreateDictionary(Theme theme, Accent accent)
        {
            // Build the combined dictionary (base theme + accent overlay) OFF the live tree
            // first, then publish it in ONE assignment. Setting merged[0] to a brand-new
            // ResourceDictionary fires exactly one resource-invalidation pass across the whole
            // app; the old code instead did existing[key] = newDict[key] in a loop, which fires
            // a SEPARATE invalidation pass per key. With ~150+ keys in a theme dictionary (this
            // app has ANSI terminal colors, editor syntax colors, registry/event-viewer colors
            // on top of the usual chrome brushes - a lot more than a page-viewer app's palette),
            // that was 150+ passes over the whole visual tree on every single theme click - a
            // huge lag on theme switch, nowhere near KillerPDF's instant swap. Confirmed as
            // reproducing even with zero terminal/editor tabs open, which ruled out
            // RefreshTerminalThemes/RefreshEditorThemes and pointed straight at this loop.
            // ThemeFileName, never theme.ToString(): the 98SE palette and accent folder are both
            // named for the digits, which the enum member cannot be.
            string name = ThemeFileName(theme);

            var combined = new ResourceDictionary();
            var newDict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{name}.xaml") };
            foreach (object key in newDict.Keys)
                combined[key] = newDict[key];

            bool accentOwnsMenuHover = false;
            if (HasAccents(theme) && accent != Accent.Green)
            {
                string family = theme == Theme.Light ? "Light"
                              : theme == Theme.Black ? "Black"
                              : theme == Theme.SE98  ? "98SE"
                              : "Dark";
                try
                {
                    var accentDict = new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/Themes/Accents/{family}/{accent}.xaml")
                    };
                    accentOwnsMenuHover = accentDict.Contains("MenuHoverBrush");
                    foreach (object key in accentDict.Keys)
                        combined[key] = accentDict[key];
                }
                catch { /* overlay not present - base theme stands */ }
            }


            // The app's own values go in before the family contract, so they win over its defaults.
            AppTokens?.Invoke(combined, theme, accent);

            // ── 98SE chrome contract ────────────────────────────────────────────────────
            //
            // 98SE is the only FLAT theme: a hard 2px frame, raised bevels, a short Win98
            // caption. It carries 126 keys no other palette declares, and controls that bind
            // them have to resolve on all thirteen themes - so every one gets a neutral default
            // here and 98SE's own value simply wins, with no branch on the theme name anywhere.
            //
            // SetIfAbsent, never a plain assignment: the palette is read FIRST and must not be
            // overwritten. Mirror() points a token at an existing brush so a default tracks the
            // theme instead of being a dead literal.
            {
                var Transparent = new SolidColorBrush(Colors.Transparent);
                Transparent.Freeze();

                void SetIfAbsent(string key, object value)
                {
                    if (!combined.Contains(key)) combined[key] = value;
                }
                void Mirror(string key, string source)
                {
                    if (!combined.Contains(key) && combined.Contains(source))
                        combined[key] = combined[source];
                    else if (!combined.Contains(key)) combined[key] = Transparent;
                }

            Mirror("FooterBackgroundBrush", "BackgroundBrush");
            // The wordmark's bold run. Mirrors the accent, so a theme only states it when its
            // text accent cannot carry the brand color (Light/Yellow: yellow text is unreadable).
            Mirror("AccentLogo", "PrimaryBrush");
            SetIfAbsent("WordmarkAccentShadowOpacity", 0.0);   // halo behind the accent run; Light/Yellow turns it on
            Mirror("MenuSeparatorBrush", "MenuBorderBrush");

            // The keyboard map's key caps. 98SE names its own, white rather than button-face gray.
            Mirror("KeyCapBrush", "PaneBrush");

            SetIfAbsent("AboutCaptionMargin", new Thickness(0));
            // TRANSPARENT with zero thickness: no ordinary theme draws a lifted edge on its bars.
            // Mirroring PaneBorderBrush would have been harmless only because the thickness is 0,
            // which is the kind of accident that breaks the moment someone changes the thickness.
            SetIfAbsent("BarEdgeBrush", Transparent);
            SetIfAbsent("BarEdgeThickness", new Thickness(0));
            SetIfAbsent("LocationBarEdgeThickness", new Thickness(0));
            // The bar's DARK half, so a raised toolbar has both edges. Transparent/0 by default,
            // exactly like BarEdge* above, so no other theme grows one.
            SetIfAbsent("BarEdgeDarkBrush", Transparent);
            SetIfAbsent("BarEdgeDarkThickness", new Thickness(0));
            SetIfAbsent("BarPadding", new Thickness(0));
            Mirror("BarSepBrush", "PaneBorderBrush");
            SetIfAbsent("BarSepWidth", 0.0);
            SetIfAbsent("BarShadowOpacity", 0.38);
            SetIfAbsent("BevelDarkBrush", Transparent);
            SetIfAbsent("BevelDarkThickness", new Thickness(0));
            SetIfAbsent("BevelLightBrush", Transparent);
            SetIfAbsent("BevelLightThickness", new Thickness(0));
            // The second ring of the raised edge (Controls.xaml): the inner light and outer dark
            // lines that make a Win98 button's four-color bevel. Zero and transparent here, so the
            // twelve rounded themes draw exactly what they did; 98SE states #dfdfdf and black.
            SetIfAbsent("BevelInnerMargin", new Thickness(0));
            SetIfAbsent("BevelInnerLightBrush", Transparent);
            SetIfAbsent("BevelInnerLightThickness", new Thickness(0));
            SetIfAbsent("BevelOuterDarkBrush", Transparent);
            SetIfAbsent("BevelOuterDarkThickness", new Thickness(0));
            // Toolbar buttons (ViewToggleBtn, PickerViewBtn): the edge at rest and on hover, and
            // the hovered face. 0 and the ordinary hover wash here, so the twelve rounded themes
            // are unchanged; 98SE is flat at rest with a 1px raised edge on hover, as Explorer was.
            SetIfAbsent("ToolBevelLightThickness", new Thickness(0));
            SetIfAbsent("ToolBevelDarkThickness", new Thickness(0));
            SetIfAbsent("ToolHoverBevelLightThickness", new Thickness(0));
            SetIfAbsent("ToolHoverBevelDarkThickness", new Thickness(0));
            // The pane's outer radius and the nested-bar radius, as SCALARS. Tabs.cs builds its
            // corners one at a time from first/last-tab state, so it needs the number rather than
            // a ready-made CornerRadius - and it had the numbers as literals, which is why the
            // panes kept three rounded corners on 98SE no matter what the markup said.
            SetIfAbsent("PaneCornerRadiusValue", 6.0);
            SetIfAbsent("BarCornerRadiusValue", 5.0);
            // The pane card and tab-strip insets.
            SetIfAbsent("PaneOuterMargin", new Thickness(0, -1, 8, 0));
            SetIfAbsent("TabBarMargin", new Thickness(0, 0, 8, 0));
            // Keep the pane's top and side rails. Only the BOTTOM rule follows scroll position:
            // a fixed bottom line reads as the end of content while a tab is still scrolling,
            // whereas the top rail is the pane's real frame and must remain present.
            SetIfAbsent("PaneEdgeSideThickness", new Thickness(1, 1, 1, 0));
            SetIfAbsent("PaneEdgeBottomThickness", new Thickness(0, 0, 0, 1));
            // Tabs and the pane's bars are the darker menu tier; the content below them is the
            // lighter PaneBrush (ListPaneBrush). The family reads content as the main surface.
            Mirror("FocusedPaneBrush", "MenuBackgroundBrush");
            Mirror("TabActiveBrush", "MenuBackgroundBrush");
            Mirror("TabInactiveBrush", "BackgroundBrush");
            Mirror("DocumentPaneBevelTopLeftBrush", "PaneBevelDarkBrush");
            Mirror("DocumentPaneBevelBottomRightBrush", "PaneBevelLightBrush");
            Mirror("DocumentPaneBevelInnerTopLeftBrush", "PaneBevelDark2Brush");
            Mirror("DocumentPaneBevelInnerBottomRightBrush", "PaneBevelLight2Brush");
            // The ACTIVE tab's dark bevel: BevelDarkThickness with the BOTTOM dropped, so the tab
            // opens into the content below it instead of ruling a line across the join. 0 here
            // rather than a computed "BevelDarkThickness minus its bottom" - on the twelve
            // non-flat themes the bevel is zero already, so the two are the same value, and a
            // literal cannot go wrong if a theme later states an asymmetric bevel.
            SetIfAbsent("TabActiveBevelDarkThickness", new Thickness(0));
            SetIfAbsent("TabBevelLightThickness", new Thickness(0));
            // The INACTIVE tab's dark bevel. Mirrors BevelDarkThickness's default of 0, so the
            // twelve rounded themes are unaffected; a flat theme states a 1px foot instead of the
            // shared 2px, because a doubled rule under the tab strip reads as a sunken menu bar.
            SetIfAbsent("TabInactiveBevelDarkThickness", new Thickness(0));
            // The tab band's ring line when the pane is NOT the lit half of a dual pane - which in
            // a single-pane window is always. UpdatePaneFocusRing (DualPane.cs) used to hardcode
            // PaneBorderBrush here, overriding the transparent PaneEdgeBrush the markup asks for,
            // so on 98SE the ring drew a gray rule at the foot of the tab strip AND two 1px
            // verticals hanging 6px into the top of the pane - the line under the active tab and
            // the little gray line at the left of the menu bar were this one
            // element. Mirrors PaneBorderBrush by default, so the twelve rounded themes keep
            // exactly the brush they had; 98SE states it transparent.
            Mirror("TabRingIdleBrush", "PaneBorderBrush");
            // The 1px rule along the bottom of the shell/document bar (FilePane.xaml), which was a
            // hardcoded literal in the markup. Default is that literal, so nothing else moves.
            SetIfAbsent("BarUnderlineThickness", new Thickness(0, 0, 0, 1));
            // The footer row's height, applied by Chrome.cs. 24 is what MainWindow.xaml hardcoded,
            // so the twelve rounded themes are unchanged; a beveled theme states a taller one
            // because its sunken cells paint over the content instead of reserving space.
            SetIfAbsent("FooterHeight", 24.0);
            // The tab bevel's negative margin, which undoes the tab's padding so the bevel sits on
            // the tab's real edges. Default is the literal FilePane.xaml carried; a flat theme
            // trims the BOTTOM so the right-hand dark edge stops at the tab's foot instead of
            // hanging a pixel into the pane below it.
            SetIfAbsent("TabBevelMargin", new Thickness(-12, -4, -5, -5));
            // The dark right edge's own copy, so a bottom strip can shorten it alone (TabPlacement.cs).
            SetIfAbsent("TabBevelDarkMargin", combined["TabBevelMargin"]);
            // Set true per strip by TabPlacement.cs for 98SE tabs below the page.
            SetIfAbsent("TabRetroBottomEdges", false);
            SetIfAbsent("TabGrainMargin", new Thickness(-12, -4, -5, -5));
            // The folder tree's inset inside its well (MainWindow.xaml TreeFadeHost). Default is
            // the literal it replaced; a flat theme zeroes the top so the well's own fill does not
            // show above the first row as a white strip, and the scrollbar starts at the frame.
            SetIfAbsent("TreePanePadding", new Thickness(4, 6, 2, 6));
            Mirror("BgFlyout", "MenuBackgroundBrush");
            Mirror("ButtonEdgeBrush", "CardBorderBrush");
            SetIfAbsent("CaptionButtonBrush", Transparent);
            SetIfAbsent("CaptionButtonHeight", 32.0);
            SetIfAbsent("CaptionButtonMargin", new Thickness(0));
            SetIfAbsent("CaptionButtonWidth", 44.0);
            SetIfAbsent("CaptionButtonsMargin", new Thickness(0));
            Mirror("CaptionCloseBrush", "ChromeTextBrush");
            SetIfAbsent("CaptionCloseGap", new Thickness(0));
            // The close button's EXISTING hover: a red fill with white on it. Defaulting these to
            // transparent would have removed the red hover from all twelve non-flat themes the
            // moment ChromeCloseButton started binding them. 98SE states its own - a transparent
            // hover and a black glyph, because a Win98 close button reacted only to being pressed.
            // #e04444 is DangerRed's literal from Controls.xaml. It cannot be read from the
            // palette - DangerRed is a control-level resource, not a theme key, so
            // combined.Contains would always be false and the default would silently fall to
            // transparent, removing the red hover everywhere.
            if (!combined.Contains("CaptionCloseHoverBrush"))
            {
                var red = new SolidColorBrush(Color.FromRgb(0xE0, 0x44, 0x44)); red.Freeze();
                combined["CaptionCloseHoverBrush"] = red;
            }
            if (!combined.Contains("CaptionCloseHoverFgBrush"))
            {
                var white = new SolidColorBrush(Colors.White); white.Freeze();
                combined["CaptionCloseHoverFgBrush"] = white;
            }
            // The theme picker's ring, dot and label while HOVERED. Defaults to the accent so
            // nothing changes; a theme whose accent equals its hover fill (Sepulchre) overrides
            // it, or the radio vanishes into its own highlight. KillerPDF's key.
            if (!combined.Contains("RadioHoverFgBrush") && combined.Contains("PrimaryBrush"))
                combined["RadioHoverFgBrush"] = combined["PrimaryBrush"];
            // TextBrush, not ChromeTextBrush: ChromeButton drew its glyphs in TextBrush before the
            // caption tokens existed, and ChromeTextBrush is a good deal dimmer (#b6b6b6 against
            // #e0e0e0 on Dark) - mirroring that would have quietly faded the window buttons on all
            // twelve non-flat themes. 98SE states its own, so it is unaffected either way.
            Mirror("CaptionGlyphBrush", "TextBrush");
            // The elevated caption follows the accent; 98SE supplies white for its accent bar.
            // Light keeps the accent hue while ensuring it reads on the gray title bar.
            Mirror("ElevatedTagBrush", "PrimaryBrush");
            if (theme == Theme.Light
                && combined["ElevatedTagBrush"] is SolidColorBrush elevatedText
                && combined["TitleBarBrush"] is SolidColorBrush elevatedBackground)
                combined["ElevatedTagBrush"] = Frozen(ColorContrast.ReadableOn(
                    elevatedText.Color, elevatedBackground.Color, 4.5));
            // PaneBorderBrush, matching what SurfaceButton actually drew. CardBorderBrush is a
            // different tier and is #787878 on Black, which would have put a bright gray box
            // round every secondary button.
            Mirror("ChipEdgeBrush", "PaneBorderBrush");
            // SurfaceButton's EXISTING face and hover - PaneBrush filling, RowHoverBrush on hover.
            // ChipFaceBrush had been mirroring SurfaceBrush, which is a different tier and would
            // have restyled every secondary button in the app.
            Mirror("ChipFaceBrush", "PaneBrush");
            Mirror("ChipHoverBrush", "RowHoverBrush");
            SetIfAbsent("ChromeFontFamily", KillerSansUiStack);
            SetIfAbsent("DialogWordmarkVisibility", Visibility.Visible);
            SetIfAbsent("DialogPlainTitleVisibility", Visibility.Collapsed);
            SetIfAbsent("ContentPaneMargin", new Thickness(0));
            // The search card's accent outline and the search chips' outline and rounding;
            // 98SE replaces both with raised Win98 bevels.
            SetIfAbsent("SearchCardBorderThickness", new Thickness(1));
            SetIfAbsent("ModeButtonBorderThickness", new Thickness(1));
            SetIfAbsent("ModeButtonCornerRadius", new CornerRadius(4));
            // The title-bar search glyph; 98SE's 16px caption buttons need it at caption size.
            SetIfAbsent("CaptionSearchGlyphSize", 13.0);
            SetIfAbsent("CaptionSearchMargin", new Thickness(0));
            // The file and folder pickers' button row, field rows and list rows. 98SE states its
            // own tight Win98 insets; these defaults are the 16px inset the pickers always had.
            SetIfAbsent("DialogButtonsMargin", new Thickness(16, 12, 16, 16));
            SetIfAbsent("DialogContentMargin", new Thickness(0));
            SetIfAbsent("DialogOuterBorderThickness", new Thickness(1));
            SetIfAbsent("DialogTitleBarPadding", new Thickness(6, 0, 14, 0));
            SetIfAbsent("DialogFieldMargin", new Thickness(16, 12, 16, 0));
            // 10, the halo every dialog already reserved for its drop shadow. 98SE sets 0: it
            // casts no shadow, and an invisible 10px gutter would put the resize grab outside
            // the visible window.
            SetIfAbsent("DialogHaloMargin", new Thickness(10));
            // The selection shown beside the picker buttons. 98SE moves it into the status bar.
            SetIfAbsent("DialogInlineSelVisibility", Visibility.Visible);
            SetIfAbsent("DialogRowMargin", new Thickness(16, 0, 16, 0));
            SetIfAbsent("DialogStatusBarHeight", 0.0);
            SetIfAbsent("DialogStatusBarVisibility", Visibility.Collapsed);
            // 36, the caption row height every dialog already used. 98SE states its own short
            // band. Defaulting to KillerNotes' 28 would have shortened every dialog's title bar.
            //
            // This stays a DOUBLE: 98SE declares it as <sys:Double> and AboutCaptionHeight below
            // derives from it numerically, and both are assigned to FrameworkElement.Height, which
            // is a double.
            SetIfAbsent("DialogTitleBarHeight", 32.0);

            // The SAME height as a GridLength, for the one consumer that is a RowDefinition.
            //
            // A DynamicResource is assigned to its target property with NO type conversion - unlike
            // a literal XAML attribute, nothing runs a TypeConverter on the way in. So a double
            // reaching RowDefinition.Height, which is a GridLength, throws InvalidOperationException
            // out of DependencyObject.EvaluateExpression and takes the process down as an unhandled
            // XamlParseException the moment the dialog is constructed.
            //
            // That is exactly what happened: ConfirmDialog.xaml bound its caption row to
            // DialogTitleBarHeight, so clicking Install (or any other confirm) killed the app on
            // every theme, and it read as "the installer just closes the app" (2026-08-10).
            // Derived here rather than declared per theme so 98SE's own 20 is picked up too, and
            // so no palette file has to carry the same number twice in two types.
            combined["DialogTitleBarRowHeight"] = new GridLength(
                combined["DialogTitleBarHeight"] is double dialogCaptionH ? dialogCaptionH : 32.0);
            SetIfAbsent("EdgeFadeOpacity", 1.0);
            // The EDITOR's selection strength (Editing/EditorControl.cs), distinct from
            // TextSelectionOpacity which is the plain TextBox one. 98SE states 0.75 with
            // EditorSelectionOverlay true - a solid Win98 navy block with white text - where
            // every other theme keeps the translucent accent wash at 1.0.
            SetIfAbsent("EditorSelectionOpacity", 1.0);
            SetIfAbsent("EditorSelectionOverlay", false);
            // 1.0, because this scales the shadow BORDER's opacity and the effect already carries
            // its own 0.5 - the twelve non-flat themes must keep exactly the shadow they had.
            // 98SE states 0, which removes it entirely; a flat theme casts nothing.
            SetIfAbsent("FlyoutShadowOpacity", 1.0);
            Mirror("FooterBevelDarkBrush", "PaneBorderBrush");
            Mirror("FooterBevelLightBrush", "PaneBorderBrush");
            SetIfAbsent("FooterCellDarkThickness", new Thickness(0));
            SetIfAbsent("FooterCellLightThickness", new Thickness(0));
            SetIfAbsent("FooterCellMargin", new Thickness(0));
            SetIfAbsent("FooterCellPadding", new Thickness(0));
            SetIfAbsent("FooterPadding", new Thickness(0));
            SetIfAbsent("FrameInnerDarkBrush", Transparent);
            SetIfAbsent("FrameInnerDarkThickness", new Thickness(0));
            SetIfAbsent("FrameInnerLightBrush", Transparent);
            SetIfAbsent("FrameInnerLightThickness", new Thickness(0));
            SetIfAbsent("FrameInnerMargin", new Thickness(0));
            SetIfAbsent("FrameOuterDarkBrush", Transparent);
            SetIfAbsent("FrameOuterDarkThickness", new Thickness(0));
            SetIfAbsent("FrameOuterLightBrush", Transparent);
            SetIfAbsent("FrameOuterLightThickness", new Thickness(0));
            SetIfAbsent("GrainOpacity", 0.24);
            SetIfAbsent("IconShadowOpacity", 0.9);
            Mirror("InputEdgeBrush", "InputBorderBrush");
            // The surface a LIST sits on - the file listing and the terminal screen. It is the
            // pane color, the family's content surface, while the tab and the pane's bars above
            // it take the darker menu tier. The menu tier it used to take sat within a few steps
            // of the window on most themes (Dark #1e1e1e on #1c1c1c) and made every tab look like
            // bare background. 98SE states #ffffff, a Win98 sunken WHITE well; Malaise states its
            // #303638.
            Mirror("ListPaneBrush", "PaneBrush");

            // Two KillerShell-only surfaces a RETRO theme wants BLACK even when the rest of its
            // palette is light gray: the terminal screen and the Performance tab's metric tiles.
            // Both default to the surface they already used, so the other twelve are untouched;
            // 98SE states #000000 for each in its app layer (Themes/98SE.xaml).
            Mirror("TerminalBackgroundBrush", "ListPaneBrush");
            // A document surface is usually the palette's SurfaceBrush, but it is its own
            // semantic token so flat desktop themes can use a white edit well without turning
            // every gray window/control surface white too. 98SE supplies that override.
            Mirror("EditorBackgroundBrush", "SurfaceBrush");
            // Text ON a MonitorCellBrush surface (the Performance tab's tiles, info panel and
            // detail card). Plain text brushes everywhere - except a theme whose cells are a
            // different world from its page, like 98SE's black CRT readouts on a light gray app,
            // which states retro phosphor greens.
            Mirror("MonitorTextBrush", "TextBrush");
            Mirror("MonitorMutedBrush", "MutedTextBrush");
            // The accent ON a MonitorCellBrush surface: the CPU trace and headline, the
            // per-processor squares, the read and dedicated-memory series. PrimaryBrush
            // everywhere, for the same reason the two above mirror the text brushes - and 98SE
            // states its own for the same reason too: its accent is picked against light gray
            // and all but disappears on the black cells.
            Mirror("MonitorAccentBrush", "PrimaryBrush");
            // The DataGrids' alternate-row stripe (Events, Processes, Registry). RowAltBrush
            // everywhere, exactly as the style hardcoded; 98SE states a real Win98-adjacent
            // stripe - white rows with a gray that is NOT the window face gray.
            Mirror("GridRowAltBrush", "RowAltBrush");
            // The Performance tab's hover fill: RowHoverBrush everywhere - but on 98SE that is
            // the window face gray, which made a hovered black tile vanish into the window.
            Mirror("MonitorHoverBrush", "RowHoverBrush");
            // The tool tabs' content-well face (Events/Processes grid area, Registry values):
            // Transparent everywhere, so nothing changes on the ordinary themes; 98SE states
            // WHITE - a sunken client area.
            SetIfAbsent("ToolContentBrush", Transparent);
            // The Registry tree's fill. Transparent, so the tree shows the control's own
            // PaneBrush root (and its grain) and matches the value grid beside it, whose
            // ToolContentBrush face is Transparent the same way. 98SE states WHITE for both in
            // its own layer and is untouched.
            // This mirrored BackgroundBrush, which is a full-window LinearGradientBrush on five
            // themes - and a gradient painted into a narrow column re-ramps its ENTIRE sweep
            // inside that column, so the tree rendered the ramp's near end instead of the color
            // actually behind it: pink against Delirium's purple. Same recurring bug as the
            // picker side columns, the text fields and the tree chevron (BACKLOG.md).
            SetIfAbsent("ToolTreeBrush", Transparent);
            // The details pane's big filename: the family heading face everywhere, Courier New
            // on 98SE. NOT Mirror(): BrandSansFont lives in App.xaml, not in the theme
            // dictionaries, so Mirror's missing-source fallback handed every ordinary theme a
            // TRANSPARENT BRUSH as a font - "'#00FFFFFF' is not a valid value for property
            // 'FontFamily'", the Mourning theme-switch crash.
            if (!combined.Contains("DetailsNameFont"))
                combined["DetailsNameFont"] =
                    Application.Current?.TryFindResource("BrandSansFont") as System.Windows.Media.FontFamily
                    ?? new System.Windows.Media.FontFamily("Consolas");
            // The About card's content inset - 0 like the old AboutCaptionMargin the wrapper
            // borrowed; 98SE adds top room under the caption band.
            SetIfAbsent("AboutContentMargin", new Thickness(0));
            // The Performance tab's outer margins and per-tile margin - the literals they
            // replaced. 98SE collapses them to thin 2px seams so the black cells sit together
            // instead of floating in gray gutters.
            SetIfAbsent("MonitorDetailMargin",   new Thickness(4, 0, 8, 8));
            SetIfAbsent("MonitorInfoMargin",     new Thickness(8, 8, 8, 8));
            // The Storage Analyzer address row (StorageAnalyzerControl.cs). Mirrors the info
            // panel margin it used to share, so nothing moves on any ordinary theme; 98SE states
            // its own inset because there the info margin is flush.
            Mirror("StorageBarMargin", "MonitorInfoMargin");
            // The toolbar overflow popup's corner and shadow (FilePane.xaml). Defaults are the
            // literals the markup carried, so the ordinary themes are unchanged; 98SE squares it.
            SetIfAbsent("OverflowCornerRadius", new CornerRadius(4));
            SetIfAbsent("OverflowShadowOpacity", 0.5);
            SetIfAbsent("MonitorTileListMargin", new Thickness(8, 0, 4, 8));
            // The Performance cards' standard 8px: 4 around each card, so 8 between any two, and
            // 4 more inside the pane's edges (MonitorStripMargin above the panels, MonitorPanelsPadding
            // around them), so 8 there too. 98SE states its own tile margin and a 0 grid, and both
            // performance insets follow its grid so its cells still run to the pane edge.
            bool gridStated = combined.Contains("MonitorGridMargin");
            SetIfAbsent("MonitorTileMargin",     new Thickness(4));
            // The Storage Analyzer map's outer margin.
            SetIfAbsent("MonitorGridMargin", new Thickness(2, 0, 2, 5));
            SetIfAbsent("MonitorStripMargin",   gridStated ? combined["MonitorGridMargin"] : new Thickness(4, 4, 4, 0));
            SetIfAbsent("MonitorPanelsPadding", gridStated ? combined["MonitorGridMargin"] : new Thickness(4, 0, 4, 4));
            SetIfAbsent("PerformanceTabFootThickness", new Thickness(0));
            // Space reserved INSIDE the Performance tab host for its classic page bevel. The
            // ordinary themes have no bevel and no inset; 98SE reserves two physical pixels.
            SetIfAbsent("PerformanceContentInset", new Thickness(0));
            SetIfAbsent("PerformancePageFrameBrush", Transparent);
            SetIfAbsent("PerformancePageFrameDarkBrush", Transparent);
            SetIfAbsent("PerformancePageFrameLightBrush", Transparent);
            SetIfAbsent("PerformancePageFrameDarkThickness", new Thickness(0));
            SetIfAbsent("PerformancePageFrameLightThickness", new Thickness(0));
            SetIfAbsent("OverlayCardBorderThickness", new Thickness(1));
            SetIfAbsent("CheckBoxBackgroundBrush", combined["SurfaceBrush"]);
            SetIfAbsent("CheckBoxCheckedBackgroundBrush", combined["PrimaryBrush"]);
            SetIfAbsent("CheckBoxBorderBrush", combined["InputBorderBrush"]);
            SetIfAbsent("CheckBoxCheckedBorderBrush", combined["PrimaryBrush"]);
            SetIfAbsent("CheckBoxCheckBrush", combined["OnPrimaryBrush"]);
            SetIfAbsent("CheckBoxGlyphSize", 10.0);
            SetIfAbsent("CheckBoxSunkenDarkThickness", new Thickness(0));
            SetIfAbsent("CheckBoxSunkenLightThickness", new Thickness(0));
            // Tool grids retain their side insets, but no bottom gutter: when a horizontal
            // scrollbar appears it should sit against the content edge, not float 8px above it.
            // Processes already runs flush on the right. 98SE overrides both to zero throughout.
            SetIfAbsent("ToolGridMargin", new Thickness(8, 0, 8, 0));
            SetIfAbsent("ProcessGridMargin", new Thickness(8, 0, 0, 0));
            SetIfAbsent("RegSplitMargin", new Thickness(8, 0, 8, 6));
            SetIfAbsent("RegGridMargin",  new Thickness(6, 0, 0, 0));
            SetIfAbsent("RegSplitterWidth", 5.0);
            // The dual-pane FOCUSED tab's border/padding sets - exactly the literals the four
            // PaneFocused triggers in FilePane.xaml hardcoded. 98SE zeroes the thicknesses and
            // keeps the active padding: its ring brush is transparent, and even a transparent
            // 1px border kept the tab's fill out of its own edge column, which let the menu
            // bar's white top line show through as a stray white pixel.
            SetIfAbsent("TabFocusThickness",      new Thickness(1, 3, 1, 0));
            SetIfAbsent("TabFocusFirstThickness", new Thickness(0, 3, 1, 0));
            SetIfAbsent("TabFocusLastThickness",  new Thickness(1, 3, 0, 0));
            SetIfAbsent("TabFocusOnlyThickness",  new Thickness(0, 3, 0, 0));
            // Every tab padding below carries 2px more on top and 2px less below than the band
            // needs, so the icon and title sit level in the tab: the art and Killer Sans both
            // ride high in their boxes. Same total height, and the same top in every state, so
            // nothing jumps when a tab becomes active.
            SetIfAbsent("TabFocusPadding",      new Thickness(1, 3, 4, 3));
            SetIfAbsent("TabFocusFirstPadding", new Thickness(2, 3, 4, 3));
            SetIfAbsent("TabFocusLastPadding",  new Thickness(1, 3, 5, 3));
            SetIfAbsent("TabFocusOnlyPadding",  new Thickness(2, 3, 5, 3));
            Mirror("MonitorCellBrush", "MenuBackgroundBrush");

            // A terminal that overrides its BACKGROUND has to override its foreground and accent
            // too, or the text is picked for the wrong surface. 98SE is the case that proves it:
            // its TextBrush is #000000 and its PrimaryBrush #004f00, both chosen against a light
            // gray app - on the black console they are invisible and nearly invisible.
            // These default to the app's own, so the other twelve are unchanged.
            Mirror("TerminalForegroundBrush", "TextBrush");
            Mirror("TerminalAccentBrush", "PrimaryBrush");
            SetIfAbsent("MenuBevel2DarkBrush", Transparent);
            SetIfAbsent("MenuBevel2DarkThickness", new Thickness(0));
            SetIfAbsent("MenuBevel2LightBrush", Transparent);
            SetIfAbsent("MenuBevel2LightThickness", new Thickness(0));
            SetIfAbsent("MenuBevelDarkBrush", Transparent);
            SetIfAbsent("MenuBevelDarkThickness", new Thickness(0));
            SetIfAbsent("MenuBevelInnerMargin", new Thickness(0));
            SetIfAbsent("MenuBevelLightBrush", Transparent);
            SetIfAbsent("MenuBevelLightThickness", new Thickness(0));
            SetIfAbsent("MenuTextBrush", Transparent);
            // The outline button, split into the six parts a Win98 button needs. Defaults keep
            // exactly what OutlineButton already did: transparent at rest with an accent edge and
            // accent text, filling with the accent on hover under the AA-derived label color.
            // 98SE turns it into a real raised gray button - a #c0c0c0 face at rest, lighter on
            // hover, darker when pressed, black text throughout and NO accent anywhere.
            // The accent outline at rest is the family's confirm button (dialogs). Title-bar and
            // toolbar buttons that should be bare until hover use BareButton (Controls.xaml).
            SetIfAbsent("OutlineFaceBrush", Transparent);          // rest FILL
            Mirror("OutlineRestBrush", "OutlineBtnBrush");         // rest EDGE
            SetIfAbsent("OutlineLiftRestVisibility", Visibility.Visible);   // resting shadow
            Mirror("OutlineTextBrush", "OutlineBtnBrush");         // rest TEXT
            Mirror("OutlineHoverBrush", "OutlineBtnBrush");        // hover FILL
            // OutlineHoverTextBrush is set AFTER the accent overlay, next to OnOutlineBtnBrush -
            // it defaults to that value and it does not exist yet at this point in the method.
            Mirror("OutlinePressedBrush", "RowSelectedBrush");     // pressed FILL

            // Filled buttons and the outline button's hover fill carry the family's soft vertical
            // gradient (the KillerNotes treatment): the outline color itself at the top, 16% darker
            // at the bottom. 98SE and any palette that already states a gradient stay as they are.
            object ButtonFill(object brush)
            {
                if (theme == Theme.SE98 || brush is not SolidColorBrush solid) return brush;
                var c = solid.Color;
                var top = c;
                var bottom = Color.FromArgb(c.A, (byte)(c.R * 0.84), (byte)(c.G * 0.84), (byte)(c.B * 0.84));
                var gradient = new LinearGradientBrush(top, bottom, 90);
                gradient.Freeze();
                return gradient;
            }
            // A palette can fill buttons with a deeper shade than its accent text (PrimaryFillBaseBrush).
            combined["PrimaryFillBrush"] = ButtonFill(combined.Contains("PrimaryFillBaseBrush") ? combined["PrimaryFillBaseBrush"] : combined["PrimaryBrush"]);
            object outlineBase = combined.Contains("OutlineHoverFillBaseBrush") ? combined["OutlineHoverFillBaseBrush"] : combined["OutlineHoverBrush"];
            combined["OutlineHoverBrush"] = ButtonFill(outlineBase);
            // Pressed is the same accent pushed in: a flat, deeper shade of the hover fill. The old
            // OutlinePressedBrush wash read as gray against every accent.
            if (outlineBase is SolidColorBrush pressBase)
            {
                var p = pressBase.Color;
                var pressed = new SolidColorBrush(Color.FromArgb(p.A, (byte)(p.R * 0.72), (byte)(p.G * 0.72), (byte)(p.B * 0.72)));
                pressed.Freeze();
                combined["OutlinePressedFillBrush"] = pressed;
            }
            else combined["OutlinePressedFillBrush"] = combined["OutlineHoverBrush"];
            SetIfAbsent("PaneBevel2DarkThickness", new Thickness(0));
            SetIfAbsent("PaneBevel2LightThickness", new Thickness(0));
            SetIfAbsent("PaneBevelDark2Brush", Transparent);
            SetIfAbsent("PaneBevelDarkBrush", Transparent);
            SetIfAbsent("PaneBevelDarkThickness", new Thickness(0));
            SetIfAbsent("PaneBevelInnerMargin", new Thickness(0));
            SetIfAbsent("PaneBevelLight2Brush", Transparent);
            SetIfAbsent("PaneBevelLightBrush", Transparent);
            SetIfAbsent("PaneBevelLightThickness", new Thickness(0));
            Mirror("PaneEdgeBrush", "PaneBorderBrush");
            SetIfAbsent("PaneShadowOpacity", 0.6);
            // TRANSPARENT: a theme radio row has no fill of its own on any ordinary theme.
            // Mirroring SurfaceBrush would have put an opaque tile behind every option in the
            // theme and language flyouts.
            SetIfAbsent("RadioWellBrush", Transparent);
            SetIfAbsent("RowSelectedBrush", Transparent);
            SetIfAbsent("ScrollArrowSize", 0.0);
            // 12, the width the ScrollBar style already used. 98SE asks for 16, a real Win98 bar.
            SetIfAbsent("ScrollBarThickness", 12.0);
            SetIfAbsent("ScrollThumbBrush", Transparent);
            SetIfAbsent("ScrollThumbHoverBrush", Transparent);
            // 4,0 - the inset the thumb already had, which is what makes it read as a slim
            // overlay rather than filling the gutter. 98SE states 0: a Win98 thumb fills its
            // track edge to edge. The horizontal orientation trigger flips this to 0,4 and the
            // hover trigger to 2,2, both of which still override this as they always did.
            SetIfAbsent("ScrollThumbMargin", new Thickness(4, 0, 4, 0));
            // The HORIZONTAL thumb's inset - the 0,4 the template hardcoded in its orientation
            // trigger, which kept the slim-overlay squeeze on 98SE's 16px bar (the "too skinny"
            // tree scrollbar). 98SE states 0: a Win98 thumb fills its track.
            SetIfAbsent("ScrollThumbMarginH", new Thickness(0, 4, 0, 4));
            // 3, the radius the thumb already had. 98SE states 0 - a Win98 thumb is square.
            SetIfAbsent("ScrollThumbRadius", new CornerRadius(3));
            SetIfAbsent("ChartCornerRadius", new CornerRadius(4));
            // The 45-degree cut across a tab's top corners. 0 = no chamfer, and
            // TabChamferConverter returns a null Clip for 0, so the twelve rounded themes are not
            // clipped at all. Only a flat theme states a value.
            SetIfAbsent("TabChamfer", 0.0);
            // The tab's own padding: 2px from the tab's left edge to its icon. A flat theme
            // supplies its compact horizontal inset and state-aligned vertical padding.
            // Pulls the X into the tab's right padding, leaving 8px from the glyph to the edge.
            SetIfAbsent("TabCloseMargin", new Thickness(4, -4, -4, 0));
            SetIfAbsent("TabPadding", new Thickness(2, 6, 5, 3));   // 2px lower: see TabFocusPadding
            // No PageBevel* keys: the tab page reuses the PaneBevel* set the sidebar well already
            // uses, so the two recesses are the same four Borders and cannot drift apart. A
            // single-ring PageBevel* pair was tried on 2026-08-09 and rejected - a Win98 recess is
            // a DOUBLE bevel (outer #808080/#ffffff, inner #000000/#c0c0c0 at a 1px margin).
            // The details strip's own edge and its internal fields|preview divider. Defaults are
            // the literals FilePane.xaml carried (a top rule and a 1px rule); a flat theme drops
            // both so the strip is completely flat.
            SetIfAbsent("DetailsPaneBorderThickness", new Thickness(0, 1, 0, 0));
            SetIfAbsent("DetailsDividerWidth", 1.0);
            // The tab's outer margin. Default is the literal it replaced; a flat theme opens a 2px
            // gap on the right so both chamfered corners are visible against the band.
            SetIfAbsent("TabMargin", new Thickness(0, 3, 0, 1));
            SetIfAbsent("TabActiveMargin", new Thickness(0, 3, 0, 0));
            SetIfAbsent("TabInactiveFirstMargin", new Thickness(0, 3, 0, 1));
            SetIfAbsent("TabInactiveLastMargin", new Thickness(0, 3, 0, 1));
            SetIfAbsent("TabActiveFirstMargin", new Thickness(0, 3, 0, 0));
            SetIfAbsent("TabActiveLastMargin", new Thickness(0, 3, 0, 0));
            SetIfAbsent("TabActiveOnlyMargin", new Thickness(0, 3, 0, 0));
            SetIfAbsent("TabActiveOuterOutlineMargin", new Thickness(0));
            SetIfAbsent("TabInactiveOuterOutlineMargin", new Thickness(0));
            // The retro outer outline's brush and thickness (FilePane.xaml, MainWindow.xaml drag
            // ghost). Defaults are the PaneBorderBrush 1,1,1,0 cap the markup used to hardcode,
            // and the outline is collapsed on every theme but 98SE anyway, so the twelve rounded
            // themes are unchanged. 98SE states a black right-only line.
            Mirror("TabOuterOutlineBrush", "PaneBorderBrush");
            SetIfAbsent("TabOuterOutlineThickness", new Thickness(1, 1, 1, 0));
            // The dark rule at the foot of the tab band (RetroTabJoinLine). Mirrors the
            // PaneBorderBrush it used to hardcode; collapsed on every theme but 98SE.
            Mirror("RetroTabJoinLineBrush", "PaneBorderBrush");
            SetIfAbsent("TabActiveInnerBevelBrush", Transparent);
            SetIfAbsent("TabActiveInnerBevelThickness", new Thickness(0));
            SetIfAbsent("TabActiveInnerBevelMargin", new Thickness(0));
            SetIfAbsent("TabActiveBevelLightMargin", new Thickness(-12, -4, -5, -5));
            SetIfAbsent("RetroTabJoinVisibility", Visibility.Collapsed);
            SetIfAbsent("RetroActiveTabOutlineVisibility", Visibility.Collapsed);
            SetIfAbsent("TabBandHeight", double.NaN);
            // The ACTIVE tab's dark-bevel margin. Defaults to the same family literal the shared
            // TabBevelMargin uses (harmless - only a flat theme draws tab bevels at all); 98SE
            // pulls its bottom in so the dark right edge stops at the menu bar's white top line.
            SetIfAbsent("TabActiveBevelDarkMargin", new Thickness(-12, -4, -5, -5));
            SetIfAbsent("TabSeamPatchBrush", Transparent);
            // ComboBox chrome - all defaults are exactly what the template hardcoded, so the
            // ordinary themes render untouched; 98SE turns the field white, the drop arrow into
            // a raised gray Marlett-triangle button, and the list into a white well.
            // ComboFieldBrush is NOT set here - it mirrors TextFieldBrush, which is computed
            // further down, and a mirror reads its source at the line it runs on. See there.
            Mirror("ComboPopupBrush", "MenuBackgroundBrush");
            SetIfAbsent("ComboButtonBrush", Transparent);
            SetIfAbsent("ComboButtonMinWidth", 0.0);
            SetIfAbsent("ComboChevMargin", new Thickness(5, 0, 0, 0));
            SetIfAbsent("ComboChevGlyphMargin", new Thickness(0, 1, 0, 0));
            SetIfAbsent("ComboChevGlyph", "\uE70D");
            SetIfAbsent("ComboChevFont", new System.Windows.Media.FontFamily("Segoe MDL2 Assets"));
            // The footer STATUS cell's inner inset. Deliberately its own key rather than reusing
            // FooterCellPadding, which belongs to the version cell and carries a different number.
            // Default is the literal MainWindow.xaml had, so the twelve rounded themes do not move.
            SetIfAbsent("FooterStatusPadding", new Thickness(10.5, 0, 12, 0));
            SetIfAbsent("ScrollTrackBevelDark", Transparent);
            SetIfAbsent("ScrollTrackBevelLight", Transparent);
            // TRANSPARENT: the scrollbar is an overlay on every ordinary theme and has never had
            // a visible track. Mirroring BackgroundBrush would have painted a gradient strip down
            // the side of every list on the five gradient themes. 98SE states a real track.
            SetIfAbsent("ScrollTrackBrush", Transparent);
            // A text FIELD is a small recessed surface and must never carry the WINDOW's own
            // gradient: BackgroundBrush is a full-window LinearGradientBrush on five themes,
            // and a brush like that re-ramps its whole sweep inside every input box - which is
            // exactly what put a gradient in the Storage tab's target box (2026-08-09). Solid
            // themes keep exactly the color they always had (the gradient check simply fails
            // and the old mirror runs); gradient themes get a SOLID field in the gradient's
            // own starting color, so the fill still belongs to the palette. Computed HERE,
            // before SearchFieldBrush, which now mirrors it - the two always shared one value
            // and the address box had the same gradient. Derived-token order matters: a
            // mirror reads its source at this line, not later (the FlyoutCardEffect lesson).
            if (!combined.Contains("TextFieldBrush")
                && combined["BackgroundBrush"] is LinearGradientBrush bgGrad
                && bgGrad.GradientStops.Count > 0)
            {
                var solidField = new SolidColorBrush(bgGrad.GradientStops[0].Color);
                solidField.Freeze();
                combined["TextFieldBrush"] = solidField;
            }
            else Mirror("TextFieldBrush", "BackgroundBrush");
            Mirror("SearchFieldBrush", "TextFieldBrush");
            // A ComboBox closed face is a FIELD, so it takes the field brush - not
            // BackgroundBrush, which it mirrored until 2026-08-09. That put the full-window
            // gradient inside a 130px dropdown, where it re-ramped its entire sweep and painted
            // the box a color from the middle of the ramp: the Event Viewer's two pickers came
            // up as purple-to-magenta bars against the tab. Same bug as the text fields
            // directly above, and it has to be mirrored HERE, after TextFieldBrush exists.
            // Solid themes are unaffected - TextFieldBrush is BackgroundBrush there, the exact
            // value this key already had. 98SE states its own #ffffff and is untouched.
            Mirror("ComboFieldBrush", "TextFieldBrush");

            // A SOLID stand-in for the window background, for anything small that needs to read
            // as "the same color as what is behind me". BackgroundBrush itself cannot be used
            // there: it is a full-window LinearGradientBrush on five themes, and a gradient
            // painted into a 16px box re-ramps its ENTIRE sweep inside those 16 pixels, so the
            // shape comes out some arbitrary color from the middle of the ramp instead of
            // matching its surroundings. That is what made the tree's expanded chevron a gray
            // wedge instead of disappearing into the sidebar (2026-08-09), and it is the same
            // trap the picker panes and the text fields both hit.
            if (!combined.Contains("SolidBackgroundBrush"))
            {
                if (combined["BackgroundBrush"] is LinearGradientBrush wg && wg.GradientStops.Count > 0)
                {
                    var solid = new SolidColorBrush(wg.GradientStops[0].Color);
                    solid.Freeze();
                    combined["SolidBackgroundBrush"] = solid;
                }
                else Mirror("SolidBackgroundBrush", "BackgroundBrush");
            }
            // The fill behind a tree chevron, whose only job is to mask the connecting line, so
            // it must be whatever surface that particular tree sits on. SolidBackgroundBrush is
            // right for the FOLDER tree, which shows the window through it - but the Registry
            // Editor's tree sits on its control's PaneBrush, so the same fill painted a wedge of
            // window color there (pink on the gradient themes, whose first stop is nothing like
            // the pane). RegistryEditorControl overrides this key on its own TreeView's
            // Resources, which is where the template's DynamicResource lookup finds it first.
            Mirror("TreeChevronMaskBrush", "SolidBackgroundBrush");
            // TRANSPARENT, not PaneBrush: the sidebar well is a 98SE idea (a sunken white list
            // box). Every other theme lets the window show through the tree exactly as before, so
            // wiring this must not hand them an opaque fill they never had.
            SetIfAbsent("SidebarPaneBrush", Transparent);
            Mirror("FileDialogPaneBrush", "PaneBrush");
            SetIfAbsent("SidebarPanelMargin", new Thickness(0));
            // The slider's unfilled groove - InputBorderBrush, which is what ThinSlider drew.
            // Transparent would have made the track vanish on every theme.
            Mirror("SliderTrack", "InputBorderBrush");
            // TRANSPARENT: a toolbar button has no face on any ordinary theme, it is a bare glyph
            // that washes on hover. Mirroring PaneBrush would have given every one of them an
            // opaque tile. 98SE states #c0c0c0 - there, a toolbar button IS a raised button.
            SetIfAbsent("SortButtonBrush", Transparent);
            Mirror("SurfaceHoverBrush", "RowHoverBrush");
            // The toolbar buttons' hovered face (Controls.xaml ViewToggleBtn). Mirrors the wash
            // above so it reads its source AFTER it exists; 98SE states the flat button face.
            Mirror("ToolHoverFaceBrush", "SurfaceHoverBrush");
            // The selection's EXISTING values, moved into the contract rather than replaced by
            // it: DarkTextBox drew a PrimaryBrush selection at 0.3. TextFieldBrush itself is
            // computed further up (before SearchFieldBrush mirrors it), where the gradient
            // themes get a solid field instead of the window sweep.
            Mirror("TextSelectionBrush", "PrimaryBrush");
            SetIfAbsent("TextSelectionOpacity", 0.3);
            // TextBrush, not SelectionFg. The existing selection is PrimaryBrush at 0.3 opacity -
            // a translucent wash the normal text shows through - so the text color must not
            // change or every selection in the app would repaint. 98SE states white, which it
            // needs because its selection is a solid navy fill at full opacity.
            Mirror("TextSelectionTextBrush", "TextBrush");
            SetIfAbsent("TitleBarBleed", new Thickness(0));
            SetIfAbsent("TitleBarHeight", 32.0);
            // These six are the title bar's EXISTING hardcoded numbers, moved into the contract
            // rather than replaced by it. A generic default would have quietly resized the icon
            // and the wordmark on all twelve non-flat themes the moment the markup started
            // binding them; 98SE states its own and is the only theme that changes.
            SetIfAbsent("TitleBarPadding", new Thickness(14, 0, 14, 0));
            // The main window logo (icon + wordmark) sits 5px off the top-left corner, as in KillerPDF.
            // The file picker's caption close (Controls/FileDialog.xaml): the caption button's size on a
            // theme that draws a dialog caption, a compact 28x26 everywhere else, flush with the
            // caption buttons' right inset.
            bool pickerCaption = combined.Contains("UseDialogCaption") && combined["UseDialogCaption"] is bool fp && fp;
            SetIfAbsent("DialogCloseWidth", pickerCaption ? combined["CaptionButtonWidth"] : 28.0);
            SetIfAbsent("DialogCloseHeight", pickerCaption ? combined["CaptionButtonHeight"] : 26.0);
            SetIfAbsent("DialogCaptionButtonsMargin", new Thickness(0, 0,
                combined["CaptionButtonsMargin"] is Thickness pickerButtons ? pickerButtons.Right : 0, 0));
            SetIfAbsent("TitleLogoMargin", combined.Contains("UseDialogCaption") && combined["UseDialogCaption"] is bool fc && fc
                ? combined["TitleBarPadding"] : new Thickness(4, 3, 0, 0));
            // The UI face for everything that is not the app (mono) slot; a flat theme keeps its caption font.
            SetIfAbsent("UiFont", combined.Contains("UseDialogCaption") && combined["UseDialogCaption"] is bool fu && fu
                ? combined["ChromeFontFamily"] : KillerSansUiStack);
            // Primary headings (dialog headings, page and period titles): Lacquer, the app-name face of
            // the wordmark, falling back to Killer Sans UI for scripts Lacquer does not cover. 98SE keeps
            // Killer Sans. Secondary headings, the footer and the interface stay Killer Sans.
            SetIfAbsent("HeadingFont", combined.Contains("UseDialogCaption") && combined["UseDialogCaption"] is bool fh && fh
                ? new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Killer Sans") : LacquerHeadingStack);
            // KillerSearch is a product name, so it keeps Lacquer even on 98SE. The search box's own
            // section labels follow HeadingFont, except where 98SE sets them to its interface face.
            SetIfAbsent("SearchTitleFont", LacquerHeadingStack);
            SetIfAbsent("SearchSectionFont", combined["HeadingFont"]);
            SetIfAbsent("SearchSectionFontSize", 18.0);
            SetIfAbsent("SearchSectionFontWeight", FontWeights.Normal);
            SetIfAbsent("ModeButtonFontWeight", FontWeights.SemiBold);
            // The titlebar text beside the wordmark (the elevated caption) sits on the wordmark
            // baseline in Killer Sans, as KillerPDF's filename and KillerScan's subtitle do. A flat
            // theme keeps its caption font beside its plain title.
            bool flatTitle = combined.Contains("UseDialogCaption") && combined["UseDialogCaption"] is bool ft && ft;
            SetIfAbsent("FileNameFont", flatTitle ? combined["ChromeFontFamily"]
                : new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Killer Sans"));
            SetIfAbsent("FileNameFontSize", flatTitle ? 11.0 : 18.0);
            if (!combined.Contains("FileNameTransform"))
            {
                var fileNameShift = new TranslateTransform(0, flatTitle ? 0 : 3);
                fileNameShift.Freeze();
                combined["FileNameTransform"] = fileNameShift;
            }
            // The app (mono) slot: Killer Sans UI, the family UI face, over the Segoe stack. It is
            // completed here rather than in AppStyles.xaml so 98SE can state its own face; a font
            // picked in Fonts still overrides it from the top-level application dictionary.
            SetIfAbsent("MonoFont", new FontFamily(new Uri("pack://application:,,,/"),
                $"/{AssemblyName};component/Fonts/#Killer Sans UI, Segoe UI"));
            SetIfAbsent("TitleIconMargin", new Thickness(0, 0, 7, 0));
            SetIfAbsent("TitleIconSize", 27.0);
            SetIfAbsent("TitleTextMargin", new Thickness(0));
            SetIfAbsent("TitleWordmarkBoldSize", 22.52);
            SetIfAbsent("TitleWordmarkSize", 15.0);
            SetIfAbsent("UseDialogCaption", false);
            // The family's 1px app border (KillerUI: WindowEdgeThickness 1). 98SE states 0 and
            // draws its own frame. At 0 the window had no visible edge on a dark desktop.
            Mirror("WindowEdgeBrush", "AppBorderBrush");
            SetIfAbsent("WindowEdgeThickness", new Thickness(1));
            // The frame's FACE. 98SE does not declare one, and a transparent default left a
            // see-through gutter around the window where its 5,4,5,5 padding
            // is. AppBorderBrush is the right source: it is already the frame's color,
            // #c0c0c0 on 98SE - the Win98 gray the bevels are cut into. Harmless on the other
            // twelve, whose WindowFramePadding is 0, so the Border has no visible area at all.
            // NOT BackgroundBrush, which is a full-window gradient on five themes and would have
            // re-ramped behind everything.
            Mirror("WindowFrameBrush", "AppBorderBrush");
            SetIfAbsent("WindowFrameMargin", new Thickness(0));
            SetIfAbsent("WindowFramePadding", new Thickness(0));
            SetIfAbsent("WindowFrameThickness", new Thickness(0));
            // The title bar wordmark's blurred copy. Defaults to the family WordmarkShadowOpacity, the
            // same strength the dialogs and About card use. A flat theme states 0 - no emboss behind a
            // Win98 caption. Copied rather than SetIfAbsent(0.0), which would have removed the
            // emboss from all twelve non-flat themes.
            if (!combined.Contains("WordmarkEmbossOpacity"))
                combined["WordmarkEmbossOpacity"] = combined["WordmarkShadowOpacity"] ?? 0.0;

                // Derived, not defaulted: these follow from whether the theme asked for a flat
                // Win98 caption at all, so they cannot be a fixed literal.
                bool flat = combined.Contains("UseDialogCaption") && combined["UseDialogCaption"] is bool f && f;

                // A Win98 caption has no logotype in it - just the icon and the window name in
                // plain bold - so the wordmark and the plain title trade places.
                combined["WordmarkVisibility"]   = flat ? Visibility.Collapsed : Visibility.Visible;
                combined["PlainTitleVisibility"] = flat ? Visibility.Visible   : Visibility.Collapsed;

                // The resize grip. Win98 drew three beveled DIAGONAL lines in the corner, not the
                // dotted triangle the rest of the family uses. The two shapes
                // are different geometry, not a recolor, so both sit in the markup and trade
                // visibility rather than one being restyled into the other.
                combined["GripDotsVisibility"]  = flat ? Visibility.Collapsed : Visibility.Visible;
                combined["GripLinesVisibility"] = flat ? Visibility.Visible   : Visibility.Collapsed;

                // A Win98 caption button reacted only to being PRESSED, never to hover.
                if (!combined.Contains("CaptionHoverBrush"))
                    combined["CaptionHoverBrush"] = flat ? (object)Transparent : combined["OutlineHoverBrush"];

                // A 1px MDL2 stroke disappears against a gray button face at 16px.
                combined["CaptionGlyphWeight"] = flat ? FontWeights.Bold : FontWeights.Normal;

                // The About card only gets a caption band when the theme asks for one; 0 keeps
                // every other theme's card at the exact layout it already had.
                combined["AboutCaptionHeight"] = flat ? combined["DialogTitleBarHeight"] : 0.0;

                // The About card's close X, the shared OverlayCloseButton style: on a flat
                // theme a small Win98 caption button sitting inside the 20px band (16x14 with a
                // 3px gap, next to the band's 2,2,2 inset), elsewhere the 28x26 corner slot the
                // bare glyph always occupied (matching KillerNotes' card).
                combined["AboutCloseWidth"]  = flat ? 16.0 : 28.0;
                combined["AboutCloseHeight"] = flat ? 14.0 : 26.0;
                combined["AboutCloseMargin"] = flat ? new Thickness(0, 5, 5, 0) : new Thickness(0, 6, 6, 0);

                // ...and the rest of the OverlayCloseButton split (Controls.xaml): the ordinary
                // themes keep the card X's ORIGINAL look - muted U+2715, red glyph on hover, no
                // fill - and 98SE gets the black bold E8BB on the gray caption-button face with
                // no hover change at all.
                combined["AboutCloseGlyph"] = flat ? "\uE8BB" : "\u2715";
                combined["AboutCloseFont"]  = flat ? new System.Windows.Media.FontFamily("Segoe MDL2 Assets")
                                                   : new System.Windows.Media.FontFamily("Segoe UI");
                combined["AboutCloseFg"] = flat ? combined["CaptionCloseBrush"] : combined["MutedTextBrush"];
                if (flat) combined["AboutCloseHoverFg"] = combined["CaptionCloseBrush"];
                else
                {
                    var closeRed = new SolidColorBrush(Color.FromRgb(0xE0, 0x44, 0x44)); closeRed.Freeze();
                    combined["AboutCloseHoverFg"] = closeRed;
                }

                // The About wordmark's hard 1px WHITE offset copy - the Win98 chiseled
                // letterpress, only on a flat theme whose blurred shadow copy is off.
                combined["AboutEmbossOpacity"] = flat ? 1.0 : 0.0;

                // The elevation halo is an overlay ring on every ordinary theme; a flat theme
                // hides it and marks an admin window by repainting the frame's outermost ring in
                // the accent instead (Elevation.cs).
                combined["ElevationHaloVisibility"] = flat ? Visibility.Collapsed : Visibility.Visible;

                // Folder tree and places icons. An ordinary theme draws them smoothed at 20 in a
                // 16px slot. The 98SE pack is 32px pixel art, which a 20px draw smears, so a flat
                // theme draws it at 16 with nearest-neighbor sampling, the way Win98 drew small icons.
                combined["SidebarIconSize"]    = flat ? 16.0 : 20.0;
                combined["SidebarIconMargin"]  = flat ? new Thickness(0, 0, 4, 0) : new Thickness(-2, -2, 4, -2);
                combined["SidebarIconScaling"] = flat ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality;

                // Menu and flyout shadows. Win98 context menus cast a HARD solid drop shadow -
                // the one shadow this otherwise-flat theme keeps - while
                // every ordinary theme keeps exactly the soft treatment it had: the ContextMenu
                // template's 12px blur at FlyoutShadowOpacity, and the flyout cards' shared
                // CardShadowEffect.
                combined["MenuShadowOpacity"] = flat ? 1.0 : combined["FlyoutShadowOpacity"];
                if (!combined.Contains("MenuFontFamily")) combined["MenuFontFamily"] = flat ? new FontFamily("Microsoft Sans Serif") : KillerSansUiStack;
                if (!combined.Contains("MenuFontSize")) combined["MenuFontSize"] = flat ? 11.0 : 12.0;
                if (!combined.Contains("MenuItemPadding")) combined["MenuItemPadding"] = new Thickness(8, 6, 10, 6);
                if (flat)
                {
                    // BlurRadius 5, not 0: fully hard-edged read as a black slab with too hard
                    // of an edge - this keeps the
                    // Win98 offset-shadow shape with just enough softening to sit right.
                    var hard = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 5, Direction = 315, Opacity = 0.35 };
                    hard.Freeze();
                    combined["MenuShadowEffect"] = hard;
                    // Rail flyouts follow KillerNotes/KillerPDF: the 98SE card is a raised,
                    // shadowless menu. Context menus and combo dropdowns keep their hard shadow.
                    combined["FlyoutCardEffect"] = null;
                    combined["ComboPopupShadow"] = hard;
                }
                else
                {
                    // 22/4, the family flyout shadow, matching FlyoutCardEffect below. The menu
                    // template's halo is sized to exactly this; change one and the other follows.
                    var soft = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.5 };
                    soft.Freeze();
                    combined["MenuShadowEffect"] = soft;
                    // Built HERE, not read from CardShadowEffect - that key is only created
                    // further DOWN this method, so reading it stored null and every flyout card
                    // lost its shadow on the ordinary themes. Same recipe CardShadowEffect uses.
                    double cardFso = combined["FlyoutShadowOpacity"] is double cf ? cf : 1.0;
                    var cardShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.55 * cardFso };
                    cardShadow.Freeze();
                    combined["FlyoutCardEffect"] = cardShadow;
                    // The ComboBox dropdown's own shadow - the literal its template hardcoded.
                    var combo = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.55 };
                    combo.Freeze();
                    combined["ComboPopupShadow"] = combo;
                }

                // The elevated flat-theme edge uses the lighter end of the titlebar gradient.
                // Every 98SE accent overlay supplies that gradient, so the edge follows accent
                // changes without repeating six color values here.
                Color fallbackEdge = combined["PrimaryBrush"] is SolidColorBrush primary
                    ? primary.Color : Colors.Transparent;
                var lightEdge = new SolidColorBrush(
                    LightTitleBarColor(combined["TitleBarBrush"] as Brush, fallbackEdge));
                lightEdge.Freeze();
                combined["ElevationEdgeBrush"] = flat ? lightEdge : combined["WindowEdgeBrush"];
                combined["ElevationEdgeThickness"] = flat ? new Thickness(2) : combined["WindowEdgeThickness"];

                // KillerScan is the family reference for overlay-card padding. The flat theme's
                // extra 12px clears its 20px caption band; ordinary themes use the bare-card inset.
                // The bottom inset is tighter: only the one-line hint sits there.
                combined["ShortcutsContentMargin"] = flat ? new Thickness(24, 30, 24, 12)
                                                          : new Thickness(24, 18, 24, 10);
                // The Fonts and File associations cards, which pad their own sides: the same 12px
                // more at the top clears the caption band.
                combined["OverlayCardContentMargin"] = flat ? new Thickness(0, 32, 0, 20)
                                                            : new Thickness(0, 20, 0, 20);

                // The details filename's family drop shadow - null on a flat theme, which casts
                // nothing (its 98SE depth comes from the hard white emboss copy instead).
                if (flat) combined["DetailsNameEffect"] = null;
                else
                {
                    var nameShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2, Direction = 270, Opacity = 0.6 };
                    nameShadow.Freeze();
                    combined["DetailsNameEffect"] = nameShadow;
                }

                // Photo thumbnails in the icon views (TileArt): a small lift so pictures read as
                // prints sitting on the surface, distinct from the flat file-type icons around
                // them. NULL on a flat theme, same rule as every other shadow here.
                if (flat) combined["ThumbShadowEffect"] = null;
                else
                {
                    var thumbShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 3, Direction = 270, Opacity = 0.55 };
                    thumbShadow.Freeze();
                    combined["ThumbShadowEffect"] = thumbShadow;
                }

                // A ready-made pane shadow at this theme's opacity, or NULL on a flat theme.
                // Built per load and FROZEN: a DynamicResource inside a shared keyed Freezable's
                // Opacity does not reliably resolve, which is how a flat theme ended up casting a
                // full-strength shadow in KillerNotes.
                if (combined["PaneShadowOpacity"] is double pso && pso > 0)
                {
                    var paneShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 5, Direction = 270, Opacity = pso };
                    paneShadow.Freeze();
                    combined["PaneShadowEffect"] = paneShadow;
                }
                else combined["PaneShadowEffect"] = null;

                // The lighter BAR-tier shadow, for things that sit just proud of their surface -
                // the active tab, toolbars. Same rule: NULL on a flat theme, which is what makes
                // 98SE genuinely shadowless rather than shadowless-except-the-bits-I-missed.
                if (combined["BarShadowOpacity"] is double bso && bso > 0)
                {
                    var barShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 9, ShadowDepth = 0, Opacity = bso };
                    barShadow.Freeze();
                    combined["BarShadowEffect"] = barShadow;
                }
                else combined["BarShadowEffect"] = null;

                // The scrollbar thumb's HOVER treatment - the 2px shrink and the soft glow the
                // template hardcoded. On a flat theme the thumb is a raised beveled button that
                // fills its track, so shrinking it on hover pulled the fill out from under its
                // own bevel ring, and the glow put a shadow on a theme that has none anywhere
                // (there, the scrollbars should read raised, not flat). Defaults are
                // exactly the literals the template carried, so the other twelve do not move.
                if (!combined.Contains("ScrollThumbHoverMargin"))
                    combined["ScrollThumbHoverMargin"] = flat ? new Thickness(0) : new Thickness(2);
                if (flat) combined["ScrollThumbHoverEffect"] = null;
                else
                {
                    var thumbGlow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.3 };
                    thumbGlow.Freeze();
                    combined["ScrollThumbHoverEffect"] = thumbGlow;
                }

                // The heavy CARD shadow - dialogs, the About card, floating overlays. Scaled by
                // FlyoutShadowOpacity, so a flat theme gets NULL and 98SE's dialogs sit flat
                // against the window the way a Win98 dialog does.
                if (combined["FlyoutShadowOpacity"] is double fso && fso > 0)
                {
                    var cardShadow = new DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.55 * fso };
                    cardShadow.Freeze();
                    combined["CardShadowEffect"] = cardShadow;
                }
                else combined["CardShadowEffect"] = null;

                // The ACTIVE tab's accent outline runs across the top and both sides by default.
                // A flat theme gets none because a Win98 tab is identified by its bevel and join.
                // The padding compensates either way so the title never shifts on activation.
                if (!combined.Contains("TabStripeThickness"))
                    combined["TabStripeThickness"] = flat ? new Thickness(0) : new Thickness(1, 3, 1, 0);
                if (!combined.Contains("TabActivePadding"))
                    combined["TabActivePadding"] = flat ? new Thickness(12, 4, 5, 5)
                                                        : new Thickness(1, 3, 4, 3);

                // The tab's picture: larger on the rounded themes, overlapping the tab's padding by
                // a pixel top and bottom so the tab keeps its height. 98SE keeps its 20px box.
                if (!combined.Contains("TabIconSize")) combined["TabIconSize"] = flat ? 20.0 : 22.0;
                if (!combined.Contains("TabIconMargin"))
                    combined["TabIconMargin"] = flat ? new Thickness(2, 0, 3, 0) : new Thickness(1, -1, 4, -1);

                // A TAB is the panel radius on its TOP corners only - the bottom two are where it
                // joins the pane and must stay square. Derived so it tracks PanelCornerRadius and
                // squares off automatically on a flat theme rather than needing its own token.
                if (!combined.Contains("TabCornerRadius"))
                {
                    double p = combined["PanelCornerRadius"] is CornerRadius pcr2 ? pcr2.TopLeft : 6.0;
                    combined["TabCornerRadius"] = new CornerRadius(p, p, 0, 0);
                }

                // The close button's hover block follows the CARD's radius on its own corner and
                // stays square on the three interior edges.
                if (!combined.Contains("CaptionCloseCornerRadius"))
                {
                    double tr = combined["WindowCornerRadius"] is CornerRadius wcr ? wcr.TopRight : 0.0;
                    combined["CaptionCloseCornerRadius"] = new CornerRadius(0, tr, 0, 0);
                }
            }

            // OutlineButton's hover fills with OutlineBtnBrush but used to put OnPrimaryBrush on
            // top of it. Those are two different tokens, and on any theme where the outline color
            // is a MID tone they disagree badly: white on Sepulchre's #4faaa8 measures 2.75:1,
            // Delirium 2.83, Black's #00ff66 just 1.36 (the Install button in
            // the portable badge was the one that showed it). Derived rather than hand-picked per
            // theme so it cannot drift, and computed HERE, after the accent overlay, because
            // picking an accent replaces OutlineBtnBrush.
            combined["OnOutlineBtnBrush"] = ReadableOn(combined["OutlineBtnBrush"] as SolidColorBrush,
                                                       combined["OnPrimaryBrush"] as SolidColorBrush);

            // The dual-pane focus ring's brush: the active tab's side/stripe borders, the band's
            // TabEdge verticals and the lit TabBarRing all draw with it. PrimaryBrush on every
            // ordinary theme, exactly as they always did - but a theme may state its own, and 98SE
            // states Transparent, because a Win98 tab is identified by its bevel and its join to
            // the page, never by an accent ring (dual pane). Computed HERE,
            // after the accent overlay, so it follows the picked accent like OnOutlineBtnBrush.
            if (!combined.Contains("TabActiveRingBrush"))
                combined["TabActiveRingBrush"] = combined["PrimaryBrush"];

            // One pane open: the active tab's outline has no pane ring to join below it, so its
            // sides used to stop dead at the tab's foot. This copy of the ring fades from the full
            // accent at the top to clear at the bottom, so the sides melt into the tab instead.
            // A non-solid ring (98SE's is transparent anyway) is used as it is.
            if (!combined.Contains("TabActiveRingFadeBrush"))
            {
                if (combined["TabActiveRingBrush"] is SolidColorBrush ring)
                {
                    var c = ring.Color;
                    var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                    fade.GradientStops.Add(new GradientStop(c, 0));
                    fade.GradientStops.Add(new GradientStop(c, 0.3));
                    fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
                    fade.Freeze();
                    combined["TabActiveRingFadeBrush"] = fade;
                }
                else combined["TabActiveRingFadeBrush"] = combined["TabActiveRingBrush"];
            }

            // A flat-theme checkbox keeps its white sunken well when selected, so its mark uses
            // the live accent rather than the ordinary themes' on-accent foreground.
            if (combined.Contains("UseDialogCaption") && combined["UseDialogCaption"] is bool flatCheck && flatCheck)
                combined["CheckBoxCheckBrush"] = combined["PrimaryBrush"];

            // Selected DataGrid CELL text - see DarkDataGridCell: the PrimaryBrush its selected
            // trigger always set, so nothing changes off 98SE, which states white. HERE, after
            // the accent overlay, for the same reason as TabActiveRingBrush above.
            if (!combined.Contains("GridSelectedTextBrush"))
                combined["GridSelectedTextBrush"] = combined["PrimaryBrush"];

            // Highlighted ComboBox item text - same shape: the PrimaryBrush the trigger always
            // set, white on 98SE where the highlight bar is the solid navy MenuHoverBrush.
            if (!combined.Contains("ComboHighlightTextBrush"))
                combined["ComboHighlightTextBrush"] = combined["PrimaryBrush"];
            if (theme == Theme.SE98 && !accentOwnsMenuHover)
                combined["MenuHoverBrush"] = combined["PrimaryBrush"];

            // A hovered menu or list item takes the accent for its text, nudged lighter or darker
            // only as far as it takes to read at 4.5:1 on that theme's hover fill. 98SE keeps its
            // Win98 highlight text.
            combined["MenuHoverTextBrush"] =
                combined.Contains("UseDialogCaption") && combined["UseDialogCaption"] is true
                    || combined["PrimaryBrush"] is not SolidColorBrush hoverAccent
                    || combined["MenuHoverBrush"] is not SolidColorBrush hoverFill
                ? combined["ComboHighlightTextBrush"]
                : Frozen(ColorContrast.ReadableOn(hoverAccent.Color, hoverFill.Color, 4.5));

            // A dialog's caption band. TRANSPARENT by default, so the band shows the card's own
            // face and is invisible - the family look, where a dialog title blends into the
            // surface. A theme that genuinely wants a distinct caption declares UseDialogCaption
            // and gets TitleBarBrush instead. Computed HERE, after the accent overlay: reading it
            // earlier picks up the BASE TitleBarBrush, and the overlay then replaces that key
            // without touching this copy, so every dialog would keep the base theme's caption
            // color while the main window followed the accent.
            if (!combined.Contains("DialogTitleBarBrush"))
            {
                bool wantsCaption = combined.Contains("UseDialogCaption")
                                 && combined["UseDialogCaption"] is bool dc && dc;
                if (wantsCaption && combined.Contains("TitleBarBrush"))
                    combined["DialogTitleBarBrush"] = combined["TitleBarBrush"];
                else
                {
                    var clear = new SolidColorBrush(Colors.Transparent); clear.Freeze();
                    combined["DialogTitleBarBrush"] = clear;
                }
            }

            // The outline button's hover TEXT defaults to that AA-derived color. Set here rather
            // than in the contract block above, because OnOutlineBtnBrush does not exist until
            // this line - a theme that states its own OutlineHoverTextBrush (98SE wants plain
            // black) still wins, since this only fills a gap.
            if (!combined.Contains("OutlineHoverTextBrush"))
                combined["OutlineHoverTextBrush"] = combined["OnOutlineBtnBrush"];

            // About and Keyboard Shortcuts use the app's outer window surface verbatim. Assign
            // after every palette/accent merge so gradient brushes remain gradients.
            combined["OverlayWindowBrush"] = combined["BackgroundBrush"];

            // Their 1px edges. A single border color over a gradient surface reads as one end's
            // color stuck to the other end (Delirium's red border under its purple side), so a
            // gradient surface gets a gradient edge: the same stops lifted toward white. A solid
            // surface keeps the border it always had.
            Brush? gradientEdge = combined["BackgroundBrush"] is LinearGradientBrush surface ? LiftedEdge(surface) : null;
            combined["OverlayEdgeBrush"] = gradientEdge ?? combined["AppBorderBrush"];
            combined["OverlayRuleBrush"] = gradientEdge ?? combined["PaneBorderBrush"];

            ApplyFamilyAdditions(combined);
            AppDerivedTokens?.Invoke(combined, theme, accent);
            return combined;
        }

        /// <summary>
        /// Family tokens from the other apps that KillerShell's own views never needed: dialog
        /// frames, the About panel bevel, combo button metrics, the shortcut map colors and the
        /// picker's title tokens. Every default is what the flat themes already drew; 98SE states
        /// its own in its palette.
        /// </summary>
        private static void ApplyFamilyAdditions(ResourceDictionary d)
        {
            bool flat = d.Contains("UseDialogCaption") && d["UseDialogCaption"] is bool f && f;
            object Clear() { var b = new SolidColorBrush(Colors.Transparent); b.Freeze(); return b; }

            Mirror(d, "DialogFrameBrush", "AppBorderBrush");
            SetIfAbsent(d, "DialogFrameThickness", new Thickness(1));
            SetIfAbsent(d, "DialogFramePadding", new Thickness(0));
            SetIfAbsent(d, "DialogWindowFrameThickness", new Thickness(0));
            SetIfAbsent(d, "DialogWindowFramePadding", new Thickness(0));
            SetIfAbsent(d, "DialogCloseCornerRadius", new CornerRadius(0, 6, 0, 0));
            SetIfAbsent(d, "DialogTitleMargin", flat ? d["TitleBarPadding"] : new Thickness(6, 0, 0, 0));
            SetIfAbsent(d, "OverlayFrameBleed", new Thickness(0));
            SetIfAbsent(d, "InputDialogTitleBrush", Clear());
            Mirror(d, "AboutPanelBrush", "PaneBrush");
            Mirror(d, "AboutPanelEdgeBrush", "PaneBorderBrush");
            SetIfAbsent(d, "AboutPanelBorderThickness", flat ? new Thickness(0) : new Thickness(1));
            SetIfAbsent(d, "AboutPanelBevelTopBrush", Clear());
            SetIfAbsent(d, "AboutPanelBevelBottomBrush", Clear());
            SetIfAbsent(d, "AboutPanelBevelTopThickness", new Thickness(0));
            SetIfAbsent(d, "AboutPanelBevelBottomThickness", new Thickness(0));
            Mirror(d, "ComboButtonHoverBrush", "RowHoverBrush");
            Mirror(d, "ComboFieldHoverBrush", "RowHoverBrush");
            SetIfAbsent(d, "ComboButtonSize", 18.0);
            SetIfAbsent(d, "ComboButtonHeight", d["ComboButtonSize"]);
            Mirror(d, "ContentPaneBrush", "PaneBrush");
            SetIfAbsent(d, "PaneGrainCornerRadius", new CornerRadius(flat ? 0 : 5));
            // Accent swatches are round pills, and square where the small corners are (98SE).
            SetIfAbsent(d, "AccentSwatchCornerRadius",
                d.Contains("SmallCornerRadius") && d["SmallCornerRadius"] is CornerRadius small && small.TopLeft == 0
                    ? new CornerRadius(0) : new CornerRadius(9));
            Mirror(d, "FilenameBrush", "MutedTextBrush");
            SetIfAbsent(d, "TitleSubtitleSize", 18.0);
            Mirror(d, "TreeLineBrush", "PaneBorderBrush");
            SetIfAbsent(d, "TableRowBrush", Clear());
            // The kit's older styles call the outline hover fill by this name.
            d["OutlineHoverFillBrush"] = d["OutlineHoverBrush"];
            ApplyShortcutCategoryBrushes(d);
        }

        /// <summary>
        /// The F1 overlay's category colors (KsCat* brushes, Shell/ShortcutsOverlay.cs). A theme
        /// may state its own; otherwise a dark surface gets the neon set and a light surface
        /// (Light, 98SE) the deep set, because the neon is close to invisible on white. Pure
        /// black lifts Nav's violet so it still separates from the background.
        /// </summary>
        private static void ApplyShortcutCategoryBrushes(ResourceDictionary d)
        {
            var surface = (d.Contains("SurfaceBrush") ? d["SurfaceBrush"] : null) as SolidColorBrush;
            var background = (d.Contains("BackgroundBrush") ? d["BackgroundBrush"] : null) as SolidColorBrush;
            bool light = surface != null && 0.299 * surface.Color.R + 0.587 * surface.Color.G + 0.114 * surface.Color.B > 140;
            bool pureBlack = background != null && background.Color.R == 0 && background.Color.G == 0 && background.Color.B == 0;

            (string Cat, string Neon, string Deep)[] set =
            [
                ("File",   "#00fff9", "#009e99"),
                ("Tools",  "#ffd319", "#a67c00"),
                ("Edit",   "#ff2975", "#d1105a"),
                ("Nav",    pureBlack ? "#a64dff" : "#8c1eff", "#6a11cc"),
                ("View",   "#f222ff", "#a316b5"),
                ("Search", "#00b3ff", "#0077b3"),
                ("Help",   "#39ff14", "#128c07"),
                ("Ocr",    "#ff901f", "#cc6a00"),
            ];
            foreach (var (cat, neon, deep) in set)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? deep : neon));
                brush.Freeze();
                SetIfAbsent(d, "KsCat" + cat, brush);
            }
            SetIfAbsent(d, "ShortcutShadowOpacity", d.Contains("FlyoutShadowOpacity") ? d["FlyoutShadowOpacity"] : 0.6);
        }

        /// <summary>
        /// The surface gradient with every stop mixed one fifth of the way to white, which lands
        /// on each gradient theme's own border tone at that end (Delirium #641d25 to #834a51,
        /// against its #81423b border).
        /// </summary>
        internal static LinearGradientBrush LiftedEdge(LinearGradientBrush surface)
        {
            var edge = new LinearGradientBrush
            {
                StartPoint = surface.StartPoint,
                EndPoint = surface.EndPoint,
                MappingMode = surface.MappingMode,
                SpreadMethod = surface.SpreadMethod,
                ColorInterpolationMode = surface.ColorInterpolationMode,
            };
            static byte Lift(byte channel) => (byte)(channel + (255 - channel) / 5);
            foreach (var stop in surface.GradientStops)
                edge.GradientStops.Add(new GradientStop(
                    Color.FromArgb(255, Lift(stop.Color.R), Lift(stop.Color.G), Lift(stop.Color.B)), stop.Offset));
            edge.Freeze();
            return edge;
        }
    }
}
