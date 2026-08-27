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
            ("pol", "Polish"),
            ("tur", "Turkish"),
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
            ("pl-PL", "pol"),
            ("tr-TR", "tur"),
            ("zh-CN", "chi_sim"),
            ("zh-TW", "chi_tra"),
        ];
    }
}
