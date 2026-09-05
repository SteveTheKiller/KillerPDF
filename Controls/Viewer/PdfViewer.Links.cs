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
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // Moved from Shell/Links.cs; the namespace and class line are the only changes. Window members
    // spelled bare here resolve through PdfViewer.Bridge.cs.
    public partial class PdfViewer
    {
        // ============================================================
        // PDF Link Annotation Overlays
        // ============================================================

        // LinkInfo lives in Models/LinkTypes.cs, not here - ContextMenu.cs is on the window and also
        // reads the link rects, so the type cannot be nested in whichever class owns them.

        // Per-page link rects for the tiled views (continuous / grid / two-page), keyed by page index.
        // Clicks and the hover cursor are resolved by bounds-testing these in Canvas_MouseLeftButtonDown
        // and Canvas_MouseMove: a per-link overlay swallows the click in the tiled layout but its own
        // handler never fires, so no visual overlay is created - these rects are the source of truth.
        private readonly Dictionary<int, List<LinkInfo>> _continuousLinks = [];

        /// <summary>The link-rect map, for the window side. ContextMenu.cs bounds-tests it to build
        /// the right-click menu and FileOperations.cs clears it on document change; both live on the
        /// window while Links.cs lives here.</summary>
        internal Dictionary<int, List<LinkInfo>> ContinuousLinks => _continuousLinks;

        /// <summary>Hit-slop around a link rect, shared with ContextMenu.cs so the menu targets the
        /// same links the click and hover paths do.</summary>
        internal const double LinkHitPadShared = LinkHitPad;

        // Small hit-slop (render-dim units) added around a link rect for click / hover / right-click
        // hit-testing so thin one-line link strips are easy to hit without over-reaching neighbors.
        // Applied identically in single-page (grows the overlay in RenderPageLinks) and tiled views
        // (bounds-checks) so both feel the same.
        private const double LinkHitPad = 5;

        // Persisted opt-IN for the click-safety confirmation prompt, surfaced as the
        // "Confirm before opening links" toggle on the About card footer.
        //
        // Positive sense and default OFF: links keep opening immediately unless you ask for the
        // prompt. ONE key, deliberately - a hardcoded master switch plus an inverted
        // "SkipLinkConfirm" opt-out can disagree with each other. The dialog's "Don't ask again" is
        // the same switch as the checkbox.
        internal const string ConfirmLinksSetting = "ConfirmLinks";

        // Confirms before opening an external link in the browser, unless the user opted out. Returns true
        // to proceed. Internal go-to-page links never call this.
        private bool ConfirmOpenLink(string url)
        {
            if (App.GetSetting(ConfirmLinksSetting) != "1") return true;
            var (result, dontAsk) = KillerDialog.ShowWithCheckbox(
                Host!.Window,
                $"{Loc("Str_LinkConfirmBody")}\n\n{url}",
                Loc("Str_LinkDontAsk"),
                Loc("Str_LinkConfirmTitle"),
                MessageBoxButton.OKCancel);
            if (result != MessageBoxResult.OK) return false;
            // "Don't ask again" IS the toggle, so turn it off rather than setting a second key the
            // About checkbox knows nothing about - that is how the two could drift apart before.
            if (dontAsk)
            {
                App.SetSetting(ConfirmLinksSetting, "0");
                LinkConfirmCheck?.IsChecked = false;
            }
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

        // Maps a PDF rectangle (points, origin bottom-left, already min/max-normalized) to a canvas-space
        // rectangle (pixels, origin top-left) for a page rendered at bitmapW x bitmapH. Shared by the
        // Engine link geometry and the WPF viewer stay pixel-identical through this mapping.
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
                {
                    RecordNavJump();   // Alt+Left retraces the link hop
                    _currentPage = pageIndex;
                }
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
        // path), Copy Link Address / Copy Email Address, and Remove Link from PDF when the native
        // annotation index is available. Shared by the single-page overlay menu and the tiled-
        // view canvas menu so both views offer the same actions.
        private void AddLinkMenuItems(ContextMenu menu, object target, int annotIndex, int pageIndex)
        {
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_OpenLink"), (_, _) => FollowLinkTarget(target), glyph: ""));
            if (target is string uri)
            {
                if (uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_CopyEmail"), (_, _) => TrySetClipboard(uri["mailto:".Length..]), "Ctrl+C", ""));
                else
                    menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_CopyLink"), (_, _) => TrySetClipboard(uri), "Ctrl+C", ""));
            }
            if (annotIndex >= 0)
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RemoveLink"), (_, _) => RemoveLinkAnnotation(pageIndex, annotIndex), "Delete", ""));
        }

        // Clipboard COM calls throw when another app is holding the clipboard open; swallow so a copy
        // never crashes the app (the worst case is the copy silently not happening).
        private static void TrySetClipboard(string text)
        {
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
            if (_currentFile is null) return links;
            try
            {
                PdfEngineDocumentSession session = EnsureEngineDocumentSession();
                if (pageIndex < 0 || pageIndex >= session.Pages.Count) return links;
                var page = session.Pages[pageIndex];
                double pageWidthPt  = page.Width;
                double pageHeightPt = page.Height;
                if (pageWidthPt  <= 0) pageWidthPt  = 595.28;
                if (pageHeightPt <= 0) pageHeightPt = 841.89;
                foreach (KillerPdf.Engine.Documents.PdfLinkInfo link in
                    PdfEngineIntegration.ReadPageLinks(session.Document, pageIndex))
                {
                    var (cx, cy, cw, ch) = PdfRectToCanvas(
                        link.Left, link.Bottom, link.Right, link.Top,
                        pageWidthPt, pageHeightPt, bitmapW, bitmapH);
                    if (cw < 1 || ch < 1) continue;
                    int? targetPage = link.DestinationPageIndex;
                    string? uri = link.Uri;
                    if (targetPage is null && uri is null) continue;
                    object tag = targetPage.HasValue ? (object)targetPage.Value : uri!;
                    string tip = targetPage.HasValue
                        ? string.Format(Loc("Str_Link_GoToPage"), targetPage.Value + 1)
                        : uri!;
                    links.Add(new LinkInfo(cx, cy, cw, ch, tag, tip, link.AnnotationIndex));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageLinks (engine): {ex}"); }
            return links;
        }

        private PdfEngineDocumentSession? _engineDocumentSession;

        private PdfEngineDocumentSession EnsureEngineDocumentSession()
        {
            if (_currentFile is null) throw new InvalidOperationException("No PDF is open.");
            if (_engineDocumentSession is not null && _engineDocumentSession.Path == _currentFile)
                return _engineDocumentSession;
            return _engineDocumentSession = PdfEngineDocumentSession.Open(_currentFile);
        }

        private void CloseEngineDocumentSession() => _engineDocumentSession = null;

        /// <summary>
        /// Renders link overlays for the primary page onto the annotation canvas.
        /// Uses a manual bounds-check in Canvas_MouseLeftButtonDown for hit detection
        /// (transparent Canvas children are unreliable for WPF hit-testing alone).
        /// </summary>
        internal void RenderPageLinks(int pageIndex, int bitmapW, int bitmapH)
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
                if (TryFindResource(typeof(ContextMenu)) is Style menuStyle) cm.Style = menuStyle;
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
            if (_doc is null || annotIndex < 0) return;
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            if (pageIndex < 0 || pageIndex >= session.PageCount) return;
            try
            {
                MarkDirty();
                SaveTempAndReload(finalizeSavedFile: path =>
                    PdfEngineIntegration.RemoveAnnotation(path, pageIndex, annotIndex));
                // Refresh the current page view so the overlay disappears.
                int sel = _currentPage;
                _currentPage = -1;
                _currentPage = sel;
                SetStatus(Loc("Str_LinkRemoved"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(Host!.Window, $"{Loc("Str_LinkRemoveFailed")}\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Save and Save As normalize native link appearances through PdfEngineIntegration.

        /// <summary>
        /// Records a page's link rectangles for the tiled views (continuous, grid, two-page). No
        /// clickable overlay is created: in the tiled layout a per-link overlay swallows the click
        /// but its own handler never fires, so clicks and the hover cursor are resolved by bounds-
        /// testing these rects in Canvas_MouseLeftButtonDown and Canvas_MouseMove instead.
        /// </summary>
        internal void AddSecondaryPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            _continuousLinks[pageIndex] = GetPageLinks(pageIndex, bitmapW, bitmapH);
        }

    }
}
