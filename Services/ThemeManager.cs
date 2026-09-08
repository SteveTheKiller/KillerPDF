using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace KillerPDF.Services
{
    internal enum Theme
    {
        Dark, Light, Black, SE98, Blood, Greed, Cyanotic, Ectoplasm, Decay,
        Mourning, Sepulchre, Delirium, Malaise
    }

    // Accent-hue variants of the Dark theme. Green is the base Dark.xaml (no overlay); the
    // others apply a small overlay dictionary that recolors only the accent-family keys.
    internal enum DarkAccent { Green, Red, Blue, Purple, Orange, Teal }

    internal static partial class ThemeManager
    {
        // ── P/Invoke ──────────────────────────────────────────────────────

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;

        // ── State ─────────────────────────────────────────────────────────

        private static Theme _current = Theme.Dark;
        // Dark, Light, and Black each remember their own accent independently.
        private static DarkAccent _darkAccent  = DarkAccent.Green;
        private static DarkAccent _lightAccent = DarkAccent.Green;
        private static DarkAccent _blackAccent = DarkAccent.Green;
        // Match the shared KillerTools 98SE palette: classic Win98 navy is the default.
        private static DarkAccent _se98Accent = DarkAccent.Blue;

        public static Theme Current => _current;
        public static DarkAccent DarkAccentChoice  => _darkAccent;
        public static DarkAccent LightAccentChoice => _lightAccent;
        public static DarkAccent BlackAccentChoice => _blackAccent;
        public static DarkAccent SE98AccentChoice => _se98Accent;
        private static DarkAccent AccentFor(Theme t) =>
            t == Theme.Light ? _lightAccent : t == Theme.Black ? _blackAccent : t == Theme.SE98 ? _se98Accent : _darkAccent;
        public static DarkAccent AccentChoiceFor(Theme t) => AccentFor(t);

        // True for the theme families that support accent variants.
        private static bool HasAccents(Theme t) =>
            t == Theme.Dark || t == Theme.Light || t == Theme.Black || t == Theme.SE98;

        /// <summary>Fired after the theme dictionary has been updated.</summary>
        public static event Action? ThemeChanged;

        // ── Public API ───────────────────────────────────────────────────

        /// <summary>
        /// Call once at startup (before MainWindow is created) to restore the saved theme.
        /// DWM title bar is applied later via ApplyDwm(hwnd) from SourceInitialized.
        /// </summary>
        public static void Initialize()
        {
            var saved = App.GetSetting("Theme");
            // Back-compat: the Black theme's enum value was renamed from "HighContrast".
            if (saved == "HighContrast") saved = nameof(Theme.Black);
            _current = Enum.TryParse<Theme>(saved, out var t) ? t : Theme.Dark;
            _darkAccent  = Enum.TryParse<DarkAccent>(App.GetSetting("DarkAccent"),  out var da) ? da : DarkAccent.Green;
            _lightAccent = Enum.TryParse<DarkAccent>(App.GetSetting("LightAccent"), out var la) ? la : DarkAccent.Green;
            _blackAccent = Enum.TryParse<DarkAccent>(App.GetSetting("BlackAccent"), out var ba) ? ba : DarkAccent.Green;
            _se98Accent = Enum.TryParse<DarkAccent>(App.GetSetting("98SEAccent"), out var wa) ? wa : DarkAccent.Blue;
            ApplyInternal(_current, applyDwm: false);
        }

        /// <summary>
        /// Change a theme family's accent hue, persist it, and reapply if that family is active.
        /// Dark and Light keep independent accents, so changing one never disturbs the other.
        /// </summary>
        public static void ApplyAccent(Theme family, DarkAccent accent)
        {
            if      (family == Theme.Light)        { _lightAccent = accent; App.SetSetting("LightAccent", accent.ToString()); }
            else if (family == Theme.Black)        { _blackAccent = accent; App.SetSetting("BlackAccent", accent.ToString()); }
            else if (family == Theme.SE98)         { _se98Accent = accent; App.SetSetting("98SEAccent", accent.ToString()); }
            else                                   { _darkAccent  = accent; App.SetSetting("DarkAccent",  accent.ToString()); }

            if (_current == family)
            {
                LoadDict(_current);
                ThemeChanged?.Invoke();
            }
        }

        /// <summary>
        /// Change to a new theme, persist the choice, and update DWM immediately.
        /// </summary>
        public static void Apply(Theme theme)
        {
            _current = theme;
            App.SetSetting("Theme", theme.ToString());
            ApplyInternal(theme, applyDwm: true);
            ThemeChanged?.Invoke();
        }

        /// <summary>
        /// Called from Window.SourceInitialized to set the native title bar color.
        /// </summary>
        public static void ApplyDwm(IntPtr hwnd)
        {
            SetDwm(hwnd, !UsesLightChrome(_current));
        }

        // ── Internal ─────────────────────────────────────────────────────

        private static void ApplyInternal(Theme theme, bool applyDwm)
        {
            LoadDict(theme);

            if (applyDwm)
            {
                var win = Application.Current?.MainWindow;
                if (win != null)
                {
                    var hwnd = new WindowInteropHelper(win).Handle;
                    if (hwnd != IntPtr.Zero)
                        SetDwm(hwnd, !UsesLightChrome(theme));
                }
            }
        }

        /// <summary>Complete the standalone installer palette without reading or saving preferences.</summary>
        internal static void InitializeInstallerTheme() => LoadDict(Theme.Dark, DarkAccent.Green);

        private static void LoadDict(Theme theme, DarkAccent? accentOverride = null)
        {
            var uri = theme switch
            {
                Theme.Light        => new Uri("pack://application:,,,/Themes/Light.xaml"),
                Theme.Black        => new Uri("pack://application:,,,/Themes/Black.xaml"),
                Theme.SE98         => new Uri("pack://application:,,,/Themes/98SE.xaml"),
                Theme.Blood        => new Uri("pack://application:,,,/Themes/Blood.xaml"),
                Theme.Greed        => new Uri("pack://application:,,,/Themes/Greed.xaml"),
                Theme.Cyanotic     => new Uri("pack://application:,,,/Themes/Cyanotic.xaml"),
                Theme.Ectoplasm    => new Uri("pack://application:,,,/Themes/Ectoplasm.xaml"),
                Theme.Decay        => new Uri("pack://application:,,,/Themes/Decay.xaml"),
                Theme.Mourning     => new Uri("pack://application:,,,/Themes/Mourning.xaml"),
                Theme.Sepulchre    => new Uri("pack://application:,,,/Themes/Sepulchre.xaml"),
                Theme.Delirium     => new Uri("pack://application:,,,/Themes/Delirium.xaml"),
                Theme.Malaise      => new Uri("pack://application:,,,/Themes/Malaise.xaml"),
                _                  => new Uri("pack://application:,,,/Themes/Dark.xaml"),
            };

            var newDict = new ResourceDictionary { Source = uri };
            // Recorded BEFORE CompleteAppPalette materializes it: whether the THEME FILE itself
            // defines the tab ring (98SE does - classic chrome, not an accent derivation). After
            // materialization the key always exists, so this is the only moment the distinction
            // is readable, and the accent overlay below needs it to know whether re-deriving the
            // ring from the overlay's accent would honor the theme or clobber it.
            bool themeOwnsTabRing = newDict.Contains("TabActiveRingBrush");
            // 98SE owns the entire classic button treatment. Other themes derive these roles from
            // their accent; record ownership before CompleteAppPalette fills the fallback keys.
            bool themeOwnsOutlineRest = newDict.Contains("OutlineRestBrush");
            bool themeOwnsOutlineText = newDict.Contains("OutlineTextBrush");
            bool themeOwnsOutlineHover = newDict.Contains("OutlineHoverBrush");
            bool themeOwnsOutlineHoverText = newDict.Contains("OutlineHoverTextBrush");
            CompleteAppPalette(newDict);
            var merged  = Application.Current.Resources.MergedDictionaries;

            // In-place per-key update: fires a targeted notification for each changed key without
            // structurally modifying MergedDictionaries. Structural add/remove fires a synchronous
            // ResourcesChanged that can invoke FindResource() calls (e.g. in SwitchSidebarToPagesTab)
            // before the new dict is fully in place, causing ResourceReferenceKeyNotFoundException.
            if (merged.Count > 0)
            {
                var existing = merged[0];
                foreach (object key in newDict.Keys)
                    existing[key] = newDict[key];
                // The two effect keys can be NULL (98SE: no shadows), and a null-valued entry
                // does not reliably survive the per-key copy above - the previous theme's effect
                // then stays in the live dictionary and 98SE keeps casting pane shadows. Force
                // them through explicitly, null included.
                existing["PaneShadowEffect"] = newDict.Contains("PaneShadowEffect") ? newDict["PaneShadowEffect"] : null;
                existing["BarShadowEffect"]  = newDict.Contains("BarShadowEffect")  ? newDict["BarShadowEffect"]  : null;
            }
            else
            {
                merged.Add(newDict);
            }

            // Dark and Light families: overlay the chosen accent hue on top of the base green keys.
            // Green is the base itself, so it needs no overlay (and re-applying the base above
            // already restored green, so switching back from a colored accent works automatically).
            // Each theme has its own tuned overlay (Dark = bright text on dark; Light = dark text
            // on white), loaded from Accents/<Theme>/<Accent>.xaml.
            var accent = accentOverride ?? AccentFor(theme);
            if (HasAccents(theme) && accent != DarkAccent.Green)
            {
                // Dark overlays live in Accents/Dark/; Light in Accents/Light/; Black in Accents/Black/.
                string sub = theme == Theme.Light ? "Light/" : theme == Theme.Black ? "Black/" : theme == Theme.SE98 ? "98SE/" : "Dark/";
                var accentDict = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Themes/Accents/{sub}{accent}.xaml")
                };
                var target = merged[0];
                foreach (object key in accentDict.Keys)
                    target[key] = accentDict[key];
                // Aliases derived from PrimaryBrush were materialized against the BASE palette
                // (CompleteAppPalette runs before this overlay), so an overlay that recolors
                // PrimaryBrush without carrying the alias left them on the base hue - the green
                // wordmark on blue-accented 98SE. Re-point them at the overlay's accent.
                if (accentDict.Contains("PrimaryBrush"))
                    foreach (string aliased in new[] { "AccentLogo", "InstallBtnBg", "SelectionAccent", "RadioAccent" })
                        if (!accentDict.Contains(aliased))
                            target[aliased] = accentDict["PrimaryBrush"];
                // The outline-button roles were materialized against the BASE palette before this
                // overlay ran. Re-derive every accent-led role together; otherwise the Install
                // button gets (for example) a red outline with the base green text. A theme that
                // explicitly owns a role (98SE's black-on-gray treatment) keeps it untouched.
                object outlineAccent = accentDict.Contains("OutlineBtnBrush")
                    ? accentDict["OutlineBtnBrush"]
                    : accentDict.Contains("PrimaryBrush") ? accentDict["PrimaryBrush"] : target["OutlineRestBrush"];
                if (!themeOwnsOutlineRest && !accentDict.Contains("OutlineRestBrush"))
                    target["OutlineRestBrush"] = outlineAccent;
                if (!themeOwnsOutlineText && !accentDict.Contains("OutlineTextBrush"))
                    target["OutlineTextBrush"] = outlineAccent;
                if (!themeOwnsOutlineHover && !accentDict.Contains("OutlineHoverBrush"))
                    target["OutlineHoverBrush"] = outlineAccent;
                if (!themeOwnsOutlineHoverText && !accentDict.Contains("OutlineHoverTextBrush"))
                    target["OutlineHoverTextBrush"] = target["OnPrimaryBrush"];
                // TabActiveRingBrush is the same pre-overlay derivation (from SelectionAccent), so
                // the active tab's ring and underline sat on the theme's base hue under every
                // colored accent, on every theme. Re-derive from the overlay's SelectionAccent -
                // already merged into target above - UNLESS the theme file defines the ring
                // itself (98SE's classic chrome) or the overlay carries it explicitly.
                if (!themeOwnsTabRing && !accentDict.Contains("TabActiveRingBrush"))
                    target["TabActiveRingBrush"] = target["SelectionAccent"];
            }

            // App.xaml owns startup fallbacks for these legacy aliases, and local application
            // resources outrank merged dictionaries. Keep those fallbacks synchronized so 98SE's
            // zero radius actually reaches panes, tabs, flyouts, and dialogs.
            var appResources = Application.Current.Resources;
            var liveResources = merged[0];
            // One semantic role for the two window-like overlays. This is assigned after the
            // palette and accent overlay are fully merged so gradient BackgroundBrush values are
            // preserved instead of being flattened or replaced by MenuBackgroundBrush.
            liveResources["OverlayWindowBrush"] = liveResources["BackgroundBrush"];
            appResources["RadWindow"] = liveResources["WindowCornerRadius"];
            appResources["RadCard"] = liveResources["PanelCornerRadius"];
            appResources["RadControl"] = liveResources["ControlCornerRadius"];
            appResources["UiFont"] = liveResources["UiFont"];

            // One SystemIdle pass to nudge any elements whose effective value didn't auto-update
            // (e.g. ControlTemplate trigger bindings with TargetName that missed the per-key signal).
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, (Action)RefreshIcons);
        }

        private static bool UsesLightChrome(Theme theme) => theme is Theme.Light or Theme.SE98;

        private static void CompleteAppPalette(ResourceDictionary d)
        {
            object Pick(string key, string fallback) => d.Contains(key) ? d[key] : d[fallback];
            void Alias(string key, string fallback)
            {
                if (!d.Contains(key)) d[key] = Pick(fallback, "PaneBrush");
            }

            Alias("BgRecentPanel", "SurfaceBrush");
            Alias("BgFlyout", "MenuBackgroundBrush");
            Alias("AnnotationBarBrush", "BgFlyout");
            // Match KillerNotes: the inner About grouping panel uses the context-menu surface.
            Alias("AboutPanelBrush", "MenuBackgroundBrush");
            Alias("SelectionAccent", "PrimaryBrush");
            Alias("AccentLogo", "PrimaryBrush");
            Alias("InstallBtnBg", "PrimaryBrush");
            Alias("DropBorder", "PaneBorderBrush");
            Alias("SettingsOpenRowBg", "RowHoverBrush");
            Alias("FilenameBrush", "MutedTextBrush");
            Alias("BgDragHandle", "PaneBorderBrush");
            Alias("DragLine", "InputBorderBrush");
            Alias("SliderTrack", "InputBorderBrush");
            Alias("RadioAccent", "PrimaryBrush");
            Alias("BgCanvas", "PaneBrush");
            Alias("FocusedPaneBrush", "BgCanvas");
            // Keep a solid fallback for controls that cannot use the full-window gradient.
            if (!d.Contains("SolidBackgroundBrush"))
            {
                if (d["BackgroundBrush"] is LinearGradientBrush gradient && gradient.GradientStops.Count > 0)
                {
                    var solid = new SolidColorBrush(gradient.GradientStops[0].Color);
                    solid.Freeze();
                    d["SolidBackgroundBrush"] = solid;
                }
                else
                {
                    Alias("SolidBackgroundBrush", "BackgroundBrush");
                }
            }
            // Inactive tabs belong to the strip behind them and use the theme background.
            // Replacing this with the first gradient stop creates visibly unrelated color blocks.
            Alias("TabInactiveBrush", "BackgroundBrush");
            Alias("RadioWellBrush", "BgCanvas");
            // App.xaml owns a local UiFont fallback, and local application resources outrank
            // merged theme dictionaries. Materialize this key in every palette so LoadDict can
            // copy the active value into that higher-precedence slot (98SE supplies Microsoft
            // Sans Serif; the other themes deliberately return to the family Segoe stack).
            if (!d.Contains("UiFont"))
                d["UiFont"] = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI");
            Alias("ComboFieldBrush", "PaneBrush");
            Alias("ComboFieldHoverBrush", "RowHoverBrush");
            Alias("ComboPopupBrush", "PaneBrush");
            Alias("FooterZoomClosedBrush", "MenuBackgroundBrush");
            Alias("FooterZoomSurfaceBrush", "MenuBackgroundBrush");
            // The chevron sits directly on the combo field: no button face by default, so the
            // arrow does not read as a separate boxed control. 98SE sets ComboButtonBrush
            // explicitly (#c0c0c0) and keeps its raised Win98 drop-down button.
            if (!d.Contains("ComboButtonBrush")) d["ComboButtonBrush"] = Brushes.Transparent;
            Alias("ComboButtonHoverBrush", "RowHoverBrush");
            // These must be materialized into every completed palette. Theme dictionaries are
            // copied into the live dictionary in place, so a missing key would otherwise retain
            // the previously selected theme's caption (most visibly 98SE green on Blood).
            // Keep the titlebar continuous with the surrounding app chrome. Themes that need
            // distinct titlebar treatment (notably 98SE and the gradient themes) provide an
            // explicit TitleBarBrush and are therefore left untouched by this fallback.
            Alias("TitleBarBrush", "BackgroundBrush");
            Alias("DialogTitleBarBrush", "TitleBarBrush");
            Alias("KeyboardKeyBrush", "PaneBrush");
            if (!d.Contains("DangerRed")) d["DangerRed"] = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
            if (!d.Contains("BgOverlay")) d["BgOverlay"] = new SolidColorBrush(Color.FromArgb(0xbb, 0, 0, 0));
            if (!d.Contains("HeaderShadowOpacity")) d["HeaderShadowOpacity"] = 0.5;
            if (!d.Contains("WordmarkShadowOpacity")) d["WordmarkShadowOpacity"] = 0.45;
            if (!d.Contains("AboutShadowOpacity")) d["AboutShadowOpacity"] = d.Contains("FlyoutShadowOpacity") ? d["FlyoutShadowOpacity"] : 0.6;
            if (!d.Contains("AboutIconShadowOpacity")) d["AboutIconShadowOpacity"] = 0.6;
            if (!d.Contains("AboutCaptionVisibility")) d["AboutCaptionVisibility"] = Visibility.Collapsed;
            if (!d.Contains("AboutModernCloseVisibility")) d["AboutModernCloseVisibility"] = Visibility.Visible;
            if (!d.Contains("ShortcutShadowOpacity")) d["ShortcutShadowOpacity"] = d.Contains("FlyoutShadowOpacity") ? d["FlyoutShadowOpacity"] : 0.6;
            if (!d.Contains("ShortcutHeaderShadowOpacity")) d["ShortcutHeaderShadowOpacity"] = 0.55;
            // Every *ShadowOpacity key needs a materialized default: 98SE defines all ten as
            // zeroes, and the in-place merge keeps them zeroed for any later theme that omits
            // the key - which is how visiting 98SE once stripped the pane shadows from every
            // theme that relied on lookup fallbacks. Defaults mirror Black.xaml.
            if (!d.Contains("PaneShadowOpacity"))   d["PaneShadowOpacity"]   = 0.60;
            if (!d.Contains("BarShadowOpacity"))    d["BarShadowOpacity"]    = 0.38;
            if (!d.Contains("FlyoutShadowOpacity")) d["FlyoutShadowOpacity"] = 0.55;
            if (!d.Contains("ThemeRadioShadowOpacity")) d["ThemeRadioShadowOpacity"] = 0.5;
            if (!d.Contains("ShortcutCaptionVisibility")) d["ShortcutCaptionVisibility"] = Visibility.Collapsed;
            if (!d.Contains("ShortcutModernHeaderVisibility")) d["ShortcutModernHeaderVisibility"] = Visibility.Visible;
            if (!d.Contains("FooterVersionDividerVisibility")) d["FooterVersionDividerVisibility"] = Visibility.Visible;
            if (!d.Contains("KsCatTools")) d["KsCatTools"] = new SolidColorBrush(Color.FromRgb(0xff, 0xd3, 0x19));
            if (!d.Contains("KsCatOcr")) d["KsCatOcr"] = new SolidColorBrush(Color.FromRgb(0xff, 0x90, 0x1f));
            if (!d.Contains("WindowFramePadding")) d["WindowFramePadding"] = new Thickness(0);
            if (!d.Contains("FrameOuterLightThickness")) d["FrameOuterLightThickness"] = new Thickness(0);
            if (!d.Contains("FrameOuterDarkThickness")) d["FrameOuterDarkThickness"] = new Thickness(0);
            if (!d.Contains("FrameInnerLightThickness")) d["FrameInnerLightThickness"] = new Thickness(0);
            if (!d.Contains("FrameInnerDarkThickness")) d["FrameInnerDarkThickness"] = new Thickness(0);
            if (!d.Contains("FrameInnerMargin")) d["FrameInnerMargin"] = new Thickness(0);
            if (!d.Contains("FrameOuterLightBrush")) d["FrameOuterLightBrush"] = Brushes.Transparent;
            if (!d.Contains("FrameOuterDarkBrush")) d["FrameOuterDarkBrush"] = Brushes.Transparent;
            if (!d.Contains("FrameInnerLightBrush")) d["FrameInnerLightBrush"] = Brushes.Transparent;
            if (!d.Contains("FrameInnerDarkBrush")) d["FrameInnerDarkBrush"] = Brushes.Transparent;
            if (!d.Contains("WindowFrameBrush")) d["WindowFrameBrush"] = Pick("SurfaceBrush", "PaneBrush");
            // Dialogs carry the same outline as the main window (AppBorderBrush - what DWM paints
            // on the main frame), not the neutral menu hairline they had drifted to. 98SE disables
            // this uniform outline because its directional frame rings supply the classic bevel.
            if (!d.Contains("DialogFrameBrush")) d["DialogFrameBrush"] = Pick("AppBorderBrush", "MenuBorderBrush");
            if (!d.Contains("DialogFrameThickness")) d["DialogFrameThickness"] = new Thickness(1);
            if (!d.Contains("DialogFramePadding")) d["DialogFramePadding"] = new Thickness(0);
            if (!d.Contains("DialogWindowFrameThickness")) d["DialogWindowFrameThickness"] = new Thickness(0);
            if (!d.Contains("DialogWindowFramePadding")) d["DialogWindowFramePadding"] = new Thickness(0);
            if (!d.Contains("ButtonBevelLightThickness")) d["ButtonBevelLightThickness"] = d.Contains("BevelLightThickness") ? d["BevelLightThickness"] : new Thickness(0);
            if (!d.Contains("ButtonBevelDarkThickness")) d["ButtonBevelDarkThickness"] = d.Contains("BevelDarkThickness") ? d["BevelDarkThickness"] : new Thickness(0);
            // Only 98SE defines these; unmaterialized they resolve to nothing and the picker's
            // footer buttons lose their borders on every other theme. Family standard: the confirm
            // button rests with the accent outline; the neutral button gets the menu hairline.
            // Text input fill. Only 98SE defined it (classic white), so the picker's fields
            // rendered with no background on every other theme. BgCanvas matches the Document
            // Info dialog's fields - the reference look.
            if (!d.Contains("TextFieldBrush")) d["TextFieldBrush"] = Pick("BgCanvas", "PaneBrush");
            // No theme dictionary carries these two keys, so on the fresh dict this method runs
            // against, plain assignment and if-absent are equivalent - assignment states the
            // intent. NOTE this still runs BEFORE the accent overlay: overlay-time re-derivation
            // (LoadDict) is what keeps them on the chosen accent, not this line - the green
            // Install outline on teal-accent Black was fixed THERE.
            if (!d.Contains("OutlineRestBrush")) d["OutlineRestBrush"] = Pick("OutlineBtnBrush", "PrimaryBrush");
            if (!d.Contains("ButtonEdgeBrush")) d["ButtonEdgeBrush"] = Pick("MenuBorderBrush", "PaneBrush");
            Alias("SurfaceHoverBrush", "RowHoverBrush");
            // The confirm button remains accent-led in modern themes. 98SE supplies classic gray
            // face/text/hover values so Open and Save never become a blue selection rectangle.
            if (!d.Contains("OutlineFaceBrush")) d["OutlineFaceBrush"] = Brushes.Transparent;
            if (!d.Contains("OutlineTextBrush")) d["OutlineTextBrush"] = Pick("OutlineBtnBrush", "PrimaryBrush");
            if (!d.Contains("OutlineHoverBrush")) d["OutlineHoverBrush"] = Pick("OutlineBtnBrush", "PrimaryBrush");
            if (!d.Contains("OutlineHoverTextBrush")) d["OutlineHoverTextBrush"] = Pick("OnPrimaryBrush", "TextBrush");
            // Circle swatches by default; 98SE's own 0 makes them squares. Materialized so 98SE's
            // square cannot leak into a later theme through the in-place merge.
            if (!d.Contains("AccentSwatchCornerRadius")) d["AccentSwatchCornerRadius"] = new CornerRadius(9);
            // The checked picker row's label while HOVERED. Defaults to the normal checked color
            // (RadioAccent) so nothing changes; a theme whose accent equals its hover fill
            // (Sepulchre) overrides it, or the selected label vanishes into its own highlight.
            Alias("RadioHoverFgBrush", "RadioAccent");
            // Overlay (About / shortcuts) close button, KillerScan reference: a bare muted X that
            // turns red on hover. 98SE overrides all of these in its own xaml for the classic
            // MDL2 glyph and metrics.
            if (!d.Contains("AboutCloseGlyph")) d["AboutCloseGlyph"] = ((char)0x2715).ToString();   // multiplication X, by codepoint so the file stays ASCII-clean
            if (!d.Contains("AboutCloseFont")) d["AboutCloseFont"] = new FontFamily("Segoe UI");
            if (!d.Contains("AboutCloseWidth")) d["AboutCloseWidth"] = 28.0;
            if (!d.Contains("AboutCloseHeight")) d["AboutCloseHeight"] = 26.0;
            if (!d.Contains("AboutCloseMargin")) d["AboutCloseMargin"] = new Thickness(0, 6, 6, 0);
            if (!d.Contains("AboutCloseFg")) d["AboutCloseFg"] = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            if (!d.Contains("AboutCloseHoverFg")) d["AboutCloseHoverFg"] = new SolidColorBrush(Color.FromRgb(0xe0, 0x44, 0x44));
            if (!d.Contains("CheckSunkenDarkThickness")) d["CheckSunkenDarkThickness"] = new Thickness(0);
            if (!d.Contains("CheckSunkenLightThickness")) d["CheckSunkenLightThickness"] = new Thickness(0);
            // 98SE opts into the compact native-style caption and removes the shadow halo. These
            // values must exist in every completed palette because the live dictionary is updated
            // in place; otherwise its caption geometry survives after selecting another theme.
            if (!d.Contains("UseDialogCaption")) d["UseDialogCaption"] = false;
            if (!d.Contains("DialogTitleBarHeight")) d["DialogTitleBarHeight"] = 40.0;
            if (!d.Contains("DialogHaloMargin")) d["DialogHaloMargin"] = new Thickness(12);
            if (!d.Contains("TitleBarHeight")) d["TitleBarHeight"] = 36.0;
            if (!d.Contains("FooterHeight")) d["FooterHeight"] = 24.0;
            if (!d.Contains("FooterStatusPadding")) d["FooterStatusPadding"] = new Thickness(16, 0, 16, 0);
            if (!d.Contains("FooterStatusFont")) d["FooterStatusFont"] = new FontFamily("Segoe UI");
            if (!d.Contains("FooterMetaFont")) d["FooterMetaFont"] = new FontFamily("Consolas");
            if (!d.Contains("FooterPadding")) d["FooterPadding"] = new Thickness(16, 0, 16, 0);
            if (!d.Contains("FooterCellMargin")) d["FooterCellMargin"] = new Thickness(0);
            if (!d.Contains("FooterCellPadding")) d["FooterCellPadding"] = new Thickness(0);
            if (!d.Contains("RootBorderThickness")) d["RootBorderThickness"] = new Thickness(1);
            if (!d.Contains("PlainTitleVisibility")) d["PlainTitleVisibility"] = Visibility.Collapsed;
            if (!d.Contains("WordmarkVisibility")) d["WordmarkVisibility"] = Visibility.Visible;
            if (!d.Contains("GripDotsVisibility")) d["GripDotsVisibility"] = Visibility.Visible;
            if (!d.Contains("GripHatchVisibility")) d["GripHatchVisibility"] = Visibility.Collapsed;
            if (!d.Contains("ComboButtonSize")) d["ComboButtonSize"] = 18.0;
            // Width and height are separate: modern themes use a compact square chevron face,
            // while 98SE keeps the native narrow button but stretches it to the field's height.
            if (!d.Contains("ComboButtonHeight")) d["ComboButtonHeight"] = d["ComboButtonSize"];
            if (!d.Contains("ZoomBoxHeight")) d["ZoomBoxHeight"] = 28.0;
            if (!d.Contains("RetroTabJoinVisibility")) d["RetroTabJoinVisibility"] = Visibility.Collapsed;
            if (!d.Contains("RetroActiveTabOutlineVisibility")) d["RetroActiveTabOutlineVisibility"] = Visibility.Collapsed;
            if (!d.Contains("TabBandHeight")) d["TabBandHeight"] = double.NaN;
            if (!d.Contains("CaptionButtonWidth")) d["CaptionButtonWidth"] = 46.0;
            if (!d.Contains("CaptionButtonHeight")) d["CaptionButtonHeight"] = 36.0;
            if (!d.Contains("CaptionButtonMargin")) d["CaptionButtonMargin"] = new Thickness(0);
            if (!d.Contains("CaptionCloseGap")) d["CaptionCloseGap"] = new Thickness(0);
            if (!d.Contains("CaptionButtonsMargin")) d["CaptionButtonsMargin"] = new Thickness(0);
            bool compactDialogCaption = d["UseDialogCaption"] is bool useDialogCaption && useDialogCaption;
            if (!d.Contains("DialogCloseWidth"))
                d["DialogCloseWidth"] = compactDialogCaption ? d["CaptionButtonWidth"] : 28.0;
            if (!d.Contains("DialogCloseHeight"))
                d["DialogCloseHeight"] = compactDialogCaption ? d["CaptionButtonHeight"] : 26.0;
            if (!d.Contains("DialogCaptionButtonsMargin"))
            {
                var captionButtonsMargin = d["CaptionButtonsMargin"] is Thickness margin
                    ? margin
                    : new Thickness(0);
                d["DialogCaptionButtonsMargin"] = new Thickness(0, 0, captionButtonsMargin.Right, 0);
            }
            if (!d.Contains("CaptionButtonBrush")) d["CaptionButtonBrush"] = Brushes.Transparent;
            if (!d.Contains("CaptionGlyphBrush")) d["CaptionGlyphBrush"] = Pick("TextBrush", "PaneBrush");
            if (!d.Contains("CaptionHoverBrush")) d["CaptionHoverBrush"] = Pick("RowHoverBrush", "PaneBrush");
            if (!d.Contains("CaptionCloseBrush")) d["CaptionCloseBrush"] = Pick("DangerRed", "TextBrush");
            if (!d.Contains("CaptionCloseHoverBrush")) d["CaptionCloseHoverBrush"] = Pick("DangerRed", "TextBrush");
            if (!d.Contains("CaptionCloseHoverFgBrush")) d["CaptionCloseHoverFgBrush"] = Brushes.White;
            bool classicCaption = d["PlainTitleVisibility"] is Visibility titleVisibility
                                  && titleVisibility == Visibility.Visible;
            d["CaptionGlyphWeight"] = classicCaption ? FontWeights.Bold : FontWeights.Normal;
            d["CaptionFontGlyphVisibility"] = classicCaption ? Visibility.Collapsed : Visibility.Visible;
            d["CaptionDrawnGlyphVisibility"] = classicCaption ? Visibility.Visible : Visibility.Collapsed;
            if (!d.Contains("ChromeFontFamily")) d["ChromeFontFamily"] = new FontFamily("Tahoma");
            if (!d.Contains("TitleIconSize")) d["TitleIconSize"] = 25.0;
            if (!d.Contains("TitleIconMargin")) d["TitleIconMargin"] = new Thickness(0, 0, 7, 0);
            if (!d.Contains("TitleBarPadding")) d["TitleBarPadding"] = new Thickness(12, 0, 0, 0);
            if (!d.Contains("FooterBevelDarkBrush")) d["FooterBevelDarkBrush"] = Brushes.Transparent;
            if (!d.Contains("FooterBevelLightBrush")) d["FooterBevelLightBrush"] = Brushes.Transparent;
            if (!d.Contains("FooterCellLightThickness")) d["FooterCellLightThickness"] = new Thickness(0);
            if (!d.Contains("FooterCellDarkThickness")) d["FooterCellDarkThickness"] = new Thickness(0);
            if (!d.Contains("BevelLightBrush")) d["BevelLightBrush"] = Brushes.Transparent;
            if (!d.Contains("BevelDarkBrush")) d["BevelDarkBrush"] = Brushes.Transparent;
            if (!d.Contains("BevelLightThickness")) d["BevelLightThickness"] = new Thickness(0);
            if (!d.Contains("BevelDarkThickness")) d["BevelDarkThickness"] = new Thickness(0);
            if (!d.Contains("PaneBevelLightBrush")) d["PaneBevelLightBrush"] = Brushes.Transparent;
            if (!d.Contains("PaneBevelDarkBrush")) d["PaneBevelDarkBrush"] = Brushes.Transparent;
            if (!d.Contains("PaneBevelLightThickness")) d["PaneBevelLightThickness"] = new Thickness(0);
            if (!d.Contains("PaneBevelDarkThickness")) d["PaneBevelDarkThickness"] = new Thickness(0);
            if (!d.Contains("PaneBevelDark2Brush")) d["PaneBevelDark2Brush"] = Brushes.Transparent;
            if (!d.Contains("PaneBevelLight2Brush")) d["PaneBevelLight2Brush"] = Brushes.Transparent;
            if (!d.Contains("PaneBevel2LightThickness")) d["PaneBevel2LightThickness"] = new Thickness(0);
            if (!d.Contains("PaneBevel2DarkThickness")) d["PaneBevel2DarkThickness"] = new Thickness(0);
            if (!d.Contains("PaneBevelInnerMargin")) d["PaneBevelInnerMargin"] = new Thickness(0);
            if (!d.Contains("SidebarPanelMargin")) d["SidebarPanelMargin"] = new Thickness(0);
            if (!d.Contains("SidebarInnerDarkThickness")) d["SidebarInnerDarkThickness"] = new Thickness(0);
            if (!d.Contains("SidebarInnerDarkVisibility")) d["SidebarInnerDarkVisibility"] = Visibility.Collapsed;
            if (!d.Contains("SplitHostMargin")) d["SplitHostMargin"] = new Thickness(0, 0, 8, 0);
            if (!d.Contains("SplitPaneGutterWidth")) d["SplitPaneGutterWidth"] = 8.0;
            if (!d.Contains("ContentPaneMargin")) d["ContentPaneMargin"] = new Thickness(0, 0, 8, 0);
            if (!d.Contains("FileDialogPaneBrush")) d["FileDialogPaneBrush"] = d.Contains("PaneBrush") ? d["PaneBrush"] : Brushes.White;
            // Theme dictionaries are copied into the live dictionary in place, so a key that is
            // absent from the next theme keeps the previous theme's value. 98SE sets this to zero;
            // materialize the modern default so switching away from 98SE restores both edge fades.
            if (!d.Contains("EdgeFadeOpacity")) d["EdgeFadeOpacity"] = 1.0;
            // Match KillerNotes and KillerShell: modern sidebars do not paint a second surface;
            // the themed app background continues through them. 98SE explicitly overrides this
            // with its white recessed client pane.
            if (!d.Contains("SidebarPaneBrush")) d["SidebarPaneBrush"] = Brushes.Transparent;
            if (!d.Contains("SidebarRailBrush")) d["SidebarRailBrush"] = Brushes.Transparent;
            if (!d.Contains("PaneEdgeBrush")) d["PaneEdgeBrush"] = Pick("PaneBorderBrush", "CardBorderBrush");
            if (!d.Contains("BarEdgeBrush")) d["BarEdgeBrush"] = Pick("PaneBorderBrush", "CardBorderBrush");
            if (!d.Contains("BarEdgeThickness")) d["BarEdgeThickness"] = new Thickness(1, 0, 1, 1);
            if (!d.Contains("BarEdgeDarkBrush")) d["BarEdgeDarkBrush"] = Brushes.Transparent;
            if (!d.Contains("BarEdgeDarkThickness")) d["BarEdgeDarkThickness"] = new Thickness(0);
            if (!d.Contains("BarPadding")) d["BarPadding"] = new Thickness(4);
            if (!d.Contains("MenuBevelLightBrush")) d["MenuBevelLightBrush"] = Brushes.Transparent;
            if (!d.Contains("MenuBevelDarkBrush")) d["MenuBevelDarkBrush"] = Brushes.Transparent;
            if (!d.Contains("MenuBevel2LightBrush")) d["MenuBevel2LightBrush"] = Brushes.Transparent;
            if (!d.Contains("MenuBevel2DarkBrush")) d["MenuBevel2DarkBrush"] = Brushes.Transparent;
            if (!d.Contains("MenuBevelLightThickness")) d["MenuBevelLightThickness"] = new Thickness(0);
            if (!d.Contains("MenuBevelDarkThickness")) d["MenuBevelDarkThickness"] = new Thickness(0);
            if (!d.Contains("MenuBevel2LightThickness")) d["MenuBevel2LightThickness"] = new Thickness(0);
            if (!d.Contains("MenuBevel2DarkThickness")) d["MenuBevel2DarkThickness"] = new Thickness(0);
            if (!d.Contains("MenuBevelInnerMargin")) d["MenuBevelInnerMargin"] = new Thickness(0);
            if (!d.Contains("TabInactiveBevelDarkThickness")) d["TabInactiveBevelDarkThickness"] = new Thickness(0);
            if (!d.Contains("TabActiveBevelDarkThickness")) d["TabActiveBevelDarkThickness"] = new Thickness(0);
            if (!d.Contains("TabBevelMargin")) d["TabBevelMargin"] = new Thickness(0);
            if (!d.Contains("TabActiveInnerBevelBrush")) d["TabActiveInnerBevelBrush"] = Brushes.Transparent;
            if (!d.Contains("TabActiveInnerBevelThickness")) d["TabActiveInnerBevelThickness"] = new Thickness(0);
            if (!d.Contains("TabActiveInnerBevelMargin")) d["TabActiveInnerBevelMargin"] = new Thickness(0);
            if (!d.Contains("TabMargin")) d["TabMargin"] = new Thickness(0, 3, 0, 1);
            if (!d.Contains("TabInactiveFirstMargin")) d["TabInactiveFirstMargin"] = d["TabMargin"];
            if (!d.Contains("TabInactiveLastMargin")) d["TabInactiveLastMargin"] = d["TabMargin"];
            if (!d.Contains("TabActiveFirstMargin")) d["TabActiveFirstMargin"] = new Thickness(0, 3, 0, 0);
            if (!d.Contains("TabActiveLastMargin")) d["TabActiveLastMargin"] = new Thickness(0, 3, 0, 0);
            if (!d.Contains("TabActiveOnlyMargin")) d["TabActiveOnlyMargin"] = new Thickness(0, 3, 0, 0);
            if (!d.Contains("TabPadding")) d["TabPadding"] = new Thickness(12, 4, 5, 5);
            if (!d.Contains("TabActiveBevelDarkMargin")) d["TabActiveBevelDarkMargin"] = d["TabBevelMargin"];
            if (!d.Contains("TabActivePadding")) d["TabActivePadding"] = new Thickness(12, 1, 5, 5);
            if (!d.Contains("TabStripeThickness")) d["TabStripeThickness"] = new Thickness(0, 3, 0, 0);
            if (!d.Contains("TabSeamPatchBrush")) d["TabSeamPatchBrush"] = Brushes.Transparent;
            if (!d.Contains("TabActiveRingBrush")) d["TabActiveRingBrush"] = Pick("SelectionAccent", "PrimaryBrush");
            if (!d.Contains("TabFocusThickness")) d["TabFocusThickness"] = new Thickness(1, 3, 1, 0);
            if (!d.Contains("TabFocusPadding")) d["TabFocusPadding"] = new Thickness(11, 1, 4, 5);
            if (!d.Contains("TabFocusFirstThickness")) d["TabFocusFirstThickness"] = new Thickness(0, 3, 1, 0);
            if (!d.Contains("TabFocusFirstPadding")) d["TabFocusFirstPadding"] = new Thickness(12, 1, 4, 5);
            if (!d.Contains("TabFocusLastThickness")) d["TabFocusLastThickness"] = new Thickness(1, 3, 0, 0);
            if (!d.Contains("TabFocusLastPadding")) d["TabFocusLastPadding"] = new Thickness(11, 1, 5, 5);
            if (!d.Contains("TabFocusOnlyThickness")) d["TabFocusOnlyThickness"] = new Thickness(0, 3, 0, 0);
            if (!d.Contains("TabFocusOnlyPadding")) d["TabFocusOnlyPadding"] = new Thickness(12, 1, 5, 5);
            if (!d.Contains("FlyoutCornerRadius")) d["FlyoutCornerRadius"] = new CornerRadius(6);
            // Annotation bars attach directly beneath the main toolbar: their top edge is always
            // square, while the exposed bottom corners follow the theme. 98SE's flyout radius is
            // zero, so this naturally squares all four corners there.
            var flyoutRadius = d["FlyoutCornerRadius"] is CornerRadius fr ? fr : new CornerRadius(6);
            d["AnnotationBarCornerRadius"] = new CornerRadius(
                0, 0, flyoutRadius.BottomRight, flyoutRadius.BottomLeft);
            if (!d.Contains("MenuFontFamily")) d["MenuFontFamily"] = new FontFamily("Segoe UI");
            if (!d.Contains("MenuFontSize")) d["MenuFontSize"] = 12.0;
            if (!d.Contains("MenuItemPadding")) d["MenuItemPadding"] = new Thickness(8, 6, 10, 6);
            if (!d.Contains("ComboButtonMinWidth")) d["ComboButtonMinWidth"] = 22.0;
            if (!d.Contains("ComboChevGlyph")) d["ComboChevGlyph"] = "\uE70D";
            if (!d.Contains("ComboChevFont")) d["ComboChevFont"] = new FontFamily("Segoe MDL2 Assets");
            if (!d.Contains("ComboChevMargin")) d["ComboChevMargin"] = new Thickness(0);
            if (!d.Contains("ComboHighlightTextBrush")) d["ComboHighlightTextBrush"] = Pick("SelectionFg", "TextBrush");
            if (!d.Contains("ScrollBarThickness")) d["ScrollBarThickness"] = 12.0;
            if (!d.Contains("ScrollArrowSize")) d["ScrollArrowSize"] = 0.0;
            if (!d.Contains("ScrollArrowTopBevelMargin")) d["ScrollArrowTopBevelMargin"] = new Thickness(0);
            if (!d.Contains("ScrollThumbRadius")) d["ScrollThumbRadius"] = new CornerRadius(3);
            if (!d.Contains("ScrollThumbMargin")) d["ScrollThumbMargin"] = new Thickness(4, 0, 4, 0);
            if (!d.Contains("ScrollThumbMarginH")) d["ScrollThumbMarginH"] = new Thickness(0, 4, 0, 4);
            if (!d.Contains("ScrollTrackBrush")) d["ScrollTrackBrush"] = Brushes.Transparent;
            if (!d.Contains("ScrollTrackBevelDark")) d["ScrollTrackBevelDark"] = Brushes.Transparent;
            if (!d.Contains("ScrollTrackBevelLight")) d["ScrollTrackBevelLight"] = Brushes.Transparent;
            if (!d.Contains("WindowCornerRadius")) d["WindowCornerRadius"] = new CornerRadius(7);
            if (!d.Contains("PanelCornerRadius")) d["PanelCornerRadius"] = new CornerRadius(6);
            if (!d.Contains("ControlCornerRadius")) d["ControlCornerRadius"] = new CornerRadius(3);
            d["RadWindow"] = d["WindowCornerRadius"];
            d["RadCard"] = d["PanelCornerRadius"];
            d["RadControl"] = d["ControlCornerRadius"];
            var windowRadius = d["WindowCornerRadius"] is CornerRadius wr ? wr.TopLeft : 7;
            d["TitleBarCornerRadius"] = new CornerRadius(windowRadius, windowRadius, 0, 0);
            d["FooterCornerRadius"] = new CornerRadius(0, 0, windowRadius, windowRadius);
            if (!d.Contains("TabCornerRadius"))
            {
                var panelRadius = d["PanelCornerRadius"] is CornerRadius radius ? radius.TopLeft : 6;
                d["TabCornerRadius"] = new CornerRadius(panelRadius, panelRadius, 0, 0);
            }
            if (!d.Contains("TabStripFadeBrush"))
            {
                var end = (Pick("PaneBrush", "SurfaceBrush") as SolidColorBrush)?.Color ?? Colors.Transparent;
                d["TabStripFadeBrush"] = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(end, 1) }
                };
            }
            // KillerShell's selected-tab elevation is a centered, palette-driven shadow.
            // A null resource on 98SE removes the effect completely instead of rasterizing
            // classic text through a zero-opacity effect.
            if (!d.Contains("BarShadowEffect"))
            {
                double opacity = d["BarShadowOpacity"] is double value ? value : 0;
                if (opacity > 0)
                {
                    var shadow = new DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 9,
                        ShadowDepth = 0,
                        Opacity = opacity
                    };
                    shadow.Freeze();
                    d["BarShadowEffect"] = shadow;
                }
                else
                {
                    d["BarShadowEffect"] = null;
                }
            }
            // The document pane's drop shadow, same pattern as BarShadowEffect above: the App.xaml
            // PaneShadow effect is an app-level Freezable, so its DynamicResource opacity froze at
            // startup and 98SE's zero never reached it - the shadow stayed on. Build the effect
            // per theme instead; null removes it completely.
            if (!d.Contains("PaneShadowEffect"))
            {
                double paneOp = d["PaneShadowOpacity"] is double pv ? pv : 0.60;
                if (paneOp > 0)
                {
                    var paneShadow = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 16,
                        ShadowDepth = 5,      // family standard: downward cast over the footer
                        Direction = 270,
                        Opacity = paneOp,
                        RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
                    };
                    paneShadow.Freeze();
                    d["PaneShadowEffect"] = paneShadow;
                }
                else
                {
                    d["PaneShadowEffect"] = null;
                }
            }
            d[SystemColors.HighlightBrushKey] = Pick("SelectionBg", "PrimaryBrush");
            d[SystemColors.HighlightTextBrushKey] = Pick("SelectionFg", "OnPrimaryBrush");
            d[SystemColors.InactiveSelectionHighlightBrushKey] = Pick("SelectionBg", "PrimaryBrush");
            d[SystemColors.InactiveSelectionHighlightTextBrushKey] = Pick("SelectionFg", "OnPrimaryBrush");
        }

        /// <summary>
        /// Call from MainWindow.ContentRendered to fix icon colors on initial load
        /// when the theme was restored from settings (no switch event fires).
        /// </summary>
        public static void RefreshIcons()
        {
            if (Application.Current == null) return;
            foreach (Window w in Application.Current.Windows)
                ForceRender(w);
        }

        private static void ForceRender(DependencyObject node)
        {
            if (node is System.Windows.Controls.Primitives.ToggleButton tb)
            {
                // ClearValue + InvalidateProperty forces style-setter DynamicResources to
                // re-resolve from the updated dictionary without firing Checked/Unchecked
                // event handlers (which would re-trigger Apply and cause an infinite loop).
                tb.ClearValue(Control.ForegroundProperty);
                tb.InvalidateProperty(Control.ForegroundProperty);
            }
            if (node is Control ctrl)
            {
                ctrl.InvalidateProperty(Control.ForegroundProperty);
                ctrl.InvalidateProperty(Control.BackgroundProperty);
                ctrl.InvalidateProperty(Control.BorderBrushProperty);
            }
            if (node is UIElement el) el.InvalidateVisual();
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                ForceRender(VisualTreeHelper.GetChild(node, i));
        }

        private static void SetDwm(IntPtr hwnd, bool dark)
        {
            try
            {
                int value = dark ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));

                // Tint the Win11 1px frame border to the theme's pane border so the
                // window outline follows the palette instead of staying system gray.
                // AppBorderBrush lets a theme override the tone (family standard).
                if ((Application.Current?.TryFindResource("AppBorderBrush")
                     ?? Application.Current?.TryFindResource("PaneBorderBrush")) is SolidColorBrush b)
                {
                    // COLORREF is 0x00BBGGRR
                    int colorref = b.Color.R | (b.Color.G << 8) | (b.Color.B << 16);
                    _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
                }
            }

            catch { /* DWMWA not supported on older Windows builds */ }
        }
    }
}
