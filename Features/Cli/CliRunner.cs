using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Printing;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
// The scrubs, bitmap helpers, import helpers and PDFium interop all live in Services
// (BitmapHelpers.cs, PdfImport.cs, PdfiumInterop.cs; KillerUI refactor,
// 2026-07-31), called qualified below. No Features-to-Shell reaches remain in this file.
// OpenBatchConsole and FlattenBatchDetail are shared with the batch runner.
using static KillerPDF.Features.BatchRunner;

namespace KillerPDF.Features
{
    // ============================================================
    // Command-line interface
    // ============================================================
    //
    // Dispatcher for every headless CLI command. Invoked from App.OnStartup
    // BEFORE the single-instance mutex, so CLI runs work while a GUI instance
    // is open, never forward to it, and never show a window. A launch with no
    // recognized command flag falls through to the normal GUI (including the
    // classic "KillerPDF.exe file.pdf" file-association open).
    //
    // Each command reuses the same pipeline its GUI equivalent runs - the
    // merge named-destination rewrite, the pre-save scrubs, the PDFium
    // decrypt, the rotation-safe rasterizer, the OCR text-layer builder - so
    // CLI output is byte-for-byte the kind of file the GUI would produce.
    //
    // Exit codes: 0 = success, 1 = operation failed, 2 = bad usage.
    //
    // Console output rides on AttachConsole (GUI-subsystem exe, see
    // BatchMode.cs); lines can interleave with the shell prompt. Exit codes
    // are the scripting contract.
    //
    // Every member here is static and none of them touch the window, so this was never really a
    // MainWindow partial - it just happened to be declared as one. Extracted to its own class
    // 2026-07-31.
    internal static class CliRunner
    {
        // Options that consume the next argument as their value.
        private static readonly string[] CliValueOptions =
        [
            "--log", "--dpi", "--format", "--pages", "--printer", "--lang", "--password", "--copies",
            "--color-mode", "--threshold", "--compression", "--jpeg-quality",
        ];

        /// <summary>
        /// Entry point for all CLI commands. Returns false when args carry no
        /// recognized command (normal GUI launch); otherwise runs the command
        /// and returns true with the process exit code set.
        /// </summary>
        internal static bool TryRunCli(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args is null || args.Length == 0) return false;

            // The validation resave keeps its dedicated runner in BatchMode.cs.
            if (args.Any(a => Eq(a, "--batch-resave")))
                return BatchRunner.TryRunBatch(args, out exitCode);

            string? command = args.FirstOrDefault(a =>
                Eq(a, "--help") || Eq(a, "-h") || Eq(a, "/?") ||
                Eq(a, "--version") || Eq(a, "-v") ||
                Eq(a, "--verify") || Eq(a, "/verify") ||
                Eq(a, "--merge") || Eq(a, "--extract-pages") || Eq(a, "--split") ||
                Eq(a, "--decrypt") || Eq(a, "--to-image") || Eq(a, "--flatten") ||
                Eq(a, "--print") || Eq(a, "--ocr"));
            if (command is null) return false;

            var con = OpenBatchConsole();
            var (positionals, options) = ParseCliArgs(args, command);

