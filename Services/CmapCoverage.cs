namespace KillerPDF.Services
{
    // ============================================================
    // Minimal 'cmap' reader: answers "does this face have a glyph for this
    // codepoint" and nothing else (#168). Deliberately small - it does not
    // decode glyph outlines or metrics, it only walks the character-to-glyph
    // table so the save path can pick a font that will not emit boxes.
    //
    // Formats handled: 4 (BMP, the universal one) and 12 (full Unicode, needed
    // for anything past U+FFFF - CJK Ext B, emoji). Format 6 is read too since
    // some older CJK faces still ship it. Anything else yields no ranges, which
    // reads as "cannot promise coverage" and simply moves the chain along.
    // ============================================================
    internal sealed class CmapCoverage
    {
        // Sorted, non-overlapping-enough ranges of covered codepoints. A list plus binary search
        // beats a HashSet here: a CJK face covers tens of thousands of codepoints, and the ranges
        // collapse that to a few hundred entries.
        private readonly List<(int lo, int hi)> _ranges;

        private CmapCoverage(List<(int lo, int hi)> ranges) => _ranges = ranges;

        internal bool Covers(int codePoint)
        {
            int lo = 0, hi = _ranges.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var r = _ranges[mid];
                if (codePoint < r.lo) hi = mid - 1;
                else if (codePoint > r.hi) lo = mid + 1;
                else return true;
            }
            return false;
        }

        // ── Parsing ───────────────────────────────────────────────────────────────────────────

        private static ushort U16(byte[] b, int p) => (ushort)((b[p] << 8) | b[p + 1]);
        private static uint U32(byte[] b, int p) => (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);

        /// <summary>Reads the best available cmap subtable. Returns null when the font has none
        /// this understands, which callers treat as "unknown coverage".</summary>
        internal static CmapCoverage? Parse(byte[] font)
        {
            try
            {
                if (font.Length < 12) return null;
                int numTables = U16(font, 4);
                int cmapOff = -1;
                for (int i = 0; i < numTables; i++)
                {
                    int e = 12 + i * 16;
                    if (e + 16 > font.Length) return null;
                    // 'cmap' = 0x636D6170
                    if (U32(font, e) == 0x636D6170) { cmapOff = (int)U32(font, e + 8); break; }
                }
                if (cmapOff < 0 || cmapOff + 4 > font.Length) return null;

                int numSub = U16(font, cmapOff + 2);
                int best = -1, bestScore = -1;
                for (int i = 0; i < numSub; i++)
                {
                    int rec = cmapOff + 4 + i * 8;
                    if (rec + 8 > font.Length) break;
                    int platform = U16(font, rec);
                    int encoding = U16(font, rec + 2);
                    int off = cmapOff + (int)U32(font, rec + 4);
                    if (off < 0 || off + 2 > font.Length) continue;
                    int format = U16(font, off);

                    // Prefer full-Unicode tables, then BMP ones. (3,10) and (3,1) are the Windows
                    // encodings every Windows font ships; platform 0 is Unicode-proper.
                    int score = (platform, encoding, format) switch
                    {
                        (3, 10, 12) => 100,
                        (0, _, 12)  => 95,
                        (3, 1, 4)   => 80,
                        (0, _, 4)   => 75,
                        (_, _, 12)  => 60,
                        (_, _, 4)   => 50,
                        (_, _, 6)   => 20,
                        _           => -1,
                    };
                    if (score > bestScore) { bestScore = score; best = off; }
                }
                if (best < 0) return null;

                var ranges = U16(font, best) switch
                {
                    4  => ParseFormat4(font, best),
                    6  => ParseFormat6(font, best),
                    12 => ParseFormat12(font, best),
                    _  => null,
                };
                if (ranges is null || ranges.Count == 0) return null;

                ranges.Sort((a, b) => a.lo.CompareTo(b.lo));
                // Merge touching/overlapping ranges so the binary search stays correct and small.
                var merged = new List<(int lo, int hi)>(ranges.Count);
                foreach (var r in ranges)
                {
                    if (merged.Count > 0 && r.lo <= merged[^1].hi + 1)
                    {
                        if (r.hi > merged[^1].hi) merged[^1] = (merged[^1].lo, r.hi);
                    }
                    else merged.Add(r);
                }
                return new CmapCoverage(merged);
            }
            catch { return null; }
        }

        // Format 4: segmented mapping. Segments whose idRangeOffset is 0 map by delta (covered
        // wholesale unless the delta lands on glyph 0); the rest index a glyph array, so each
        // codepoint is checked individually.
        private static List<(int lo, int hi)>? ParseFormat4(byte[] b, int off)
        {
            if (off + 14 > b.Length) return null;
            int segX2 = U16(b, off + 6);
            int seg = segX2 / 2;
            if (seg <= 0) return null;
            int endP = off + 14;
            int startP = endP + segX2 + 2;          // +2 skips reservedPad
            int deltaP = startP + segX2;
            int rangeP = deltaP + segX2;
            if (rangeP + segX2 > b.Length) return null;

            var list = new List<(int, int)>(seg);
            for (int i = 0; i < seg; i++)
            {
                int end = U16(b, endP + i * 2);
                int start = U16(b, startP + i * 2);
                if (start > end) continue;
                if (start == 0xFFFF) continue;      // the mandatory terminator segment
                int delta = (short)U16(b, deltaP + i * 2);
                int rangeOff = U16(b, rangeP + i * 2);

                if (rangeOff == 0)
                {
                    // Glyph = (code + delta) mod 65536. Only a delta that maps something to 0
                    // needs the per-codepoint walk; otherwise the whole segment is covered.
                    if (((start + delta) & 0xFFFF) != 0 && ((end + delta) & 0xFFFF) != 0)
                    {
                        list.Add((start, Math.Min(end, 0xFFFE)));
                        continue;
                    }
                    for (int c = start; c <= end && c <= 0xFFFE; c++)
                        if (((c + delta) & 0xFFFF) != 0) list.Add((c, c));
                    continue;
                }

                int glyphBase = rangeP + i * 2 + rangeOff;
                for (int c = start; c <= end && c <= 0xFFFE; c++)
                {
                    int gp = glyphBase + (c - start) * 2;
                    if (gp + 2 > b.Length) break;
                    if (U16(b, gp) != 0) list.Add((c, c));
                }
            }
            return list;
        }

        // Format 6: a single contiguous run of codes.
        private static List<(int lo, int hi)>? ParseFormat6(byte[] b, int off)
        {
            if (off + 10 > b.Length) return null;
            int first = U16(b, off + 6);
            int count = U16(b, off + 8);
            var list = new List<(int, int)>();
            for (int i = 0; i < count; i++)
            {
                int gp = off + 10 + i * 2;
                if (gp + 2 > b.Length) break;
                if (U16(b, gp) != 0) list.Add((first + i, first + i));
            }
            return list;
        }

        // Format 12: groups of (startChar, endChar, startGlyph) covering all of Unicode.
        private static List<(int lo, int hi)>? ParseFormat12(byte[] b, int off)
        {
            if (off + 16 > b.Length) return null;
            uint nGroups = U32(b, off + 12);
            if (nGroups > 200000) return null;   // implausible: treat as corrupt rather than churn
            var list = new List<(int, int)>((int)Math.Min(nGroups, 4096));
            for (uint i = 0; i < nGroups; i++)
            {
                int g = off + 16 + (int)i * 12;
                if (g + 12 > b.Length) break;
                int start = (int)U32(b, g);
                int end = (int)U32(b, g + 4);
                int startGlyph = (int)U32(b, g + 8);
                if (startGlyph == 0) continue;   // maps onto .notdef: not coverage
                if (start > end || start < 0 || end > 0x10FFFF) continue;
                list.Add((start, end));
            }
            return list;
        }
    }
}
