using System.Collections.Generic;

namespace KillerPDF
{
    /// <summary>
    /// The tab and session members under the names the window already calls them by, routed to the
    /// focused pane. Keeping the old names resolvable leaves the call sites in FileOperations,
    /// KeyboardShortcuts, ImportAndZip, TempReload, WindowChrome and SettingsPanel unchanged while
    /// making them act on whichever pane has focus.
    ///
    /// Ctrl+W, Ctrl+Tab, Ctrl+Q and CloseFile_Click are keyboard- or XAML-bound, so these
    /// declarations are load-bearing rather than convenience.
    /// </summary>
    public partial class MainWindow
    {
        private void OpenInNewTab(string path) => ActiveViewer.OpenInNewTabExt(path);
        private void CloseTab(Controls.PdfViewer.DocumentSession? s) => ActiveViewer.CloseTabExt(s);
        private void CloseAllTabs() => ActiveViewer.CloseAllTabsExt();
        private void CloseOtherTabs(Controls.PdfViewer.DocumentSession? s) => ActiveViewer.CloseOtherTabsExt(s);
        private void CycleTab(int dir) => ActiveViewer.CycleTabExt(dir);
        private void EnsureInitialSession() => ActiveViewer.EnsureInitialSessionExt();
        private void MaterializeDeferred(Controls.PdfViewer.DocumentSession target)
            => ActiveViewer.MaterializeDeferredExt(target);

        private Controls.PdfViewer.DocumentSession BeginTabLoad(
            out Controls.PdfViewer.DocumentSession? prev, out bool createdNew)
            => ActiveViewer.BeginTabLoadExt(out prev, out createdNew);
        private void AbortTabLoad(Controls.PdfViewer.DocumentSession target,
                                  Controls.PdfViewer.DocumentSession? prev, bool createdNew)
            => ActiveViewer.AbortTabLoadExt(target, prev, createdNew);

        private void CaptureSessionState(Controls.PdfViewer.DocumentSession s)
            => ActiveViewer.CaptureSessionStateExt(s);
        private void ApplySessionState(Controls.PdfViewer.DocumentSession s)
            => ActiveViewer.ApplySessionStateExt(s);
        private void SaveDocState(string? path, FitMode fit, double zoom, ViewMode view, int page)
            => ActiveViewer.SaveDocStateExt(path, fit, zoom, view, page);
        private bool TryGetDocState(string? path, out FitMode fit, out double zoom,
                                    out ViewMode view, out int page)
            => ActiveViewer.TryGetDocStateExt(path, out fit, out zoom, out view, out page);

        private void RebuildTabStrip() => ActiveViewer.RebuildTabStripExt();
        private void ScheduleTabReflow() => ActiveViewer.ScheduleTabReflowExt();
        private void RenderActiveSession() => ActiveViewer.RenderActiveSessionExt();
        private void ShowEmptyState() => ActiveViewer.ShowEmptyStateExt();
        private void InvalidateRenderCache(Controls.PdfViewer.DocumentSession? s)
            => ActiveViewer.InvalidateRenderCacheExt(s);

        /// <summary>Night mode changed: flush both panes. The invert state is baked into cached
        /// pixels, so pane B's cache is as stale as pane A's.</summary>
        private void FlushAllRenderCaches()
        {
            Viewer.FlushAllRenderCachesExt();
            ViewerB.FlushAllRenderCachesExt();
        }

        /// <summary>Every open document across both panes. The quit prompt and the settings writer
        /// need all of them, or closing the window silently drops pane B's unsaved work.</summary>
        private IEnumerable<Controls.PdfViewer.DocumentSession> AllSessions()
        {
            foreach (var s in Viewer.SessionsRef) yield return s;
            foreach (var s in ViewerB.SessionsRef) yield return s;
        }

        /// <summary>The focused pane's open documents. Callers that mean "this pane" - the tab
        /// context menu's Close Others, the reorder resync - want this rather than AllSessions.</summary>
        private System.Collections.ObjectModel.ObservableCollection<Controls.PdfViewer.DocumentSession> _sessions
            => ActiveViewer.SessionsRef;

        /// <summary>The focused pane's tab strip, for the chrome that positions or hides it.
        /// AppScale and FullScreen act on both panes at their own call sites; SidebarLayout's fade
        /// mask only describes the pane it is measuring, so it takes the active one.</summary>
        private System.Windows.Controls.Border TabStripBorder => ActiveViewer.TabStripBorderCtl;
        private System.Windows.Controls.Border TabStripFade => ActiveViewer.TabStripFadeCtl;

        /// <summary>True when the other pane holds an unsaved copy of the same file.
        ///
        /// The split opens a file as two independent copies, not two views of one document: each
        /// pane has its own annotations, undo stack and dirty flag, so whichever saves last wins.
        /// This is the guard on that.
        ///
        /// Compares OriginalFile, not CurrentFile: crop and rotate swap the working file out to a
        /// temp path, so CurrentFile can differ between two panes showing the same document.</summary>
        private bool OtherPaneHasDirtyCopyOf(string? originalFile)
        {
            if (string.IsNullOrEmpty(originalFile)) return false;
            var other = ReferenceEquals(ActiveViewer, Viewer) ? ViewerB : Viewer;
            other.CaptureActiveIfAny();   // its live dirty flag may not be folded into its session yet
            return other.SessionsRef.Any(s => s.IsDirty
                && string.Equals(s.OriginalFile, originalFile, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Place the restored session list into pane A. The startup restore builds the list
        /// itself rather than going through EnsureInitialSession, so it hands the result over.
        /// Per-pane restore is not implemented; everything reopens in pane A.</summary>
        private void SetRestoredSessions(IEnumerable<Controls.PdfViewer.DocumentSession> sessions,
                                         Controls.PdfViewer.DocumentSession? active)
            => Viewer.SetSessionsExt(sessions, active);
    }
}
