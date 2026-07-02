using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // PDF Link Annotation Overlays
        // ============================================================

        private readonly record struct LinkInfo(double Cx, double Cy, double Cw, double Ch, object Tag, string Tip, int AnnotIndex);

        // Per-page link rects for the tiled views (continuous / grid / two-page), keyed by page index.
        // Clicks and the hover cursor are resolved by bounds-testing these in Canvas_MouseLeftButtonDown
        // and Canvas_MouseMove: a per-link overlay swallows the click in the tiled layout but its own
        // handler never fires, so no visual overlay is created - these rects are the source of truth.
        private readonly Dictionary<int, List<LinkInfo>> _continuousLinks = [];

        // Small hit-slop (render-dim units) added around a link rect for click / hover / right-click
        // hit-testing so thin one-line link strips are easy to hit without over-reaching neighbours.
        // Applied identically in single-page (grows the overlay in RenderPageLinks) and tiled views
        // (bounds-checks) so both feel the same.
        private const double LinkHitPad = 5;

        // Persisted opt-out key for the click-safety confirmation prompt.
        private const string SkipLinkConfirmSetting = "SkipLinkConfirm";

        // Confirms before opening an external link in the browser, unless the user opted out. Returns true
        // to proceed. Internal go-to-page links never call this.
        private bool ConfirmOpenLink(string url)
        {
            if (App.GetSetting(SkipLinkConfirmSetting) == "1") return true;
            var (result, dontAsk) = KillerDialog.ShowWithCheckbox(
                this,
                $"{Loc("Str_LinkConfirmBody")}\n\n{url}",
                Loc("Str_LinkDontAsk"),
                Loc("Str_LinkConfirmTitle"),
                MessageBoxButton.OKCancel);
            if (result != MessageBoxResult.OK) return false;
            if (dontAsk) App.SetSetting(SkipLinkConfirmSetting, "1");
            return true;
        }

        // Schemes we will hand to the OS shell when a PDF link is clicked. A PDF can embed ANY URI, and
        // Process.Start(UseShellExecute=true) would happily launch file:// paths, UNC shares, javascript:,
        // or registered protocol handlers (ms-msdt:/search-ms: - real malware vectors). Anything outside
        // this allow-list is refused. http/https = web links; mailto = email links.
        private static readonly HashSet<string> AllowedLinkSchemes =
            new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

        // True only for an absolute URI in an allowed scheme. Rejects scheme-less / relative URIs (a bare
        // "www.example.com" is a Tier 2 follow-up), plus file:, javascript:, and custom protocol handlers.
        private static bool IsAllowedLinkUri(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && AllowedLinkSchemes.Contains(uri.Scheme);

        // A PDF can store a scheme-less link like "www.example.com" or "example.com/page". Treat a domain-
        // shaped target as https so it still opens; anything with an explicit scheme, a backslash (UNC/path),
        // or whitespace is left untouched (and thus refused by IsAllowedLinkUri unless it's http/https/mailto).
        private static string NormalizeLinkUri(string raw)
        {
            raw = raw.Trim();
            if (raw.Length == 0) return raw;
            if (raw.Contains('\\') || raw.Contains(' ')) return raw;    // Windows path / UNC / junk - don't touch
            if (raw.Contains("://")) return raw;                        // already scheme://...
            int colon = raw.IndexOf(':');
            int slash = raw.IndexOf('/');
            if (colon >= 0 && (slash < 0 || colon < slash)) return raw; // "scheme:" (mailto:, file:, C:) - don't touch
            string host = slash >= 0 ? raw[..slash] : raw;              // host part before any path
            return host.Contains('.') ? "https://" + raw : raw;         // dotted host => assume https
        }

        // Maps a PDF rectangle (points, origin bottom-left, already min/max-normalised) to a canvas-space
        // rectangle (pixels, origin top-left) for a page rendered at bitmapW x bitmapH. Shared by the
        // PdfSharpCore and PDFium link readers so the two stay pixel-identical.
        private static (double x, double y, double w, double h) PdfRectToCanvas(
            double rx1, double ry1, double rx2, double ry2,
            double pageWidthPt, double pageHeightPt, int bitmapW, int bitmapH)
        {
            double x = rx1 / pageWidthPt * bitmapW;
            double y = (pageHeightPt - ry2) / pageHeightPt * bitmapH;
            double w = (rx2 - rx1) / pageWidthPt * bitmapW;
            double h = (ry2 - ry1) / pageHeightPt * bitmapH;
            return (x, y, w, h);
        }

        /// <summary>
        /// Follows a resolved link target: an int page index navigates within the document; a string URI
        /// is scheme-checked, confirmed, then opened via the shell. Single choke point for both the
        /// single-page (_linkOverlays) and tiled (_continuousLinks) click paths, so the safety checks
        /// can't be bypassed by one route and a failed open is always reported instead of silent.
        /// </summary>
        private void FollowLinkTarget(object? target)
        {
            if (target is int pageIndex)
            {
                if (_doc != null && pageIndex >= 0 && pageIndex < _doc.PageCount)
                    PageList.SelectedIndex = pageIndex;
                return;
            }

            if (target is not string raw || string.IsNullOrWhiteSpace(raw)) return;

            // Scheme-less but domain-shaped targets (e.g. "www.example.com") become https:// here.
            string url = NormalizeLinkUri(raw);
            if (!IsAllowedLinkUri(url))
            {
                SetStatus($"{Loc("Str_LinkBlocked")} {raw}");
                return;
            }

            if (!ConfirmOpenLink(url)) return;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Open link failed: {ex}");
                SetStatus(Loc("Str_LinkOpenFailed"));
            }
        }

        // Builds the right-click actions for a link onto `menu`: Open Link (via the safe FollowLinkTarget
        // path), Copy Link Address / Copy Email Address, and - only for PdfSharpCore-sourced links
        // (annotIndex >= 0) - Remove Link from PDF. Shared by the single-page overlay menu and the tiled-
        // view canvas menu so both views offer the same actions.
        private void AddLinkMenuItems(ContextMenu menu, object target, int annotIndex, int pageIndex)
        {
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_OpenLink"), (_, _) => FollowLinkTarget(target)));
            if (target is string uri)
            {
                if (uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_CopyEmail"), (_, _) => TrySetClipboard(uri["mailto:".Length..])));
                else
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_CopyLink"), (_, _) => TrySetClipboard(uri)));
            }
            if (annotIndex >= 0)
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RemoveLink"), (_, _) => RemoveLinkAnnotation(pageIndex, annotIndex)));
        }

        // Clipboard COM calls throw when another app is holding the clipboard open; swallow so a copy
        // never crashes the app (the worst case is the copy silently not happening).
        private static void TrySetClipboard(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch { }
        }

        // Status-bar hover feedback: shows the hovered link's target, restoring the prior status on exit.
        private string? _preHoverStatus;
        private void ShowLinkHoverStatus(string? target)
        {
            if (target != null)
            {
                _preHoverStatus ??= StatusText.Text;
                StatusText.Text = target;
            }
            else if (_preHoverStatus != null)
            {
                StatusText.Text = _preHoverStatus;
                _preHoverStatus = null;
            }
        }

        /// <summary>
        /// Carries the link target (page index or URI string) plus the annotation's location in
        /// the PDF so the overlay can be used to remove the native annotation on demand.
        /// </summary>
        private sealed class LinkAnnotInfo(object target, int pageIndex, int annotIndex)
        {
            public object   Target     { get; } = target;      // int pageIndex or string URI
            public int      PageIndex  { get; } = pageIndex;   // 0-based page in _doc
            public int      AnnotIndex { get; } = annotIndex;  // index inside page /Annots array
        }

        /// <summary>
        /// Parses all link annotations from a PDF page and converts them to canvas-space
        /// rectangles. Works for both primary and secondary page renders.
        /// </summary>
        private List<LinkInfo> GetPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            var links = new List<LinkInfo>();
            if (_doc is null) return links;
            try
            {
                var pdfPage = _doc.Pages[pageIndex];
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null || annotsArr.Elements.Count == 0) return links;

                double pageWidthPt  = pdfPage.Width.Point;
                double pageHeightPt = pdfPage.Height.Point;
                if (pageWidthPt  <= 0) pageWidthPt  = 595.28;
                if (pageHeightPt <= 0) pageHeightPt = 841.89;

                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Link")) continue;

                    var rectArr = ann.Elements.GetArray("/Rect");
                    if (rectArr is null || rectArr.Elements.Count < 4) continue;
                    double rx1 = rectArr.Elements.GetReal(0);
                    double ry1 = rectArr.Elements.GetReal(1);
                    double rx2 = rectArr.Elements.GetReal(2);
                    double ry2 = rectArr.Elements.GetReal(3);
                    if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
                    if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

                    var (cx, cy, cw, ch) = PdfRectToCanvas(rx1, ry1, rx2, ry2, pageWidthPt, pageHeightPt, bitmapW, bitmapH);
                    if (cw < 1 || ch < 1) continue;

                    int? targetPage = null;
                    string? uri = null;

                    var actionDict = ann.Elements.GetDictionary("/A");
                    if (actionDict != null)
                    {
                        var s = actionDict.Elements["/S"]?.ToString() ?? "";
                        if (s.Contains("GoTo"))
                            targetPage = ResolveDest(actionDict.Elements["/D"]);
                        else if (s.Contains("URI"))
                            uri = actionDict.Elements.GetString("/URI");
                    }
                    else
                    {
                        targetPage = ResolveDest(ann.Elements["/Dest"]);
                    }

                    if (targetPage is null && uri is null) continue;

                    object tag = targetPage.HasValue ? (object)targetPage.Value : uri!;
                    string tip = targetPage.HasValue ? $"Go to page {targetPage.Value + 1}" : uri!;
                    links.Add(new LinkInfo(cx, cy, cw, ch, tag, tip, i));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageLinks (PdfSharpCore): {ex}"); }

            // PdfSharpCore cannot dereference link annotations stored in object streams (common in
            // linearized / PDF 1.5+ files): it sees the /Annots references but resolves them to null,
            // yielding zero links. PDFium reads object streams natively, so when PdfSharpCore found no
            // links here, fall back to it. The early "no /Annots" return above means this only runs on
            // pages that actually declare annotations, so link-free pages never pay the PDFium cost.
            if (links.Count == 0)
            {
                try
                {
                    var viaPdfium = GetPageLinksViaPdfium(pageIndex, bitmapW, bitmapH);
                    if (viaPdfium.Count > 0) return viaPdfium;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageLinks (PDFium fallback): {ex}"); }
            }
            return links;
        }

        // ============================================================
        // PDFium link extraction (fallback for object-stream PDFs)
        //
        // PdfSharpCore silently drops link annotations stored in object streams (linearized /
        // PDF 1.5+). PDFium - already shipped with Docnet and used elsewhere for security
        // stripping - resolves them natively. FPDF_LoadDocument / FPDF_LoadPage / FPDF_ClosePage /
        // FPDF_CloseDocument are declared in FileOperations.cs; only the link + page-size entry
        // points are added here.
        // ============================================================

        [StructLayout(LayoutKind.Sequential)]
        private struct FS_RECTF { public float left, top, right, bottom; }

        private const int PDFACTION_GOTO = 1;
        private const int PDFACTION_URI  = 3;

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double FPDF_GetPageWidth(IntPtr page);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double FPDF_GetPageHeight(IntPtr page);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFLink_Enumerate(IntPtr page, ref int startPos, out IntPtr linkAnnot);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFLink_GetAnnotRect(IntPtr linkAnnot, out FS_RECTF rect);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFLink_GetAction(IntPtr link);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFAction_GetType(IntPtr action);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, byte[]? buffer, uint buflen);
        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

        // Cached PDFium document handle for link extraction. Object-stream PDFs take the PDFium fallback
        // on every annotated page; without this we'd FPDF_LoadDocument (re-parse the whole file) once per
        // page during a render sweep. Keyed by path so it self-heals when the working file changes
        // (SaveTempAndReload swaps in a new temp). _currentFile is a TEMP working copy - the user's real
        // file is _originalFile - so holding this open never locks the user's document. Only touched from
        // UI-thread render paths (RenderPageLinks / AddSecondaryPageLinks), so no locking is needed.
        private IntPtr _linkPdfiumDoc = IntPtr.Zero;
        private string? _linkPdfiumDocPath;

        /// <summary>Returns the cached PDFium handle for the current file, (re)opening it if the file
        /// changed or it isn't open yet. Returns IntPtr.Zero if there is no file or the load fails.</summary>
        private IntPtr EnsureLinkPdfiumDoc()
        {
            if (_currentFile is null) { CloseLinkPdfiumDoc(); return IntPtr.Zero; }
            if (_linkPdfiumDoc != IntPtr.Zero && _linkPdfiumDocPath == _currentFile)
                return _linkPdfiumDoc;

            CloseLinkPdfiumDoc();
            try { _ = DocLib.Instance; } catch { }   // force Docnet to init PDFium before direct pdfium.dll calls
            IntPtr doc = FPDF_LoadDocument(_currentFile, null);
            if (doc != IntPtr.Zero)
            {
                _linkPdfiumDoc     = doc;
                _linkPdfiumDocPath = _currentFile;
            }
            return doc;
        }

        /// <summary>Closes the cached PDFium link handle if open. Called when the document changes or
        /// closes; the path check in EnsureLinkPdfiumDoc is the backstop for anything not closed here.</summary>
        private void CloseLinkPdfiumDoc()
        {
            CloseTextPage();   // text-page/page handles are children of this doc - release them first
            if (_linkPdfiumDoc != IntPtr.Zero)
            {
                try { FPDF_CloseDocument(_linkPdfiumDoc); } catch { }
                _linkPdfiumDoc = IntPtr.Zero;
            }
            _linkPdfiumDocPath = null;
        }

        /// <summary>
        /// Reads a page's link annotations via PDFium (handles object-stream PDFs that PdfSharpCore
        /// cannot). Returns the same canvas-space LinkInfo list as GetPageLinks, with AnnotIndex = -1
        /// because the native annotation isn't addressable through PdfSharpCore's /Annots array - so
        /// "Remove Link from PDF" is not offered for these.
        /// </summary>
        private List<LinkInfo> GetPageLinksViaPdfium(int pageIndex, int bitmapW, int bitmapH)
        {
            var links = new List<LinkInfo>();

            // Reuse the PDFium handle cached per document (EnsureLinkPdfiumDoc) instead of reloading the
            // whole file on every annotated page - object-stream PDFs take this path on every page, so a
            // per-call FPDF_LoadDocument would re-parse the file once per page during a render sweep. The
            // page itself is still loaded/closed per call; only the document handle is shared.
            IntPtr doc = EnsureLinkPdfiumDoc();
            if (doc == IntPtr.Zero) return links;

            IntPtr page = FPDF_LoadPage(doc, pageIndex);
            if (page == IntPtr.Zero) return links;
            try
            {
                double pageWidthPt  = FPDF_GetPageWidth(page);
                double pageHeightPt = FPDF_GetPageHeight(page);
                if (pageWidthPt  <= 0) pageWidthPt  = 595.28;
                if (pageHeightPt <= 0) pageHeightPt = 841.89;

                int startPos = 0;
                while (FPDFLink_Enumerate(page, ref startPos, out IntPtr link))
                {
                    if (!FPDFLink_GetAnnotRect(link, out FS_RECTF r)) continue;

                    // PDFium may report top/bottom in either order; normalise to min/max so the
                    // mapping matches GetPageLinks (PDF origin is bottom-left, y up).
                    double rx1 = Math.Min(r.left, r.right);
                    double rx2 = Math.Max(r.left, r.right);
                    double ry1 = Math.Min(r.top,  r.bottom);
                    double ry2 = Math.Max(r.top,  r.bottom);

                    var (cx, cy, cw, ch) = PdfRectToCanvas(rx1, ry1, rx2, ry2, pageWidthPt, pageHeightPt, bitmapW, bitmapH);
                    if (cw < 1 || ch < 1) continue;

                    int? targetPage = null;
                    string? uri = null;

                    IntPtr dest = FPDFLink_GetDest(doc, link);
                    if (dest != IntPtr.Zero)
                    {
                        int t = FPDFDest_GetDestPageIndex(doc, dest);
                        if (t >= 0) targetPage = t;
                    }
                    else
                    {
                        IntPtr action = FPDFLink_GetAction(link);
                        if (action != IntPtr.Zero)
                        {
                            uint at = FPDFAction_GetType(action);
                            if (at == PDFACTION_URI)
                            {
                                uint len = FPDFAction_GetURIPath(doc, action, null, 0);
                                if (len > 1)
                                {
                                    var buf = new byte[len];
                                    FPDFAction_GetURIPath(doc, action, buf, len);
                                    uri = System.Text.Encoding.UTF8.GetString(buf, 0, (int)len - 1);
                                }
                            }
                            else if (at == PDFACTION_GOTO)
                            {
                                IntPtr d2 = FPDFAction_GetDest(doc, action);
                                if (d2 != IntPtr.Zero)
                                {
                                    int t = FPDFDest_GetDestPageIndex(doc, d2);
                                    if (t >= 0) targetPage = t;
                                }
                            }
                        }
                    }

                    if (targetPage is null && string.IsNullOrEmpty(uri)) continue;

                    object tag = targetPage.HasValue ? (object)targetPage.Value : uri!;
                    string tip = targetPage.HasValue ? $"Go to page {targetPage.Value + 1}" : uri!;
                    links.Add(new LinkInfo(cx, cy, cw, ch, tag, tip, -1));
                }
            }
            finally { FPDF_ClosePage(page); }
            return links;
        }

        /// <summary>
        /// Renders link overlays for the primary page onto the annotation canvas.
        /// Uses a manual bounds-check in Canvas_MouseLeftButtonDown for hit detection
        /// (transparent Canvas children are unreliable for WPF hit-testing alone).
        /// </summary>
        private void RenderPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            if (_doc is null || _currentFile is null) return;

            var links = GetPageLinks(pageIndex, bitmapW, bitmapH);
            foreach (var lnk in links)
            {
                var info = new LinkAnnotInfo(lnk.Tag, pageIndex, lnk.AnnotIndex);
                // Grow the overlay by LinkHitPad on every side so the hand cursor, right-click menu, and the
                // click bounds-check all share the padded hit area the tiled views use - thin one-line link
                // strips are easy to hit in single-page view too.
                var overlay = new Canvas
                {
                    Width            = lnk.Cw + LinkHitPad * 2,
                    Height           = lnk.Ch + LinkHitPad * 2,
                    Background       = Brushes.Transparent,
                    Cursor           = Cursors.Hand,
                    ToolTip          = lnk.Tip,
                    Tag              = info,
                    IsHitTestVisible = true,
                };
                Canvas.SetLeft(overlay, lnk.Cx - LinkHitPad);
                Canvas.SetTop(overlay, lnk.Cy - LinkHitPad);

                // Right-click menu: same actions as the tiled-view canvas menu, from the shared builder.
                var cm = new ContextMenu();
                TextOptions.SetTextFormattingMode(cm, TextFormattingMode.Display);
                TextOptions.SetTextRenderingMode(cm, TextRenderingMode.Grayscale);
                AddLinkMenuItems(cm, lnk.Tag, lnk.AnnotIndex, pageIndex);
                if (cm.Items.Count > 0) overlay.ContextMenu = cm;

                _annotationCanvas.Children.Add(overlay);
                _linkOverlays.Add(overlay);
            }

            if (links.Count > 0)
                SetStatus(string.Format(Loc("Str_PageOfLinks"), pageIndex + 1, _doc.PageCount, links.Count));
        }

        /// <summary>
        /// Removes a native PDF link annotation from the page /Annots array and persists the change.
        /// Called from the "Remove Link from PDF" context-menu item on link overlays.
        /// </summary>
        private void RemoveLinkAnnotation(int pageIndex, int annotIndex)
        {
            if (_doc is null || pageIndex >= _doc.PageCount || annotIndex < 0) return;
            try
            {
                var pdfPage = _doc.Pages[pageIndex];
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null || annotIndex >= annotsArr.Elements.Count) return;

                // Neutralize the annotation object before removing the /Annots reference.
                // If PdfSharpCore writes the orphaned indirect object to the output file,
                // aggressive PDF viewers that scan cross-reference tables directly (rather
                // than following /Annots) would still trigger the link without this step.
                PdfItem? elem = annotsArr.Elements[annotIndex];
                PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                if (ann != null)
                {
                    ann.Elements.Remove("/A");
                    ann.Elements.Remove("/Dest");
                    ann.Elements.Remove("/Subtype");
                }

                annotsArr.Elements.RemoveAt(annotIndex);
                MarkDirty();
                SaveTempAndReload();
                // Refresh the current page view so the overlay disappears.
                int sel = PageList.SelectedIndex;
                PageList.SelectedIndex = -1;
                PageList.SelectedIndex = sel;
                SetStatus(Loc("Str_LinkRemoved"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"{Loc("Str_LinkRemoveFailed")}\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Strips visual styling (border, color, appearance stream) from all Link annotations
        /// in the document so they render as invisible clickable areas rather than colored
        /// rectangles that can look like strikethroughs in other PDF viewers.
        /// </summary>
        private static void StripLinkAnnotationBorders(PdfDocument doc)
        {
            foreach (var pdfPage in doc.Pages)
            {
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null) continue;
                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    // Dereference subtype in case it is an indirect name.
                    var subtypeItem = ann.Elements["/Subtype"];
                    var subtype = (subtypeItem as PdfDictionary ?? DerefItem(subtypeItem) as PdfDictionary) is null
                        ? subtypeItem?.ToString() ?? ""
                        : "";
                    if (!subtype.Contains("Link")) continue;

                    // Remove appearance stream and color.
                    ann.Elements.Remove("/AP");
                    ann.Elements.Remove("/C");

                    // /BS (border style dict) takes precedence over /Border in PDF spec;
                    // set W=0 explicitly.  Also set /Border [0 0 0] for older viewers.
                    var bs = new PdfDictionary();
                    bs.Elements["/W"] = new PdfInteger(0);
                    ann.Elements["/BS"] = bs;

                    var borderArr = new PdfArray();
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    ann.Elements["/Border"] = borderArr;
                }
            }
        }

        /// <summary>
        /// Records a page's link rectangles for the tiled views (continuous, grid, two-page). No
        /// clickable overlay is created: in the tiled layout a per-link overlay swallows the click
        /// but its own handler never fires, so clicks and the hover cursor are resolved by bounds-
        /// testing these rects in Canvas_MouseLeftButtonDown and Canvas_MouseMove instead.
        /// </summary>
        private void AddSecondaryPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            _continuousLinks[pageIndex] = GetPageLinks(pageIndex, bitmapW, bitmapH);
        }

        /// <summary>
        /// Resolves a /Dest value (PdfArray, PdfString, or PdfName) to a 0-based page index.
        /// Returns null if the destination cannot be resolved.
        /// Note: PdfReference is internal to PdfSharpCore so we use reflection for ObjectNumber
        /// and var-inferred types instead of the type name.
        /// </summary>
        private int? ResolveDest(PdfItem? destItem)
        {
            if (destItem is null || _doc is null) return null;

            // Dereference indirect object if needed (PdfReference is internal, use duck-typing).
            destItem = DerefItem(destItem);

            PdfArray? arr = null;

            if (destItem is PdfArray a)
            {
                arr = a;
            }
            else if (destItem is PdfString || destItem is PdfName)
            {
                // Named destination - look up in the document catalog
                arr = ResolveNamedDest(destItem);
            }

            if (arr is null || arr.Elements.Count == 0) return null;

            // First element of the destination array is an indirect page reference.
            // PdfReference.ObjectNumber is public but its type is internal; use reflection.
            var pageRefItem = arr.Elements[0];
            int elemObjNum = GetObjectNumber(pageRefItem);
            if (elemObjNum > 0)
            {
                for (int i = 0; i < _doc.PageCount; i++)
                {
                    // PdfPage.Reference (public) gives us access to ObjectNumber
                    var pgRef = _doc.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == elemObjNum)
                        return i;
                }
            }
            else if (pageRefItem is PdfInteger pageInt)
            {
                int pn = pageInt.Value;
                if (pn >= 0 && pn < _doc.PageCount) return pn;
            }

            return null;
        }

        /// <summary>
        /// Resolves a named destination (string or name) to a destination array using the
        /// catalog's /Dests dictionary or /Names /Dests name tree.
        /// </summary>
        private PdfArray? ResolveNamedDest(PdfItem nameItem)
        {
            if (_doc is null) return null;
            string name = nameItem switch
            {
                PdfString s => s.Value,
                PdfName   n => n.Value.TrimStart('/'),
                _           => ""
            };
            if (string.IsNullOrEmpty(name)) return null;

            var catalog = _doc.Internals.Catalog;

            // Legacy /Dests dictionary (direct mapping)
            var dests = catalog.Elements.GetDictionary("/Dests");
            if (dests != null)
            {
                PdfItem? val = DerefItem(dests.Elements[name] ?? dests.Elements["/" + name] ?? new PdfInteger(-1));
                if (val is PdfArray da) return da;
                if (val is PdfDictionary dd) return dd.Elements.GetArray("/D");
            }

            // Modern /Names /Dests name tree
            var names = catalog.Elements.GetDictionary("/Names");
            var destTree = names?.Elements.GetDictionary("/Dests");
            if (destTree != null)
                return ResolveNameTree(destTree, name);

            return null;
        }

        /// <summary>
        /// Walks a PDF name tree to find the destination array for the given name.
        /// </summary>
        private static PdfArray? ResolveNameTree(PdfDictionary node, string name)
        {
            // Leaf node: flat /Names array [key val key val ...]
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var key = namesArr.Elements[i];
                    string keyStr = key is PdfString ks ? ks.Value : key?.ToString() ?? "";
                    if (keyStr == name)
                    {
                        PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                        if (val is PdfArray va) return va;
                        if (val is PdfDictionary vd) return vd.Elements.GetArray("/D");
                    }
                }
            }

            // Intermediate node: recurse into /Kids
            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    PdfItem? kid = DerefItem(kids.Elements[i]);
                    if (kid is PdfDictionary kd)
                    {
                        var result = ResolveNameTree(kd, name);
                        if (result != null) return result;
                    }
                }
            }

            return null;
        }
    }
}
