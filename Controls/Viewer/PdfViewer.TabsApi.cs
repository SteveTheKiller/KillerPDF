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
            var owner = Owner;
            if (owner == null || _inOwnSessionScope || ReferenceEquals(owner.ActiveViewer, this))
            {
                work();
                return;
            }

            var focused = owner.ActiveViewer;
            focused.CaptureActiveIfAny();
            _inOwnSessionScope = true;
            // The element accessors have to follow the fields. Swapping only the document state
            // left the render path resolving PageHost / PreviewScroller through ActiveViewer, so
            // this pane's fit measured the OTHER pane's viewport and painted into its tiles.
            var prevActive = owner.SwapActiveViewer(this);
            try
            {
                if (_active != null)
                {
                    ApplySessionState(_active);
                    // ApplySessionState deliberately leaves PageIndex to RenderActiveSession,
                    // which never runs on this path - so an unfocused pane's State.CurrentPage
                    // sat stale (often -1) and the repointed fit math then fell back to the
                    // pane's stale tile size and fitted to garbage ("Page 0 of 73" in the
                    // status, pane zoomed wrong when the split opens - Steve, 2026-08-01).
                    // Seed it from the session being swapped in; navigation cannot happen
                    // inside the scope, so nothing needs folding back.
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
            }
            finally
            {
                owner.SwapActiveViewer(prevActive);
                _inOwnSessionScope = false;
                // RestoreActiveFieldsOnly, NOT ApplyActiveSessionIfAny. The latter shows the empty
                // state when a pane has no session, which repopulates the recents panel, which
                // resizes it, which raises the viewport's SizeChanged that called us - an infinite
                // layout recursion that left the window painting nothing at all. This path only
                // ever needs the fields put back; it must not touch the UI.
                focused.RestoreActiveFieldsOnly();
            }
        }

        /// <summary>This pane's sidebar thumbnails, kept so focusing it again does not re-decode the
        /// document. Read by RestorePageListForActivePane (PageOperations.cs).</summary>
        internal PageThumbnailVm[]? ThumbCache { get; set; }
        internal string? ThumbCacheFile { get; set; }

        /// <summary>This pane's thumbnail loader cancellation. PER PANE, not per window: one shared
        /// token meant focusing either pane cancelled whatever the other was still decoding, and
        /// since the half-filled cache still matched the page count it counted as usable - so the
        /// list re-seated with the labels and no pictures, permanently.</summary>
        internal System.Threading.CancellationTokenSource? ThumbCts { get; set; }

        /// <summary>Highlight this pane's current page after the list is re-seated: assigning
        /// ItemsSource clears the selection.</summary>
        internal void SyncPageListSelection()
        {
            if (State.CurrentPage >= 0) SyncCurrentPageTo(State.CurrentPage);
        }
        internal void SaveDocStateExt(string? path, FitMode fit, double zoom, ViewMode view, int page)
            => SaveDocState(path, fit, zoom, view, page);
        internal bool TryGetDocStateExt(string? path, out FitMode fit, out double zoom,
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
        internal void InvalidateRenderCacheExt(DocumentSession? s) => InvalidateRenderCache(s);

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
