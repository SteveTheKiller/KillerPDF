using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerPDF.Services
{
    internal enum Theme { Dark, Light, Black, Blood, Greed, Cyanotic }

    // Accent-hue variants of the Dark theme. Green is the base Dark.xaml (no overlay); the
    // others apply a small overlay dictionary that recolours only the accent-family keys.
    internal enum DarkAccent { Green, Red, Blue, Purple, Orange, Teal }

    internal static class ThemeManager
    {
        // ── P/Invoke ──────────────────────────────────────────────────────

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;

        // ── State ─────────────────────────────────────────────────────────

        private static Theme _current = Theme.Dark;
        // Dark, Light, and Black each remember their own accent independently.
        private static DarkAccent _darkAccent  = DarkAccent.Green;
        private static DarkAccent _lightAccent = DarkAccent.Green;
        private static DarkAccent _blackAccent = DarkAccent.Green;

        public static Theme Current => _current;
        public static DarkAccent DarkAccentChoice  => _darkAccent;
        public static DarkAccent LightAccentChoice => _lightAccent;
        public static DarkAccent BlackAccentChoice => _blackAccent;
        private static DarkAccent AccentFor(Theme t) =>
            t == Theme.Light ? _lightAccent : t == Theme.Black ? _blackAccent : _darkAccent;
        public static DarkAccent AccentChoiceFor(Theme t) => AccentFor(t);

        // True for the theme families that support accent variants.
        private static bool HasAccents(Theme t) =>
            t == Theme.Dark || t == Theme.Light || t == Theme.Black;

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
        /// Called from Window.SourceInitialized to set the native title bar colour.
        /// </summary>
        public static void ApplyDwm(IntPtr hwnd)
        {
            SetDwm(hwnd, _current != Theme.Light);
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
                        SetDwm(hwnd, theme != Theme.Light);
                }
            }
        }

        private static void LoadDict(Theme theme)
        {
            var uri = theme switch
            {
                Theme.Light        => new Uri("pack://application:,,,/Themes/Light.xaml"),
                Theme.Black        => new Uri("pack://application:,,,/Themes/Black.xaml"),
                Theme.Blood        => new Uri("pack://application:,,,/Themes/Blood.xaml"),
                Theme.Greed        => new Uri("pack://application:,,,/Themes/Greed.xaml"),
                Theme.Cyanotic     => new Uri("pack://application:,,,/Themes/Cyanotic.xaml"),
                _                  => new Uri("pack://application:,,,/Themes/Dark.xaml"),
            };

            var newDict = new ResourceDictionary { Source = uri };
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
            }
            else
            {
                merged.Add(newDict);
            }

            // Dark and Light families: overlay the chosen accent hue on top of the base green keys.
            // Green is the base itself, so it needs no overlay (and re-applying the base above
            // already restored green, so switching back from a coloured accent works automatically).
            // Each theme has its own tuned overlay (Dark = bright text on dark; Light = dark text
            // on white), loaded from Accents/<Theme>/<Accent>.xaml.
            var accent = AccentFor(theme);
            if (HasAccents(theme) && accent != DarkAccent.Green)
            {
                // Dark overlays live in Accents/Dark/; Light in Accents/Light/; Black in Accents/Black/.
                string sub = theme == Theme.Light ? "Light/" : theme == Theme.Black ? "Black/" : "Dark/";
                var accentDict = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Themes/Accents/{sub}{accent}.xaml")
                };
                var target = merged[0];
                foreach (object key in accentDict.Keys)
                    target[key] = accentDict[key];
            }

            // "PDF" wordmark colour. In the accent-capable families (Dark/Light/Black) it tracks the chosen
            // accent; in the bold fixed-colour themes (Blood/Greed/Cyanotic) it reads as TextSecondary so it
            // doesn't fight their loud accents. AccentLogo is the brush the wordmark (and a few other marks)
            // bind to; pointing it at the live PrimaryBrush/MutedTextBrush updates them all via DynamicResource.
            {
                var live = merged[0];
                object src = HasAccents(theme) ? live["PrimaryBrush"] : live["MutedTextBrush"];
                if (src is not null) live["AccentLogo"] = src;
            }

            // One SystemIdle pass to nudge any elements whose effective value didn't auto-update
            // (e.g. ControlTemplate trigger bindings with TargetName that missed the per-key signal).
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, (Action)RefreshIcons);
        }

        /// <summary>
        /// Call from MainWindow.ContentRendered to fix icon colours on initial load
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
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));

                // Tint the Win11 1px frame border to the theme's pane border so the
                // window outline follows the palette instead of staying system gray.
                // AppBorderBrush lets a theme override the tone (family standard).
                if ((Application.Current?.TryFindResource("AppBorderBrush")
                     ?? Application.Current?.TryFindResource("PaneBorderBrush")) is SolidColorBrush b)
                {
                    // COLORREF is 0x00BBGGRR
                    int colorref = b.Color.R | (b.Color.G << 8) | (b.Color.B << 16);
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
                }
            }
            catch { /* DWMWA not supported on older Windows builds */ }
        }
    }
}
