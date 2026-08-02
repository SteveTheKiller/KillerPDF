using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PdfSharpCore.Pdf;

namespace KillerPDF.Controls
{
    // Tabbed document support. KillerPDF keeps one window and one live "working set" of
    // per-document fields (in MainWindow.xaml.cs). Each open PDF is a DocumentSession that
    // owns its own copy of those fields. Switching tabs captures the live fields into the
    // outgoing session and applies the incoming session's fields, then re-renders.
    // Moved from Shell/Tabs.cs. THIS PANE's open documents and its own tab strip - `_sessions` and
    // `_active` are per-pane, NOT one window-level list serving one window-level strip. That single
    // ownership is what puts a document opened in the second pane's tab above the first one, and
    // what makes the two panes fight over which shows a document; both symptoms are the same bug.
    //
    // DocumentSession deliberately carries VIEW state (zoom, page, scroll, view mode) alongside
    // document state. That is correct because a session belongs to exactly one pane, so there is no
    // second viewer to disagree with it. Opening the same file in both panes gives two independent
    // copies, which is what makes that true - see the duplicate-file save guard, because two copies
    // can otherwise save over each other.
    //
    // The tab STRIP - the band, the drag physics and the focus ring - lives in
    // PdfViewer.TabStrip.cs. This file is the session model and its lifecycle.
    public partial class PdfViewer
    {
        // One open document. Holds the per-document state that the rest of MainWindow reads
        // and writes through its instance fields. The collection references here ARE the live
        // collections while this session is active.
        // internal, not private: PdfViewer.Bridge.cs types the active session as
        // MainWindow.DocumentSession so the moved render pipeline can pass it to the render cache
        // unchanged. Still nested, so it is only reachable as MainWindow.DocumentSession.
        internal sealed class DocumentSession : System.ComponentModel.INotifyPropertyChanged
        {
            public PdfDocument? Doc;
            public string? CurrentFile;
            public string? OriginalFile;
            // Set on a restored tab that hasn't been loaded yet (lazy tabs): Doc stays null until the
            // user first switches to it, so startup doesn't render every reopened PDF.
            public string? DeferredPath;

            public double ZoomLevel = 1.0;
            public double LastRenderZoom = 1.0;
            public FitMode Fit = FitMode.None;
            public ViewMode View = ViewMode.Continuous;
            public int GridColumns = 3;                // grid column count; grid zoom is derived from this, so it must be per-tab too
            public EditTool Tool = EditTool.Select;   // active editing tool, remembered per document
            public int PageIndex;
            public bool IsDirty;
            public bool ProtectedSource;               // #149: source file had a password/encryption when opened
            public double ScrollH;
            public double ScrollV;
            public int SearchPageCursor = -1;

            public Dictionary<int, List<PageAnnotation>> Annotations = [];
            public Dictionary<int, (int w, int h)> RenderDims = [];
            // LRU render cache: rasterized page bitmaps keyed by (page, size-bucket, rotation). Lets a
            // switch back to a recent tab reuse the bitmaps instead of re-running pdfium. Concurrent because
            // the continuous/secondary streamers read it from a background thread. Cleared on edits that change
            // a page's pixels or page order, and dropped entirely when the tab falls out of the LRU window.
            public readonly System.Collections.Concurrent.ConcurrentDictionary<(int page, int bucket, int rot), System.Windows.Media.Imaging.BitmapSource> RenderCache = new();
            public Dictionary<int, int> PageRotations = [];
            public Dictionary<int, string> FormTextValues = [];
            public Dictionary<int, bool> FormCheckValues = [];
            public Dictionary<string, string> FormRadioValues = [];
            public Dictionary<int, double> FormFontSizes = [];
            public Stack<UndoEntry> UndoStack = new();
            public Stack<UndoEntry> RedoStack = new();
            public Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects = [];
            public List<int> SearchResultPages = [];

            public string Title =>
                string.IsNullOrEmpty(OriginalFile)
                    ? "Untitled"
                    : System.IO.Path.GetFileNameWithoutExtension(OriginalFile);

            // ── Tab-strip presentation state ─────────────────────────────────────────────────
            // Everything below is bound by the tab template (PdfViewer.xaml) and nothing else
            // reads it. It has to NOTIFY: a strip row is only rebuilt when the collection itself
            // changes, so a property edited in place on a live row would otherwise never repaint.
            // (The same trap as KillerNotes issue #13.)

