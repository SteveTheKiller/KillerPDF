using System.IO;
using System.Threading;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services
{
    // ============================================================
    // Document rasterization cores - pure functions over a rendered
    // source file, no window state (KillerUI refactor; split out of
    // FileOperations.cs' Save Flattened and Export Images flows).
    // The shell keeps the dialogs, the annotation burn, and the
    // progress overlay; progress arrives via callback, cancel via
    // token, exactly the BuildSearchablePdf pattern.
    // ============================================================
    internal static class PdfRasterize
    {
        internal sealed record FlattenOptions(
            double Dpi = 150,
            PageColorMode ColorMode = PageColorMode.Color,
            int BlackWhiteThreshold = 160,
            bool UseJpegCompression = false,
            int JpegQuality = 85,
            ScanLookOptions? Look = null,
            bool KeepResolution = false)
        {
            internal void Validate()
            {
                if (Dpi < 24 || Dpi > 1200)
                    throw new ArgumentOutOfRangeException(nameof(Dpi), "DPI must be between 24 and 1200.");
                if (BlackWhiteThreshold < 0 || BlackWhiteThreshold > 255)
                    throw new ArgumentOutOfRangeException(nameof(BlackWhiteThreshold), "Threshold must be between 0 and 255.");
                if (JpegQuality < 1 || JpegQuality > 100)
                    throw new ArgumentOutOfRangeException(nameof(JpegQuality), "JPEG quality must be between 1 and 100.");
            }
        }

        /// <summary>
        /// Rasterizes every page of <paramref name="sourcePath"/> at 150 DPI and assembles them
        /// into a new PDF at <paramref name="outputPath"/>, each page at its original point size.
        /// Cancellable - nothing is saved if canceled. Runs entirely off the UI thread. With
        /// <paramref name="incomplete"/> a page that cannot be drawn exactly is kept as rendered and
        /// noted there; without it such a page fails the flatten.
        /// </summary>
        internal static void FlattenToPdf(string sourcePath, int pageCount,
            (double widthPt, double heightPt)[] pageDims, string outputPath,
            Action<int, int> progress, CancellationToken ct)
            => FlattenToPdf(sourcePath, pageCount, pageDims, outputPath,
                new FlattenOptions(), progress, ct);

        internal static void FlattenToPdf(string sourcePath, int pageCount,
            (double widthPt, double heightPt)[] pageDims, string outputPath,
            FlattenOptions options, Action<int, int> progress, CancellationToken ct,
            IncompletePageLog? incomplete = null)
        {
            options.Validate();
            // Rasterize pages across CPU cores. Pages are authored by the
            // engine afterwards in their original order.
            //
            // The source document is opened ONCE here. The old code re-opened it inside
            // the per-page loop, re-parsing the whole file on every page (O(pages) full
            // document parses) - the dominant cost on large files. A single scaling
            // factor renders each page at its own size at 150 DPI (150/72), so the doc
            // no longer needs reopening to apply per-page pixel dimensions.
            PdfDocument sourceDocument = PdfDocument.Open(File.ReadAllBytes(sourcePath));
            IReadOnlyList<bool> bitonalHints =
                PdfPageRasterInformation.ReadBitonalImagePageHints(sourceDocument);
            IReadOnlyList<bool> jpegHints =
                PdfPageRasterInformation.ReadJpegImagePageHints(sourceDocument);
            var passthrough = new PdfImage?[pageCount];
            double[] pageDpi = [.. Enumerable.Range(0, pageCount).Select(page => options.KeepResolution
                ? OutputPixelDimensions.NativeDpi(PdfPageRasterInformation.ReadPageImageSize(sourceDocument, page),
                    pageDims[page].widthPt, pageDims[page].heightPt, options.Dpi)
                : options.Dpi)];
            bool scanLook = options.Look?.Any == true;
            if (options.ColorMode == PageColorMode.Color && !options.UseJpegCompression && !scanLook)
                for (int i = 0; i < pageCount; i++)
                    if (PdfPageRasterInformation.TryReadFullPageJpeg(
                            sourceDocument, i, out PdfImage? candidate)
                        && candidate is not null
                        && OutputPixelDimensions.MatchesDpi(candidate.Width, candidate.Height,
                            pageDims[i].widthPt, pageDims[i].heightPt, pageDpi[i]))
                        passthrough[i] = candidate;
            var rasterPages = new PdfEngineIntegration.RasterPage[pageCount];
            var docGate  = new object();
            int done     = 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
            var renderSessions = new Dictionary<double, PdfPageRenderSession>();
            using var closeSessions = new SessionSet(renderSessions);
            Parallel.For(0, pageCount, po, i =>
            {
                if (ct.IsCancellationRequested) return;   // cooperative: skip remaining pages' work
                byte[] bgra; int rw, rh;
                lock (docGate)
                {
                    // Composite over white (#148, Ryokoxx): the engine leaves unpainted
                    // background as BGRA 0,0,0,0, which used to embed a full-page
                    // /SMask alpha channel in the flattened output.
                    // #141: WithAnnotations, or flattening an annotated PDF silently dropped the
                    // markup the file carried - this path builds a NEW document from the pixels.
                    PdfRenderedPage rendered = SessionFor(renderSessions, sourcePath, pageDpi[i]).RenderPage(i,
                        requireComplete: incomplete is null, cancellationToken: ct);
                    incomplete?.Note(i, rendered);
                    rw = rendered.Width;
                    rh = rendered.Height;
                    bgra = rendered.Pixels;
                }
                if (passthrough[i] is PdfImage original)
                {
                    rasterPages[i] = new PdfEngineIntegration.RasterPage(
                        original.Width, original.Height,
                        pageDims[i].widthPt, pageDims[i].heightPt,
                        ReadOnlyMemory<byte>.Empty, original.Data);
                    int passed = System.Threading.Interlocked.Increment(ref done);
                    progress(passed, pageCount);
                    return;
                }
                // The scanned look goes on before the color mode, so a grayscale copy grays the
                // warm paper and grain along with the page.
                if (scanLook) bgra = ScanLook.Apply(bgra, rw, rh, pageDpi[i], options.Look!, i);
                PageQualityConverter.ApplyColorModeToBgra(
                    bgra, options.ColorMode, options.BlackWhiteThreshold);
                bool bitonal = options.ColorMode == PageColorMode.BlackAndWhite
                    || options.ColorMode == PageColorMode.Color && !scanLook
                    && i < bitonalHints.Count && bitonalHints[i]
                    && BitonalPageDetector.IsOpaqueGrayscaleBgra(bgra, rw, rh);
                bool grayscale = options.ColorMode == PageColorMode.Grayscale;
                bool useJpeg = !bitonal && (options.UseJpegCompression
                    || options.ColorMode == PageColorMode.Color
                    && i < jpegHints.Count && jpegHints[i]);
                ReadOnlyMemory<byte> jpeg = useJpeg
                    ? BitmapHelpers.EncodeJpeg(bgra, rw, rh, pageDpi[i],
                        options.JpegQuality, grayscale)
                    : default;
                rasterPages[i] = new PdfEngineIntegration.RasterPage(
                    rw, rh, pageDims[i].widthPt, pageDims[i].heightPt, bgra,
                    JpegData: jpeg,
                    Bitonal: bitonal,
                    Grayscale: grayscale);

                int n = System.Threading.Interlocked.Increment(ref done);
                progress(n, pageCount);
            });

            if (ct.IsCancellationRequested) return;   // canceled during render: assemble/save nothing

            AtomicFileOutput.Write(outputPath,
                PdfEngineIntegration.CreateRasterDocument(rasterPages), ct);
        }

        // One session per resolution, because a session renders at a single scale.
        private static PdfPageRenderSession SessionFor(
            Dictionary<double, PdfPageRenderSession> sessions, string sourcePath, double dpi)
        {
            if (!sessions.TryGetValue(dpi, out PdfPageRenderSession? session))
                sessions[dpi] = session = PdfPageRenderSession.OpenEngineFirst(sourcePath, dpi / 72.0);
            return session;
        }

        private sealed class SessionSet(Dictionary<double, PdfPageRenderSession> sessions) : IDisposable
        {
            public void Dispose()
            {
                foreach (PdfPageRenderSession session in sessions.Values) session.Dispose();
            }
        }

        /// <summary>
        /// Plans unique numbered image destinations before rendering any page.
        /// </summary>
        internal static string[] PlanPageImagePaths(string sourcePath, IReadOnlyList<int> pages,
            string outDir, string baseName, int digits, bool jpeg)
        {
            string[] paths = [.. pages.Select(index => Path.GetFullPath(Path.Combine(outDir,
                $"{baseName}-page-{(index + 1).ToString().PadLeft(digits, '0')}.{(jpeg ? "jpg" : "png")}")))];
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length
                || paths.Any(path => string.Equals(path, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Image output filenames must be unique and cannot replace the source file.");
            return paths;
        }

        internal static void ValidateImageOutputs(IReadOnlyList<string> paths, IReadOnlySet<string>? approvedOverwrites)
        {
            foreach (string path in paths)
                if (Directory.Exists(path) || File.Exists(path) && !(approvedOverwrites?.Contains(path) ?? false))
                    throw new IOException($"Image output already exists: {path}. Approve replacement before exporting.");
        }

        /// <summary>Renders selected pages with pending rotations and returns the number of complete
        /// images published, including a partial batch when canceled. Without an incomplete-page log,
        /// a page that cannot render completely fails the export.</summary>
        internal static int ExportPageImages(string sourcePath, IReadOnlyList<int> pages,
            int[] rotSnapshot, double dpi, bool jpeg, string outDir, string baseName, int digits,
            Action<int, int> progress, CancellationToken ct, IncompletePageLog? incomplete = null,
            IReadOnlySet<string>? approvedOverwrites = null, Func<int, double>? pageDpi = null)
        {
            string[] paths = PlanPageImagePaths(sourcePath, pages, outDir, baseName, digits, jpeg);
            ValidateImageOutputs(paths, approvedOverwrites);
            int written = 0;
            var renderSessions = new Dictionary<double, PdfPageRenderSession>();
            using var closeSessions = new SessionSet(renderSessions);
            int done = 0;
            try
            {
                foreach (var idx in pages)
                {
                    if (ct.IsCancellationRequested) return written;
                    byte[] raw; int w, h;
                    double outputDpi = pageDpi?.Invoke(idx) ?? dpi;
                    {
                        // Composite over white (#148, Ryokoxx): bare GetImage leaves the
                        // unpainted background at BGRA 0,0,0,0 - JPEG export dropped the
                        // alpha and produced black pages, PNG came out transparent.
                        // #141: WithAnnotations - an exported image should show the markup the file
                        // carries, the same as the page does on screen.
                        PdfRenderedPage rendered = SessionFor(renderSessions, sourcePath, outputDpi).RenderPage(idx,
                            requireComplete: incomplete is null, cancellationToken: ct);
                        incomplete?.Note(idx, rendered);
                        w = rendered.Width;
                        h = rendered.Height;
                        raw = rendered.Pixels;
                    }
                    int rot = idx < rotSnapshot.Length ? rotSnapshot[idx] : 0;
                    if (rot != 0) (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);
                    var bytes = jpeg ? BitmapHelpers.EncodeJpeg(raw, w, h, outputDpi) : BitmapHelpers.RenderToPng(raw, w, h, outputDpi);
                    string destination = paths[written];
                    if (approvedOverwrites?.Contains(destination) == true) AtomicFileOutput.Write(destination, bytes, ct);
                    else AtomicFileOutput.WriteNew(destination, bytes, ct);
                    written++;
                    int n = ++done;
                    progress(n, pages.Count);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return written; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new IOException($"Image export stopped after {written} of {pages.Count} images in {outDir}. " + error.Message, error);
            }
            return written;
        }
    }
}
