using System.IO;
using System.Text;
using SixLabors.Fonts;

namespace KillerPDF.Services
{
    // ============================================================
    // Font resolution for the SAVE path (#168).
    //
    // The editor is a WPF TextBox, which falls back per character across the
    // whole system font set, so anything typed LOOKS right. The engine save path
    // embeds exactly one selected face and emits .notdef (a box)
    // for every codepoint that face lacks - so CJK, Indic and other non-Latin
    // text was displayed correctly and then saved as boxes.
    //
    // The stock resolver enumerates "*.ttf" ONLY, and on Windows nearly every
    // CJK family ships as a TrueType Collection (.ttc): Yu Gothic, MS Gothic,
    // Meiryo, BIZ UD, Microsoft YaHei, JhengHei, SimSun, MingLiU. They appear
    // in our font picker (that is populated from WPF, which reads .ttc fine),
    // so a user could pick one, see it render, and still get boxes on save.
    //
    // Collection faces are extracted into standalone sfnt bytes before embedding.
    //
    // NOTE ON FILE SIZE: embedded fonts are SUBSET (PdfTrueTypeFont /PdfCIDFont
    // call CreateFontSubSet), so a few Japanese characters cost tens of KB in
    // the output, not megabytes. The exception is fonts with no 'loca' table -
    // i.e. CFF/.otf - which PdfCIDFont embeds WHOLE. That is why .otf is
    // enumerated last and only used when nothing else covers the text.
    // ============================================================
    internal static partial class InstalledFontCatalog
    {
        // faceKey -> the physical face. Keys only need to be unique inside this catalog.
        private static readonly Dictionary<string, FaceFile> Faces = new(StringComparer.OrdinalIgnoreCase);
        // family (lower) -> style -> faceKey
        private static readonly Dictionary<string, Dictionary<FaceStyle, string>> Families = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Lock Gate = new();
        private static bool _indexed;
        // Faces in registration order, so the persisted index replays the same first-wins choices.
        private static readonly List<(string Family, FaceStyle Style, string Path, int FaceIndex)> Order = [];
        private const string IndexFormat = "killerpdf-font-index-2";

        private readonly record struct FaceFile(string Path, int FaceIndex);
        private enum FaceStyle { Regular, Bold, Italic, BoldItalic }

        // ── Index ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Builds or loads the catalog ahead of the first lookup so no render waits on it.</summary>
        internal static void Warm() => EnsureIndexed();

        private static void EnsureIndexed()
        {
            lock (Gate)
            {
                if (_indexed) return;
                _indexed = true;   // set first: a failed scan must not retry on every glyph
                List<string> dirs = FontDirectories();
                string signature = Signature(dirs);
                if (TryLoadIndex(signature)) return;
                foreach (var dir in dirs)
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
                SaveIndex(signature);
            }
        }

        // ── Persisted index ───────────────────────────────────────────────────────────────────
        // Scanning every installed font with the description reader costs hundreds of milliseconds
        // and used to land on the render thread of the first page that needed a non-standard
        // face. The catalog is saved next to the settings, keyed by a fingerprint of the font
        // folders; any added, removed or changed font file changes the fingerprint and triggers a
        // fresh scan.

        private static string IndexPath => Path.Combine(AppDataPaths.UserRoot, "font-index.txt");

        private static string Signature(List<string> dirs)
        {
            var sb = new StringBuilder(IndexFormat);
            foreach (string dir in dirs)
            {
                long count = 0, bytes = 0, newest = 0;
                try
                {
                    foreach (var info in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        count++;
                        bytes += info.Length;
                        long ticks = info.LastWriteTimeUtc.Ticks;
                        if (ticks > newest) newest = ticks;
                    }
                }
                catch { count = -1; }
                sb.Append('|').Append(dir).Append(':').Append(count).Append(':').Append(bytes).Append(':').Append(newest);
            }
            return sb.ToString();
        }

