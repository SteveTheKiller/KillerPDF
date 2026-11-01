using System.Windows;

namespace KillerPDF.Services
{
    /// <summary>
    /// The theme menu's Film Grain switch, on by default. Turning it off sets GrainOpacity to 0
    /// in the application resources, which outrank every theme dictionary, so each grain layer
    /// bound to it disappears whatever theme or accent is active. A layer drawn at a fixed
    /// strength (the content pane's own grain) follows FilmGrainVisibility instead. The theme is then
    /// applied again so surfaces that take their grain when the theme changes redraw too.
    /// Alt+Shift+G toggles it in every app (Shell/ThemeFlyout.cs).
    /// </summary>
    internal static class FilmGrain
    {
        private const string SettingKey = "FilmGrain";
        private static int _refreshPending;

        /// <summary>True while the grain texture is shown.</summary>
        internal static bool Enabled { get; private set; } = true;

        /// <summary>Restores the saved choice. Called once at startup, after the theme loads.</summary>
        internal static void Initialize()
        {
            Enabled = ThemeManager.GetSetting(SettingKey) != "0";
            SystemParameters.StaticPropertyChanged -= OnSystemColorsChanged;
            SystemParameters.StaticPropertyChanged += OnSystemColorsChanged;
            Publish();
        }

        private static void OnSystemColorsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (!string.IsNullOrEmpty(args.PropertyName) && args.PropertyName != nameof(SystemParameters.HighContrast)) return;
            var app = Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return;
            if (System.Threading.Interlocked.Exchange(ref _refreshPending, 1) != 0) return;
            app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new System.Action(() =>
            {
                System.Threading.Interlocked.Exchange(ref _refreshPending, 0);
                Publish();
            }));
        }

        /// <summary>Shows or hides the grain on every surface and saves the choice.</summary>
        internal static void Set(bool enabled)
        {
            if (enabled == Enabled) return;
            Enabled = enabled;
            ThemeManager.SetSetting(SettingKey, enabled ? "1" : "0");
            Publish();
            ThemeManager.Apply(ThemeManager.Current);
        }

        private static void Publish()
        {
            var resources = Application.Current?.Resources;
            if (resources == null) return;
            bool visible = Enabled && !SystemParameters.HighContrast;
            if (visible) resources.Remove("GrainOpacity");
            else resources["GrainOpacity"] = 0.0;
            resources["FilmGrainVisibility"] = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
