namespace KillerPDF.Services
{
    // ============================================================
    // Glyph coverage + the fallback chain (#168).
    //
    // The editor is WPF, which falls back per character across every installed
    // font, so anything typed looks right on screen. PdfSharpCore resolves ONE
    // face and emits .notdef (a box) for anything that face lacks. So before
    // drawing, ask which family can actually carry this text.
    //
    // Coverage is read from the font's own 'cmap' table rather than from a
    // helper library: the bytes are already in hand (KillerFontResolver hands
    // back a standalone face, collections included), and parsing the table is
    // deterministic across font-library versions.
    //
    // SCOPE: this picks the best single family for a whole run of text, which
    // is what real documents need - a Japanese face covers Latin too, so a line
    // mixing English and Japanese still lands on one font. Text no ONE can carry
    // (say Japanese and Bengali in the same box) still falls back to the user's
    // font for the uncovered part; that case is what the commit-time warning is
    // for. True per-character run splitting would mean re-implementing
    // XTextFormatter's line breaking, which is not worth it for that tail.
    // ============================================================
    internal static class FontCoverage
    {
        // Per-script preference, first match wins. Sans-first throughout, mirroring what Windows
        // itself falls back to, so a saved file looks like the editor did. Every entry is a family
        // that ships with Windows; missing ones are skipped harmlessly at lookup time.
        private static readonly string[] ChainJapanese  = ["Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic", "Yu Mincho"];
        private static readonly string[] ChainSimplified = ["Microsoft YaHei", "DengXian", "SimSun"];
        private static readonly string[] ChainTraditional = ["Microsoft JhengHei", "MingLiU", "PMingLiU"];
        private static readonly string[] ChainKorean    = ["Malgun Gothic", "Gulim"];
        private static readonly string[] ChainIndic     = ["Nirmala UI"];
        private static readonly string[] ChainThai      = ["Leelawadee UI", "Tahoma"];
        private static readonly string[] ChainArabic    = ["Segoe UI", "Tahoma", "Traditional Arabic"];
        private static readonly string[] ChainDefault   = ["Segoe UI", "Arial", "Tahoma"];

        /// <summary>The family to draw <paramref name="text"/> with: the user's choice when it
        /// covers everything, otherwise the first family in the script's chain that does. Falls
        /// back to the user's choice when nothing covers it, so behavior never gets worse than
        /// before - the caller warns in that case.</summary>
        internal static string PickFamily(string preferred, string? text)
        {
            if (string.IsNullOrEmpty(text)) return preferred;
            if (Covers(preferred, text!)) return preferred;

            foreach (var family in ChainFor(text!))
            {
                if (string.Equals(family, preferred, StringComparison.OrdinalIgnoreCase)) continue;
                if (Covers(family, text!)) return family;
            }
            return preferred;
        }

        /// <summary>True when no installed family in the text's chain can render all of it, so the
        /// save will contain boxes however it is drawn. Drives the commit-time warning.</summary>
        internal static bool WillLoseGlyphs(string preferred, string? text)
            => !string.IsNullOrEmpty(text) && !Covers(PickFamily(preferred, text), text!);

        /// <summary>The characters that survive nothing - what the warning shows the user.</summary>
        internal static string UncoveredChars(string family, string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var cov = CoverageFor(family);
            if (cov is null) return "";
            var bad = new List<char>();
            foreach (int cp in CodePoints(text!))
            {
                if (cov.Covers(cp) || IsIgnorable(cp)) continue;
                string s = char.ConvertFromUtf32(cp);
                foreach (char c in s) if (!bad.Contains(c)) bad.Add(c);
                if (bad.Count >= 12) break;   // a sample is enough; the box could be a whole page
            }
            return new string([.. bad]);
        }

        // ── Chain selection ───────────────────────────────────────────────────────────────────

        // Picked from the first character that needs help, not the first character overall: a line
        // starting "Re: " and continuing in Japanese is Japanese text, not Latin text.
        private static string[] ChainFor(string text)
        {
            foreach (int cp in CodePoints(text))
            {
                if (cp < 0x0370) continue;   // Latin / punctuation: no chain needed to decide
                if (cp is >= 0x3040 and <= 0x30FF) return ChainJapanese;       // kana - unambiguous
                if (cp is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF) return ChainKorean;
                if (cp is >= 0x0E00 and <= 0x0E7F) return ChainThai;
                if (cp is >= 0x0590 and <= 0x08FF) return ChainArabic;          // Hebrew + Arabic
                if (cp is >= 0x0900 and <= 0x0DFF) return ChainIndic;           // Devanagari..Sinhala
                if (cp is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF)
                {
                    // Han with no kana anywhere: Chinese. Traditional-only blocks are rare, so
                    // prefer Simplified and let the Traditional chain cover what it misses.
                    foreach (int c2 in CodePoints(text))
                        if (c2 is >= 0x3040 and <= 0x30FF) return ChainJapanese;
                    return HasTraditionalMarker(text) ? ChainTraditional : ChainSimplified;
                }
            }
            return ChainDefault;
        }

        // Bopomofo is Traditional-only, so it settles the Simplified/Traditional question when a
        // document carries it. Otherwise the Simplified chain leads and Traditional follows.
        private static bool HasTraditionalMarker(string text)
        {
            foreach (int cp in CodePoints(text))
                if (cp is >= 0x3100 and <= 0x312F) return true;
            return false;
        }

        // ── Coverage ──────────────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, CmapCoverage?> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        private static bool Covers(string family, string text)
        {
            var cov = CoverageFor(family);
            if (cov is null) return false;   // not installed / unreadable: cannot promise anything
            foreach (int cp in CodePoints(text))
                if (!cov.Covers(cp) && !IsIgnorable(cp)) return false;
            return true;
        }

        // Whitespace and control characters never render a box, so they must not veto a font.
        private static bool IsIgnorable(int cp) =>
            cp is 0x09 or 0x0A or 0x0D or 0x20 or 0xA0 or 0x200B or 0x200C or 0x200D or 0xFEFF;

        private static CmapCoverage? CoverageFor(string family)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(family, out var hit)) return hit;
                CmapCoverage? cov = null;
                try
                {
                    var bytes = KillerFontResolver.RegularFaceBytes(family);
                    if (bytes is not null) cov = CmapCoverage.Parse(bytes);
                }
                catch { cov = null; }
                Cache[family] = cov;
                return cov;
            }
        }

        private static IEnumerable<int> CodePoints(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    yield return char.ConvertToUtf32(s[i], s[i + 1]);
                    i++;
                }
                else yield return s[i];
            }
        }
    }
}
