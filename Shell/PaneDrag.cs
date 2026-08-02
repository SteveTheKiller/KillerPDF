using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerPDF.Controls;

namespace KillerPDF
{
    /// <summary>
    /// Moving a document tab from one pane to the other. Partial of MainWindow.
    ///
    /// A DocumentSession is already self-contained - it owns its document, annotations, undo stack,
    /// render cache and view state - so a move is a move: take it out of one pane's collection and
    /// put it in the other's. Nothing is reloaded and nothing is re-parsed, which is what lets a
    /// large PDF cross panes without a visible reload.
    ///
    /// The reorder-WITHIN-a-pane drag lives in PdfViewer.TabStrip.cs and is untouched; this only
    /// takes over when the drop lands somewhere that pane is not.
    ///
    /// Ported from KillerShell's PaneDrag.cs.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// The pane a drop landed on, or null when it landed on the pane it came from (or on
        /// nothing). Only ever returns the OTHER pane, so a drop inside the source pane still goes
        /// through the normal reorder path.
        /// </summary>
        /// <remarks>
        /// Generous on purpose: anywhere in the other pane counts, not just its tab band. A tab band
        /// is a 24px target and aiming for it mid-drag is fussy - if you let go over the other pane,
        /// you meant the other pane.
        /// </remarks>
        internal PdfViewer? TabDropTargetPane(PdfViewer source, MouseEventArgs e)
        {
            if (!_isSplit) return null;

            var other = ReferenceEquals(source, Viewer) ? ViewerB : Viewer;
            if (other.Visibility != Visibility.Visible) return null;

            var p = e.GetPosition(other);
            return p.X >= 0 && p.Y >= 0 && p.X <= other.ActualWidth && p.Y <= other.ActualHeight
                ? other
                : null;
        }

        // ── Drag feedback ───────────────────────────────────────────────────────────────────────
        // Within a pane the REAL tab slides under the pointer, which is feedback enough. The moment
        // the pointer crosses into the other pane that stops working: the tab is still parked in the
        // strip it came from and nothing follows the hand. So a ghost takes over for the journey, and
        // a caret shows where it would land.
        private bool _tabGhostShown;

        /// <summary>
        /// Called on every drag move. Shows the ghost while the pointer is over the other pane and
        /// hides it again the moment it comes home, so a drag that wanders out and back hands control
        /// cleanly to the in-strip reorder.
        /// </summary>
        internal void UpdateTabDragFeedback(PdfViewer source, PdfViewer.DocumentSession s,
                                            MouseEventArgs e, PdfViewer? over)
        {
            if (over == null) { HideTabDragFeedback(); return; }

            if (!_tabGhostShown)
            {
                _tabGhostShown = true;
                TabDragGhostText.Text = s.TabLabel;
                DragLayer.Visibility = Visibility.Visible;
            }

            // Positioned by the same grab offset the in-strip drag uses, so the ghost sits under the
            // pointer exactly where the tab did when it was picked up.
            var p = e.GetPosition(DragLayer);
            Canvas.SetLeft(TabDragGhost, p.X - source.TabGrabOffsetX);
            Canvas.SetTop(TabDragGhost, p.Y - 10);

            ShowTabDropCaret(over, e);
        }

        private void ShowTabDropCaret(PdfViewer target, MouseEventArgs e)
        {
            var band = target.TabBandCtl;
            var strip = e.GetPosition(target.TabStripCtl);
            bool onStrip = band.Visibility == Visibility.Visible
                           && strip.Y >= 0 && strip.Y <= band.ActualHeight;

            if (!onStrip) { TabDropCaret.Visibility = Visibility.Collapsed; return; }

            int idx = TabInsertIndexFor(target, e);
            double w = target.TabCount > 0 ? target.TabStripCtl.ActualWidth / target.TabCount : 0;

            var at = target.TabStripCtl.TransformToVisual(DragLayer).Transform(new Point(idx * w, 0));

            Canvas.SetLeft(TabDropCaret, at.X - 1);
            Canvas.SetTop(TabDropCaret, at.Y);
            TabDropCaret.Height = Math.Max(4, band.ActualHeight);
            TabDropCaret.Visibility = Visibility.Visible;
        }

        internal void HideTabDragFeedback()
        {
            if (!_tabGhostShown) return;
            _tabGhostShown = false;
            DragLayer.Visibility = Visibility.Collapsed;
            TabDropCaret.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Move <paramref name="s"/> into <paramref name="target"/> at the position the drop implies,
        /// and leave it active and focused there.
        /// </summary>
        /// <remarks>
        /// The order below is the whole trick, and every step of it is load-bearing.
        ///
        /// _doc, _annotations, _undoStack and the rest are WINDOW fields that both panes bridge to,
        /// so they describe one pane's active document at a time. The tab being dragged is usually
        /// the source pane's active one, which means its real state is in those shared fields rather
        /// than in the session - so it is CAPTURED first, or the move carries a stale copy. Then the
        /// source pane is pointed at whatever it has left, so the shared fields describe something
        /// that still lives there before FocusPane captures them again on the way past.
        ///
        /// The target renders explicitly: FocusPane deliberately does not, because each pane keeps
        /// its own tile tree and a focus change is chrome rather than pixels. Here the pixels really
        /// are new - this pane has never drawn this document.
        ///
        /// The source re-renders inside WithOwnSession, which swaps BOTH its session fields and
        /// ActiveViewer. Without the second half its work would measure and paint into the target
        /// pane's tiles, which is the same trap SwapActiveViewer exists for.
        /// </remarks>
        internal void MoveTabToPane(PdfViewer source, PdfViewer target,
                                    PdfViewer.DocumentSession s, MouseEventArgs e)
        {
            if (ReferenceEquals(source, target)) return;

            int insert = TabInsertIndexFor(target, e);

            source.CaptureActiveIfAny();     // the dragged tab's live state, into the session that is leaving
            source.DetachSessionExt(s);
            source.ApplyActiveSessionIfAny();   // shared fields now describe what the source has left

            target.AdoptSessionExt(s, insert);

            FocusPane(target);               // swaps s into the shared fields and moves the chrome
            target.RenderActiveSessionExt(); // this pane has never drawn this document

            // And the source pane, with its own fields and its own tiles restored for the call.
            source.WithOwnSession(source.RenderActiveSessionExt);

            source.CleanupTabTransforms();   // drop the drag offset the grabbed tab still carries
            source.RebuildTabStripExt();
            target.RebuildTabStripExt();
        }

        /// <summary>
        /// Where in the target strip the drop belongs. Dropping on the tab band inserts at the
        /// position under the pointer; dropping anywhere else in the pane appends, because there is
        /// no position being pointed at.
        /// </summary>
        private static int TabInsertIndexFor(PdfViewer target, MouseEventArgs e)
        {
            var band = target.TabBandCtl;
            if (band.Visibility != Visibility.Visible) return target.TabCount;

            var p = e.GetPosition(target.TabStripCtl);
            if (p.Y < 0 || p.Y > band.ActualHeight) return target.TabCount;

            double w = target.TabCount > 0 ? target.TabStripCtl.ActualWidth / target.TabCount : 0;
            if (w <= 0) return target.TabCount;

            // Rounded, so the boundary is the midpoint of a tab rather than its left edge - dropping
            // on the right half of a tab means "after this one".
            return Math.Max(0, Math.Min(target.TabCount, (int)Math.Round(p.X / w)));
        }
    }
}