            private string _tabLabel = "Untitled";
            /// <summary>Title with the dirty dot, as the tab shows it.</summary>
            public string TabLabel { get => _tabLabel; private set { if (_tabLabel != value) { _tabLabel = value; Notify(); } } }

            private string _tabTip = "Untitled";
            /// <summary>The tab's tooltip: the full path this document came from.</summary>
            public string TabTip { get => _tabTip; private set { if (_tabTip != value) { _tabTip = value; Notify(); } } }

            /// <summary>Re-read the label and tooltip off the document. Called from RebuildTabStrip,
            /// which is the one funnel every add, close, save and load already goes through, so
            /// there is no second place that has to remember to keep the strip current.</summary>
            internal void RefreshTabLabel()
            {
                TabLabel = (IsDirty ? "• " : "") + Title;
                TabTip   = OriginalFile ?? "Untitled";
            }

            private bool _isActive;
            /// <summary>The front tab of its pane.</summary>
            public bool IsActive { get => _isActive; set { if (_isActive != value) { _isActive = value; Notify(); } } }

            // Leftmost tab in the strip. Only the focus ring reads this: the band draws the ring's
            // outermost verticals itself (TabEdgeLeft / TabEdgeRight), because a tab's own outer
            // border sits on the ScrollViewer's clip edge and survives or vanishes depending on how
            // the UniformGrid divided a fractional band width. Without it the first and last tab
            // drew that side TOO, so the outer edge of the ring came out 2px wherever the clip
            // spared it and 1px everywhere else.
            private bool _isFirst;
            public bool IsFirst { get => _isFirst; set { if (_isFirst != value) { _isFirst = value; Notify(); } } }

            // Sitting on the strip's right EDGE - the last visible tab, but only while the overflow
            // chevron is hidden. The tab's 1px right border is a divider BETWEEN tabs, so a tab on
            // the edge drops it, where it would read as a stray rule; a tab with the chevron beside
            // it still wants it. It also decides who owns the ring's right vertical.
            private bool _isLast;
            public bool IsLast { get => _isLast; set { if (_isLast != value) { _isLast = value; Notify(); } } }

            // True only for the ACTIVE tab of the FOCUSED pane, and only while split. The focus ring
            // has to continue around the active tab - the tab and the card are one surface, so a
            // ring that stops at the strip reads as broken.
            private bool _paneFocused;
            public bool PaneFocused { get => _paneFocused; set { if (_paneFocused != value) { _paneFocused = value; Notify(); } } }

            // Active tab of the pane that does NOT have focus. Not simply !PaneFocused: with one
            // pane open there is no focused/unfocused distinction to draw, and the single pane's lip
            // stays bright.
            private bool _paneDimmed;
            public bool PaneDimmed { get => _paneDimmed; set { if (_paneDimmed != value) { _paneDimmed = value; Notify(); } } }

