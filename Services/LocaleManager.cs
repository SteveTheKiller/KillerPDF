using System;
using System.Globalization;
using System.IO;
using System.Windows;

namespace KillerPDF.Services
{
    // The family's 20 UI languages, KillerPDF's set and names. en-US is always the base layer, so a
    // locale that omits a key falls back to English; the chosen locale's file layers on top.
    //
    // The choice is saved by NAME, not ordinal, so members can be reordered freely - but keep these
    // names, because they are the "Locale" setting every app already has stored.
    internal enum Locale { EnUS, Bn, CsCZ, De, Es, Fr, HuHU, ItIT, JaJP, KkKZ, PlPL, RuRU, NbNO, PtBR, TrTR, UkUA, ViVN, ZhCN, ZhTW, SlSI }

    /// <summary>
    /// Hot-swaps the string dictionaries at runtime. Persistence goes through the same
    /// ThemeManager.GetSetting / SetSetting hooks as everything else in the kit ("Locale").
    ///
    /// REQUIRES the App.xaml merged-dictionary layout from the README:
    ///   [0] theme palette (ThemeManager swaps it)
    ///   [1] Controls/Controls.xaml
    ///   [2] Strings/en-US.xaml   (the BASE - always present)
    ///   [3] the chosen locale's overrides (added here at runtime; absent for English)
    /// and Strings/&lt;code&gt;.xaml files built as Resource for each language the app ships. A file
    /// that is missing leaves the app on English instead of crashing.
    /// </summary>
    internal static class LocaleManager
    {
        private static Locale _current = Locale.EnUS;
        public static Locale Current => _current;

        /// <summary>The BCP 47 tag of the language actually in use, such as "de-DE", for reports.</summary>
        public static string LanguageTag { get; private set; } = "en-US";

        /// <summary>
        /// Fired after the string dictionary has been swapped. Anything bound with DynamicResource
        /// follows the swap on its own; this is for text resolved once in code and stored, which
        /// otherwise keeps the language it was created in.
        /// </summary>
        public static event Action? LocaleChanged;

        // The override dictionary this class merged, found again by reference. A fixed slot is
        // not safe: an app merges its own dictionaries after the strings (KillerScan's App.xaml
        // does), and replacing or removing "slot 3" overwrote or dropped them on a language switch.
        private static ResourceDictionary? _override;

        /// <summary>Localized string for a Str_ key. Falls back to the key itself when it is
        /// missing, so a translation gap shows up as itself instead of as blank UI.</summary>
        public static string Loc(string key) =>
            Application.Current.TryFindResource(key) as string ?? key;

        // ---- Translator test mode (KillerPDF #211) ----
        // --lang-file <path> reads an external translation file into the override slot, winning
        // over the saved locale, and re-applies it on every save of the file, so a translator can
        // check string length and context in the live app before the file is built in. Keys the
        // file leaves out fall back to the en-US base like any partial locale.

        /// <summary>Full path of the external translation file, or null. Set it from the
        /// --lang-file switch before <see cref="Initialize"/> runs.</summary>
        internal static string? ExternalFile;

        /// <summary>Raised on the UI thread after a successful live re-apply of the external file,
        /// so the window can rebuild its code-built captions the way a language switch does.</summary>
        internal static event Action? ExternalReloaded;

        private static FileSystemWatcher? _watcher;
        private static System.Windows.Threading.DispatcherTimer? _reloadDebounce;
        private static bool _externalLoadedOnce;

        /// <summary>Call once at startup, after ThemeManager.Initialize, to restore the saved locale,
        /// or on first run the one matching the Windows display language.</summary>
        public static void Initialize()
        {
            if (ParseSaved(ThemeManager.GetSetting("Locale")) is { } saved)
            {
                _current = saved;
            }
            else
            {
                _current = LocaleForCulture(CultureInfo.CurrentUICulture);
                ThemeManager.SetSetting("Locale", _current.ToString());
            }
            ApplyInternal(_current);
        }

