using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using KillerPDF.Controls;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        internal bool IsOutsideWindow(MouseEventArgs e)
        {
            const double slack = 40;
            Point p = e.GetPosition(this);
            return p.X < -slack || p.X > ActualWidth + slack
                || p.Y < -slack || p.Y > ActualHeight + slack;
        }

        // #513: files picked with Shift in the Open dialog open as tabs of a new window, placed like a torn-out tab.
        internal void OpenInNewWindow(IReadOnlyList<string> files)
        {
            if (files.Count == 0) return;
            var window = new MainWindow([.. files])
            {
                Width = ActualWidth,
                Height = ActualHeight,
                Left = Math.Max(0, Left + 32),
                Top = Math.Max(0, Top + 32),
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowState = WindowState.Normal,
            };
            window.Show();
            window.Activate();
        }

        internal void TearOutTab(PdfViewer source, PdfViewer.DocumentSession session)
        {
            source.CaptureActiveIfAny();

            var detachedWindow = new MainWindow(session)
            {
                Width = ActualWidth,
                Height = ActualHeight,
                Left = Math.Max(0, Left + 32),
                Top = Math.Max(0, Top + 32),
                WindowStartupLocation = WindowStartupLocation.Manual,
            };

            // What the source held, so a window that fails to show gives the tab back exactly as it was.
            var sourceSessions = source.SessionsRef.ToList();
            var sourceActive = source.ActiveSessionRef;
            source.DetachSessionExt(session);
            source.ApplyActiveSessionIfAny();
            source.ShowActiveSessionExt();
            source.RebuildTabStripExt();

            try
            {
                detachedWindow.Show();
                if (session.NativeDoc is { IsClosed: false } book)
                    detachedWindow.FollowNativePagination(book, Path.GetFileName(session.OriginalFile ?? session.CurrentFile ?? ""));
                detachedWindow.Activate();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The session leaves the half-made window before it closes, so closing it cannot take the document.
                RestoreFailedMove(source, sourceSessions, sourceActive, session, detachedWindow);
                System.Diagnostics.Debug.WriteLine("TearOutTab: the tab stayed: " + error);
                try { detachedWindow.Close(); }
                catch (Exception closeError) when (closeError is not OutOfMemoryException)
                {
                    System.Diagnostics.Debug.WriteLine("TearOutTab: closing the new window: " + closeError);
                }
                SetStatus(error.Message);
            }
        }

        // #503: other visible KillerPDF windows a tab can move into, named by the document each shows.
        internal IReadOnlyList<(string Name, Window Window)> OtherDocumentWindows()
            => [.. Application.Current.Windows.OfType<MainWindow>()
                .Where(window => !ReferenceEquals(window, this) && window.IsVisible && window.ActiveViewer != null)
                .Select(window => (window.DocumentWindowName(), (Window)window))];

        private string DocumentWindowName()
        {
            var session = ActiveViewer?.ActiveSessionExt;
            string? path = session?.OriginalFile ?? session?.DeferredPath ?? session?.CurrentFile;
            return string.IsNullOrEmpty(path) ? Title : Path.GetFileName(path);
        }

        internal void MoveTabToWindow(PdfViewer source, PdfViewer.DocumentSession session, Window targetWindow)
        {
            if (targetWindow is not MainWindow target || ReferenceEquals(target, this)
                || target.ActiveViewer == null || !source.SessionsRef.Contains(session)) return;

            source.CaptureActiveIfAny();
            // What the source held, so a move the target cannot take puts the tab back exactly as it was
            // (without the blank tab ApplyActiveSessionIfAny adds when the last one leaves).
            var sourceSessions = source.SessionsRef.ToList();
            var sourceActive = source.ActiveSessionRef;
            source.DetachSessionExt(session);
            source.ApplyActiveSessionIfAny();
            source.ShowActiveSessionExt();
            source.RebuildTabStripExt();

            try { target.AdoptMovedTab(session); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The document stays in this window; the source does not close over a failed move.
                RestoreFailedMove(source, sourceSessions, sourceActive, session, target);
                System.Diagnostics.Debug.WriteLine("MoveTabToWindow: the tab stayed: " + error);
                SetStatus(error.Message);
                return;
            }

            // The window the last document left has nothing to show, so it closes the way a
            // browser window does when its last tab is dragged away.
            if (!AllSessions().Any(s => s.HasReadDocument || !string.IsNullOrEmpty(s.DeferredPath)))
                Close();
        }

        // Takes the session back out of the target if the adopt got that far, and gives the source its
        // tabs and front tab back.
        private static void RestoreFailedMove(PdfViewer source, List<PdfViewer.DocumentSession> sourceSessions,
            PdfViewer.DocumentSession? sourceActive, PdfViewer.DocumentSession session, MainWindow target)
        {
            if (target.ActiveViewer is { } viewer && viewer.SessionsRef.Contains(session))
            {
                try
                {
                    viewer.DetachSessionExt(session);
                    viewer.ApplyActiveSessionIfAny();
                    viewer.RenderActiveSessionExt();
                    viewer.RebuildTabStripExt();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    System.Diagnostics.Debug.WriteLine("RestoreFailedMove: " + error);
                }
            }
            source.SetSessionsExt(sourceSessions, sourceActive);
            if (sourceActive is not null) source.ApplySessionStateExt(sourceActive);
            source.RebuildTabStripExt();
            source.ShowActiveSessionExt();
        }

        private void AdoptMovedTab(PdfViewer.DocumentSession session)
        {
            var viewer = ActiveViewer!;
            bool empty = !viewer.SessionsRef.Any(s => s.HasReadDocument || !string.IsNullOrEmpty(s.DeferredPath));
            viewer.CaptureActiveIfAny();
            if (empty) viewer.SetSessionsExt([session], session);
            else viewer.AdoptSessionExt(session, viewer.TabCount);
            viewer.ApplySessionStateExt(session);
            viewer.RebuildTabStripExt();
            if (session.Doc == null && session.DeferredPath != null) viewer.MaterializeDeferredExt(session);
            else viewer.RenderActiveSessionExt();
            if (session.NativeDoc is { IsClosed: false } book)
                FollowNativePagination(book, Path.GetFileName(session.OriginalFile ?? session.CurrentFile ?? ""));
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        internal bool TryMoveTabToWindowUnderCursor(PdfViewer source, PdfViewer.DocumentSession session)
        {
            if (!GetCursorPos(out POINT point)) return false;
            IntPtr root = GetAncestor(WindowFromPoint(point), 2 /* GA_ROOT */);
            if (root == IntPtr.Zero) return false;
            var target = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(window =>
                !ReferenceEquals(window, this) && window.IsVisible
                && new System.Windows.Interop.WindowInteropHelper(window).Handle == root);
            if (target == null) return false;
            MoveTabToWindow(source, session, target);
            return true;
        }

        // GetCursorPos and POINT are Chrome.cs's.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
}