            // In the strip right now, as opposed to behind the chevron. The strip caps the NUMBER of
            // tabs rather than letting them shrink without limit (ApplyTabWindow), and a tab outside
            // the window collapses - UniformGrid ignores a collapsed child when it divides the band,
            // so the ones left still fill it edge to edge. True by default: a tab is in the strip
            // until something works out that it does not fit.
            private bool _isStripVisible = true;
            public bool IsStripVisible { get => _isStripVisible; set { if (_isStripVisible != value) { _isStripVisible = value; Notify(); } } }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            private void Notify([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
                => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        // ObservableCollection, not List: the strip is an ItemsControl bound straight to this, so an
        // add, a close or a drag-reorder repaints on its own. That binding is the whole point of the
        // port - the strip used to be code-built Borders kept in step by hand.
        private readonly System.Collections.ObjectModel.ObservableCollection<DocumentSession> _sessions = [];
        private DocumentSession? _active;

        // ============================================================
        // Session state capture / apply
        // ============================================================

        // Copy the live working set INTO the session (call before switching away from it).
        private void CaptureSessionState(DocumentSession s)
        {
            s.Doc            = _doc;
            s.CurrentFile    = _currentFile;
            s.OriginalFile   = _originalFile;
            s.ZoomLevel      = _zoomLevel;
            s.LastRenderZoom = _lastRenderZoom;
            s.Fit            = _fitMode;
            s.View           = _viewMode;
            s.GridColumns    = _gridColumns;
            s.Tool           = _currentTool;
            s.IsDirty        = _isDirty;
            s.ProtectedSource = _openedFromProtected;
            s.SearchPageCursor = Search.PageCursor;
            // State.CurrentPage, not PageList.SelectedIndex: the sidebar is a window singleton
            // that follows the FOCUSED pane, so an unfocused pane capturing (the close path, the
            // save path's dirty check) was parking the OTHER pane's page number into its session.
            // Identical for the focused pane by the stage-3a sync.
            s.PageIndex      = State.CurrentPage >= 0 ? State.CurrentPage : s.PageIndex;
            s.ScrollH        = PagePreviewPanel?.HorizontalOffset ?? 0;
            s.ScrollV        = PagePreviewPanel?.VerticalOffset ?? 0;

            s.Annotations      = _annotations;
            s.RenderDims       = _renderDims;
            s.PageRotations    = _pageRotations;
            s.FormTextValues   = _formTextValues;
            s.FormCheckValues  = _formCheckValues;
            s.FormRadioValues  = _formRadioValues;
            s.FormFontSizes    = _formFontSizes;
            s.UndoStack        = _undoStack;
            s.RedoStack        = _redoStack;
            s.AllSearchRects   = Search.AllSearchRects;
            s.SearchResultPages = Search.ResultPages;
            // Persist this document's fit/zoom/view/page so reopening it (even after a restart) restores it.
            // Two-pane guard: DocStates is keyed by file path, so the SAME file open in BOTH panes
            // (two independent copies) is two writers on one entry - whichever pane captured last
            // silently overwrote the state the user actually left the file in, and on quit that was
            // just the close path's fixed A-then-B capture order. Only the focused pane writes when
            // the other pane also holds the file. FocusPane captures the outgoing pane BEFORE the
            // swap, so the pane being LEFT still counts as focused here - the rule this yields is
            // "the most recently used pane wins". A pane holding the only copy always writes.
            if (Owner == null || ReferenceEquals(Owner.ActiveViewer, this) || !OtherPaneHasCopyOf(s.OriginalFile))
                SaveDocState(s.OriginalFile, s.Fit, s.ZoomLevel, s.View, s.PageIndex);
        }

        /// <summary>True when the other pane has a session holding the same file (loaded or
        /// deferred). Compares OriginalFile, not CurrentFile, for the same reason
        /// OtherPaneHasDirtyCopyOf does: crop and rotate swap the working file to a temp path.
        /// Read-only - it must NOT capture the other pane (this runs inside a capture).</summary>
        private bool OtherPaneHasCopyOf(string? originalFile)
        {
            if (string.IsNullOrEmpty(originalFile) || Owner == null) return false;
            var other = ReferenceEquals(Owner.Viewer, this) ? Owner.ViewerB : Owner.Viewer;
            return other.SessionsRef.Any(x => (x.Doc != null || x.DeferredPath != null)
                && string.Equals(x.OriginalFile, originalFile, StringComparison.OrdinalIgnoreCase));
        }

        // ── Per-document view state (persisted across restarts, keyed by file path) ──────────────────
        // So reopening a file restores how you left it (fit mode, zoom, view mode, page) instead of the
        // per-view-mode default. Stored as one registry value: lines of "path|fit|zoom|view|page", most
        // recent first, capped. '|' and newline are both illegal in Windows paths, so they're safe delimiters.
        private const int DocStatesMax = 40;

        private void SaveDocState(string? path, FitMode fit, double zoom, ViewMode view, int page)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;   // skip Untitled/imported
            string entry = string.Join("|", path,
                fit.ToString(),
                zoom.ToString(System.Globalization.CultureInfo.InvariantCulture),
                view.ToString(),
                page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var lines = new List<string> { entry };
            var raw = App.GetSetting("DocStates");
            if (!string.IsNullOrEmpty(raw))
                foreach (var line in raw!.Split('\n'))
                {
                    if (line.Length == 0) continue;
                    int bar = line.IndexOf('|');
                    string lpath = bar > 0 ? line[..bar] : line;
                    if (!string.Equals(lpath, path, StringComparison.OrdinalIgnoreCase))
                        lines.Add(line);
                }
            if (lines.Count > DocStatesMax) lines = lines.GetRange(0, DocStatesMax);
            App.SetSetting("DocStates", string.Join("\n", lines));
        }

        private bool TryGetDocState(string? path, out FitMode fit, out double zoom, out ViewMode view, out int page)
        {
            fit = FitMode.None; zoom = 1.0; view = ViewMode.Continuous; page = 0;
            if (string.IsNullOrEmpty(path)) return false;
            var raw = App.GetSetting("DocStates");
            if (string.IsNullOrEmpty(raw)) return false;
            foreach (var line in raw!.Split('\n'))
            {
                var p = line.Split('|');
                if (p.Length < 5 || !string.Equals(p[0], path, StringComparison.OrdinalIgnoreCase)) continue;
                Enum.TryParse(p[1], out fit);
                double.TryParse(p[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out zoom);
                Enum.TryParse(p[3], out view);
                int.TryParse(p[4], out page);
                if (zoom <= 0) zoom = 1.0;
                return true;
            }
            return false;
        }

        // Point the live working set AT the session's state. Pure field assignment - no UI.
        private void ApplySessionState(DocumentSession s)
        {
            _doc            = s.Doc;
            _currentFile    = s.CurrentFile;
            _originalFile   = s.OriginalFile;
            // The cached PDFium link handle belongs to the file we're switching AWAY from. This is the one
            // chokepoint every active-doc swap funnels through (tab switch, close-tab, close-all), so drop
            // it here and it can never outlive its document; the next link extraction reopens it lazily for
            // the new file (see EnsureLinkPdfiumDoc). CloseLinkPdfiumDoc is idempotent and cheap.
            CloseLinkPdfiumDoc();
            _zoomLevel      = s.ZoomLevel;
            _lastRenderZoom = s.LastRenderZoom;
            _fitMode        = s.Fit;
            _viewMode       = s.View;
            _gridColumns    = s.GridColumns;
            _currentTool    = s.Tool;
            _isDirty        = s.IsDirty;
            _openedFromProtected = s.ProtectedSource;
            Search.PageCursor = s.SearchPageCursor;

            _annotations      = s.Annotations;
            _renderDims       = s.RenderDims;
            _pageRotations    = s.PageRotations;
            _formTextValues   = s.FormTextValues;
            _formCheckValues  = s.FormCheckValues;
            _formRadioValues  = s.FormRadioValues;
            _formFontSizes    = s.FormFontSizes;
            _undoStack        = s.UndoStack;
            _redoStack        = s.RedoStack;
            _navBack.Clear();      // jump history is per-view-session: a tab switch starts fresh
            _navForward.Clear();
            Search.AllSearchRects = s.AllSearchRects;
            Search.ResultPages    = s.SearchResultPages;
            TouchRenderLru(s);   // this tab is now active: keep its render cache, evict tabs beyond the window
        }

        // ── LRU render-bitmap cache ───────────────────────────────────────────────────────────────────
        // Keeps the rasterized page bitmaps of the most-recent few tabs so switching back skips pdfium and
        // fills instantly. The render paths (single / secondary tiles / continuous) check the active tab's
        // cache before rasterizing and store the frozen bitmap after building it.
        private readonly List<DocumentSession> _renderLru = [];
        private const int RenderCacheTabCap = 3;

        // Background-thread safe: a cached frozen bitmap for this render, or null (the caller must rasterize).
        internal static System.Windows.Media.Imaging.BitmapSource? TryGetCachedRender(DocumentSession? s, int page, int bucket, int rot)
            => (s != null && s.RenderCache.TryGetValue((page, bucket, rot), out var b)) ? b : null;

        // #122: cap the number of cached page bitmaps per tab. The cache used to grow without
        // bound (one bitmap per page ever rendered, several MB each), so scrolling a large
        // image-heavy document in Continuous view pinned gigabytes in one tab.
        private const int RenderCachePageCap = 48;

        internal static void CacheRender(DocumentSession? s, int page, int bucket, int rot, System.Windows.Media.Imaging.BitmapSource bmp)
        {
            if (s == null) return;
            if (bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();
            s.RenderCache[(page, bucket, rot)] = bmp;
            // Evict the entries farthest from the page just cached: renders arrive around the
            // viewport, so this keeps a moving window of nearby pages hot and stays safe to run
            // from any thread (no UI state needed).
            while (s.RenderCache.Count > RenderCachePageCap)
            {
                var farthest = default((int page, int bucket, int rot));
                int bestDist = -1;
                foreach (var key in s.RenderCache.Keys)
                {
                    int d = Math.Abs(key.page - page);
                    if (d > bestDist) { bestDist = d; farthest = key; }
                }
                if (bestDist <= 0) break;   // only current-page entries left; nothing sane to evict
                s.RenderCache.TryRemove(farthest, out _);
            }
        }

        // #135: the invert state is baked into cached pixels - drop every cached tab's page
        // bitmaps when it flips so no stale-colored bitmap survives the toggle. The image-rect
        // cache (the carve-out that keeps pictures uninverted) goes with it; it re-fills lazily.
        private void FlushAllRenderCaches()
        {
            foreach (var s in _renderLru) s.RenderCache.Clear();
            // THIS pane's rect cache - the bare call, NOT `Viewer.FlushImageRectCache()`, which
            // hardcodes pane A and leaves pane B's night-mode carve-out cache serving rects from
            // the previous state after an invert toggle.
            FlushImageRectCache();
        }

        // Mark a tab most-recently-used; drop the bitmap caches of tabs that fall outside the LRU window.
        private void TouchRenderLru(DocumentSession? s)
        {
            if (s == null) return;
            _renderLru.Remove(s);
            _renderLru.Add(s);
            bool dropped = false;
            while (_renderLru.Count > RenderCacheTabCap)
            {
                var old = _renderLru[0];
                _renderLru.RemoveAt(0);
                old.RenderCache.Clear();
                dropped = true;
            }
            if (dropped) CompactLohSoon();
        }

        // #122: .NET Framework never compacts the Large Object Heap on its own, so even after the
        // page-bitmap caches are dropped the process keeps its peak RAM (the classic "closed the
        // tab, Task Manager still shows gigabytes"). Request a one-shot LOH compaction at idle,
        // deferred so it never janks the close/switch animation itself.
        private void CompactLohSoon()
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, (Action)(() =>
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
            }));
        }

        // Drop a tab's cached bitmaps after an edit that changes page pixels or page order.
        private void InvalidateRenderCache(DocumentSession? s) => s?.RenderCache.Clear();

        // Make sure there is always at least one session, adopting whatever is currently live.
        private void EnsureInitialSession()
        {
            if (_sessions.Count > 0) return;
            var s = new DocumentSession();
            _sessions.Add(s);
            _active = s;
            // Only adopt the live working set when this pane actually owns it. CaptureSessionState
            // copies _doc, _annotations, _undoStack and the rest BY REFERENCE, so capturing while
            // the shared fields still describe the other pane makes this session an alias of that
            // pane's live document - the same trap ApplyActiveSessionIfAny guards against. An
            // unfocused pane's first session stays genuinely blank instead.
            if (Owner == null || ReferenceEquals(Owner.ActiveViewer, this)) CaptureSessionState(s);
        }

        // Commit / cancel any in-progress interaction so it doesn't bleed onto another document.
        private void CancelTransientForSwitch()
        {
            CommitActiveTextBox();
            RemoveTextEditHandles();
            ClearSelection();
            ClearTextSelection();
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
        }

        // ============================================================
        // Rendering the active session
        // ============================================================

        // Re-render whatever document the active session holds (or show the empty drop zone).
        private void RenderActiveSession()
        {
            if (_active == null || _active.Doc == null) { ShowEmptyState(); return; }

            FileNameLabel.Text = System.IO.Path.GetFileName(_active.OriginalFile ?? "");
            _annotationCanvas.Children.Clear();
            MarkDirty(_isDirty);   // sync the Save button color to this tab's dirty state
            BootstrapDocumentView(_active.PageIndex, autoFit: false);
            SetTool(_active.Tool); // restore this document's active editing tool (and its tool bar)

            // Restore the saved scroll position after the Background zoom pass queued inside
            // BootstrapDocumentView has run (ContextIdle is lower priority than Background).
            double sh = _active.ScrollH, sv = _active.ScrollV;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, (Action)(() =>
            {
                try
                {
                    PagePreviewPanel.ScrollToHorizontalOffset(sh);
                    PagePreviewPanel.ScrollToVerticalOffset(sv);
                }
                catch { }
            }));
        }

