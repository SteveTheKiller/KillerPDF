using System;
using System.Windows;

namespace KillerPDF.Services
{
    internal enum Locale { EnUS, Es, ZhTW, ZhCN, Tr }

    internal static class LocaleManager
    {
        private static Locale _current = Locale.EnUS;

        public static Locale Current => _current;

        /// <summary>
        /// Call once at startup (after ThemeManager.Initialize) to restore the saved locale.
        /// </summary>
        public static void Initialize()
        {
            var saved = App.GetSetting("Locale");
            _current = Enum.TryParse<Locale>(saved, out var l) ? l : Locale.EnUS;
            ApplyInternal(_current);
        }

        /// <summary>
        /// Switch locale, persist choice, and hot-swap the string ResourceDictionary.
        /// </summary>
        public static void Apply(Locale locale)
        {
            _current = locale;
            App.SetSetting("Locale", locale.ToString());
            ApplyInternal(locale);
        }

        // ── Internal ─────────────────────────────────────────────────────

        private static void ApplyInternal(Locale locale)
        {
            var uri = locale switch
            {
                Locale.Es   => new Uri("pack://application:,,,/Strings/es.xaml"),
                Locale.ZhTW => new Uri("pack://application:,,,/Strings/zh-TW.xaml"),
                Locale.ZhCN => new Uri("pack://application:,,,/Strings/zh-CN.xaml"),
                Locale.Tr   => new Uri("pack://application:,,,/Strings/tr-TR.xaml"),
                _           => new Uri("pack://application:,,,/Strings/en-US.xaml"),
            };

            var dict   = new ResourceDictionary { Source = uri };
            var merged = Application.Current.Resources.MergedDictionaries;

            // Index 0 = theme dict, Index 1 = strings dict
            if (merged.Count > 1)
                merged[1] = dict;
            else
                merged.Add(dict);
        }
    }
}
