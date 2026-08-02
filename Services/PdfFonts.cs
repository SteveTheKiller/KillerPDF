using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using SixLabors.Fonts;

namespace KillerPDF.Services
{
    // ============================================================
    // Font resolution for the SAVE path (#168).
    //
    // The editor is a WPF TextBox, which falls back per character across the
    // whole system font set, so anything typed LOOKS right. The save path is
    // PdfSharpCore, which resolves exactly one face and emits .notdef (a box)
    // for every codepoint that face lacks - so CJK, Indic and other non-Latin
    // text was displayed correctly and then saved as boxes.
    //
    // The stock resolver enumerates "*.ttf" ONLY, and on Windows nearly every
    // CJK family ships as a TrueType Collection (.ttc): Yu Gothic, MS Gothic,
    // Meiryo, BIZ UD, Microsoft YaHei, JhengHei, SimSun, MingLiU. They appear
    // in our font picker (that is populated from WPF, which reads .ttc fine),
    // so a user could pick one, see it render, and still get boxes on save.
    //
    // Enumerating .ttc is necessary but NOT sufficient: PdfSharpCore's parser
    // rejects collections outright -
    //     OpenTypeFontface.Read(): if (startTag == TTCF) throw ...
    //         "TrueType collection fonts are not yet supported"
    // - so the bytes handed back must already be a single standalone face.
    // ExtractTtcFace below rebuilds one, which is why nothing in third_party/
    // needed patching: the engine never sees a 'ttcf' tag.
    //
    // NOTE ON FILE SIZE: embedded fonts are SUBSET (PdfTrueTypeFont /PdfCIDFont
    // call CreateFontSubSet), so a few Japanese characters cost tens of KB in
    // the output, not megabytes. The exception is fonts with no 'loca' table -
    // i.e. CFF/.otf - which PdfCIDFont embeds WHOLE. That is why .otf is
    // enumerated last and only used when nothing else covers the text.
    // ============================================================
    internal sealed class KillerFontResolver : IFontResolver
    {
        public string DefaultFontName => "Arial";

        // faceKey -> the physical face. faceKey is what we hand PdfSharpCore in
        // FontResolverInfo and get back in GetFont, so it just has to be unique.
        private static readonly Dictionary<string, FaceFile> Faces = new(StringComparer.OrdinalIgnoreCase);
        // family (lower) -> style -> faceKey
        private static readonly Dictionary<string, Dictionary<XFontStyle, string>> Families = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();
        private static bool _indexed;

        private readonly record struct FaceFile(string Path, int FaceIndex);

        /// <summary>Installs this resolver process-wide. Call once at startup, BEFORE any XFont is
        /// created - PdfSharpCore caches the resolver on first use and warns on a later swap.</summary>
        internal static void Install()
        {
            try { GlobalFontSettings.FontResolver = new KillerFontResolver(); }
            catch { /* a resolver is already in use; the stock one still works for Latin */ }
        }

        // ── Index ─────────────────────────────────────────────────────────────────────────────

        private static void EnsureIndexed()
        {
            lock (Gate)
            {
                if (_indexed) return;
                _indexed = true;   // set first: a failed scan must not retry on every glyph
                foreach (var dir in FontDirectories())
                {
                    // .ttf first, then .ttc, then .otf - AddFace keeps the first face registered
                    // for a (family, style), so this order is the preference order. .otf is last
                    // because CFF faces embed unsubsetted (see the note above).
                    foreach (var pattern in new[] { "*.ttf", "*.ttc", "*.otf" })
                    {
                        string[] files;
                        try { files = Directory.GetFiles(dir, pattern, SearchOption.AllDirectories); }
                        catch { continue; }
                        foreach (var file in files) IndexFile(file);
                    }
                }
            }
        }