        // Visual reset to the no-document drop-zone state. Mirrors CloseFile's teardown but
        // does not close the document or touch session bookkeeping (callers handle that).
        private void ShowEmptyState()
        {
            _activeTextBox = null;
            RemoveTextEditHandles();
            _thumbCts?.Cancel();
            PageList.ItemsSource = null;
            PageImage.Source = null;
            _annotationCanvas.Children.Clear();
            FileNameLabel.Text = "";
            DropZone.Visibility = Visibility.Visible;
            PopulateRecentFilesList();
            PagePreviewPanel.Visibility = Visibility.Collapsed;
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
            SetTool(EditTool.Select);
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
            _pageJumpBox.IsEnabled = false;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _pageJumpBox.Text = "";
            _pageTotalLabel.Text = "/ –";
            OutlineTree.Items.Clear();
            SidebarOutlinesTab.IsEnabled = false;
            if (_sidebarShowingOutlines) SwitchSidebarToPagesTab();
            SyncSidebarToDocState(hasDoc: false, startup: false);   // nothing open: collapse the rail, hide page controls
            MarkDirty(false);
            SetStatus(Loc("Str_Ready"));
        }

        // ============================================================
        // Opening / switching / closing tabs
        // ============================================================

        // Prepare a tab to receive a document load: capture the current tab, then either reuse
        // the active tab if it's empty or create a new one, and blank the live working set.
        private DocumentSession BeginTabLoad(out DocumentSession? prev, out bool createdNew)
        {
            EnsureInitialSession();
            CommitActiveTextBox();
            CancelTransientForSwitch();
            prev = _active;
            if (_active != null) CaptureSessionState(_active);

            DocumentSession target;
            if (_active != null && _active.Doc == null && _active.DeferredPath == null)
            {
                target = _active;          // reuse the current empty tab (never a deferred one)
                createdNew = false;
            }
            else
            {
                target = new DocumentSession();
                // Inherit the current view mode so a newly opened PDF doesn't snap back to the
                // default (Continuous) when the user prefers Single / Two-Page / Grid.
                if (prev != null) { target.View = prev.View; target.Fit = prev.Fit; }
                _sessions.Add(target);
                createdNew = true;
            }
            SetActiveSession(target);
            ApplySessionState(target);     // blank live fields (target has no document yet)
            return target;
        }

