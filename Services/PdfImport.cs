using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

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
                using var importDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                var cleanDoc = new PdfDocument();
                for (int i = 0; i < importDoc.PageCount; i++)
                    cleanDoc.Pages.Add(importDoc.Pages[i]);
                if (stripRotations)
                    for (int i = 0; i < cleanDoc.PageCount; i++)
                        cleanDoc.Pages[i].Rotate = 0;
                cleanDoc.Save(destPath);
                cleanDoc.Close();
                return true;
            }
            catch { return false; }
        }

        // Appends one page per image frame (multi-frame TIFF/GIF expand to one page per frame). Page
        // size matches the image's physical size at its own DPI (96 if it declares none).
        internal static void AddImagePagesFromFile(PdfDocument pdf, string path)
        {
            using var img = System.Drawing.Image.FromFile(path);
            var dim = new System.Drawing.Imaging.FrameDimension(img.FrameDimensionsList[0]);
            int frameCount = Math.Max(1, img.GetFrameCount(dim));

            for (int f = 0; f < frameCount; f++)
            {
                img.SelectActiveFrame(dim, f);

                int wpx = img.Width, hpx = img.Height;
                // Broken resolution metadata is common (WhatsApp and some scanners tag ~1 DPI,
                // screenshots 0); trusting it makes pages Adobe Reader refuses to display
                // ("dimensions out-of-range", limit 3-14400 pt per side). PDFium renders any
                // size, so the file looks fine here and only fails in other viewers. Outside a
                // plausible DPI range, fall back to 96.
                double dpiX = img.HorizontalResolution;
                double dpiY = img.VerticalResolution;
                if (!(dpiX >= 24 && dpiX <= 4800)) dpiX = 96.0;
                if (!(dpiY >= 24 && dpiY <= 4800)) dpiY = 96.0;
                double wPt = wpx * 72.0 / dpiX;
                double hPt = hpx * 72.0 / dpiY;

                // Even with a sane DPI, clamp into Adobe's supported range, preserving aspect.
                double shrink = Math.Min(1.0, MaxAdobePageDim / Math.Max(wPt, hPt));
                wPt *= shrink; hPt *= shrink;
                double grow = Math.Max(1.0, MinAdobePageDim / Math.Min(wPt, hPt));
                wPt *= grow; hPt *= grow;

                // Copy the active frame to a fresh 32bpp bitmap, then encode PNG (XImage reads that).
                byte[] png;
                using (var frame = new System.Drawing.Bitmap(wpx, hpx,
                           System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(frame))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, wpx, hpx);
                    }
                    using var ms = new MemoryStream();
                    frame.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    png = ms.ToArray();
                }

                var page = pdf.AddPage();
                page.Width  = wPt;   // XUnit implicitly treats a double as points
                page.Height = hPt;

                using var gfx  = XGraphics.FromPdfPage(page);
                using var xImg = XImage.FromStream(() => new MemoryStream(png));
                gfx.DrawImage(xImg, 0, 0, wPt, hPt);
            }
        }

        /// <summary>
        /// Builds a map of named destination string -> 0-based page index from a source document's
        /// /Dests dictionary and /Names /Dests name tree.
        /// </summary>
        internal static Dictionary<string, int> BuildNamedDestMap(PdfDocument src)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var catalog = src.Internals.Catalog;

                // Legacy flat /Dests dictionary
                var destsDict = catalog.Elements.GetDictionary("/Dests");
                if (destsDict != null)
                {
                    foreach (var key in destsDict.Elements.Keys)
                    {
                        PdfItem? val = PdfScrub.DerefItemStatic(destsDict.Elements[key] ?? new PdfInteger(-1));
                        int? idx = ResolveDestPageIndexInDoc(src, val);
                        if (idx.HasValue) map[key.TrimStart('/')] = idx.Value;
                    }
                }

                // Modern /Names /Dests name tree
                var namesDict = catalog.Elements.GetDictionary("/Names");
                var destTree  = namesDict?.Elements.GetDictionary("/Dests");
                if (destTree != null)
                    WalkNameTree(src, destTree, map);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildNamedDestMap: {ex}"); }
            return map;
        }

        private static void WalkNameTree(PdfDocument src, PdfDictionary node, Dictionary<string, int> map)
        {
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var keyItem = namesArr.Elements[i];
                    string key  = keyItem is PdfString ks ? ks.Value : keyItem?.ToString()?.TrimStart('/') ?? "";
                    if (string.IsNullOrEmpty(key)) continue;
                    PdfItem? val = PdfScrub.DerefItemStatic(namesArr.Elements[i + 1]);
                    int? idx = ResolveDestPageIndexInDoc(src, val);
                    if (idx.HasValue) map[key] = idx.Value;
                }
            }

            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    if (PdfScrub.DerefItemStatic(kids.Elements[i]) is PdfDictionary kid)
                        WalkNameTree(src, kid, map);
                }
            }
        }

        /// <summary>
        /// Resolves a destination value (PdfArray or PdfDictionary with /D) to a page index
        /// within the given source document by matching the page object number.
        /// </summary>
        private static int? ResolveDestPageIndexInDoc(PdfDocument src, PdfItem? val)
        {
            PdfArray? arr = val as PdfArray;
            if (arr is null && val is PdfDictionary vd)
                arr = vd.Elements.GetArray("/D");
            if (arr is null || arr.Elements.Count == 0) return null;

            var first = arr.Elements[0];
            int objNum = PdfScrub.GetObjectNumber(first);
            if (objNum > 0)
            {
                for (int i = 0; i < src.PageCount; i++)
                {
                    var pgRef = src.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == objNum) return i;
                }
            }
            else if (first is PdfInteger pi && pi.Value >= 0 && pi.Value < src.PageCount)
            {
                return pi.Value;
            }
            return null;
        }

        /// <summary>
        /// Walks all link annotations in pages [pageOffset, doc.PageCount) and rewrites any
        /// named-destination /D values to explicit [pageRef /Fit] arrays using the merged
        /// document's page references. This is needed because PdfSharpCore's import does not
        /// copy the source document's /Names /Dests catalog entries.
        /// </summary>
        internal static void RewriteNamedDestLinks(PdfDocument doc, int pageOffset,
            Dictionary<string, int> namedDestMap)
        {
            for (int pi = pageOffset; pi < doc.PageCount; pi++)
            {
                try
                {
                    var page    = doc.Pages[pi];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int ai = 0; ai < annotsArr.Elements.Count; ai++)
                    {
                        PdfItem? elem = annotsArr.Elements[ai];
                        PdfDictionary? ann = elem as PdfDictionary
                            ?? (PdfScrub.DerefItemStatic(elem) as PdfDictionary);
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Link")) continue;

                        // Check /A /D (GoTo action)
                        var actionDict = ann.Elements.GetDictionary("/A");
                        if (actionDict != null)
                        {
                            var s = actionDict.Elements["/S"]?.ToString() ?? "";
                            if (s.Contains("GoTo"))
                            {
                                var destItem = actionDict.Elements["/D"];
                                string? name = ExtractDestName(destItem);
                                if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                                {
                                    int targetIdx = pageOffset + srcIdx;
                                    if (targetIdx < doc.PageCount)
                                        actionDict.Elements["/D"] = MakeExplicitDest(doc, targetIdx);
                                }
                            }
                        }
                        else
                        {
                            // Bare /Dest on annotation
                            var destItem = ann.Elements["/Dest"];
                            string? name = ExtractDestName(destItem);
                            if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                            {
                                int targetIdx = pageOffset + srcIdx;
                                if (targetIdx < doc.PageCount)
                                    ann.Elements["/Dest"] = MakeExplicitDest(doc, targetIdx);
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RewriteNamedDestLinks p{pi}: {ex}"); }
            }
        }

        private static string? ExtractDestName(PdfItem? item)
        {
            if (item is null) return null;
            if (item is PdfString ps) return ps.Value;
            if (item is PdfName   pn) return pn.Value.TrimStart('/');
            return null;
        }

        private static PdfArray MakeExplicitDest(PdfDocument doc, int pageIndex)
        {
            var arr = new PdfArray(doc);
            arr.Elements.Add(doc.Pages[pageIndex].Reference);
            arr.Elements.Add(new PdfName("/Fit"));
            return arr;
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
                if (msg.IndexOf("EOF", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("end of file", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("Inflater", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("FlateDecode", StringComparison.OrdinalIgnoreCase) >= 0
                    || type.IndexOf("SharpZip", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // True for recoverable PdfSharpCore read/parse failures that our repair path
        // (import-rebuild / PDFium round-trip) can usually fix. Named for the original xref case,
        // but now also covers other parser-level errors surfaced when reopening a saved temp.
        internal static bool IsXRefException(Exception ex) =>
            ex.Message.IndexOf("XRef", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("cross-reference", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("trailer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Invalid PDF file", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("startxref", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Unexpected token", StringComparison.OrdinalIgnoreCase) >= 0 ||
            // #106: "Cannot retrieve stream length." - a stream whose /Length is indirect or broken.
            ex.Message.IndexOf("stream length", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("File streams are not yet implemented", StringComparison.OrdinalIgnoreCase) >= 0;

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
            ex.Message.IndexOf("owner", StringComparison.OrdinalIgnoreCase) >= 0 &&
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static bool IsPasswordException(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;

        // ---- Background-safe repair strategies -----------------------------------------------

        /// <summary>
        /// Strategy 1 worker (background-safe, no UI/_doc access): page-copies the source through
        /// PdfSharpCore Import mode into a clean temp PDF and returns its path.
        /// </summary>
        internal static string? RepairViaImportToFile(string path)
        {
            // Returns null (never throws) so a failed strategy falls through cleanly to the next one
            // and doesn't surface as a debugger "user-unhandled" break during the awaited Task.
            try
            {
                PdfDocument repairedDoc;
                using (var importDoc = PdfReader.Open(path, PdfDocumentOpenMode.Import))
                {
                    repairedDoc = new PdfDocument();
                    for (int i = 0; i < importDoc.PageCount; i++)
                        repairedDoc.Pages.Add(importDoc.Pages[i]);
                }
                var repairedPath = App.MakeTempFile("repaired");
                repairedDoc.Save(repairedPath);
                repairedDoc.Close();
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

                using var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(RenderPx, RenderPx));
                int pageCount = docReader.GetPageCount();
                if (pageCount <= 0) return null;

                var newDoc = new PdfDocument();

                for (int i = 0; i < pageCount; i++)
                {
                    using var pr = docReader.GetPageReader(i);
                    int bw = pr.GetPageWidth();
                    int bh = pr.GetPageHeight();
                    if (bw <= 0 || bh <= 0) continue;

                    var raw = pr.GetImage(PdfRender.WithAnnotations);   // #141
                    if (raw is null || raw.Length == 0) continue;

                    var wb = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
                    wb.WritePixels(new Int32Rect(0, 0, bw, bh), raw, bw * 4, 0);
                    wb.Freeze();

                    byte[] pngBytes;
                    using (var ms = new System.IO.MemoryStream())
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(wb));
                        enc.Save(ms);
                        pngBytes = ms.ToArray();
                    }

                    // Build the page at correct aspect ratio scaled to A4-ish width.
                    double pageW = 595.28;
                    double pageH = pageW * bh / bw;

                    var page = newDoc.AddPage();
                    page.Width  = XUnit.FromPoint(pageW);
                    page.Height = XUnit.FromPoint(pageH);

                    using var gfx = XGraphics.FromPdfPage(page);
                    var xImg = XImage.FromStream(() => new System.IO.MemoryStream(pngBytes));
                    gfx.DrawImage(xImg, 0, 0, pageW, pageH);
                }

                if (newDoc.PageCount == 0) return null;

                var repairedPath = App.MakeTempFile("repaired");
                newDoc.Save(repairedPath);
                newDoc.Close();
                return repairedPath;
            }
            catch { return null; }
        }
    }
}
