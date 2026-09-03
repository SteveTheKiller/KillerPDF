using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KillerPDF.Services
{
    internal readonly record struct DetectedPdfFontStyle(string Family, bool Bold, bool Italic);

    internal static partial class PdfFontStyle
    {
        // #187: PDF font resources carry POSTSCRIPT names, which are not Windows family names.
        // "ArialMT", "TimesNewRomanPSMT" and "Helvetica" resolve to no installed family, so WPF
        // silently fell back and the save path landed on the default font - the detected face was
        // right but the family never applied, which read as "all formatting lost". Keyed on the
        // name with separators removed, lowercase.
        private static readonly Dictionary<string, string> PsNameMap = new(StringComparer.Ordinal)
        {
            ["helvetica"]         = "Arial",
            ["helveticaneue"]     = "Arial",
            ["arial"]             = "Arial",
            ["arialmt"]           = "Arial",
            ["arialnarrow"]       = "Arial Narrow",
            ["times"]             = "Times New Roman",
            ["timesnewroman"]     = "Times New Roman",
            ["timesnewromanps"]   = "Times New Roman",
            ["timesnewromanpsmt"] = "Times New Roman",
            ["courier"]           = "Courier New",
            ["couriernew"]        = "Courier New",
            ["couriernewps"]      = "Courier New",
            ["couriernewpsmt"]    = "Courier New",
            ["symbol"]            = "Symbol",
            ["zapfdingbats"]      = "Wingdings",
            ["segoeui"]           = "Segoe UI",
        };

        // Return the installed spelling so the editor and font selector use the same family.
        internal static string ResolveInstalledFamily(string requested, IEnumerable<string> installed)
        {
            string key = FamilySeparatorRegex().Replace(requested, "");
            foreach (string family in installed)
                if (string.Equals(FamilySeparatorRegex().Replace(family, ""), key, StringComparison.OrdinalIgnoreCase))
                    return family;
            return "Segoe UI";
        }

        // PDF font resources commonly carry face styling in their PostScript names rather than
        // separate metadata. Keep that styling when a source line is lifted into the text editor.
        internal static DetectedPdfFontStyle FromPdfName(string rawName)
        {
            string name = rawName?.Trim() ?? string.Empty;
            int subset = name.IndexOf('+');
            if (subset >= 0 && subset + 1 < name.Length) name = name[(subset + 1)..];

            bool bold = BoldStyleRegex().IsMatch(name);
            bool italic = ItalicStyleRegex().IsMatch(name);

            // Remove only trailing face tokens. A style word that is genuinely part of a family
            // name elsewhere in the string is left alone.
            string family = TrailingStyleRegex().Replace(name, string.Empty).Trim(' ', '-', '_', ',');

            family = NormalizePsFamily(family);

            if (string.IsNullOrWhiteSpace(family)) family = "Segoe UI";
            return new DetectedPdfFontStyle(family, bold, italic);
        }

        // Maps a face-stripped PostScript family to the Windows family it means (#187). Unknown
        // names get their trailing PS/MT foundry tags dropped and CamelCase split into words
        // ("BookAntiqua" -> "Book Antiqua"), which is how PostScript names encode the family.
        private static string NormalizePsFamily(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return family;

            string key = FamilySeparatorRegex().Replace(family, "").ToLowerInvariant();
            if (PsNameMap.TryGetValue(key, out var mapped)) return mapped;

            // Trailing foundry tags: TimesNewRomanPSMT-style names that are not in the map.
            string trimmed = FoundrySuffixRegex().Replace(family, string.Empty);
            if (trimmed.Length > 0 && trimmed != family)
            {
                key = FamilySeparatorRegex().Replace(trimmed, "").ToLowerInvariant();
                if (PsNameMap.TryGetValue(key, out mapped)) return mapped;
                family = trimmed;
            }

            // CamelCase -> spaced words, only when the name has no separators already.
            if (!family.Contains(' ') && !family.Contains('-') && !family.Contains('_'))
                family = FamilyWordBoundaryRegex().Replace(family, " ");

            return family;
        }

        [GeneratedRegex(@"(bold|semibold|demibold|black|heavy|[-_,]bd(?:mt)?$)", RegexOptions.IgnoreCase)]
        private static partial Regex BoldStyleRegex();

        [GeneratedRegex(@"(italic|oblique|[-_,](?:it|obl)(?:mt)?$)", RegexOptions.IgnoreCase)]
        private static partial Regex ItalicStyleRegex();

        [GeneratedRegex(@"(?:[-_, ]?(?:bolditalic|boldoblique|semibolditalic|demibolditalic|bold|semibold|demibold|black|heavy|italic|oblique|regular|roman|bd|it|obl)(?:mt)?)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrailingStyleRegex();

        [GeneratedRegex(@"[-_, ]")]
        private static partial Regex FamilySeparatorRegex();

        [GeneratedRegex(@"(?:PSMT|PS|MT)$")]
        private static partial Regex FoundrySuffixRegex();

        [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=\d)")]
        private static partial Regex FamilyWordBoundaryRegex();
    }
}