        // Roll back a failed / cancelled load started by BeginTabLoad.
        private void AbortTabLoad(DocumentSession target, DocumentSession? prev, bool createdNew)
        {
            if (createdNew) _sessions.Remove(target);
            SetActiveSession(prev);
            if (prev != null) { ApplySessionState(prev); RenderActiveSession(); }
            else { EnsureInitialSession(); RenderActiveSession(); }
            RebuildTabStrip();
        }

        // Returns an open session for the given file path (case-insensitive full-path match), or null.
        private DocumentSession? FindOpenSession(string path)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(path); } catch { full = path; }
            return _sessions.FirstOrDefault(s =>
                (s.Doc != null || s.DeferredPath != null) &&
                !string.IsNullOrEmpty(s.OriginalFile) &&
                string.Equals(SafeFullPath(s.OriginalFile!), full, StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeFullPath(string p)
        {
            try { return System.IO.Path.GetFullPath(p); } catch { return p; }
        }

        // Open a PDF in its own tab (reusing the current tab if it is empty). If the same file is
        // already open in an unedited tab, switch to that tab instead of opening a duplicate.
        private void OpenInNewTab(string path)
        {
            EnsureInitialSession();
            CommitActiveTextBox();
            if (_active != null) CaptureSessionState(_active);   // keep dirty / path current for the check

            var existing = FindOpenSession(path);
            if (existing != null && !existing.IsDirty)
            {
                SwitchToTab(existing);
                SetStatus($"Already open: {System.IO.Path.GetFileName(path)}");
                return;
            }

            var target = BeginTabLoad(out var prev, out bool createdNew);
            OpenFile(path);
            if (_doc == null)
            {
                // A background open (encryption strip / repair) finalizes this tab itself, so the
                // not-yet-loaded _doc isn't a failure - leave the tab in place.
                if (_asyncOpenPending) return;
                // Open failed, was cancelled, or a password prompt was dismissed.
                AbortTabLoad(target, prev, createdNew);
                return;
            }
            CaptureSessionState(_active!);
            SetTool(_currentTool);   // sync the tool UI to this (new) tab's tool
            RebuildTabStrip();
        }

        // Cycle to the next (dir = +1) or previous (dir = -1) open document tab.
        private void CycleTab(int dir)
        {
            var docTabs = _sessions.Where(t => t.Doc != null || t.DeferredPath != null).ToList();
            if (docTabs.Count < 2 || _active == null) return;
            int i = docTabs.IndexOf(_active);
            if (i < 0) return;
            int next = (i + dir + docTabs.Count) % docTabs.Count;
            SwitchToTab(docTabs[next]);
        }

        /// <summary>Make <paramref name="s"/> this pane's active session and mark its tab.
        ///
        /// The IsActive flags are what the strip template triggers on, so every write to _active
        /// goes through here - a raw assignment leaves the old tab drawn as the front one.</summary>
        private void SetActiveSession(DocumentSession? s)
        {
            _active = s;
            foreach (var t in _sessions) t.IsActive = ReferenceEquals(t, s);
        }

        // Switch the active tab to an already-loaded session.
        private void SwitchToTab(DocumentSession target)
        {
            if (target == _active) return;
            // _doc, _annotations and the rest of the "live working set" are window fields shared by
            // BOTH panes (see PdfViewer.Bridge.cs) - they describe whichever pane is ActiveViewer,
            // not this pane specifically. Clicking a tab in a pane that is not (yet) focused must
            // claim that ownership FIRST, or CaptureSessionState/ApplySessionState below read and
            // write the OTHER pane's fields: this pane's tab strip ends up showing the right tab
            // while its canvas gets repainted with whatever the actually-focused pane rendered next.
            // FocusPane no-ops when this pane already owns focus. (#161 - "clicking a tab on one
            // pane is making it show up in the other one again", 2026-08-01.)
            Owner?.FocusPane(this);
            CommitActiveTextBox();
            CancelTransientForSwitch();
            if (_active != null) CaptureSessionState(_active);
            SetActiveSession(target);
            ApplySessionState(target);
            // Hide the document content while the new tab renders and restores its scroll position, then fade
            // it in. This masks the rebuild and the "loads at the top then snaps to my place" jump - the user
            // only sees the final, correctly-scrolled view fade in. PageContentGrid is the parent of BOTH the
            // single/grid panel and the continuous panel, so one fade covers every view mode.
            PageContentGrid.BeginAnimation(UIElement.OpacityProperty, null);
            PageContentGrid.Opacity = 0;
            if (target.Doc == null && target.DeferredPath != null)
                MaterializeDeferred(target);
            else
                RenderActiveSession();
            // The switch can move the overflow window (the incoming tab may have been behind the
            // chevron), which changes which tab sits on each edge - and that is the card's corner
            // rounding and the ring's outer verticals, not just the strip.
            RebuildTabStrip();
            FadeInDocContent();
        }

        // Fade the document pane content back in after a switch. Queued at ContextIdle so it runs AFTER the
        // scroll-position restore (also ContextIdle, queued earlier by RenderActiveSession) - the snap to
        // position happens while hidden, so it's never seen. Always lands at full opacity.
        private void FadeInDocContent()
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, (Action)(() =>
            {
                var fade = new System.Windows.Media.Animation.DoubleAnimation(
                    0, 1, new Duration(TimeSpan.FromMilliseconds(140)))
                { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
                fade.Completed += (_, _) =>
                {
                    PageContentGrid.BeginAnimation(UIElement.OpacityProperty, null);
                    PageContentGrid.Opacity = 1;
                };
                PageContentGrid.BeginAnimation(UIElement.OpacityProperty, fade);
            }));
        }

        // Load a restored-but-deferred tab's PDF the first time it is viewed (lazy tabs). The session
        // must already be the live working set (ApplySessionState called) before this runs.
        private void MaterializeDeferred(DocumentSession target)
        {
            var path = target.DeferredPath;
            target.DeferredPath = null;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                RenderActiveSession();        // file vanished since last session - show the empty state
                return;
            }
            OpenFile(path!);                  // loads into the live fields and renders the view
            if (_doc == null)
            {
                if (_asyncOpenPending) return;   // background strip/repair finalizes the tab itself
                RenderActiveSession(); return;
            }
            CaptureSessionState(target);      // persist the now-loaded document back into the session
        }