            try
            {
                switch (command.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        con.WriteLine(CliHelpText());
                        break;
                    case "--version":
                    case "-v":
                        con.WriteLine(AppVersion.Display);
                        break;
                    case "--verify":
                    case "/verify":
                        exitCode = CliVerifyPayload(con);
                        break;
                    case "--merge":
                        exitCode = CliMerge(positionals, con);
                        break;
                    case "--extract-pages":
                        exitCode = CliExtractPages(positionals, con);
                        break;
                    case "--split":
                        exitCode = CliSplit(positionals, con);
                        break;
                    case "--decrypt":
                        exitCode = CliDecrypt(positionals, options, con);
                        break;
                    case "--to-image":
                        exitCode = CliToImage(positionals, options, con);
                        break;
                    case "--flatten":
                        exitCode = CliFlatten(positionals, options, con);
                        break;
                    case "--print":
                        exitCode = CliPrint(positionals, options, con);
                        break;
                    case "--ocr":
                        exitCode = CliOcr(positionals, options, con);
                        break;
                }
            }
            catch (Exception ex)
            {
                con.WriteLine("Error: " + FlattenBatchDetail(ex.Message));
                exitCode = 1;
            }
            finally
            {
                App.CleanupSessionTemps();   // drop any decrypt/rotation temps the run created
            }
            return true;
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string CliHelpText() => string.Join(Environment.NewLine,
        [
            "KillerPDF " + AppVersion.Display + " - command line usage",
            "",
            "  KillerPDF.exe <file.pdf>                                    open in the app",
            "  KillerPDF.exe --version | -v                                print version",
            "  KillerPDF.exe --help | -h | /?                              this text",
            "  KillerPDF.exe --verify | /verify                            verify every installed payload file",
            "",
            "  --merge <out.pdf> <in1> <in2> ...        merge PDFs (and images) into one PDF",
            "  --extract-pages <in.pdf> <pages> <out.pdf>",
            "                                           pull pages into a new PDF (pages like 1-3,5,9-12)",
            "  --split <in.pdf> <outDir>                write one PDF per page",
            "  --decrypt <in.pdf> <out.pdf> [--password <p>]",
            "                                           remove encryption (lossless when possible)",
            "  --to-image <in.pdf> <outDir> [--dpi <n>] [--format png|jpg] [--pages <range>] [--transparent]",
            "                                           render pages to images (default 150 dpi, png;",
            "                                           background composites to white unless --transparent, png only)",
            "  --flatten <in.pdf> <out.pdf> [--dpi <n>] [--color-mode color|grayscale|black-white]",
            "                                           [--threshold <0-255>] [--compression lossless|jpeg]",
            "                                           [--jpeg-quality <1-100>] rasterize into an uneditable PDF",
            "  --print <in.pdf> [--printer <name>] [--pages <range>] [--copies <n>]",
            "                                           print silently (default printer if none named)",
            "  --ocr <in.pdf> <out.pdf> [--lang <code>] add an invisible searchable text layer (default eng;",
            "                                           other languages download on first use)",
            "  --batch-resave <in> <out> [--log <f.csv>] [--quiet]",
            "                                           resave a file or tree through the standard",
            "                                           open/save pipeline (validation harness)",
            "",
            "Exit codes: 0 success, 1 operation failed, 2 bad usage.",
            "Runs headless and works while the KillerPDF window is open.",
        ]);

        private static int CliVerifyPayload(TextWriter output)
        {
            PayloadIntegrityResult result = PayloadIntegrityVerifier.Verify(AppContext.BaseDirectory);
            if (result.Success)
            {
                output.WriteLine($"PASS: verified {result.VerifiedFiles} payload files.");
                return 0;
            }
            output.WriteLine("FAIL: installed payload verification failed.");
            foreach (string error in result.Errors) output.WriteLine("  " + error);
            return 1;
        }

        /// <summary>
        /// Splits args into positionals (everything after the command flag that
        /// is not an option) and an option dictionary (case-insensitive keys).
        /// </summary>
        private static (List<string> Positionals, Dictionary<string, string> Options)
            ParseCliArgs(string[] args, string command)
        {
            var positionals = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int start = Array.FindIndex(args, a => Eq(a, command)) + 1;
            for (int i = start; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    if (CliValueOptions.Any(o => Eq(o, a)) && i + 1 < args.Length)
                        options[a] = args[++i];
                    else
                        options[a] = string.Empty;
                }
                else
                {
                    positionals.Add(a);
                }
            }
            return (positionals, options);
        }

        /// <summary>
        /// Parses a 1-based page range spec like "1-3,5,9-12" into sorted,
        /// distinct 0-based indices. Returns null with a message in error when
        /// the spec is malformed or out of range.
        /// </summary>
        // internal, not private: FileOperations still calls these two. They are page-range parsing
        // and JPEG encoding, neither of which is really CLI-specific - they want a home in
        // Services/ eventually.
        internal static List<int>? CliParsePageRange(string spec, int pageCount, out string error)
        {
            error = string.Empty;
            var pages = new SortedSet<int>();
            foreach (var rawPart in spec.Split(','))
            {
                var part = rawPart.Trim();
                if (part.Length == 0) continue;
                int a, b;
                int dash = part.IndexOf('-');
                if (dash > 0)
                {
                    if (!int.TryParse(part[..dash].Trim(), out a) ||
                        !int.TryParse(part[(dash + 1)..].Trim(), out b))
                    { error = $"Bad page range: \"{part}\""; return null; }
                }
                else
                {
                    if (!int.TryParse(part, out a)) { error = $"Bad page number: \"{part}\""; return null; }
                    b = a;
                }
                if (a > b) (a, b) = (b, a);
                if (a < 1 || b > pageCount)
                { error = $"Pages {part} out of range - the document has {pageCount} pages"; return null; }
                for (int p = a; p <= b; p++) pages.Add(p - 1);
            }
            if (pages.Count == 0) { error = "Empty page range"; return null; }
            return [.. pages];
        }

