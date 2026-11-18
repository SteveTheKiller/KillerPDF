using System;

namespace KillerPDF.Services
{
    /// <summary>
    /// The family UI font stack for an interface language. Neither Killer Sans UI nor Segoe UI has
    /// CJK glyphs, so the first CJK face after them decides how Chinese and Japanese text is drawn:
    /// Simplified Chinese takes Microsoft YaHei UI and Japanese Yu Gothic UI, the faces Windows uses
    /// for those languages. Microsoft JhengHei UI and Nirmala UI stay at the end for characters the
    /// chosen face lacks, and every other language keeps exactly the stack it always had.
    /// </summary>
    internal static class UiFontStack
    {
        /// <summary>The system faces after Killer Sans UI, Segoe UI first.</summary>
        internal static string Fallbacks(string? cultureName) =>
            "Segoe UI, " + CjkFace(cultureName ?? "") + "Microsoft JhengHei UI, Nirmala UI";

        /// <summary>Killer Sans UI over the system faces: the interface stack.</summary>
        internal static string For(string? cultureName) => "./Fonts/#Killer Sans UI, " + Fallbacks(cultureName);

        /// <summary>Killer Sans over the system faces: the title bar's file name stack.</summary>
        internal static string Title(string? cultureName) => "./Fonts/#Killer Sans, " + Fallbacks(cultureName);

        /// <summary>Lacquer over the interface stack: the heading stack.</summary>
        internal static string Heading(string? cultureName) => "./Fonts/#Lacquer, " + For(cultureName);

        /// <summary>A theme's own single face (98SE's Microsoft Sans Serif) over the system faces, so
        /// Chinese and Japanese text still lands on the language's CJK face (#490). A stack the
        /// theme already wrote out is left alone.</summary>
        internal static string WithFallbacks(string face, string? cultureName) =>
            face.IndexOf(',') >= 0 ? face : face + ", " + Fallbacks(cultureName);

        private static string CjkFace(string name)
        {
            if (IsLanguage(name, "ja")) return "Yu Gothic UI, ";
            if (!IsLanguage(name, "zh")) return "";
            bool traditional = name.IndexOf("-Hans", StringComparison.OrdinalIgnoreCase) < 0 &&
                               (name.IndexOf("-Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                                name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                                name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase));
            return traditional ? "" : "Microsoft YaHei UI, ";
        }

        private static bool IsLanguage(string name, string language) =>
            name.Equals(language, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase);
    }
}