        // Close a tab. Prompts to save if that tab has unsaved changes, then switches to a
        // neighbouring tab (or the empty state when the last tab closes).
        // Closes every open document tab except `keep` (each may prompt to save if dirty, like a manual close).
        private void CloseOtherTabs(DocumentSession keep)
        {
            foreach (var s in _sessions.Where(z => !ReferenceEquals(z, keep) && (z.Doc != null || z.DeferredPath != null)).ToList())
                CloseTab(s);
        }

        private void CloseTab(DocumentSession? s)
        {
            EnsureInitialSession();
            if (s == null) return;

            // Same reason as the top of SwitchToTab: claim the shared fields for this pane before
            // touching them below, in case this pane is not (yet) ActiveViewer - e.g. the tab
            // context menu's Close Tab / Close Other Tabs, invoked directly on this pane's own
            // instance. No-ops when already focused.
            Owner?.FocusPane(this);

            // Make the target the live working set so its dirty flag / document are current.
            if (s != _active)
            {
                CommitActiveTextBox();
                CancelTransientForSwitch();
                if (_active != null) CaptureSessionState(_active);
                SetActiveSession(s);
                ApplySessionState(s);
                RenderActiveSession();
            }
            else
            {
                CommitActiveTextBox();
                CaptureSessionState(s);
            }

            if (_isDirty)
            {
                var res = KillerDialog.Show(W,   // W, not `this`: the owner parameter is Window?, and this is a UserControl
                    Loc("Str_Dlg_UnsavedClose"),
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) { RebuildTabStrip(); return; }
            }

            try { _doc?.Close(); } catch { }
            _doc = null;

            int idx = _sessions.IndexOf(s);
            _sessions.Remove(s);
            _renderLru.Remove(s);    // don't pin a closed tab's render cache in the LRU list
            s.RenderCache.Clear();
            CompactLohSoon();        // #122: give the freed bitmap memory back to the OS

            if (_sessions.Count == 0)
            {
                App.RemoveSetting("LastFile");   // a manually emptied window won't reopen on launch
                var blank = new DocumentSession();
                _sessions.Add(blank);
                SetActiveSession(blank);
                ApplySessionState(blank);
                ShowEmptyState();
            }
            else
            {
                var next = _sessions[Math.Min(idx, _sessions.Count - 1)];
                SetActiveSession(next);
                ApplySessionState(next);
                if (next.Doc == null && next.DeferredPath != null) MaterializeDeferred(next);
                else RenderActiveSession();
            }
            RebuildTabStrip();
        }

