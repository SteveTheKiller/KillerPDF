using System.Collections.Generic;

namespace KillerPDF.Controls
{
    /// <summary>
    /// This pane's tab surface, exposed to the window. Wrappers inside the same partial class, so
    /// they can reach members PdfViewer.Tabs.cs keeps private; the Ext suffix exists only because a
    /// wrapper cannot share a name with what it wraps.
    ///
    /// The window calls these against ActiveViewer, so a shortcut or toolbar button acts on the
    /// focused pane.
    /// </summary>
    public partial class PdfViewer
    {
        // ── Opening and closing ──────────────────────────────────────────────────────────────
        internal void OpenInNewTabExt(string path) => OpenInNewTab(path);
        internal DocumentSession? ActiveSessionExt => _active;
        internal readonly record struct ComparisonDocument(
            string WorkingPath, string OriginalPath, string Title);

        internal ComparisonDocument? ActiveComparisonDocumentExt()
        {
            if (_active != null) CaptureSessionState(_active);
            return ComparisonDocumentFromSession(_active);
        }

        internal IReadOnlyList<ComparisonDocument> OpenPdfTabsExt()
        {
            if (_active != null) CaptureSessionState(_active);
            return [.. _sessions
                .Select(ComparisonDocumentFromSession)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .DistinctBy(item => item.OriginalPath, StringComparer.OrdinalIgnoreCase)];
        }

        private static ComparisonDocument? ComparisonDocumentFromSession(DocumentSession? session)
        {
            string? working = session?.CurrentFile ?? session?.DeferredPath ?? session?.OriginalFile;
            string? original = session?.OriginalFile ?? session?.DeferredPath ?? working;
            if (string.IsNullOrWhiteSpace(working) || string.IsNullOrWhiteSpace(original)
                || !System.IO.File.Exists(working))
                return null;
            return new ComparisonDocument(working, original, System.IO.Path.GetFileName(original));
        }
        internal void CloseTabExt(DocumentSession? s) => CloseTab(s);
        internal void CloseAllTabsExt() => CloseAllTabs();
        internal void CloseOtherTabsExt(DocumentSession? keep = null)
        {
            var target = keep ?? _active;
            if (target != null) CloseOtherTabs(target);
        }
        internal void CycleTabExt(int dir) => CycleTab(dir);
        internal void EnsureInitialSessionExt() => EnsureInitialSession();
        internal void MaterializeDeferredExt(DocumentSession target) => MaterializeDeferred(target);
        internal void SwitchToTabExt(DocumentSession target) => SwitchToTab(target);

        // ── The load handshake (FileOperations / ImportAndZip drive this) ────────────────────
        internal DocumentSession BeginTabLoadExt(out DocumentSession? prev, out bool createdNew)
            => BeginTabLoad(out prev, out createdNew);
        internal void AbortTabLoadExt(DocumentSession target, DocumentSession? prev, bool createdNew)
            => AbortTabLoad(target, prev, createdNew);

        // ── Session state ────────────────────────────────────────────────────────────────────
        internal void CaptureSessionStateExt(DocumentSession s) => CaptureSessionState(s);

        /// <summary>Fold this pane's live fields back into its own active session, if it has one.
        /// The close path has to do this for BOTH panes before asking about unsaved work.</summary>
        internal void CaptureActiveIfAny()
        {
            if (_active != null) CaptureSessionState(_active);
        }
        internal void ApplySessionStateExt(DocumentSession s) => ApplySessionState(s);

