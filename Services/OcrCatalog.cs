namespace KillerPDF.Services
{
    // ============================================================
    // OCR catalog - pure data, no IO, no App, no bootstrap.
    // ============================================================
    //
    // Split out of OcrLanguages.cs so the test project can link THIS file alone. KillerPDF.Tests
    // compiles individual sources rather than referencing the app assembly, and OcrLanguages
    // reaches App.GetSetting, OcrNativeBootstrap and HttpClient - none of which a data check needs.
    //
    // THE RULE THIS FILE ENCODES: OCR languages track interface languages. If KillerPDF ships a UI
    // in a language, it ships an OCR model for that language. There is no interface language whose
    // text the app cannot read.
    //
    // It drifted once already, because nothing checked: the UI shipped hu-HU while the catalog
    // stayed at eleven entries, and killerpdf.net kept claiming OCR covered ten languages and that
    // Polish and Hungarian did not need models. OcrCatalogTests now reads Strings\*.xaml and fails
    // the build if these lists and that folder disagree, and release.ps1 runs the suite.
    internal static class OcrCatalog
    {
        // Tesseract code -> display name. English is NOT bundled; every model downloads on demand
        // into OcrNativeBootstrap.TessDataDir. Order mirrors the language picker (English first,
        // then the LangGroup radios in MainWindow.xaml).
        internal static readonly (string Code, string Name)[] Languages =
        [
            ("eng", "English"),
            ("ben", "Bengali"),
            ("ces", "Czech"),
            ("deu", "German"),
            ("spa", "Spanish"),
            ("fra", "French"),
            ("hun", "Hungarian"),
            ("ita", "Italian"),
            ("jpn", "Japanese"),
            ("kaz", "Kazakh"),
            ("kor", "Korean"),
            ("nor", "Norwegian"),
            ("pol", "Polish"),
            ("por", "Portuguese"),
            ("rus", "Russian"),
            ("slv", "Slovenian"),
            ("swe", "Swedish"),
            ("tur", "Turkish"),
            ("ukr", "Ukrainian"),
            ("vie", "Vietnamese"),
            ("chi_sim", "Chinese (Simplified)"),
            ("chi_tra", "Chinese (Traditional)"),
        ];

        // The Strings\*.xaml locale each model backs. Kept beside the catalog so the two are always
        // edited together; the test asserts this covers exactly the locales that ship.
        internal static readonly (string Locale, string Code)[] LocaleToCode =
        [
            ("en-US", "eng"),
            ("bn", "ben"),
            ("cs-CZ", "ces"),
            ("de-DE", "deu"),
            ("es", "spa"),
            ("fr-FR", "fra"),
            ("hu-HU", "hun"),
            ("it-IT", "ita"),
            ("ja-JP", "jpn"),
            ("kk-KZ", "kaz"),
            ("ko-KR", "kor"),
            ("nb-NO", "nor"),
            ("pl-PL", "pol"),
            ("pt-BR", "por"),
            ("ru-RU", "rus"),
            ("sl-SI", "slv"),
            ("sv-SE", "swe"),
            ("tr-TR", "tur"),
            ("uk-UA", "ukr"),
            ("vi-VN", "vie"),
            ("zh-CN", "chi_sim"),
            ("zh-TW", "chi_tra"),
        ];

        // A tessdata model name: three lowercase letters, optionally followed by one script or
        // variant suffix (chi_sim, aze_cyrl, deu_latf). Anything else is not a file name the app
        // will combine into a path or a download URL.
        internal static bool IsModelCode(string? code)
        {
            if (string.IsNullOrEmpty(code) || code.Length > 12) return false;
            int underscore = code.IndexOf('_');
            string language = underscore < 0 ? code : code[..underscore];
            string suffix = underscore < 0 ? string.Empty : code[(underscore + 1)..];
            return language.Length == 3 && language.All(c => c is >= 'a' and <= 'z')
                && (underscore < 0 || (suffix.Length is >= 3 and <= 5 && suffix.All(c => c is >= 'a' and <= 'z')));
        }
    }
}