        private static bool TryLoadIndex(string signature)
        {
            try
            {
                string path = IndexPath;
                if (!File.Exists(path)) return false;
                string[] lines = File.ReadAllLines(path);
                if (lines.Length == 0 || lines[0] != signature) return false;
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length != 4 || !Enum.TryParse(parts[1], out FaceStyle style)
                        || !int.TryParse(parts[2], out int faceIndex) || !File.Exists(parts[3]))
                        throw new InvalidDataException("Stale font index.");
                    AddFace(parts[0], style, parts[3], faceIndex);
                }
                return true;
            }
            catch
            {
                Faces.Clear();
                Families.Clear();
                Order.Clear();
                return false;
            }
        }

        private static void SaveIndex(string signature)
        {
            try
            {
                if (Order.Any(face => face.Family.Contains('\t') || face.Family.Contains('\n') || face.Family.Contains('\r')))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
                var lines = new List<string>(Order.Count + 1) { signature };
                foreach (var (family, style, path, faceIndex) in Order)
                    lines.Add(family + "\t" + style + "\t" + faceIndex + "\t" + path);
                File.WriteAllLines(IndexPath, lines);
            }
            catch { /* best effort: the next launch simply scans again */ }
        }

        private static List<string> FontDirectories()
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
                SixLabors.Fonts.FontStyle.Bold       => FaceStyle.Bold,
                SixLabors.Fonts.FontStyle.Italic     => FaceStyle.Italic,
                SixLabors.Fonts.FontStyle.BoldItalic => FaceStyle.BoldItalic,
                _                                    => FaceStyle.Regular,
            };

            AddFace(family, style, path, faceIndex);
        }

        private static void AddFace(string family, FaceStyle style, string path, int faceIndex)
        {
            string faceKey = family + "#" + style + "#" + faceIndex + "#" + Path.GetFileName(path);
            if (!Faces.ContainsKey(faceKey)) Faces[faceKey] = new FaceFile(path, faceIndex);

            if (!Families.TryGetValue(family, out var byStyle))
                Families[family] = byStyle = [];
            if (!byStyle.ContainsKey(style))
            {
                byStyle[style] = faceKey;   // first wins = pattern order
            }
            Order.Add((family, style, path, faceIndex));
        }

        /// <summary>The regular face of a family as standalone font bytes, or null when the family
        /// is not installed. Used by FontCoverage to read the 'cmap' - the collection split has
        /// already happened here, so callers never have to know a face came out of a .ttc.</summary>
        internal static byte[]? RegularFaceBytes(string family)
            => FaceBytes(family, false, false);

        /// <summary>Returns the closest installed face for a family as standalone font bytes.</summary>
        internal static byte[]? FaceBytes(string family, bool bold, bool italic)
        {
            EnsureIndexed();
            if (string.IsNullOrWhiteSpace(family)) return null;
            if (!Families.TryGetValue(family, out var byStyle)) return null;
            var wanted = (bold, italic) switch
            {
                (true, true) => FaceStyle.BoldItalic,
                (true, false) => FaceStyle.Bold,
                (false, true) => FaceStyle.Italic,
                _ => FaceStyle.Regular,
            };
            if (!byStyle.TryGetValue(wanted, out var key)
                && !byStyle.TryGetValue(FaceStyle.Regular, out key))
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
                    var (tag, checksum, srcOff, len) = tabs[i];
                    int e = 12 + i * 16;
                    WriteU32(outBytes, e, tag);
                    WriteU32(outBytes, e + 4, checksum);   // table data is copied verbatim, so it stands
                    WriteU32(outBytes, e + 8, (uint)write);
                    WriteU32(outBytes, e + 12, (uint)len);
                    Buffer.BlockCopy(ttc, srcOff, outBytes, write, len);
                    write += (len + 3) & ~3;              // the pad bytes stay zero
                }
                return outBytes;
            }
            catch { return null; }
        }
    }
}