        /// <summary>Swap this pane's active session into the window's shared document fields. The
        /// counterpart to CaptureActiveIfAny; FocusPane runs both across a pane switch.
        ///
        /// The empty-pane branch is not an optimization, it prevents cross-pane corruption. The
        /// shared fields still describe the pane we just left, and EnsureInitialSession ends with
        /// CaptureSessionState, which copies _doc, _annotations, _undoStack and the rest BY
        /// REFERENCE. So the first session an empty pane created would alias the other pane's live
        /// document: opening a file in one pane replaced the other's, and switching tabs in one
        /// moved the other. Blanking the shared fields here means there is nothing to alias.</summary>
        internal void ApplyActiveSessionIfAny()
        {
            if (_active != null) { ApplySessionState(_active); return; }

            var blank = new DocumentSession();   // every collection field has its own initializer
            _sessions.Add(blank);
            SetActiveSession(blank);
            ApplySessionState(blank);
            ShowEmptyState();
        }

        /// <summary>Put this pane's active session back into the shared fields and nothing else -
        /// pure assignment, no UI, and no session created if there is none. Used by WithOwnSession,
        /// which runs from layout events and must not cause any further layout.</summary>
        internal void RestoreActiveFieldsOnly()
        {
            if (_active != null) ApplySessionState(_active);
        }

        /// <summary>Run view math with THIS pane's document in the window's shared fields.
        ///
        /// _doc, _viewMode, _fitMode, _zoomLevel and _gridColumns are WINDOW fields, but the
        /// handlers that do view math - the viewport's SizeChanged, the resize-settle timer,
        /// ReapplyGridOrFit - are per-pane: each pane's own ScrollViewer raises them. So an
        /// unfocused pane whose viewport ticked was refitting itself against the FOCUSED pane's
        /// document and fit mode, and writing that pane's zoom back out. That is why switching
        /// tabs in one pane kept changing the other pane's view.
        ///
        /// Swap this pane's session in, run, fold the view values back, then restore the focused
        /// pane. Only the view fields are folded back, not a full CaptureSessionState: that also
        /// writes DocStates to the registry, which a resize would do dozens of times a second.</summary>
        private bool _inOwnSessionScope;
        internal void WithOwnSession(System.Action work)
        {
            if (Host == null || _inOwnSessionScope || Host.IsViewerFocused(this))
            {
                work();
                return;
            }

            _inOwnSessionScope = true;
            // The element accessors have to follow the fields. Swapping only the document state
            // left the render path resolving PageHost / PreviewScroller through ActiveViewer, so
            // this pane's fit measured the OTHER pane's viewport and painted into its tiles.
            try
            {
                Host.RunWithViewerContext(this, () =>
                {
                    if (_active != null)
                    {
                        ApplySessionState(_active);
                        // ApplySessionState deliberately leaves PageIndex to RenderActiveSession,
                        // which never runs on this path. Seed it from this pane's session.
                        State.CurrentPage = _active.PageIndex;
                    }
                    work();
                    if (_active != null)
                    {
                        _active.ZoomLevel      = _zoomLevel;
                        _active.LastRenderZoom = _lastRenderZoom;
                        _active.Fit            = _fitMode;
                        _active.View           = _viewMode;
                        _active.GridColumns    = _gridColumns;
                        _active.ScrollH        = PagePreviewPanel?.HorizontalOffset ?? _active.ScrollH;
                        _active.ScrollV        = PagePreviewPanel?.VerticalOffset   ?? _active.ScrollV;
                    }
                });
            }
            finally
            {
                _inOwnSessionScope = false;
            }
        }

        /// <summary>The active tab's sidebar thumbnails, kept so switching away and back does not
        /// re-decode the document. Read by RestorePageListForActivePane (PageOperations.cs).</summary>
        internal PageThumbnailVm[]? ThumbCache
        {
            get => _active?.ThumbCache;
            set => _active?.ThumbCache = value;
        }
        internal string? ThumbCacheFile
        {
            get => _active?.ThumbCacheFile;
            set => _active?.ThumbCacheFile = value;
        }

        /// <summary>The active tab's thumbnail loader cancellation. Keeping it per tab prevents a
        /// second document in the same pane from stealing the first document's loader and cache.</summary>
        internal System.Threading.CancellationTokenSource? ThumbCts
        {
            get => _active?.ThumbCts;
            set => _active?.ThumbCts = value;
        }
        internal bool ThumbCacheComplete
        {
            get => _active?.ThumbCacheComplete == true;
            set => _active?.ThumbCacheComplete = value;
        }