        // ============================================================
        // --merge <out.pdf> <in1> <in2> ...
        // ============================================================
        // The engine imports complete PDF graphs and authors one page per image
        // frame, retaining input order and a leading PDF's original byte prefix.
        private static int CliMerge(List<string> pos, TextWriter con)
        {
            if (pos.Count < 3)
            {
                con.WriteLine("Usage: KillerPDF.exe --merge <out.pdf> <in1.pdf> <in2.pdf> ...");
                return 2;
            }
            string outPath = Path.GetFullPath(pos[0]);
            var inputs = pos.Skip(1).Select(Path.GetFullPath).ToList();

            foreach (var f in inputs)
            {
                if (!File.Exists(f)) { con.WriteLine($"Input not found: {f}"); return 2; }
                if (string.Equals(f, outPath, StringComparison.OrdinalIgnoreCase))
                { con.WriteLine("Output file cannot also be an input."); return 2; }
            }

            byte[] merged = PdfEngineIntegration.MergeFiles(inputs);
            CliEnsureParentDir(outPath);
            File.WriteAllBytes(outPath, merged);
            int pageCount = KillerPdf.Engine.Documents.PdfDocumentInformation
                .Read(KillerPdf.Engine.Documents.PdfDocument.Open(merged)).PageCount;
            con.WriteLine($"Merged {inputs.Count} files ({pageCount} pages) -> {outPath}");
            return 0;
        }

