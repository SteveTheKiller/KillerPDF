using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerPDF.Controls;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    /// <summary>
    /// #502: whole-session restore. Every tab open, close and front-tab change schedules a fresh copy of
    /// the open tabs (App.SessionStore.cs), and a plain launch brings them back as the Restore last
    /// session setting in the About card says: Always silently, Ask through one bar at the top of the
    /// empty pane, Never not at all. A launch that names a document opens only that document.
    /// Restored tabs are deferred, so only the front tab of each pane loads at startup.
    /// </summary>
    public partial class MainWindow
    {
        // The bar is up and unanswered; the stored session must not be overwritten meanwhile.
        private bool _sessionPromptPending;
        // Set once this window's tabs are handed to App's closed-window list during teardown.
        private bool _sessionWindowClosed;

        private void WireSessionStore()
        {
            foreach (var pane in new[] { Viewer, ViewerB })
            {
                pane.SessionsRef.CollectionChanged += (_, _) => App.ScheduleSessionSave();
                pane.ActiveSessionChanged += App.ScheduleSessionSave;
            }
        }

        /// <summary>An open into a tab that already existed changes no tab list, so it is reported
        /// here. Opening a document while the restore bar waits answers the bar with Dismiss.</summary>
        private void NoteDocumentOpenedForSession()
        {
            if (_sessionPromptPending) DismissSessionPrompt();
            App.ScheduleSessionSave();
        }

        private void RestoreLastSession(string[] args)
        {
            SessionSnapshot? stored = _ownsWindowGeometry ? App.LoadStoredSession() : null;
            (SessionSnapshot present, IReadOnlyList<string> missing) = stored is null
                ? (new SessionSnapshot([]), [])
                : SessionStore.WithoutMissing(stored, File.Exists);
            SessionWindow window = SessionStore.IntoOneWindow(present);
            int count = window.Panes.Sum(pane => pane.Tabs.Count);
            SessionRestoreAction action = SessionStore.Decide(App.SessionRestoreMode, args, File.Exists,
                App.RestartedAfterRenderFailure, count);

            if (action == SessionRestoreAction.Silent)
            {
                ApplyStoredSession(window, missing);
                return;
            }

            ShowEmptyStart();
            if (action != SessionRestoreAction.Ask) return;
            _sessionPromptPending = true;
            Viewer.ShowSessionNotice(count,
                restore: () =>
                {
                    _sessionPromptPending = false;
                    // Read again: the files may have moved while the bar was up.
                    var latest = App.LoadStoredSession() is { } again
                        ? SessionStore.WithoutMissing(again, File.Exists)
                        : (new SessionSnapshot([]), (IReadOnlyList<string>)[]);
                    ApplyStoredSession(SessionStore.IntoOneWindow(latest.Item1), latest.Item2);
                },
                dismiss: DismissSessionPrompt);
        }

        private void DismissSessionPrompt()
        {
            _sessionPromptPending = false;
            Viewer.HideSessionNotice();
            App.ReleaseSessionWrites();
            App.ScheduleSessionSave();
        }

        private void ShowEmptyStart()
        {
            SetRestoredSessions([], null);
            PopulateRecentFilesList();   // empty state: show the recent list
            EnsureInitialSession();
            RebuildTabStrip();
        }

        /// <summary>Puts the stored tabs back. KillerPDF restores into the window it starts with: one
        /// stored window keeps its split, and several come back as this window's tabs. Each pane loads
        /// only its front tab; the others load the first time they are viewed.</summary>
        private void ApplyStoredSession(SessionWindow window, IReadOnlyList<string> missing)
        {
            if (!ReferenceEquals(ActiveViewer, Viewer)) FocusPane(Viewer);
            var (restored, active) = window.Panes.Count > 0
                ? PdfViewer.MakeDeferredSessions(window.Panes[0])
                : ([], null);
            if (active is null)
            {
                if (!Viewer.SessionsRef.Any(session => session.HasReadDocument || session.DeferredPath != null))
                    ShowEmptyStart();
            }
            else
            {
                // A document made while the bar waited (a new blank page, say) stays, ahead of the restored tabs.
                var kept = Viewer.SessionsRef.Where(session => session.HasReadDocument || session.DeferredPath != null
                    || !string.IsNullOrEmpty(session.OriginalFile)).ToList();
                if (kept.Count > 0) Viewer.CaptureActiveIfAny();
                static bool Same(PdfViewer.DocumentSession a, PdfViewer.DocumentSession b)
                    => string.Equals(a.OriginalFile, b.OriginalFile, StringComparison.OrdinalIgnoreCase);
                List<PdfViewer.DocumentSession> tabs = [.. kept, .. restored.Where(session => !kept.Any(existing => Same(existing, session)))];
                var front = tabs.Contains(active) ? active : kept.First(existing => Same(existing, active));
                SetRestoredSessions(tabs, front);
                ApplySessionState(front);
                if (!front.HasReadDocument && front.DeferredPath != null) MaterializeDeferred(front);   // load + render only the focused tab
                else RenderActiveSession();
                RebuildTabStrip();
            }
            RestorePaneB(window.Panes.Count > 1 ? window.Panes[1] : null);
            if (missing.Count > 0)
                SetStatus(string.Format(Loc("Str_St_SessionMissing"),
                    string.Join(", ", missing.Select(path => Path.GetFileName(path)))));
            App.ReleaseSessionWrites();
            App.ScheduleSessionSave();
        }

        /// <summary>This window's tabs as the store keeps them: pane A, then pane B while split.
        /// Null once the window has closed and handed its tabs over.</summary>
        internal SessionWindow? SessionWindowForStore()
        {
            if (_sessionWindowClosed) return null;
            Viewer.CaptureActiveIfAny();
            ViewerB.CaptureActiveIfAny();
            var panes = new List<SessionPane> { PaneForStore(Viewer) };
            if (IsSplit) panes.Add(PaneForStore(ViewerB));
            while (panes.Count > 0 && panes[^1].Tabs.Count == 0) panes.RemoveAt(panes.Count - 1);
            if (panes.Count > 0 && panes[0].Tabs.Count == 0) panes.RemoveAt(0);
            return new SessionWindow(panes);
        }

        internal SessionWindow? TakeSessionWindowForClose()
        {
            SessionWindow? window = SessionWindowForStore();
            _sessionWindowClosed = true;
            return window;
        }

        private static SessionPane PaneForStore(PdfViewer pane)
            => SessionStore.PaneFrom(
                pane.SessionsRef.Select(session => (session.OriginalFile, session.PageIndex, session.ZoomLevel)),
                pane.ActiveSessionRef?.OriginalFile,
                App.IsCleanupOwnedPath);

        // ── The setting, in the About card under Check for updates on startup ─────────────────────

        private bool _syncingRestoreSessionChoice;

        private void SyncRestoreSessionChoice()
        {
            string value = SessionStore.ModeValue(App.SessionRestoreMode);
            _syncingRestoreSessionChoice = true;
            try
            {
                RestoreSessionCombo.SelectedItem = RestoreSessionCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => (string?)item.Tag == value);
            }
            finally { _syncingRestoreSessionChoice = false; }
        }

        private void RestoreSessionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingRestoreSessionChoice || RestoreSessionCombo.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            SessionRestoreMode chosen = SessionStore.ParseMode(value);
            if (chosen == App.SessionRestoreMode && App.GetSetting(SessionStore.ModeSetting) is not null) return;   // no change
            App.SetSessionRestoreMode(chosen);
        }
    }
}