        internal void MarkThumbnailCacheComplete(PageThumbnailVm[] cache)
        {
            var owner = _sessions.FirstOrDefault(s => ReferenceEquals(s.ThumbCache, cache));
            owner?.ThumbCacheComplete = true;
        }

        /// <summary>Highlight this pane's current page after the list is re-seated: assigning
        /// ItemsSource clears the selection.</summary>
        internal int CurrentPageIndex => State.CurrentPage;

        internal void NavigateToPageExt(int pageIndex)
        {
            if (_doc is null || pageIndex < 0 || pageIndex >= _doc.PageCount) return;
            if (_viewMode == ViewMode.Continuous)
                NavigateContinuousToPage(pageIndex);
            else
            {
                _currentPage = pageIndex;
                RenderPage(_viewMode == ViewMode.Grid ? 0 : pageIndex);
            }
        }

        internal void SyncPageListSelection(int? preservedPage = null)
        {
            if (preservedPage.HasValue) State.CurrentPage = preservedPage.Value;
            if (State.CurrentPage < 0) return;
            _syncingPageList = true;
            try { Host?.ViewerPageChanged(this, State.CurrentPage); }
            finally { _syncingPageList = false; }
            Host?.PageJumpText = (State.CurrentPage + 1).ToString();
            Host?.EnsureSidebarPageVisible(this, State.CurrentPage);
        }
        internal static void SaveDocStateExt(string? path, FitMode fit, double zoom, ViewMode view, int page)
            => SaveDocState(path, fit, zoom, view, page);
        internal static bool TryGetDocStateExt(string? path, out FitMode fit, out double zoom,
                                        out ViewMode view, out int page)
            => TryGetDocState(path, out fit, out zoom, out view, out page);

        // ── Strip and render ─────────────────────────────────────────────────────────────────
        internal void InitTabStripExt() => InitTabStrip();
        internal void RebuildTabStripExt() => RebuildTabStrip();
        /// <summary>The band changed width. Kept under the old name because the window still wires
        /// the focused pane's SizeChanged to it; each pane also raises its own now, and the call is
        /// guarded and idempotent, so the two agreeing costs nothing.</summary>
        internal void ScheduleTabReflowExt() => TabBarResized();
        internal void RenderActiveSessionExt() => RenderActiveSession();
        internal void ShowEmptyStateExt() => ShowEmptyState();
        internal void FlushAllRenderCachesExt() => FlushAllRenderCaches();
        internal static void InvalidateRenderCacheExt(DocumentSession? s) => InvalidateRenderCache(s);

        /// <summary>Make a brand-new empty session the active one. The startup restore builds the
        /// session list itself, so it needs to place the result rather than go through
        /// EnsureInitialSession.</summary>
        internal void SetSessionsExt(IEnumerable<DocumentSession> sessions, DocumentSession? active)
        {
            _sessions.Clear();
            foreach (var s in sessions) _sessions.Add(s);   // ObservableCollection has no AddRange
            SetActiveSession(active);
        }

        /// <summary>Build a deferred (lazy) session for the restore path - a tab that shows its
        /// title but does not load its document until it is first switched to.</summary>
        internal static DocumentSession MakeDeferredSession(string path)
            => new() { OriginalFile = path, CurrentFile = path, DeferredPath = path };

        // ── This pane's strip elements, for the window chrome that still positions them ──────
        // AppScale scales them, FullScreen hides them, SidebarLayout flips their margins - all of
        // which now have to act on BOTH panes rather than one window-level band.
        internal System.Windows.Controls.Border TabStripBorderCtl => TabStripBorder;
        internal System.Windows.Controls.Border TabStripFadeCtl => TabStripFade;
        internal System.Windows.Controls.Border TabBarRingCtl => TabBarRing;
    }
}
