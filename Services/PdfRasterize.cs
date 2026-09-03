using System.IO;
using System.Threading;
using Docnet.Core;
using Docnet.Core.Models;
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
        /// <summary>
        /// Rasterizes every page of <paramref name="sourcePath"/> at 150 DPI and assembles them
        /// into a new PDF at <paramref name="outputPath"/>, each page at its original point size.
        /// Cancellable - nothing is saved if canceled. Runs entirely off the UI thread.
        /// </summary>
        internal static void FlattenToPdf(string sourcePath, int pageCount,
            (double widthPt, double heightPt)[] pageDims, string outputPath,
            Action<int, int> progress, CancellationToken ct)
        {
            // Rasterize pages across CPU cores. Docnet/PDFium is not thread-safe, so the
            // PDFium rendering is serialized behind a lock. Pages are authored by the
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
            var rasterPages = new PdfEngineIntegration.RasterPage[pageCount];
            var docGate  = new object();
            int done     = 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
            using var flattenReader = DocLib.Instance.GetDocReader(sourcePath, new PageDimensions(150.0 / 72.0));
            Parallel.For(0, pageCount, po, i =>
            {
                if (ct.IsCancellationRequested) return;   // cooperative: skip remaining pages' work
                byte[] bgra; int rw, rh;
                lock (docGate)
                {
                    using var pr = flattenReader.GetPageReader(i);
                    // Composite over white (#148, Ryokoxx): PDFium leaves unpainted
                    // background as BGRA 0,0,0,0, which used to embed a full-page
                    // /SMask alpha channel in the flattened output.
                    // #141: WithAnnotations, or flattening an annotated PDF silently dropped the
                    // markup the file carried - this path builds a NEW document from the pixels.
                    rw   = pr.GetPageWidth();
                    rh   = pr.GetPageHeight();
                    bgra = PdfiumInterop.RenderPageWithAnnotations(sourcePath, i, rw, rh)
                        ?? pr.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover());
                }
                bool bitonal = i < bitonalHints.Count && bitonalHints[i]
                    && BitonalPageDetector.IsOpaqueGrayscaleBgra(bgra, rw, rh);
                ReadOnlyMemory<byte> jpeg = !bitonal && i < jpegHints.Count && jpegHints[i]
                    ? BitmapHelpers.EncodeJpeg(bgra, rw, rh, 150)
                    : default;
                rasterPages[i] = new PdfEngineIntegration.RasterPage(
                    rw, rh, pageDims[i].widthPt, pageDims[i].heightPt, bgra,
                    JpegData: jpeg,
                    Bitonal: bitonal);

                int n = System.Threading.Interlocked.Increment(ref done);
                progress(n, pageCount);
            });

            if (ct.IsCancellationRequested) return;   // canceled during render: assemble/save nothing

            File.WriteAllBytes(outputPath,
                PdfEngineIntegration.CreateRasterDocument(rasterPages));
        }

        /// <summary>
        /// Renders each selected page of <paramref name="sourcePath"/> at <paramref name="dpi"/>
        /// and writes base-page-NNN.png/.jpg files into <paramref name="outDir"/>. In-app
        /// rotations arrive as a snapshot array (the working file has /Rotate stripped). Returns
        /// how many files were written; cancellation stops after the current page.
        /// </summary>
        internal static int ExportPageImages(string sourcePath, IReadOnlyList<int> pages,
            int[] rotSnapshot, double dpi, bool jpeg, string outDir, string baseName, int digits,
            Action<int, int> progress, CancellationToken ct)
        {
            int written = 0;
            using var dr = DocLib.Instance.GetDocReader(sourcePath, new PageDimensions(dpi / 72.0));
            int done = 0;
            foreach (var idx in pages)
            {
                if (ct.IsCancellationRequested) return written;
                byte[] raw; int w, h;
                using (var pr = dr.GetPageReader(idx))
                {
                    // Composite over white (#148, Ryokoxx): bare GetImage leaves the
                    // unpainted background at BGRA 0,0,0,0 - JPEG export dropped the
                    // alpha and produced black pages, PNG came out transparent.
                    // #141: WithAnnotations - an exported image should show the markup the file
                    // carries, the same as the page does on screen.
                    w   = pr.GetPageWidth();
                    h   = pr.GetPageHeight();
                    raw = PdfiumInterop.RenderPageWithAnnotations(sourcePath, idx, w, h)
                        ?? pr.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover());
                }
                int rot = idx < rotSnapshot.Length ? rotSnapshot[idx] : 0;
                if (rot != 0) (raw, w, h) = BitmapHelpers.RotateBitmap(raw, w, h, rot);
                var bytes = jpeg ? BitmapHelpers.EncodeJpeg(raw, w, h, dpi) : BitmapHelpers.RenderToPng(raw, w, h, dpi);
                var name  = $"{baseName}-page-{(idx + 1).ToString().PadLeft(digits, '0')}.{(jpeg ? "jpg" : "png")}";
                File.WriteAllBytes(Path.Combine(outDir, name), bytes);
                written++;
                int n = ++done;
                progress(n, pages.Count);
            }
            return written;
        }
    }
}
