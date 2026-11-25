using System;
using System.Windows.Media;

namespace KillerPDF.Services
{
    /// <summary>
    /// The family UI font stack for an interface language. Neither Killer Sans UI nor Segoe UI has
    /// CJK glyphs, so the first CJK face after them decides how Chinese, Japanese and Korean text is
    /// drawn: Simplified Chinese takes Microsoft YaHei UI, Japanese Yu Gothic UI and Korean Malgun
    /// Gothic (which has no separate UI cut), the faces Windows uses for those languages.
    /// Microsoft JhengHei UI and Nirmala UI stay at the end for characters the
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

        /// <summary>True for Chinese, Japanese and Korean, whose ideographs and Hangul blocks fill the em
        /// box and read larger than Latin text at the same size.</summary>
        internal static bool IsCjk(string? cultureName) =>
            IsLanguage(cultureName ?? "", "zh") || IsLanguage(cultureName ?? "", "ja") || IsLanguage(cultureName ?? "", "ko");

        /// <summary>How large CJK characters draw against the Latin text around them. Ideographs and Hangul
        /// fill the em box and read a size larger than Latin text at the same font size, so they draw at 90%
        /// while Latin text keeps its size (#490). The Fonts/*.CompositeFont files carry the same scale.</summary>
        internal const double CjkScale = 0.9;

        /// <summary>Hangul Jamo, the CJK radicals through the unified ideographs (kana, bopomofo and Hangul
        /// compatibility jamo among them), Hangul extensions and syllables, compatibility ideographs, vertical and
        /// compatibility forms, fullwidth forms, and the supplementary ideograph planes.</summary>
        private const string CjkRanges =
            "1100-11FF, 2E80-9FFF, A960-A97F, AC00-D7FF, F900-FAFF, FE30-FE4F, FF00-FFEF, 20000-3FFFF";

        /// <summary>The interface stack led by its composite family (Fonts/KillerSansUI-*.CompositeFont), which
        /// draws CJK characters at <see cref="CjkScale"/> on the language's CJK face and everything else in Killer
        /// Sans UI at the stack's own line height (#490). A Chinese file name in an English interface scales the
        /// same way. The plain stack follows, so a missing composite file draws as the plain stack did.</summary>
        internal static string ScaledFor(string? cultureName) =>
            "./Fonts/#Killer Sans UI " + CompositeTag(cultureName ?? "") + ", " + For(cultureName);

        /// <summary>The title stack led by its composite family (Fonts/KillerSans-*.CompositeFont).</summary>
        internal static string ScaledTitle(string? cultureName) =>
            "./Fonts/#Killer Sans " + CompositeTag(cultureName ?? "") + ", " + Title(cultureName);

        /// <summary>Lacquer over the scaled interface stack.</summary>
        internal static string ScaledHeading(string? cultureName) => "./Fonts/#Lacquer, " + ScaledFor(cultureName);

        /// <summary>
        /// A family of system faces (a theme's own face over <see cref="Fallbacks"/>) with CJK characters drawn at
        /// <see cref="CjkScale"/>, as the composite font files do for Killer Sans. Built in code, since the theme
        /// face is not ours to ship a file for. The line height and baseline stay the stack's own.
        /// </summary>
        internal static FontFamily SystemFamily(string stack, string? cultureName)
        {
            var plain = new FontFamily(stack);
            var family = new FontFamily { LineSpacing = plain.LineSpacing, Baseline = plain.Baseline };
            // The CJK map comes first, or the catch-all would take the CJK characters too.
            family.FamilyMaps.Add(new FontFamilyMap
            {
                Unicode = CjkRanges,
                Target = CjkFace(cultureName ?? "") + "Microsoft JhengHei UI, Malgun Gothic",
                Scale = CjkScale,
            });
            family.FamilyMaps.Add(new FontFamilyMap { Unicode = "0000-10FFFF", Target = stack });
            return family;
        }

        /// <summary>The composite family for the language's CJK face: SC (Microsoft YaHei UI), JP (Yu Gothic UI),
        /// KR (Malgun Gothic), or CJK (Microsoft JhengHei UI) for Traditional Chinese and every other language.</summary>
        private static string CompositeTag(string name)
        {
            if (IsLanguage(name, "ja")) return "JP";
            if (IsLanguage(name, "ko")) return "KR";
            return CjkFace(name).Length > 0 ? "SC" : "CJK";
        }

        /// <summary>The space above a caption under its icon. Killer Sans sets the caption's line height, and CJK
        /// glyphs, even at <see cref="CjkScale"/>, start nearly at the top of that line while Latin text starts well
        /// inside it. Chinese and Japanese take 3 more to sit as far below the icon, and Hangul, which rises higher
        /// still, takes 4 (#490).</summary>
        internal static double ToolbarCaptionUnderGap(string? cultureName) =>
            IsLanguage(cultureName ?? "", "ko") ? 6.0 : IsCjk(cultureName) ? 5.0 : 2.0;

        private static string CjkFace(string name)
        {
            if (IsLanguage(name, "ja")) return "Yu Gothic UI, ";
            if (IsLanguage(name, "ko")) return "Malgun Gothic, ";
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