        // ============================================================
        // --extract-pages <in.pdf> <range> <out.pdf>
        // ============================================================
        // Same primitive as the GUI extract (PageOperations.cs Split_Click):
        // Import-mode open, AddPage per selected index, save a fresh document.
        private static int CliExtractPages(List<string> pos, TextWriter con)
        {
            if (pos.Count != 3)
            {
                con.WriteLine("Usage: KillerPDF.exe --extract-pages <in.pdf> <pages> <out.pdf>   (pages like 1-3,5,9-12)");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), spec = pos[1], outPath = Path.GetFullPath(pos[2]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            byte[] source = File.ReadAllBytes(inPath);
            int pageCount = KillerPdf.Engine.Documents.PdfDocumentInformation
                .Read(KillerPdf.Engine.Documents.PdfDocument.Open(source)).PageCount;
            var indices = CliParsePageRange(spec, pageCount, out string err);
            if (indices is null) { con.WriteLine(err); return 2; }

            CliEnsureParentDir(outPath);
            File.WriteAllBytes(outPath, PdfEngineIntegration.ExtractPages(source, indices));
            con.WriteLine($"Extracted {indices.Count} pages -> {outPath}");
            return 0;
        }

        // ============================================================
        // --split <in.pdf> <outDir>
        // ============================================================
        private static int CliSplit(List<string> pos, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --split <in.pdf> <outputFolder>");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outDir = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            Directory.CreateDirectory(outDir);

            byte[] source = File.ReadAllBytes(inPath);
            IReadOnlyList<byte[]> pages = PdfEngineIntegration.SplitPages(source);
            string baseName = Path.GetFileNameWithoutExtension(inPath);
            int digits = Math.Max(3, pages.Count.ToString().Length);
            for (int i = 0; i < pages.Count; i++)
            {
                File.WriteAllBytes(
                    Path.Combine(outDir, $"{baseName}-page-{(i + 1).ToString().PadLeft(digits, '0')}.pdf"),
                    pages[i]);
            }
            con.WriteLine($"Split {pages.Count} pages into {outDir}");
            return 0;
        }

        // ============================================================
        // --decrypt <in.pdf> <out.pdf> [--password <p>]
        // ============================================================
        // Without a password: the same lossless PDFium strip the GUI uses at
        // open time (owner/permissions encryption), with the Import-rebuild
        // fallback. With a password, The KillerPDF.Engine authenticates and
        // fully rewrites the document without its encryption dictionary.
        private static int CliDecrypt(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --decrypt <in.pdf> <out.pdf> [--password <password>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            CliEnsureParentDir(outPath);

            options.TryGetValue("--password", out string? password);
            if (!string.IsNullOrEmpty(password))
            {
                PdfEngineIntegration.RemoveEncryption(inPath, outPath, password!);
                con.WriteLine($"Decrypted -> {outPath}");
                return 0;
            }

            if (PdfiumInterop.TryPdfiumStripEncryption(inPath, outPath))
            {
                con.WriteLine($"Decrypted (lossless) -> {outPath}");
                return 0;
            }
            if (PdfImport.TryImportRepairToPath(inPath, outPath))
            {
                con.WriteLine($"Decrypted via page rebuild -> {outPath} (bookmarks/forms may be dropped)");
                return 0;
            }
            con.WriteLine("Could not decrypt. If the file needs a password to open, pass --password.");
            return 1;
        }

        // ============================================================
        // Shared rasterization prep
        // ============================================================
        // PDFium sizes its bitmap from the un-rotated MediaBox, so pages with
        // /Rotate 90/270 clip if rendered directly (same reason TempReload
        // strips rotations app-wide). Prep: decrypt if needed, capture per-page
        // /Rotate + point dims, strip rotations to a temp, and let callers
        // rotate the pixel buffers afterward (BitmapHelpers.RotateBitmap).
        // Falls back to rendering the file as-is when PdfSharpCore cannot open
        // it (rare parser gaps PDFium tolerates); callers then derive
        // dimensions from the rendered pixels.
        private static (string RenderPath, int[]? Rotations, (double WPt, double HPt)[]? Dims)
            CliPrepareRenderSource(string inPath, string? password, TextWriter con)
        {
            string workPath = inPath;
            if (PdfImport.PdfFileHasEncryption(inPath))
            {
                var dec = App.MakeTempFile("clidec");
                if (!string.IsNullOrEmpty(password))
                {
                    PdfEngineIntegration.RemoveEncryption(inPath, dec, password!);
                }
                else if (!PdfiumInterop.TryPdfiumStripEncryption(inPath, dec) && !PdfImport.TryImportRepairToPath(inPath, dec))
                {
                    throw new InvalidOperationException(
                        "File is encrypted and could not be unlocked - pass --password if it needs one.");
                }
                workPath = dec;
            }

            try
            {
                IReadOnlyList<KillerPdf.Engine.Documents.PdfPageInformation> pages =
                    PdfEngineIntegration.ReadPageInformation(workPath);
                var rotations = new int[pages.Count];
                var dims = new (double WPt, double HPt)[pages.Count];
                bool anyRot = false;
                for (int i = 0; i < pages.Count; i++)
                {
                    rotations[i] = pages[i].Rotation;
                    dims[i] = (pages[i].Width, pages[i].Height);
                    if (rotations[i] != 0) anyRot = true;
                }
                if (!anyRot) return (workPath, rotations, dims);

                var renderTemp = App.MakeTempFile("clirender");
                PdfEngineIntegration.CreateZeroRotationCopy(workPath, renderTemp);
                return (renderTemp, rotations, dims);
            }
            catch (Exception ex)
            {
                con.WriteLine("Note: structure parse failed (" + FlattenBatchDetail(ex.Message) +
                              ") - rendering as-is; rotated pages may clip.");
                return (workPath, null, null);
            }
        }

        // Both encoders live in Services/BitmapHelpers.cs (RenderToPng, and EncodeJpeg - which
        // was born here as CliEncodeJpeg when --to-image needed a JPEG encoder).

        private static void CliEnsureParentDir(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        private static double CliParseDpi(Dictionary<string, string> options, double fallback)
        {
            if (options.TryGetValue("--dpi", out var s) &&
                double.TryParse(s, out double d) && d >= 24 && d <= 1200)
                return d;
            return fallback;
        }

        // ============================================================
        // --to-image <in.pdf> <outDir> [--dpi n] [--format png|jpg] [--pages range] [--transparent]
        // ============================================================
        // PDFium leaves unpainted background pixels as BGRA 0,0,0,0. Encoders
        // that drop alpha (JPEG) then show them BLACK, and PNG/flatten output
        // carries a useless full-page alpha channel (issue #148, Ryokoxx).
        // Default is now composite-over-white via Docnet's transparency
        // remover; --transparent keeps the raw alpha for PNG output.
        private static int CliToImage(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --to-image <in.pdf> <outputFolder> [--dpi <n>] [--format png|jpg] [--pages <range>] [--transparent]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outDir = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            double dpi = CliParseDpi(options, 150);
            options.TryGetValue("--format", out var fmtRaw);
            string fmt = (fmtRaw ?? "png").ToLowerInvariant();
            if (fmt == "jpeg") fmt = "jpg";
            if (fmt != "png" && fmt != "jpg") { con.WriteLine("--format must be png or jpg"); return 2; }
            // JPEG has no alpha channel, so --transparent only means anything for png.
            bool transparent = fmt == "png" && options.ContainsKey("--transparent");
            Directory.CreateDirectory(outDir);

            options.TryGetValue("--password", out var password);
            var (renderPath, rotations, _) = CliPrepareRenderSource(inPath, password, con);

            using var renderSession =
                PdfPageRenderSession.OpenEngineFirst(renderPath, dpi / 72.0);
            int pageCount = renderSession.PageCount;

            List<int> selected;
            if (options.TryGetValue("--pages", out var rangeSpec))
            {
                var parsed = CliParsePageRange(rangeSpec, pageCount, out string err);
                if (parsed is null) { con.WriteLine(err); return 2; }
                selected = parsed;
            }
            else
            {
                selected = [.. Enumerable.Range(0, pageCount)];
            }

            string baseName = Path.GetFileNameWithoutExtension(inPath);
            int digits = Math.Max(3, pageCount.ToString().Length);
            foreach (var idx in selected)
            {
                PdfRenderedPage rendered = renderSession.RenderPage(idx, transparent,
                    removeTransparencyOnFallback: !transparent);
                byte[] raw = rendered.Pixels;
                int w = rendered.Width, h = rendered.Height;
                int rot = rotations != null && idx < rotations.Length ? rotations[idx] : 0;
                if (rot != 0) (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);
                var bytes = fmt == "png" ? BitmapHelpers.RenderToPng(raw, w, h, dpi) : BitmapHelpers.EncodeJpeg(raw, w, h, dpi);
                var name = $"{baseName}-page-{(idx + 1).ToString().PadLeft(digits, '0')}.{fmt}";
                File.WriteAllBytes(Path.Combine(outDir, name), bytes);
            }
            con.WriteLine($"Rendered {selected.Count} pages at {dpi:0} dpi ({fmt}) into {outDir}");
            return 0;
        }

        // ============================================================
        // --flatten <in.pdf> <out.pdf> [output options]
        // ============================================================
        // Same rasterize-and-rebuild the GUI's Save Flattened runs (150 dpi
        // default, engine-authored image pages sized in points), plus the rotation
        // handling the GUI gets for free from its normalized working copy.
        private static int CliFlatten(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --flatten <in.pdf> <out.pdf> [--dpi <n>] [--color-mode color|grayscale|black-white] [--threshold <0-255>] [--compression lossless|jpeg] [--jpeg-quality <1-100>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            double dpi = CliParseDpi(options, 150);
            PageColorMode colorMode = PageColorMode.Color;
            if (options.TryGetValue("--color-mode", out string? colorRaw))
            {
                colorMode = colorRaw.ToLowerInvariant() switch
                {
                    "color" => PageColorMode.Color,
                    "grayscale" or "gray" => PageColorMode.Grayscale,
                    "black-white" or "blackandwhite" or "bitonal" => PageColorMode.BlackAndWhite,
                    _ => throw new ArgumentException(
                        "--color-mode must be color, grayscale, or black-white."),
                };
            }
            int threshold = ParseBoundedIntOption(options, "--threshold", 160, 0, 255);
            int jpegQuality = ParseBoundedIntOption(options, "--jpeg-quality", 85, 1, 100);
            bool useJpeg = false;
            if (options.TryGetValue("--compression", out string? compressionRaw))
            {
                useJpeg = compressionRaw.ToLowerInvariant() switch
                {
                    "lossless" => false,
                    "jpeg" => true,
                    _ => throw new ArgumentException("--compression must be lossless or jpeg."),
                };
            }
            if (colorMode == PageColorMode.BlackAndWhite && useJpeg)
                throw new ArgumentException("Black-and-white flattening requires lossless compression.");
            options.TryGetValue("--password", out var password);

            var (renderPath, rotations, dims) = CliPrepareRenderSource(inPath, password, con);

            using var renderSession =
                PdfPageRenderSession.OpenEngineFirst(renderPath, dpi / 72.0);
            int pageCount = renderSession.PageCount;
            PdfDocument renderDocument = PdfDocument.Open(File.ReadAllBytes(renderPath));
            IReadOnlyList<bool> bitonalHints =
                PdfPageRasterInformation.ReadBitonalImagePageHints(renderDocument);
            IReadOnlyList<bool> jpegHints =
                PdfPageRasterInformation.ReadJpegImagePageHints(renderDocument);

            var pages = new List<PdfEngineIntegration.RasterPage>(pageCount);
            for (int i = 0; i < pageCount; i++)
            {
                // Composite over white (#148): keeps the /SMask alpha channel out
                // of the rebuilt page images entirely.
                // #141: WithAnnotations, or the rebuild drops the file's own markup.
                PdfRenderedPage rendered = renderSession.RenderPage(
                    i, removeTransparencyOnFallback: true);
                byte[] raw = rendered.Pixels;
                int w = rendered.Width, h = rendered.Height;
                int rot = rotations != null && i < rotations.Length ? rotations[i] : 0;
                if (rot != 0) (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);
                double wPt, hPt;
                if (dims != null && i < dims.Length)
                {
                    // Page keeps its point size; swap for the viewed orientation.
                    bool swap = rot == 90 || rot == 270;
                    wPt = swap ? dims[i].HPt : dims[i].WPt;
                    hPt = swap ? dims[i].WPt : dims[i].HPt;
                }
                else
                {
                    wPt = w * 72.0 / dpi;
                    hPt = h * 72.0 / dpi;
                }

                if (colorMode == PageColorMode.Color && !useJpeg
                    && PdfPageRasterInformation.TryReadFullPageJpeg(
                        renderDocument, i, out PdfImage? original)
                    && original is not null
                    && OutputPixelDimensions.MatchesDpi(
                        original.Width, original.Height, wPt, hPt, dpi))
                {
                    pages.Add(new PdfEngineIntegration.RasterPage(
                        original.Width, original.Height, wPt, hPt,
                        ReadOnlyMemory<byte>.Empty, original.Data));
                    continue;
                }

                PageQualityConverter.ApplyColorModeToBgra(raw, colorMode, threshold);
                bool bitonal = colorMode == PageColorMode.BlackAndWhite
                    || colorMode == PageColorMode.Color
                    && i < bitonalHints.Count && bitonalHints[i]
                    && BitonalPageDetector.IsOpaqueGrayscaleBgra(raw, w, h);
                bool grayscale = colorMode == PageColorMode.Grayscale;
                bool encodeJpeg = !bitonal && (useJpeg
                    || colorMode == PageColorMode.Color
                    && i < jpegHints.Count && jpegHints[i]);
                ReadOnlyMemory<byte> jpeg = encodeJpeg
                    ? BitmapHelpers.EncodeJpeg(raw, w, h, dpi, jpegQuality, grayscale)
                    : default;
                pages.Add(new PdfEngineIntegration.RasterPage(
                    w, h, wPt, hPt, raw, jpeg,
                    Bitonal: bitonal, Grayscale: grayscale));
            }
            CliEnsureParentDir(outPath);
            File.WriteAllBytes(outPath, PdfEngineIntegration.CreateRasterDocument(pages));
            con.WriteLine($"Flattened {pageCount} pages at {dpi:0} dpi -> {outPath}");
            return 0;
        }

        private static int ParseBoundedIntOption(
            Dictionary<string, string> options, string name, int fallback, int minimum, int maximum)
        {
            if (!options.TryGetValue(name, out string? raw)) return fallback;
            if (!int.TryParse(raw, out int value) || value < minimum || value > maximum)
                throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
            return value;
        }

        // ============================================================
        // --print <in.pdf> [--printer name] [--pages range] [--copies n]
        // ============================================================
        // Slimmed headless version of the GUI print spool: rasterize at 300
        // dpi (the GUI's print resolution), fit-scale each page centered on
        // the printable area, build a FixedDocument, and write it to the
        // queue via XPS. Copies replicate the page sequence (ticket CopyCount
        // is unreliable across drivers - same reason the GUI does this, #83).
        private static int CliPrint(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 1)
            {
                con.WriteLine("Usage: KillerPDF.exe --print <in.pdf> [--printer <name>] [--pages <range>] [--copies <n>]");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }

            int copies = 1;
            if (options.TryGetValue("--copies", out var copiesRaw) &&
                (!int.TryParse(copiesRaw, out copies) || copies < 1 || copies > 99))
            { con.WriteLine("--copies must be 1-99"); return 2; }

            options.TryGetValue("--password", out var password);
            var (renderPath, rotations, _) = CliPrepareRenderSource(inPath, password, con);

            // Resolve the print queue. Match --printer against FullName,
            // exact first then substring, both case-insensitive.
            using var server = new LocalPrintServer();
            PrintQueue? queue = null;
            if (options.TryGetValue("--printer", out var printerName) && !string.IsNullOrWhiteSpace(printerName))
            {
                var queues = server.GetPrintQueues(
                    [EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]).ToList();
                queue = queues.FirstOrDefault(q =>
                            string.Equals(q.FullName, printerName, StringComparison.OrdinalIgnoreCase))
                     ?? queues.FirstOrDefault(q =>
                            q.FullName.Contains(printerName, StringComparison.OrdinalIgnoreCase));
                if (queue is null)
                {
                    con.WriteLine($"Printer not found: {printerName}. Available:");
                    foreach (var q in queues) con.WriteLine("  " + q.FullName);
                    return 2;
                }
            }
            else
            {
                queue = LocalPrintServer.GetDefaultPrintQueue();
            }

            // Rasterize the selected pages at 300 dpi, rotation-corrected.
            var bitmaps = new List<(BitmapSource Bs, int W, int H)>();
            List<int> selected;
            using (var renderSession = PdfPageRenderSession.OpenEngineFirst(
                renderPath, 300.0 / 72.0))
            {
                int pageCount = renderSession.PageCount;
                if (options.TryGetValue("--pages", out var rangeSpec))
                {
                    var parsed = CliParsePageRange(rangeSpec, pageCount, out string err);
                    if (parsed is null) { con.WriteLine(err); return 2; }
                    selected = parsed;
                }
                else
                {
                    selected = [.. Enumerable.Range(0, pageCount)];
                }
                foreach (var idx in selected)
                {
                    PdfRenderedPage rendered = renderSession.RenderPage(idx);
                    byte[] raw = rendered.Pixels;
                    int w = rendered.Width, h = rendered.Height;
                    int rot = rotations != null && idx < rotations.Length ? rotations[idx] : 0;
                    if (rot != 0) (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);
                    var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, raw, w * 4);
                    bs.Freeze();
                    bitmaps.Add((bs, w, h));
                }
            }

            // Orient the sheet to the majority of the selected pages.
            bool landscape = bitmaps.Count(b => b.W > b.H) * 2 > bitmaps.Count;

            var pd = new System.Windows.Controls.PrintDialog { PrintQueue = queue };
            var ticket = pd.PrintTicket;
            ticket.CopyCount = 1;
            ticket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
            pd.PrintTicket = ticket;
            double aw = pd.PrintableAreaWidth, ah = pd.PrintableAreaHeight;
            if (landscape && ah > aw) (aw, ah) = (ah, aw);

            var fixedDoc = new FixedDocument();
            for (int c = 0; c < copies; c++)
            {
                foreach (var (bs, w, h) in bitmaps)
                {
                    double wDip = w * 96.0 / 300.0, hDip = h * 96.0 / 300.0;
                    double s = Math.Min(aw / wDip, ah / hDip);
                    double sw = wDip * s, sh = hDip * s;
                    var img = new System.Windows.Controls.Image { Source = bs, Width = sw, Height = sh };
                    var fp = new FixedPage { Width = aw, Height = ah };
                    FixedPage.SetLeft(img, (aw - sw) / 2);
                    FixedPage.SetTop(img, (ah - sh) / 2);
                    fp.Children.Add(img);
                    fp.Measure(new Size(aw, ah));
                    fp.Arrange(new Rect(0, 0, aw, ah));
                    fp.UpdateLayout();
                    var pc = new PageContent();
                    ((IAddChild)pc).AddChild(fp);
                    fixedDoc.Pages.Add(pc);
                }
            }

            // Write the FixedDocument (not its paginator) - see PrintPreviewWindow
            // DoPrint for why. Synchronous Write is fine headless.
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(fixedDoc, ticket);
            con.WriteLine($"Sent {selected.Count} pages x{copies} to \"{queue.FullName}\".");
            return 0;
        }

        // ============================================================
        // --ocr <in.pdf> <out.pdf> [--lang code]
        // ============================================================
        // Reuses the GUI's searchable-PDF core (OcrController.BuildSearchablePdf):
        // Docnet render, Tesseract per page, invisible text drawn over each
        // word. The GUI's model-download gate is dialog-driven, so the CLI
        // has its own silent equivalent honoring the OcrHighQuality setting.
        private static int CliOcr(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2)
            {
                con.WriteLine("Usage: KillerPDF.exe --ocr <in.pdf> <out.pdf> [--lang <code>]   (default eng)");
                return 2;
            }
            string inPath = Path.GetFullPath(pos[0]), outPath = Path.GetFullPath(pos[1]);
            if (!File.Exists(inPath)) { con.WriteLine($"Input not found: {inPath}"); return 2; }
            options.TryGetValue("--lang", out var langRaw);
            string lang = string.IsNullOrWhiteSpace(langRaw) ? "eng" : langRaw!.Trim().ToLowerInvariant();

            if (!CliEnsureOcrLanguage(lang, con)) return 1;

            options.TryGetValue("--password", out var password);
            var (srcForOcr, rotations, _) = CliPrepareRenderSource(inPath, password, con);

            CliEnsureParentDir(outPath);
            var (pages, words) = OcrController.BuildSearchablePdf(srcForOcr, outPath,
                (i, n) => { if (i == 1 || i == n || i % 10 == 0) con.WriteLine($"OCR page {i}/{n}"); },
                lang, formAware: false, ct: CancellationToken.None);

            // The render source had /Rotate stripped; put the angles back on the
            // output so rotated pages still display rotated. Content and text
            // layer share page space, so they stay aligned.
            if (rotations != null && rotations.Any(r => r != 0))
            {
                var restoredRotations = rotations
                    .Select((rotation, pageIndex) => (pageIndex, rotation))
                    .Where(item => item.rotation != 0)
                    .ToDictionary(item => item.pageIndex, item => item.rotation);
                PdfEngineIntegration.ApplyPageRotations(outPath, restoredRotations);
            }

            con.WriteLine($"OCR complete: {pages} pages, {words} words -> {outPath}");
            return 0;
        }

        /// <summary>
        /// Silent equivalent of the GUI's model-download gate: nothing is
        /// bundled - every language model streams from the tessdata repos on
        /// first use, honoring the app's High Quality setting, with the same
        /// .part-then-move atomicity. Runs the download on the thread pool -
        /// OnStartup's dispatcher is not pumping, so awaiting here directly
        /// would deadlock on the captured WPF context.
        /// </summary>
        private static bool CliEnsureOcrLanguage(string lang, TextWriter con)
        {
            OcrNativeBootstrap.EnsureLanguageData();
            var dest = Path.Combine(OcrNativeBootstrap.TessDataDir, lang + ".traineddata");
            if (File.Exists(dest)) return true;

            bool hq = App.GetSetting("OcrHighQuality") == "1";
            string url = (hq
                ? "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/"
                : "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/") + lang + ".traineddata";
            con.WriteLine($"Downloading OCR language '{lang}' ({(hq ? "high quality" : "standard")})...");
            try
            {
                Task.Run(async () =>
                {
                    using var http = OcrLanguages.MakeDownloadClient();
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    var part = dest + ".part";
                    using (var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var f = File.Create(part))
                        await s.CopyToAsync(f).ConfigureAwait(false);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(part, dest);
                }).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                con.WriteLine($"Could not download language '{lang}': " + FlattenBatchDetail(ex.Message));
                con.WriteLine("Check the language code (e.g. eng, spa, fra, deu, jpn, tur, ben, chi_sim, chi_tra) and your connection.");
                return false;
            }
        }
    }
}