        /// <summary>
        /// The saved "Locale" setting, including names an app stored before the enum was unified:
        /// KillerScan saved Japanese as "Ja" where the family name is "JaJP", and Killendar,
        /// KillerShell and KillerShot saved Czech and Italian as "Cs" and "It".
        /// </summary>
        private static Locale? ParseSaved(string? saved)
        {
            switch (saved)
            {
                case "Ja": return Locale.JaJP;
                case "Cs": return Locale.CsCZ;
                case "It": return Locale.ItIT;
            }
            return Enum.TryParse<Locale>(saved, out var locale) && Enum.IsDefined(typeof(Locale), locale) ? locale : null;
        }

        internal static Locale LocaleForCulture(CultureInfo culture)
        {
            string name = culture.Name;
            string language = culture.TwoLetterISOLanguageName;
            if (language == "zh")
                return name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
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
                "sl" => Locale.SlSI,
                "tr" => Locale.TrTR,
                "vi" => Locale.ViVN,
                "uk" => Locale.UkUA,
                "nb" or "no" or "nn" => Locale.NbNO,
                "pt" => Locale.PtBR,
                _ => Locale.EnUS,
            };
        }

        /// <summary>Switch locale, persist the choice, and hot-swap the string dictionary.</summary>
        public static void Apply(Locale locale)
        {
            _current = locale;
            ThemeManager.SetSetting("Locale", locale.ToString());
            ApplyInternal(locale);
            LocaleChanged?.Invoke();
        }

        /// <summary>The Strings/ file for a locale, or null for English (base only).</summary>
        internal static string? FileFor(Locale locale) => locale switch
        {
            Locale.Bn   => "bn",
            Locale.CsCZ => "cs-CZ",
            Locale.De   => "de-DE",
            Locale.Es   => "es",
            Locale.Fr   => "fr-FR",
            Locale.HuHU => "hu-HU",
            Locale.ItIT => "it-IT",
            Locale.JaJP => "ja-JP",
            Locale.KkKZ => "kk-KZ",
            Locale.PlPL => "pl-PL",
            Locale.RuRU => "ru-RU",
            Locale.SlSI => "sl-SI",
            Locale.NbNO => "nb-NO",
            Locale.PtBR => "pt-BR",
            Locale.TrTR => "tr-TR",
            Locale.UkUA => "uk-UA",
            Locale.ViVN => "vi-VN",
            Locale.ZhCN => "zh-CN",
            Locale.ZhTW => "zh-TW",
            _           => null,
        };

        /// <summary>The .NET culture for a locale. Resource dictionaries translate the fixed
        /// interface strings; the culture supplies the generated ones (dates, month and day names,
        /// number formats). Applied only when <see cref="KeepRegionFormats"/> is false; by default
        /// dates and numbers follow the Windows region instead.</summary>
        internal static CultureInfo CultureFor(Locale locale) => new(locale switch
        {
            Locale.Bn   => "bn-BD",
            Locale.Es   => "es-ES",
            Locale.CsCZ => "cs-CZ",
            Locale.De   => "de-DE",
            Locale.Fr   => "fr-FR",
            Locale.HuHU => "hu-HU",
            Locale.ItIT => "it-IT",
            Locale.JaJP => "ja-JP",
            Locale.KkKZ => "kk-KZ",
            Locale.PlPL => "pl-PL",
            Locale.RuRU => "ru-RU",
            Locale.SlSI => "sl-SI",
            Locale.NbNO => "nb-NO",
            Locale.PtBR => "pt-BR",
            Locale.TrTR => "tr-TR",
            Locale.UkUA => "uk-UA",
            Locale.ViVN => "vi-VN",
            Locale.ZhCN => "zh-CN",
            Locale.ZhTW => "zh-TW",
            _           => "en-US",
        });

        // ---- Internal ----

        /// <summary>Leave the thread cultures alone, so dates and numbers keep the Windows region
        /// format whatever the interface language. On by default for every app; set it to false
        /// before <see cref="Initialize"/> to make the cultures follow
        /// the chosen language instead.</summary>
        internal static bool KeepRegionFormats { get; set; } = true;