        private static IEnumerable<string> FontDirectories()
        {
            var dirs = new List<string>();
            void Add(string p) { try { if (Directory.Exists(p)) dirs.Add(p); } catch { } }
            Add(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\Fonts"));
            // Per-user installs (fonts installed without admin rights) live here.
            Add(Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\Windows\Fonts"));
            return dirs;
        }

        private static void IndexFile(string path)
        {
            try
            {
                bool isCollection = path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);
                if (isCollection)
                {
                    // One description per face inside the collection; the array index IS the face
                    // index, which is what ExtractTtcFace needs.
                    var descs = FontDescription.LoadFontCollectionDescriptions(path);
                    for (int i = 0; i < descs.Length; i++) AddFace(descs[i], path, i);
                }
                else
                {
                    AddFace(FontDescription.LoadDescription(path), path, 0);
                }
            }
            catch { /* unreadable or exotic font file - skip it, never fail the scan */ }
        }

        private static void AddFace(FontDescription desc, string path, int faceIndex)
        {
            string family = desc.FontFamilyInvariantCulture;
            if (string.IsNullOrWhiteSpace(family)) return;

            var style = desc.Style switch
            {
                SixLabors.Fonts.FontStyle.Bold       => XFontStyle.Bold,
                SixLabors.Fonts.FontStyle.Italic     => XFontStyle.Italic,
                SixLabors.Fonts.FontStyle.BoldItalic => XFontStyle.BoldItalic,
                _                                    => XFontStyle.Regular,
            };

            string faceKey = family + "#" + style + "#" + faceIndex + "#" + Path.GetFileName(path);
            if (!Faces.ContainsKey(faceKey)) Faces[faceKey] = new FaceFile(path, faceIndex);

            if (!Families.TryGetValue(family, out var byStyle))
                Families[family] = byStyle = new Dictionary<XFontStyle, string>();
            if (!byStyle.ContainsKey(style)) byStyle[style] = faceKey;   // first wins = pattern order
        }

        // ── IFontResolver ─────────────────────────────────────────────────────────────────────

        // The interface declares these non-nullable but documents null as "cannot satisfy" (and the
        // stock resolver returns null the same way), so the null-forgiving returns below match the
        // contract as written rather than as annotated.
        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            EnsureIndexed();
            if (string.IsNullOrWhiteSpace(familyName)) return null!;

            if (!Families.TryGetValue(familyName, out var byStyle))
            {
                // WPF's picker can hand back a localized family name on a non-English Windows while
                // this index is keyed on the invariant one. Fall back to a loose match so a font the
                // user can see in the list still resolves.
                var hit = Families.FirstOrDefault(kv =>
                    kv.Key.Replace(" ", "").Equals(familyName.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                if (hit.Value is null) return null!;
                byStyle = hit.Value;
            }

            var want = (isBold, isItalic) switch
            {
                (true, true)  => XFontStyle.BoldItalic,
                (true, false) => XFontStyle.Bold,
                (false, true) => XFontStyle.Italic,
                _             => XFontStyle.Regular,
            };

            // Exact style, else regular, else whatever this family has. PdfSharpCore can simulate
            // the missing emphasis, which is better than failing to resolve the family at all.
            if (byStyle.TryGetValue(want, out var key))
                return new FontResolverInfo(key);
            if (byStyle.TryGetValue(XFontStyle.Regular, out var regular))
                return new FontResolverInfo(regular, isBold, isItalic);
            var any = byStyle.Values.FirstOrDefault();
            return any is null ? null! : new FontResolverInfo(any, isBold, isItalic);
        }

        public byte[] GetFont(string faceName)
        {
            EnsureIndexed();
            if (!Faces.TryGetValue(faceName, out var face)) return null!;
            try
            {
                byte[] bytes = File.ReadAllBytes(face.Path);
                // A collection must be split before PdfSharpCore sees it (it throws on 'ttcf').
                return (IsCollection(bytes) ? ExtractTtcFace(bytes, face.FaceIndex) : bytes) ?? null!;
            }
            catch { return null!; }
        }

        /// <summary>The regular face of a family as standalone font bytes, or null when the family
        /// is not installed. Used by FontCoverage to read the 'cmap' - the collection split has
        /// already happened here, so callers never have to know a face came out of a .ttc.</summary>
        internal static byte[]? RegularFaceBytes(string family)
        {
            EnsureIndexed();
            if (string.IsNullOrWhiteSpace(family)) return null;
            if (!Families.TryGetValue(family, out var byStyle)) return null;
            if (!byStyle.TryGetValue(XFontStyle.Regular, out var key))
            {
                key = byStyle.Values.FirstOrDefault();
                if (key is null) return null;
            }
            if (!Faces.TryGetValue(key, out var face)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(face.Path);
                return IsCollection(bytes) ? ExtractTtcFace(bytes, face.FaceIndex) : bytes;
            }
            catch { return null; }
        }

        // ── TrueType Collection -> standalone face ────────────────────────────────────────────
        // A .ttc is one file holding several faces that SHARE table data: a 'ttcf' header, then one
        // offset table per face, whose directory entries point at tables anywhere in the file. So a
        // face is extracted by copying its tables out into a fresh sfnt with rewritten offsets - no
        // glyph data is touched or re-encoded.

        private static bool IsCollection(byte[] b) =>
            b.Length >= 4 && b[0] == 0x74 && b[1] == 0x74 && b[2] == 0x63 && b[3] == 0x66;   // 'ttcf'

        private static uint ReadU32(byte[] b, int p) =>
            (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);

        private static ushort ReadU16(byte[] b, int p) => (ushort)((b[p] << 8) | b[p + 1]);

        private static void WriteU32(byte[] b, int p, uint v)
        {
            b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v;
        }

        private static void WriteU16(byte[] b, int p, ushort v) { b[p] = (byte)(v >> 8); b[p + 1] = (byte)v; }

        /// <summary>Rebuilds face <paramref name="faceIndex"/> of a TrueType Collection as a
        /// standalone font file. Returns null if the collection is malformed or the index is out
        /// of range, which sends the caller back to its own fallback.</summary>
        private static byte[]? ExtractTtcFace(byte[] ttc, int faceIndex)
        {
            try
            {
                // ttcf header: tag(4) version(4) numFonts(4) then numFonts offsets(4 each)
                if (ttc.Length < 12) return null;
                uint numFonts = ReadU32(ttc, 8);
                if (faceIndex < 0 || faceIndex >= numFonts) return null;
                int offsetPos = 12 + faceIndex * 4;
                if (offsetPos + 4 > ttc.Length) return null;
                int tableDir = (int)ReadU32(ttc, offsetPos);
                if (tableDir < 0 || tableDir + 12 > ttc.Length) return null;

                uint sfntVersion = ReadU32(ttc, tableDir);
                int numTables = ReadU16(ttc, tableDir + 4);
                if (numTables <= 0 || numTables > 512) return null;
                int entries = tableDir + 12;
                if (entries + numTables * 16 > ttc.Length) return null;

                // Lay the new file out: 12-byte header, the directory, then each table padded to a
                // 4-byte boundary (required by the sfnt spec and assumed by table checksums).
                int headerSize = 12 + numTables * 16;
                int total = headerSize;
                var tabs = new (uint tag, uint checksum, int srcOff, int len)[numTables];
                for (int i = 0; i < numTables; i++)
                {
                    int e = entries + i * 16;
                    uint tag = ReadU32(ttc, e);
                    uint sum = ReadU32(ttc, e + 4);
                    int off = (int)ReadU32(ttc, e + 8);
                    int len = (int)ReadU32(ttc, e + 12);
                    if (off < 0 || len < 0 || off + len > ttc.Length) return null;
                    tabs[i] = (tag, sum, off, len);
                    total += (len + 3) & ~3;
                }

                var outBytes = new byte[total];
                WriteU32(outBytes, 0, sfntVersion);
                WriteU16(outBytes, 4, (ushort)numTables);
                // searchRange / entrySelector / rangeShift: derived, and some parsers do read them.
                int pow2 = 1, sel = 0;
                while (pow2 * 2 <= numTables) { pow2 *= 2; sel++; }
                WriteU16(outBytes, 6, (ushort)(pow2 * 16));
                WriteU16(outBytes, 8, (ushort)sel);
                WriteU16(outBytes, 10, (ushort)(numTables * 16 - pow2 * 16));

                int write = headerSize;
                for (int i = 0; i < numTables; i++)
                {
                    var t = tabs[i];
                    int e = 12 + i * 16;
                    WriteU32(outBytes, e, t.tag);
                    WriteU32(outBytes, e + 4, t.checksum);   // table data is copied verbatim, so it stands
                    WriteU32(outBytes, e + 8, (uint)write);
                    WriteU32(outBytes, e + 12, (uint)t.len);
                    Buffer.BlockCopy(ttc, t.srcOff, outBytes, write, t.len);
                    write += (t.len + 3) & ~3;              // the pad bytes stay zero
                }
                return outBytes;
            }
            catch { return null; }
        }
    }
}
