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
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Tool selection
        // ============================================================

        // Maps an editing tool to its mouse cursor. Shared by SetTool and by the
        // per-page overlay creation so freshly rendered tiles get the right cursor.
        internal static Cursor CursorForTool(EditTool tool) => tool switch
        {
            EditTool.Text => Cursors.IBeam,
            EditTool.Highlight => Cursors.Cross,
            EditTool.Strikethrough => Cursors.Cross,
            EditTool.Underline => Cursors.Cross,
            EditTool.Draw => Cursors.Pen,
            EditTool.Line => Cursors.Cross,
            EditTool.Shape => Cursors.Cross,
            EditTool.Signature => Cursors.Pen,
            EditTool.Image => Cursors.Hand,
            EditTool.Crop => Cursors.Cross,
            EditTool.Rotate => Cursors.Cross,
            _ => Cursors.Arrow
        };

        private void SetTool(EditTool tool)
        {
            // Re-clicking the tool that owns the visible annotate bar tucks the bar away (or brings it
            // back) instead of rebuilding it - no flicker, and a quick way to get it out of the way.
            bool reclickedAnnotTool = tool == _currentTool && tool == _annotBarTool
                && (_textSettingsBar is not null || _drawSettingsBar is not null || _cropConfirmBar is not null);
            if (reclickedAnnotTool)
            {
                ToggleAnnotBarMinimized();
                return;
            }

            // Continuous view now supports annotation tools inline via per-page overlays.
            CommitActiveTextBox();
            ClearTextSelection();
            CancelShapePolygon();   // abandon an in-progress polygon when switching tools
            if (tool != EditTool.Draw) HideBrushPreview();   // drop the brush cursor when leaving Draw
            _currentTool = tool;

            var map = new (Button btn, EditTool t)[]
            {
                (_toolSelectBtn, EditTool.Select),
                (_toolTextBtn, EditTool.Text),
                (_toolHighlightBtn, EditTool.Highlight),
                (_toolUnderlineBtn, EditTool.Line),          // the old Underline button is now the Line tool
                (_toolDrawBtn, EditTool.Draw),
                (_toolShapeBtn, EditTool.Shape),
                (_toolSignatureBtn, EditTool.Signature),
                (_toolImageBtn, EditTool.Image),
                (_toolCropBtn, EditTool.Crop),
                (_toolRotateBtn, EditTool.Rotate)
            };
            foreach (var (btn, t) in map)
            {
                if (t == tool)
                {
                    btn.SetResourceReference(Control.BackgroundProperty, "SelectionBg");
                    btn.SetResourceReference(Control.ForegroundProperty, "SelectionFg");
                }
                else
                {
                    // Clear local values so the ToolbarButton style (incl. its hover trigger) applies.
                    // Setting Background locally here would override the style and kill the hover.
                    btn.ClearValue(Control.BackgroundProperty);
                    btn.ClearValue(Control.ForegroundProperty);
                }
            }

            // Apply the tool cursor to every page surface, not just the primary page.
            // In Grid / Two-Page / Continuous modes the secondary tiles are separate
            // overlay canvases tracked in _continuousCanvases; without this they keep
            // the default arrow cursor while only page 1 (_annotationCanvas) updates.
            var toolCursor = CursorForTool(tool);
            _annotationCanvas.Cursor = toolCursor;
            foreach (var overlay in _continuousCanvases.Values)
                overlay.Cursor = toolCursor;

            // Show/hide draw settings bar
            if ((tool is EditTool.Draw or EditTool.Line or EditTool.Shape) || tool == EditTool.Highlight
                || tool == EditTool.Strikethrough || tool == EditTool.Underline)
                ShowDrawSettings(tool);
            else
                HideDrawSettings();

            // Show/hide text tool settings bar
            if (tool == EditTool.Text)
                ShowTextSettings();
            else
                HideTextSettings();

            // Hide signature popup when switching away
            if (tool != EditTool.Signature)
            {
                HideSignaturePopup();
                _pendingSignature = null;
            }

            // Dismiss crop confirm bar when switching away from Crop; entering Crop drops a default box + bar.
            if (tool != EditTool.Crop)
                HideCropConfirmBar();
            else if (_cropConfirmBar is null)
                ShowDefaultCropBox();

            // NOTE: deliberately NOT reflowing the toolbar here. Reflowing on every tool switch at a narrow
            // width visibly thrashes the whole bar. The active-tool protection still runs on resize (the
            // next time the window changes width the active tool is pulled back onto the bar), which the
            // user preferred over the jank.
            UpdateOverflowActiveHighlight();   // mark the active tool in the overflow menu (cheap, no reflow)
        }

        // Tints the active tool's row in the overflow menu with the selection colors, so when a tool lives
        // in the chevron (collapsed off the bar) you can still see which one is active - same cue as the
        // highlighted icon on the bar. No-op for the rows that aren't tools.
        private void UpdateOverflowActiveHighlight()
        {
            var map = new (Button mi, EditTool t)[]
            {
                (MiText, EditTool.Text), (MiUnderline, EditTool.Line), (MiHighlight, EditTool.Highlight),
                (MiDraw, EditTool.Draw), (MiImage, EditTool.Image), (MiCrop, EditTool.Crop),
                (MiSignature, EditTool.Signature),
            };
            foreach (var (mi, t) in map)
            {
                if (mi is null) continue;
                bool active = t == _currentTool;
                if (active) mi.SetResourceReference(Control.BackgroundProperty, "SelectionBg");
                else mi.ClearValue(Control.BackgroundProperty);
                if (mi.Content is Panel sp)
                    foreach (var ch in sp.Children)
                        if (ch is TextBlock tb)
                            tb.SetResourceReference(TextBlock.ForegroundProperty, active ? "SelectionFg" : "TextBrush");
            }
        }

        private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            if (_sidebarCollapsed)
            {
                // Save current width before collapsing so expand restores it.
                if (_sidebarCol.ActualWidth > 24)
                {
                    if (_sidebarShowingOutlines)
                        _savedOutlinesWidth = Math.Min(_sidebarCol.ActualWidth, SbPx(SidebarMaxOutlines));
                    else
                        _savedPagesWidth = Math.Min(_sidebarCol.ActualWidth, SbPx(SidebarMaxPages));
                }
                _sidebarToggleBtn.ToolTip = Loc("Str_TT_ExpandSidebar");
                // Glide shut sliding, not squishing: freeze the content at its open width so
                // the shrinking border CLIPS it (thumbnails hold their size), then hide the
                // border once the strip width is reached. MinWidth drops first so the
                // animation isn't clamped at the readable floor.
                BeginSidebarSlide(SidebarContentPanel.ActualWidth);
                _sidebarCol.MinWidth = SbPx(24);
                AnimateSidebarWidth(SbPx(24), () =>
                {
                    _sidebarBorder.Visibility = Visibility.Collapsed;
                    EndSidebarSlide();
                });
                // Splitter stays enabled so the user can grab it and drag the sidebar back open.
            }
            else
            {
                _sidebarBorder.Visibility = Visibility.Visible;
                double restore = _sidebarShowingOutlines ? _savedOutlinesWidth : _savedPagesWidth;
                // Slide in at full size: content fixed at the target width from the first
                // frame, revealed by the growing border instead of reflowing up to size.
                BeginSidebarSlide(_sbSlideContentW > 0 ? _sbSlideContentW
                    : Math.Max(0, restore / Math.Max(0.01, _appScale) - 24));
                AnimateSidebarWidth(restore, () =>
                {
                    _sidebarCol.MinWidth = SbPx(SidebarMinOpen);   // open: clamp so the list can't be dragged below readable
                    EndSidebarSlide();
                });
                _sidebarToggleBtn.ToolTip = Loc("Str_TT_CollapseSidebar");
                SidebarSplitter.IsEnabled = true;
            }
            UpdateSidebarToggleGlyph();
        }

        // Sidebar slide freeze: while the column animates, the content keeps a FIXED width
        // (anchored at the outer edge) and the border clips it - so thumbnails and labels
        // hold their size and slide out of view instead of reflowing every frame. Restored
        // to normal stretch layout when the glide lands.
        private double _sbSlideContentW;

        private void BeginSidebarSlide(double contentW)
        {
            if (contentW <= 0) return;
            _sbSlideContentW = contentW;
            SidebarContentPanel.Width = contentW;
            SidebarContentPanel.HorizontalAlignment =
                _sidebarRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _sidebarBorder.ClipToBounds = true;
        }

        private void EndSidebarSlide()
        {
            SidebarContentPanel.Width = double.NaN;
            SidebarContentPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            _sidebarBorder.ClipToBounds = false;
        }

        // Animates the sidebar column between widths (toggle collapse / expand). No explicit
        // re-render here: every frame fires PagePreviewPanel_SizeChanged, whose lite fit keeps
        // the page tracking the pane smoothly and whose settle timer runs ONE crisp pass a beat
        // after the last frame - exactly the splitter-drag pipeline. An extra RefreshPageView at
        // Completed doubled up with that settle pass and read as a two-step stutter. Completion
        // clears the animation and writes the target as a plain local value so the splitter,
        // side flip, and full screen can keep setting Width directly.
        private void AnimateSidebarWidth(double target, Action? onDone = null)
        {
            var anim = new GridLengthAnimation
            {
                From     = new GridLength(Math.Max(0, _sidebarCol.ActualWidth)),
                To       = new GridLength(target),
                Duration = TimeSpan.FromMilliseconds(280),
                Easing   = new System.Windows.Media.Animation.CubicEase
                           { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
            };
            anim.Completed += (_, _) =>
            {
                _sidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, null);
                _sidebarCol.Width = new GridLength(target);
                onDone?.Invoke();
            };
            _sidebarCol.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        // Pressing the splitter while the sidebar is collapsed begins pulling it open: reveal the page
        // list (still 0-width against the 24px strip, so there's no flash) so the drag grows it live. If
        // the user doesn't drag past the threshold, OnSidebarResized snaps it shut again on release.
        private bool _sidebarDragOpening;   // true while dragging the splitter open from the collapsed strip
        private bool _sidebarWantClose;     // set during an open-resize drag when pulled past the close edge

        private void OnSidebarSplitterPress()
        {
            if (!_sidebarCollapsed) return;
            // Begin pulling open from the strip. Show the sidebar background + grain right away so the
            // growing strip matches the sidebar, but keep the content (header + list) hidden until it's
            // pulled to the readable minimum (OnSidebarSplitterMove) - so nothing shows clipped, and the
            // column grows from the 24px edge tracking the mouse with no dead zone.
            _sidebarCollapsed = false;
            _sidebarDragOpening = true;
            _sidebarBorder.Visibility = Visibility.Visible;
            SidebarContentPanel.Visibility = Visibility.Collapsed;
            _sidebarToggleBtn.ToolTip = Loc("Str_TT_CollapseSidebar");
            UpdateSidebarToggleGlyph();
        }

        // Drives the splitter drag. While opening from the strip, reveal the list only once it's past the
        // readable minimum. Once open, pulling the mouse well past the minimum closes the sidebar (the
        // MinWidth clamp already stops the column from ever shrinking below the readable floor).
        private void OnSidebarSplitterMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_sidebarCollapsed || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            if (_sidebarDragOpening)
            {
                SidebarContentPanel.Visibility = _sidebarCol.ActualWidth >= SbPx(SidebarMinOpen)
                    ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            // The column is clamped at the readable minimum (MinWidth), so it can't clip and the document
            // isn't resized as you pull. Just record intent; the actual close (and its single re-render)
            // happens on release in OnSidebarResized - so dragging the splitter never re-renders the page.
            double mx = e.GetPosition(MainContentGrid).X;
            const double closeBuffer = 36;   // pull this far past the minimum edge to close on release
            _sidebarWantClose = _sidebarRight
                ? mx > MainContentGrid.ActualWidth - SbPx(SidebarMinOpen) + closeBuffer
                : mx < SbPx(SidebarMinOpen) - closeBuffer;
        }

        // Called when the user finishes dragging the sidebar splitter. If they dragged it narrower than
        // the close threshold, snap it fully closed (to the toggle strip) instead of leaving an unusable
        // sliver; otherwise remember the new width for the current mode.
        private void OnSidebarResized()
        {
            _sidebarDragOpening = false;
            if (_sidebarCollapsed) { _sidebarWantClose = false; return; }
            if (_sidebarWantClose) { _sidebarWantClose = false; CollapseSidebarToStrip(); return; }
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                if (_sidebarCollapsed) return;
                double w = _sidebarCol.ActualWidth;
                if (w < SbPx(SidebarMinOpen))
                {
                    // Released below the readable minimum: snap to whichever end the user let go nearer
                    // to - fully closed, or the minimum open width.
                    double mid = SbPx((24 + SidebarMinOpen) / 2.0);
                    if (w < mid) { CollapseSidebarToStrip(); return; }
                    _sidebarCol.Width = new GridLength(SbPx(SidebarMinOpen));
                    w = SbPx(SidebarMinOpen);
                }
                _sidebarBorder.Visibility = Visibility.Visible;        // ensure the list shows when settled open
                SidebarContentPanel.Visibility = Visibility.Visible;
                _sidebarCol.MinWidth = SbPx(SidebarMinOpen);           // clamp future resizes so they can't clip
                if (_sidebarShowingOutlines) _savedOutlinesWidth = Math.Min(w, SbPx(SidebarMaxOutlines));
                else _savedPagesWidth = Math.Min(w, SbPx(SidebarMaxPages));
            }));
        }

        // Collapse to the 24px toggle strip without overwriting the saved width, so re-expand restores the
        // last good size rather than the thin dragged one. Mirrors SidebarToggle_Click's collapse branch.
        private void CollapseSidebarToStrip()
        {
            _sidebarCollapsed = true;
            _sidebarToggleBtn.ToolTip = Loc("Str_TT_ExpandSidebar");
            _sidebarBorder.Visibility = Visibility.Collapsed;
            SidebarContentPanel.Visibility = Visibility.Visible;   // reset so the next border-show has content
            _sidebarCol.Width = new GridLength(SbPx(24));
            _sidebarCol.MinWidth = SbPx(24);
            UpdateSidebarToggleGlyph();   // splitter stays enabled so it can be dragged back open
        }

        // The rail auto-collapses when no PDF is open and re-opens when one is (Steve, 2026-07-23): with
        // nothing loaded there are no thumbnails to show, so the strip gets out of the way. The empty
        // page-jump box + "/ –" hide alongside. Only fires on the open/close transition (FinishOpenFile /
        // ShowEmptyState) and at startup - NOT on tab switches - so a manual toggle mid-session sticks.
        // startup=true collapses instantly (no glide before the first paint); runtime transitions animate.
        private void SyncSidebarToDocState(bool hasDoc, bool startup)
        {
            if (PageControlsRow != null)
                PageControlsRow.Visibility = (hasDoc && !_sidebarShowingOutlines)
                    ? Visibility.Visible : Visibility.Collapsed;

            if (hasDoc && _sidebarCollapsed)
                SidebarToggle_Click(this, new RoutedEventArgs());          // open the rail for the document
            else if (!hasDoc && !_sidebarCollapsed)
            {
                if (startup) CollapseSidebarToStrip();                     // instant at launch
                else SidebarToggle_Click(this, new RoutedEventArgs());     // animated on last-tab close
            }
        }
    }
}
