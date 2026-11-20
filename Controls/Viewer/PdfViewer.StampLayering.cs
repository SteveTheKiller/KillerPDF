using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using ContentDoc = KillerPDF.Services.PdfContentDocument;

namespace KillerPDF.Controls
{
    /// <summary>
    /// #478: Apply puts the stamps (watermark and page number) into the page content, under the annotations the file
    /// already holds. While a stamp spec is active, the viewer burns it into an in-memory copy of the document with the
    /// same burn Apply runs, off the UI thread, and renders the pages the spec covers from that copy, so every zoom,
    /// detail tile, proof and monitor conversion and display path shows what Apply will make. Until the copy is ready
    /// the overlay draws the stamps as before; the overlay's stamps hide on a page in the same step its stamped bitmap
    /// is shown, and come back in the same step an unstamped one replaces it. Pages the spec does not cover, documents
    /// without a spec, books and protected documents render exactly as they did.
    /// </summary>
    public partial class PdfViewer
    {
        // The overlay's stamp elements on each page canvas, from its last RenderStamps.
        private readonly ConditionalWeakTable<Canvas, List<UIElement>> _stampElements = new();
        private bool _stampBurnScheduled;
        private CancellationTokenSource? _stampBurnCts;
        private (DocumentSession Session, string File, long Revision, StampSpec Spec, Dictionary<int, int> Rotations)? _stampBurnPending;
        private (string File, long Revision, StampSpec Spec, Dictionary<int, int> Rotations)? _stampBurnFailed;

        /// <summary>The background burn of the active stamps, for tests to wait on.</summary>
        internal Task StampBurnTask { get; private set; } = Task.CompletedTask;

        /// <summary>The last failure of a background burn of the stamps; the overlay keeps drawing them.</summary>
        internal string LastStampBurnError { get; private set; } = string.Empty;

        private static bool IsStampedBitmap(ImageSource? bitmap) => StampedPageBitmaps.IsStamped(bitmap);

        private static void NoteStampedRender(PdfBackgroundRenderCache.Request? request, int page, BitmapSource bitmap)
            => StampedPageBitmaps.Note(request, page, bitmap);

        // Every viewer page render captures its request here, so the page bitmaps follow the burn in force.
        // floatFrames: the renders draw float frames (a proof, a monitor conversion or the HDR presenter).
        private PdfBackgroundRenderCache.Request CaptureRenderRequest(string file, long revision, bool floatFrames)
            => _backgroundRenderCache.Capture(file, revision, _protection,
                CurrentStampBurn(file, revision)?.ForRender(floatFrames));

        // The stamped copy renders use at this revision. A burn holds only for the revision it was taken in, so every
        // render of one revision agrees on whether a page carries the stamps.
        private ViewerStampBurn? CurrentStampBurn(string file, long revision) =>
            _active is { StampBurn: { } burn } session && session.StampBurnRevision == revision
                && session.RenderRevision == revision && _nativeDoc is null && _protection is null
                && string.Equals(burn.SourcePath, file, StringComparison.OrdinalIgnoreCase) ? burn : null;

        private StampSpec? ActiveStampSpec => _docStampSpec is { } spec && (spec.NumbersEnabled || spec.WmEnabled) ? spec : null;

        // Called each time the overlay draws. The work runs once the current redraw is done.
        private void SyncStampLayering()
        {
            if (_stampBurnScheduled) return;
            _stampBurnScheduled = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(UpdateStampBurn));
        }

        /// <summary>Brings the stamped copy in line with the active stamps, the file and the page turns.</summary>
        internal void UpdateStampBurn()
        {
            _stampBurnScheduled = false;
            if (_active is not { } session) return;
            string? file = _currentFile;
            StampSpec? spec = ActiveStampSpec;
            bool wanted = spec is not null && file is not null && _nativeDoc is null && _protection is null && HasReadDocument;
            if (session.StampBurn is not null && session.StampBurnRevision != session.RenderRevision)
                session.StampBurn = null;   // the file changed since; its pages render without it already
            if (session.StampBurn is { } burn && (!wanted || !burn.MatchesDocument(file!, _pageRotations)))
            {
                // Stamps off, or the page turns moved: the page bitmaps go back to the file's own pixels. The pages keep
                // showing their stamped bitmaps until the new ones land in their place.
                session.StampBurn = null;
                InvalidateRenderCache(session);
                RepaintStampPages();
            }
            if (!wanted)
            {
                CancelStampBurn();
                return;
            }
            long revision = session.RenderRevision;
            if (session.StampBurn is { } current && current.Matches(file!, spec!, _pageRotations))
            {
                CancelStampBurn();
                return;
            }
            if (_stampBurnPending is { } pending && ReferenceEquals(pending.Session, session) && pending.File == file
                && pending.Revision == revision && ViewerStampBurn.SameSpec(pending.Spec, spec!)
                && SameTurns(pending.Rotations, _pageRotations))
                return;
            if (_stampBurnFailed is { } failed && failed.File == file && failed.Revision == revision
                && ViewerStampBurn.SameSpec(failed.Spec, spec!) && SameTurns(failed.Rotations, _pageRotations))
                return;
            CancelStampBurn();
            var cts = _stampBurnCts = new CancellationTokenSource();
            StampSpec copy = spec!.Clone();
            var turns = new Dictionary<int, int>(_pageRotations);
            _stampBurnPending = (session, file!, revision, copy, turns);
            StampBurnTask = BurnStampsAsync(session, file!, revision, copy, turns, cts);
        }