        // Ctrl+Q: close every open document and reset to a single blank tab, with one combined warning
        // if anything is unsaved (rather than a prompt per tab).
        private void CloseAllTabs()
        {
            EnsureInitialSession();
            CommitActiveTextBox();
            if (_active != null) CaptureSessionState(_active);

            var docTabs = _sessions.Where(t => t.Doc != null || t.DeferredPath != null).ToList();
            if (docTabs.Count == 0) return;

            if (docTabs.Any(t => t.IsDirty))
            {
                var res = KillerDialog.Show(W, Loc("Str_Dlg_UnsavedCloseAll"),   // W, not `this`
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) { RebuildTabStrip(); return; }
            }

            foreach (var s in docTabs) { try { s.Doc?.Close(); } catch { } }
            try { _doc?.Close(); } catch { }
            _doc = null;

            _sessions.Clear();
            App.RemoveSetting("LastFile");   // a manually emptied window won't reopen on launch
            var blank2 = new DocumentSession();
            _sessions.Add(blank2);
            SetActiveSession(blank2);
            ApplySessionState(blank2);
            ShowEmptyState();
            RebuildTabStrip();
        }

        // OpenFromExternal / RestoreAndActivate moved BACK to Shell/ExternalOpen.cs on MainWindow.
        // They are window chrome, not pane behaviour: RestoreAndActivate drives WindowState,
        // Activate() and Topmost, none of which exist on a UserControl, and App calls both on the
        // window. Only the OpenInNewTab call inside them belongs to a pane, and that now routes
        // through ActiveViewer like every other window -> viewer call.
    }
}
