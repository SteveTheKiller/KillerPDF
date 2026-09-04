using System.IO;
using Docnet.Core;
using Docnet.Core.Models;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services
{
    // ============================================================
    // File import/repair helpers - pure functions over paths and
    // PdfDocuments, no window state. Split out of FileOperations.cs
    // and ImportAndZip.cs (KillerUI refactor); shared by the GUI
    // open/merge/repair paths, TempReload, and the CLI. The one
    // import helper NOT here is TryPdfiumStripEncryption - it rides
    // the shared PDFium interop block (and its lock), which stays
    // on MainWindow until the interop gets its own deliberate home.
    // ============================================================
    internal static class PdfImport
    {
        // Adobe Reader only displays pages whose sides are within this range (points); outside it
        // shows "The dimensions of this page are out-of-range". Shared by the image importer here
        // and FileOperations' Adobe page-size guard.
        internal const double MinAdobePageDim = 3.0;
        internal const double MaxAdobePageDim = 14400.0;

        internal static bool IsPdfPath(string p) => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns true if the PDF file has an /Encrypt entry in its trailer.
        /// Scans the last 2 KB so it's fast; works regardless of how PdfSharp
        /// reports security state after authenticating with an empty password.
        /// </summary>
        internal static bool PdfFileHasEncryption(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                long scan = Math.Min(2048, fs.Length);
                fs.Seek(-scan, SeekOrigin.End);
                var buf = new byte[scan];
                _ = fs.Read(buf, 0, buf.Length);
                // Look for /Encrypt in the raw bytes (Latin-1 safe)
                var text = System.Text.Encoding.GetEncoding(1252).GetString(buf);
                return text.Contains("/Encrypt");
            }
            catch { return false; }
        }

        /// <param name="stripRotations">
        /// Pass true when called from SaveTempAndReload (rotations already stripped in source).
        /// Pass false for open-time repair so original page rotations are preserved.
        /// </param>
        internal static bool TryImportRepairToPath(string sourcePath, string destPath, bool stripRotations = false)
        {
            try
            {
                PdfEngineIntegration.RebuildDocument(
                    sourcePath, destPath, stripRotations);
                return true;
            }
            catch { return false; }
        }

        // ---- Open-failure classifiers --------------------------------------------------------
        // Pattern-match PdfSharpCore's exception messages to pick the right repair strategy.

        // PdfSharpCore throws on some structurally-valid PDFs that PDFium opens fine - most
        // often "Unexpected EOF" from SharpZipLib's Flate inflater while reading a FlateDecode
        // cross-reference stream (multi-revision PDFs with incremental updates / dangling xref
        // entries that tolerant parsers ignore). Match by message AND exception type across the
        // whole inner-exception chain so a wrapped SharpZipBaseException is still recovered.
        internal static bool IsEofParseException(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                string msg  = e.Message ?? string.Empty;
                string type = e.GetType().FullName ?? string.Empty;
                if (msg.Contains("EOF", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("end of file", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("Inflater", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("FlateDecode", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("SharpZip", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // True for recoverable PdfSharpCore read/parse failures that our repair path
        // (import-rebuild / PDFium round-trip) can usually fix. Named for the original xref case,
        // but now also covers other parser-level errors surfaced when reopening a saved temp.
        internal static bool IsXRefException(Exception ex) =>
            ex.Message.Contains("XRef", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("cross-reference", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("trailer", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Invalid PDF file", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("startxref", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase) ||
            // #106: "Cannot retrieve stream length." - a stream whose /Length is indirect or broken.
            ex.Message.Contains("stream length", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("File streams are not yet implemented", StringComparison.OrdinalIgnoreCase);

        // True for UNC paths (\\server\share, \\wsl$\..., \\wsl.localhost\...) and mapped
        // network drives. Such files are copied locally before opening to avoid 9P short reads.
        internal static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            try
            {
                var root = System.IO.Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && root!.Length >= 2 && root[1] == ':')
                    return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch { }
            return false;
        }

        internal static bool IsOwnerPasswordException(Exception ex) =>
            ex.Message.Contains("owner", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase);

        internal static bool IsPasswordException(Exception ex) =>
            ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("protected", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase);

        // ---- Background-safe repair strategies -----------------------------------------------

        /// <summary>
        /// Strategy 1 worker (background-safe, no UI/_doc access): imports the complete source
        /// graph through The KillerPDF.Engine into a clean temp PDF and returns its path.
        /// </summary>
        internal static string? RepairViaImportToFile(string path)
        {
            // Returns null (never throws) so a failed strategy falls through cleanly to the next one
            // and doesn't surface as a debugger "user-unhandled" break during the awaited Task.
            try
            {
                var repairedPath = App.MakeTempFile("repaired");
                PdfEngineIntegration.RebuildDocument(path, repairedPath);
                return repairedPath;
            }
            catch { return null; }
        }

        /// <summary>
        /// Strategy 2 worker (background-safe, no UI/_doc access): uses PDFium (Docnet) to render
        /// each page to a bitmap, rebuilds a clean PdfSharpCore document from those bitmaps, and
        /// returns its temp path. Mirrors the flatten path, which also encodes off the UI thread.
        /// </summary>
        internal static string? RepairViaDocnetRasterizeToFile(string path)
        {
            // Returns null (never throws) so the caller can show a clean "repair failed" message
            // without a debugger break on the awaited Task.
            try
            {
                const int RenderPx = 2048;

                using var renderSession = PdfPageRenderSession.Open(path, RenderPx, RenderPx);
                int pageCount = renderSession.PageCount;
                if (pageCount <= 0) return null;

                var pages = new List<PdfEngineIntegration.RasterPage>(pageCount);
                IReadOnlyList<bool> bitonalHints = [];
                IReadOnlyList<bool> jpegHints = [];
                try
                {
                    PdfDocument sourceDocument = PdfDocument.Open(File.ReadAllBytes(path));
                    bitonalHints =
                        PdfPageRasterInformation.ReadBitonalImagePageHints(sourceDocument);
                    jpegHints =
                        PdfPageRasterInformation.ReadJpegImagePageHints(sourceDocument);
                }
                catch
                {
                    // The raster repair remains available when the engine cannot parse the source.
                }

                for (int i = 0; i < pageCount; i++)
                {
                    PdfRenderedPage rendered = renderSession.RenderPage(i);
                    int bw = rendered.Width;
                    int bh = rendered.Height;
                    if (bw <= 0 || bh <= 0) continue;

                    byte[] raw = rendered.Pixels;
                    if (raw is null || raw.Length == 0) continue;

                    // Build the page at correct aspect ratio scaled to A4-ish width.
                    double pageW = 595.28;
                    double pageH = pageW * bh / bw;
                    bool bitonal = i < bitonalHints.Count && bitonalHints[i]
                        && BitonalPageDetector.IsOpaqueGrayscaleBgra(raw, bw, bh);
                    ReadOnlyMemory<byte> jpeg = !bitonal && i < jpegHints.Count && jpegHints[i]
                        ? BitmapHelpers.EncodeJpeg(raw, bw, bh)
                        : default;
                    pages.Add(new PdfEngineIntegration.RasterPage(
                        bw, bh, pageW, pageH, raw, jpeg, Bitonal: bitonal));
                }

                if (pages.Count == 0) return null;

                var repairedPath = App.MakeTempFile("repaired");
                File.WriteAllBytes(repairedPath,
                    PdfEngineIntegration.CreateRasterDocument(pages));
                return repairedPath;
            }
            catch { return null; }
        }
    }
}