        private async Task BurnStampsAsync(DocumentSession session, string file, long revision, StampSpec spec,
            Dictionary<int, int> rotations, CancellationTokenSource cts)
        {
            CancellationToken token = cts.Token;
            ViewerStampBurn made;
            try { made = await Task.Run(() => ViewerStampBurn.Create(file, spec, rotations, token), token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The overlay keeps drawing the stamps over the page, as it did before the copy existed.
                LastStampBurnError = error.ToString();
                if (ReferenceEquals(_stampBurnCts, cts)) _stampBurnFailed = (file, revision, spec, rotations);
                return;
            }
            finally
            {
                if (ReferenceEquals(_stampBurnCts, cts))
                {
                    _stampBurnCts = null;
                    _stampBurnPending = null;
                }
                cts.Dispose();
            }
            if (token.IsCancellationRequested || !ReferenceEquals(session, _active) || session.RenderRevision != revision
                || _currentFile != file || ActiveStampSpec is not { } active || !ViewerStampBurn.SameSpec(active, spec)
                || !SameTurns(rotations, _pageRotations) || _nativeDoc is not null || _protection is not null)
            {
                SyncStampLayering();
                return;
            }
            LastStampBurnError = string.Empty;
            session.StampBurn = made;
            InvalidateRenderCache(session);
            session.StampBurnRevision = session.RenderRevision;
            // The pages keep their unstamped bitmaps, with the overlay's stamps, until the stamped ones land.
            RepaintStampPages();
        }

        private void CancelStampBurn()
        {
            CancelAndRelease(_stampBurnCts);
            _stampBurnCts = null;
            _stampBurnPending = null;
        }

        private static bool SameTurns(IReadOnlyDictionary<int, int> a, IReadOnlyDictionary<int, int> b)
        {
            static int Turn(IReadOnlyDictionary<int, int> turns, int page) =>
                turns.TryGetValue(page, out int value) ? ((value % 360) + 360) % 360 : 0;
            return a.Keys.Concat(b.Keys).All(page => Turn(a, page) == Turn(b, page));
        }

        // Draws the visible pages again without clearing them, so each page shows its old bitmap until its new one is
        // ready and both change in one step.
        private void RepaintStampPages()
        {
            if (!HasReadDocument) return;
            if (_viewMode.IsContinuous())
            {
                CancelAndRelease(_continuousSharpenCts);
                _continuousSharpenCts = null;
                _continuousSharpPages.Clear();
                for (int page = 0; page < _continuousPanel.Children.Count; page++)
                    if (_continuousPanel.Children[page] is Border { Child: Grid grid }
                        && grid.Children.Count > 0 && grid.Children[0] is Image { Source: not null })
                        _continuousDrawnWidths[page] = 0;
                _ = RenderContinuousPages(Math.Max(0, _currentPage));
                StartRerenderTimer();
            }
            else RenderPage(_viewMode == ViewMode.Grid ? 0 : Math.Max(0, _currentPage), keepTiles: true);
        }

        // Called right after RenderStamps put this page's stamps on the canvas.
        private void KeepStampElements(Canvas canvas, int firstStamp)
        {
            var elements = canvas.Children.Cast<UIElement>().Skip(firstStamp).ToList();
            _stampElements.AddOrUpdate(canvas, elements);
            ShowStampElementsFor(canvas);
        }

        /// <summary>Hides the overlay's stamps on each page canvas in <paramref name="grid"/> when its page bitmap carries
        /// them, and shows them when it does not. Called in the same step a page bitmap is put in place.</summary>
        private void ShowStampElementsFor(Grid grid)
        {
            foreach (var canvas in grid.Children.OfType<Canvas>()) ShowStampElementsFor(canvas);
        }

        private void ShowStampElementsFor(Canvas canvas)
        {
            if (!_stampElements.TryGetValue(canvas, out var elements) || elements.Count == 0) return;
            Image? page = ReferenceEquals(canvas, _annotationCanvas) ? PageImage
                : canvas.Parent is Grid grid && grid.Children.Count > 0 ? grid.Children[0] as Image : null;
            StampedPageBitmaps.ShowStamps(page, elements);
        }

        /// <summary>True when the overlay's stamps on <paramref name="canvas"/> are hidden because its page bitmap holds them.</summary>
        internal bool StampElementsHidden(Canvas canvas) =>
            _stampElements.TryGetValue(canvas, out var elements) && elements.Count > 0
            && elements.All(element => element.Visibility == Visibility.Hidden);

        // The night view's picture boxes for a page as its bitmap shows it: from the stamped copy when it holds the stamps.
        private BitmapHelpers.FracRect[] ShownImageRects(int page, ref ContentDoc? content)
        {
            Image? image = _annotationCanvas.Tag is int primary && primary == page ? PageImage
                : _continuousCanvases.TryGetValue(page, out var canvas) && canvas.Parent is Grid grid
                    && grid.Children.Count > 0 ? grid.Children[0] as Image : null;
            if (IsStampedBitmap(image?.Source) && _active is { } session)
            {
                var request = CaptureRenderRequest(_currentFile!, session.RenderRevision, floatFrames: false);
                if (request.StampsInPage(page)) return ImageRectsFor(request, page, ref content);
            }
            return ImageRectsFor(_currentFile!, _protection, page, ref content);
        }
    }
}