        private static void ApplyInternal(Locale locale)
        {
            if (!KeepRegionFormats)
            {
                var culture = CultureFor(locale);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }

            var merged = Application.Current.Resources.MergedDictionaries;
            LanguageTag = "en-US";

            // Re-assert the English base so a partial locale falls back to English for missing keys.
            int baseIndex = StringsBaseIndex(merged);
            if (merged.Count > baseIndex)
                merged[baseIndex] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Strings/en-US.xaml") };

            // Translator test mode wins over the chosen locale for the override slot.
            if (ExternalFile is not null)
            {
                if (TryApplyExternal()) { EnsureWatcher(); return; }
                if (!_externalLoadedOnce)
                    MessageBox.Show($"Could not load the translation file:\n{ExternalFile}\n\nCheck the path and the file's XML, then start {AppInfo.DisplayName} again.",
                                    $"{AppInfo.DisplayName} --lang-file", MessageBoxButton.OK, MessageBoxImage.Warning);
                // Fall through to the normal locale so the app still comes up usable.
            }

            string? file = FileFor(locale);
            if (file is not null)
            {
                try
                {
                    SetOverride(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Strings/{file}.xaml") });
                    LanguageTag = file;
                    return;
                }
                catch
                {
                    // Locale file not shipped yet: stay on the English base instead of crashing.
                }
            }
            SetOverride(null);
        }

        /// <summary>Puts the chosen locale's dictionary directly after the English base, replacing
        /// the one this class merged before, or removes it (null) for English. Never touches a
        /// dictionary it did not put there.</summary>
        private static void SetOverride(ResourceDictionary? dictionary)
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            int existing = _override == null ? -1 : merged.IndexOf(_override);
            if (dictionary == null)
            {
                if (existing >= 0) merged.RemoveAt(existing);
                _override = null;
                return;
            }
            if (existing >= 0) merged[existing] = dictionary;
            else merged.Insert(Math.Min(merged.Count, StringsBaseIndex(merged) + 1), dictionary);
            _override = dictionary;
        }

        /// <summary>Where the English string base is merged, found by its source. Slot 2, the
        /// README layout, if it cannot be found.</summary>
        private static int StringsBaseIndex(System.Collections.ObjectModel.Collection<ResourceDictionary> merged)
        {
            for (int i = 0; i < merged.Count; i++)
                if (merged[i].Source?.OriginalString.EndsWith("Strings/en-US.xaml", StringComparison.OrdinalIgnoreCase) == true)
                    return i;
            return 2;
        }

        /// <summary>Loads <see cref="ExternalFile"/> into the override slot. False on any parse or
        /// IO failure; during live reload the last good version simply stays applied, since a
        /// text editor mid-save routinely produces momentarily unreadable or invalid XML.</summary>
        private static bool TryApplyExternal()
        {
            try
            {
                var merged = Application.Current.Resources.MergedDictionaries;
                int baseIndex = StringsBaseIndex(merged);
                if (merged.Count <= baseIndex) return false;
                // Parsed as data, never as XAML: XamlReader would instantiate whatever the file
                // names. Only keys the English base already has are accepted (TranslationFileReader).
                var known = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (object key in merged[baseIndex].Keys) if (key is string name) known.Add(name);
                using var fs = new FileStream(ExternalFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var rd = new ResourceDictionary();
                // An entry of another type than the English one (text where a number belongs, or the
                // reverse) is left out, so the English value stays and no binding gets the wrong type.
                // Text whose placeholders do not fit the English format ({2} where English has only
                // {0} and {1}, or a stray brace) is left out too, so string.Format never throws on it.
                foreach (var entry in TranslationFileReader.Read(fs, known))
                    if (merged[baseIndex][entry.Key] is { } english && english.GetType() == entry.Value.GetType()
                        && (entry.Value is not string text || FitsFormat((string)english, text)))
                        rd[entry.Key] = entry.Value;
                SetOverride(rd);
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
            // watch every shape. Events arrive on a worker thread and usually several per save:
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

        /// <summary>True when <paramref name="text"/> can stand in for <paramref name="english"/> as a
        /// format: it only uses the placeholders English fills. Text that English never formats is
        /// always fine.</summary>
        private static bool FitsFormat(string english, string text)
        {
            var placeholders = System.Text.RegularExpressions.Regex.Matches(english, @"(?<!\{)\{(\d+)");
            if (placeholders.Count == 0) return true;
            int count = 0;
            foreach (System.Text.RegularExpressions.Match m in placeholders)
                count = Math.Max(count, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 1);
            try { _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, text, new object[count]); return true; }
            catch (FormatException) { return false; }
        }
    }
}
