using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace KillerPDF.Services
{
    /// <summary>
    /// KillerPDF's side of the family theme engine. ThemeManager.cs is the KillerUI template and is
    /// never edited here; everything particular to KillerPDF comes in through its hooks: the saved
    /// settings, the Dark/Green first run, the keys the kit palette does not define (the values in
    /// Themes/App, their completion and the comparison bar), the app-scope radius and font
    /// mirrors, the icon refresh and the DWM title bar. Every key the kit defines keeps the kit's
    /// value.
    /// </summary>
    internal static partial class AppTheme
    {
        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;

        /// <summary>Call once before ThemeManager.Initialize or InitializeInstaller.</summary>
        internal static void Configure()
        {
            // The Black theme's saved value was once "HighContrast".
            ThemeManager.GetSetting = name =>
            {
                string? value = App.GetSetting(name);
                return name == "Theme" && value == "HighContrast" ? nameof(Theme.Black) : value;
            };
            ThemeManager.SetSetting = App.SetSetting;
            ConfigureTokens();
            ConfigureKit();
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
        }

        /// <summary>The first-run theme and the token hooks, without touching saved settings.</summary>
        internal static void ConfigureTokens()
        {
            ThemeManager.SetDefaults(Theme.Dark, dark: Accent.Green, light: Accent.Green, black: Accent.Green, se98: Accent.Blue);
            ThemeManager.AppTokens = null;
            ThemeManager.AppDerivedTokens = AppDerivedTokens;
        }

        /// <summary>KillerPDF's settings for the shared UI kit files in Controls and Shell.</summary>
        internal static void ConfigureKit()
        {
            // Kit dialog captions (Controls/DialogChrome.cs): title icon and wordmark.
            Controls.DialogChrome.AppName = "KillerPDF";
            Controls.DialogChrome.WordmarkSplit = 6;
            Controls.DialogChrome.IconUri = new Uri("pack://application:,,,/Resources/kp-icon.ico");
        }

        /// <summary>Called from Window.SourceInitialized to set the native title bar color.</summary>
        public static void ApplyDwm(IntPtr hwnd) => SetDwm(hwnd, !UsesLightChrome(ThemeManager.Current));

        // After a theme or accent change, the main window's title bar and frame follow the palette.
        private static void OnThemeChanged()
        {
            var win = Application.Current?.MainWindow;
            if (win == null) return;
            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd != IntPtr.Zero) ApplyDwm(hwnd);
        }

        private static string FileName(Theme theme) => theme == Theme.SE98 ? "98SE" : theme.ToString();

        // The kit owns every key its palette defines, value included. KillerPDF only adds the keys
        // the kit does not define: Themes/App values, the completion below and the comparison bar,
        // each worked out from the kit's finished palette, so a KillerPDF key derived from a kit
        // key follows the kit's value.
        private static void AppDerivedTokens(ResourceDictionary d, Theme theme, Accent accent)
        {
            var palette = BuildPalette(d, theme, accent);
            foreach (object key in palette.Keys)
                if (!d.Contains(key)) d[key] = palette[key];
            // KillerPDF's tabs have no icon, so the kit's 2px tab padding put the title against the
            // tab's left edge. 98SE already pads its tabs 8px and needs nothing more.
            d["TabLabelInset"] = theme == Theme.SE98 ? new Thickness(0) : new Thickness(7, 0, 0, 0);

            var app = Application.Current;
            if (app == null) return;
            // App.xaml owns startup fallbacks for these legacy aliases, and local application
            // resources outrank merged dictionaries. Keep those fallbacks synchronized so 98SE's
            // zero radius actually reaches panes, tabs, flyouts, and dialogs.
            var appResources = app.Resources;
            appResources["RadWindow"] = d["WindowCornerRadius"];
            appResources["RadCard"] = d["PanelCornerRadius"];
            appResources["RadControl"] = d["ControlCornerRadius"];
            appResources["UiFont"] = d["UiFont"];
            // One SystemIdle pass to nudge any elements whose effective value didn't auto-update
            // (e.g. ControlTemplate trigger bindings with TargetName that missed the per-key signal).
            app.Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, (Action)RefreshIcons);
        }

        private static ResourceDictionary? Load(string path)
        {
            try { return new ResourceDictionary { Source = new Uri($"pack://application:,,,/{path}") }; }
            catch (IOException) { return null; }
        }

        // The kit's finished palette with KillerPDF's own keys added and completed.
        private static ResourceDictionary BuildPalette(ResourceDictionary kit, Theme theme, Accent accent)
        {
            var palette = new ResourceDictionary();
            foreach (object key in kit.Keys) palette[key] = kit[key];
            void Add(ResourceDictionary? source)
            {
                if (source == null) return;
                foreach (object key in source.Keys)
                    if (!kit.Contains(key)) palette[key] = source[key];
            }

            var own = Load($"Themes/App/{FileName(theme)}.xaml");
            Add(own);
            if (ThemeManager.SupportsAccents(theme) && accent != Accent.Green)
            {
                string sub = theme == Theme.Light ? "Light/" : theme == Theme.Black ? "Black/" : theme == Theme.SE98 ? "98SE/" : "Dark/";
                var ownAccent = Load($"Themes/App/Accents/{sub}{accent}.xaml");
                Add(ownAccent);
                // The theme file's Install fill is its base accent; a colored accent takes over.
                if (own?.Contains("InstallBtnBg") == true && ownAccent?.Contains("InstallBtnBg") != true)
                    palette["InstallBtnBg"] = palette["PrimaryBrush"];
            }

            CompleteAppPalette(palette);
            if (!palette.Contains("FooterAccentBrush"))
                palette["FooterAccentBrush"] = palette["PrimaryBrush"];
            ApplyComparisonBarPalette(palette, theme, accent);
            return palette;
        }

        private static void ApplyComparisonBarPalette(ResourceDictionary resources, Theme theme, Accent accent)
        {
            object background = resources["PrimaryBrush"];
            object foreground = resources["OnPrimaryBrush"];
            object? effect = null;

            static SolidColorBrush Solid(byte red, byte green, byte blue, byte alpha = 0xff)
            {
                var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
                brush.Freeze();
                return brush;
            }

            switch (theme)
            {
                case Theme.Dark:
                case Theme.Black:
                    if (accent == Accent.Red || (theme == Theme.Black && accent == Accent.Purple))
                    {
                        background = resources["SelectionBg"];
                        foreground = Brushes.White;
                        effect = resources["TextStroke"];
                    }
                    else foreground = Solid(0x24, 0x21, 0x2b);
                    break;
                case Theme.Light:
                    if (accent == Accent.Orange) foreground = Solid(0x1e, 0x1f, 0x22);
                    else
                    {
                        foreground = resources["OnPrimaryBrush"];
                        effect = resources["TextStroke"];
                    }
                    break;
                case Theme.Blood:
                    background = Solid(0x8f, 0x3f, 0x46);
                    foreground = Brushes.White;
                    effect = resources["TextStroke"];
                    break;
                case Theme.Greed:
                    background = Solid(0x17, 0x6a, 0x46);
                    foreground = Brushes.White;
                    effect = resources["TextStroke"];
                    break;
                case Theme.Cyanotic:
                    background = Solid(0x12, 0x6f, 0x9f);
                    foreground = Brushes.White;
                    effect = resources["TextStroke"];
                    break;
                case Theme.Ectoplasm:
                    foreground = Solid(0x5e, 0x17, 0x64);
                    break;
            }

            resources["ComparisonBarBrush"] = background;
            resources["ComparisonBarForegroundBrush"] = foreground;
            resources["ComparisonBarCloseHoverBrush"] = foreground;
            resources["ComparisonBarHoverBrush"] = foreground is SolidColorBrush text && text.Color == Colors.White
                ? Solid(0, 0, 0, 0x26)
                : Solid(0xff, 0xff, 0xff, 0x26);
            resources["ComparisonBarTextEffect"] = effect;
            resources["ComparisonBarVerticalMask"] = BuildComparisonBarVerticalMask(theme);
        }

        private static Brush BuildComparisonBarVerticalMask(Theme theme)
        {
            if (theme == Theme.SE98) return Brushes.Black;

            var mask = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1),
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Black, 0.16),
                    new GradientStop(Colors.Black, 0.84),
                    new GradientStop(Colors.Transparent, 1)
                }
            };
            mask.Freeze();
            return mask;
        }

        private static bool UsesLightChrome(Theme theme) => theme is Theme.Light or Theme.SE98;

        // KillerPDF's own keys only; every key here is one the kit palette does not define.
        private static void CompleteAppPalette(ResourceDictionary d)
        {
            object Pick(string key, string fallback) => d.Contains(key) ? d[key] : d[fallback];
            void Alias(string key, string fallback)
            {
                if (!d.Contains(key)) d[key] = Pick(fallback, "PaneBrush");
            }
            void Default(object key, object value)
            {
                if (!d.Contains(key)) d[key] = value;
            }

            // The footer's status text. KillerPDF has no status light in the footer corner, and the kit's
            // FooterPadding is 0, so 16 restores the inset 1.8.91 shipped. 98SE states its cell padding.
            Default("FooterStatusInset", new Thickness(16, 0, 0, 0));
            // The checked label in the theme menu. Matches RadioAccent unless an accent is too pale to read as text.
            Alias("RadioAccentText", "RadioAccent");
            Alias("BgRecentPanel", "SurfaceBrush");
            // Bars sitting on the document take the menu tier, like the active tab above them.
            Alias("AnnotationBarBrush", "MenuBackgroundBrush");
            Alias("AnnotationBarBorderBrush", "PaneBorderBrush");
            Alias("InstallBtnBg", "PrimaryBrush");
            Alias("DropBorder", "PaneBorderBrush");
            Alias("SettingsOpenRowBg", "RowHoverBrush");
            Alias("BgDragHandle", "PaneBorderBrush");
            Alias("DragLine", "InputBorderBrush");
            Alias("BgCanvas", "PaneBrush");
            Alias("FooterZoomClosedBrush", "MenuBackgroundBrush");
            Alias("FooterZoomSurfaceBrush", "MenuBackgroundBrush");
            Alias("KeyboardKeyBrush", "PaneBrush");
            Alias("SelectionButtonBorderBrush", "PrimaryBrush");
            Default("DangerRed", new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)));
            Default("BgOverlay", new SolidColorBrush(Color.FromArgb(0xbb, 0, 0, 0)));
            Default("HeaderShadowOpacity", 0.5);
            Default("AboutShadowOpacity", d.Contains("FlyoutShadowOpacity") ? d["FlyoutShadowOpacity"] : 0.6);
            Default("AboutIconShadowOpacity", 0.6);
            Default("AboutCaptionVisibility", Visibility.Collapsed);
            Default("AboutModernCloseVisibility", Visibility.Visible);
            Default("ShortcutHeaderShadowOpacity", 0.55);
            Default("ThemeRadioShadowOpacity", 0.5);
            Default("ShortcutCaptionVisibility", Visibility.Collapsed);
            Default("ShortcutModernHeaderVisibility", Visibility.Visible);
            Default("FooterVersionDividerVisibility", Visibility.Visible);
            // Document tools share the File color until a theme gives them their own.
            if (!d.Contains("KsCatDocs") && d.Contains("KsCatFile")) d["KsCatDocs"] = d["KsCatFile"];
            // Tools and OCR share the Edit and Search colors for the same reason.
            if (!d.Contains("KsCatTools") && d.Contains("KsCatEdit")) d["KsCatTools"] = d["KsCatEdit"];
            if (!d.Contains("KsCatOcr") && d.Contains("KsCatSearch")) d["KsCatOcr"] = d["KsCatSearch"];
            Default("ButtonBevelLightThickness", d.Contains("BevelLightThickness") ? d["BevelLightThickness"] : new Thickness(0));
            Default("ButtonBevelDarkThickness", d.Contains("BevelDarkThickness") ? d["BevelDarkThickness"] : new Thickness(0));
            Default("CheckSunkenDarkThickness", new Thickness(0));
            Default("CheckSunkenLightThickness", new Thickness(0));
            Default("RootBorderThickness", new Thickness(1));
            Default("GripHatchVisibility", Visibility.Collapsed);
            Default("ZoomBoxHeight", 28.0);
            Default("ScrollArrowTopBevelMargin", new Thickness(0));
            Default("SidebarInnerDarkThickness", new Thickness(0));
            Default("SidebarInnerDarkVisibility", Visibility.Collapsed);
            Default("SidebarRailBrush", Brushes.Transparent);
            Default("SplitHostMargin", new Thickness(0, 0, 8, 0));
            Default("SplitPaneGutterWidth", 8.0);

            bool classicCaption = d["PlainTitleVisibility"] is Visibility titleVisibility
                                  && titleVisibility == Visibility.Visible;
            d["CaptionFontGlyphVisibility"] = classicCaption ? Visibility.Collapsed : Visibility.Visible;
            d["CaptionDrawnGlyphVisibility"] = classicCaption ? Visibility.Visible : Visibility.Collapsed;
            // The active tab's corner (matched by the card's corners at each end of the band), the
            // title and the close x, as KillerShell has them. 98SE keeps its classic sizes.
            bool compactDialogCaption = d["UseDialogCaption"] is true;
            Default("TabActiveCornerRadius", compactDialogCaption ? new CornerRadius(2, 2, 0, 0) : new CornerRadius(3, 3, 0, 0));
            Default("TabTitleFontSize", compactDialogCaption ? 11.0 : 12.0);
            Default("TabCloseFontSize", compactDialogCaption ? 14.0 : 17.0);
            // Annotation bars attach directly beneath the main toolbar: their top edge is always
            // square, while the exposed bottom corners follow the theme. 98SE's flyout radius is
            // zero, so this naturally squares all four corners there.
            var flyoutRadius = d["FlyoutCornerRadius"] is CornerRadius fr ? fr : new CornerRadius(6);
            d["AnnotationBarCornerRadius"] = new CornerRadius(0, 0, flyoutRadius.BottomRight, flyoutRadius.BottomLeft);
            d["RadWindow"] = d["WindowCornerRadius"];
            d["RadCard"] = d["PanelCornerRadius"];
            d["RadControl"] = d["ControlCornerRadius"];
            var windowRadius = d["WindowCornerRadius"] is CornerRadius wr ? wr.TopLeft : 7;
            d["TitleBarCornerRadius"] = new CornerRadius(windowRadius, windowRadius, 0, 0);
            d["FooterCornerRadius"] = new CornerRadius(0, 0, windowRadius, windowRadius);
            if (!d.Contains("TabStripFadeBrush"))
            {
                var end = (Pick("PaneBrush", "SurfaceBrush") as SolidColorBrush)?.Color ?? Colors.Transparent;
                d["TabStripFadeBrush"] = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(end, 1) }
                };
            }
            Default(SystemColors.HighlightBrushKey, Pick("SelectionBg", "PrimaryBrush"));
            Default(SystemColors.HighlightTextBrushKey, Pick("SelectionFg", "OnPrimaryBrush"));
            Default(SystemColors.InactiveSelectionHighlightBrushKey, Pick("SelectionBg", "PrimaryBrush"));
            Default(SystemColors.InactiveSelectionHighlightTextBrushKey, Pick("SelectionFg", "OnPrimaryBrush"));
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
