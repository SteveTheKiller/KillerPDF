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

            source.DetachSessionExt(session);
            source.ApplyActiveSessionIfAny();
            source.RenderActiveSessionExt();
            source.RebuildTabStripExt();

            detachedWindow.Show();
            if (session.NativeDoc is { IsClosed: false } book)
                detachedWindow.FollowNativePagination(book, Path.GetFileName(session.OriginalFile ?? session.CurrentFile ?? ""));
            detachedWindow.Activate();
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
            source.DetachSessionExt(session);
            source.ApplyActiveSessionIfAny();
            source.RenderActiveSessionExt();
            source.RebuildTabStripExt();

            target.AdoptMovedTab(session);

            // The window the last document left has nothing to show, so it closes the way a
            // browser window does when its last tab is dragged away.
            if (!AllSessions().Any(s => s.HasReadDocument || !string.IsNullOrEmpty(s.DeferredPath)))
                Close();
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
