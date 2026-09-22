using System;
using System.Globalization;
using System.IO;
using System.Windows;

namespace KillerPDF.Services
{
    internal enum Locale { EnUS, Bn, CsCZ, De, Es, Fr, HuHU, ItIT, JaJP, KkKZ, PlPL, RuRU, TrTR, ViVN, ZhCN, ZhTW }

    internal static class LocaleManager
    {
        private static Locale _current = Locale.EnUS;

        public static Locale Current => _current;

        // ── Translator test mode (#211, thanks bovirus) ─────────────────────────
        // --lang-file <path> loads an external translation xaml as the language override, winning
        // over the saved locale, and re-applies it on every save of the file - so a translation
        // can be checked for string length and context in the live app before it is built in.
        // Untranslated keys fall back to the en-US base like any partial locale.

        /// <summary>Full path of the external translation file, or null. Set from the
        /// --lang-file switch before <see cref="Initialize"/> runs.</summary>
        internal static string? ExternalFile;

        /// <summary>Raised on the UI thread after a successful live re-apply, so the window can
        /// rebuild its code-built captions (toolbar, context menu) the way a language switch does.</summary>
        internal static event Action? ExternalReloaded;

        private static FileSystemWatcher? _watcher;
        private static System.Windows.Threading.DispatcherTimer? _reloadDebounce;
        private static bool _externalLoadedOnce;

        /// <summary>
        /// Call once at startup (after ThemeManager.Initialize) to restore the saved locale.
        /// </summary>
        public static void Initialize()
        {
            var saved = App.GetSetting("Locale");
            if (Enum.TryParse<Locale>(saved, out var locale))
            {
                _current = locale;
            }
            else
            {
                _current = LocaleForCulture(CultureInfo.CurrentUICulture);
                App.SetSetting("Locale", _current.ToString());
            }
            ApplyInternal(_current);
        }

        internal static Locale LocaleForCulture(CultureInfo culture)
        {
            string name = culture.Name;
            string language = culture.TwoLetterISOLanguageName;

            if (language == "zh")
                return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase)
                    ? Locale.ZhTW
                    : Locale.ZhCN;

            return language switch
            {
                "bn" => Locale.Bn,
                "cs" => Locale.CsCZ,
                "de" => Locale.De,
                "es" => Locale.Es,
                "fr" => Locale.Fr,
                "hu" => Locale.HuHU,
                "it" => Locale.ItIT,
                "ja" => Locale.JaJP,
                "kk" => Locale.KkKZ,
                "pl" => Locale.PlPL,
                "ru" => Locale.RuRU,
                "tr" => Locale.TrTR,
                "vi" => Locale.ViVN,
                _ => Locale.EnUS,
            };
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
            var merged = Application.Current.Resources.MergedDictionaries;

            // [0] theme. [1] en-US BASE - always present so any partial locale falls back to English for
            // keys it doesn't translate. [2] the chosen locale's overrides (absent for English).
            if (merged.Count > 1)
                merged[1] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Strings/en-US.xaml") };

            // Translator test mode wins over the chosen locale for the override slot.
            if (ExternalFile is not null)
            {
                if (TryApplyExternal()) { EnsureWatcher(); return; }
                if (!_externalLoadedOnce)
                    MessageBox.Show($"Could not load the translation file:\n{ExternalFile}\n\nCheck the path and the file's XML, then start KillerPDF again.",
                                    "KillerPDF --lang-file", MessageBoxButton.OK, MessageBoxImage.Warning);
                // Fall through to the normal locale so the app still comes up usable.
            }

            Uri? overrideUri = locale switch
            {
                Locale.Bn   => new Uri("pack://application:,,,/Strings/bn.xaml"),
                Locale.CsCZ => new Uri("pack://application:,,,/Strings/cs-CZ.xaml"),
                Locale.De   => new Uri("pack://application:,,,/Strings/de-DE.xaml"),
                Locale.Es   => new Uri("pack://application:,,,/Strings/es.xaml"),
                Locale.Fr   => new Uri("pack://application:,,,/Strings/fr-FR.xaml"),
                Locale.HuHU => new Uri("pack://application:,,,/Strings/hu-HU.xaml"),
                Locale.ItIT => new Uri("pack://application:,,,/Strings/it-IT.xaml"),
                Locale.JaJP => new Uri("pack://application:,,,/Strings/ja-JP.xaml"),
                Locale.KkKZ => new Uri("pack://application:,,,/Strings/kk-KZ.xaml"),
                Locale.PlPL => new Uri("pack://application:,,,/Strings/pl-PL.xaml"),
                Locale.RuRU => new Uri("pack://application:,,,/Strings/ru-RU.xaml"),
                Locale.TrTR => new Uri("pack://application:,,,/Strings/tr-TR.xaml"),
                Locale.ViVN => new Uri("pack://application:,,,/Strings/vi-VN.xaml"),
                Locale.ZhCN => new Uri("pack://application:,,,/Strings/zh-CN.xaml"),
                Locale.ZhTW => new Uri("pack://application:,,,/Strings/zh-TW.xaml"),
                _           => null,   // English: base only
            };

            if (overrideUri is not null)
            {
                var ov = new ResourceDictionary { Source = overrideUri };
                if (merged.Count > 2) merged[2] = ov; else merged.Add(ov);
            }
            else if (merged.Count > 2)
            {
                merged.RemoveAt(2);
            }
        }

        /// <summary>Loads <see cref="ExternalFile"/> into the override slot. False on any parse or
        /// IO failure - during live reload the last good version simply stays applied, since a
        /// text editor mid-save routinely produces momentarily unreadable or invalid XML.</summary>
        private static bool TryApplyExternal()
        {
            try
            {
                using var fs = new FileStream(ExternalFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (System.Windows.Markup.XamlReader.Load(fs) is not ResourceDictionary rd) return false;
                var merged = Application.Current.Resources.MergedDictionaries;
                if (merged.Count > 2) merged[2] = rd; else merged.Add(rd);
                _externalLoadedOnce = true;
                return true;
            }
            catch { return false; }
        }

        private static void EnsureWatcher()
        {
            if (_watcher is not null) return;
            string dir  = Path.GetDirectoryName(ExternalFile!) is { Length: > 0 } d ? d : ".";
            string name = Path.GetFileName(ExternalFile!);
            _watcher = new FileSystemWatcher(dir, name) { EnableRaisingEvents = true };
            // Editors save as write, replace, or delete-and-rename depending on the editor, so
            // watch every shape. Events arrive on a worker thread and usually several per save -
            // marshal to the UI thread and debounce into one re-apply.
            _watcher.Changed += (_, _) => QueueExternalReload();
            _watcher.Created += (_, _) => QueueExternalReload();
            _watcher.Renamed += (_, _) => QueueExternalReload();
        }

        private static void QueueExternalReload()
        {
            Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
            {
                _reloadDebounce ??= NewDebounce();
                _reloadDebounce.Stop();
                _reloadDebounce.Start();
            }));
        }

        private static System.Windows.Threading.DispatcherTimer NewDebounce()
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (TryApplyExternal()) ExternalReloaded?.Invoke();
            };
            return t;
        }
    }
}
