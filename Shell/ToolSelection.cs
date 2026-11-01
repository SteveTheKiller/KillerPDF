using System.Diagnostics;
using KillerPDF.Controls;
using KillerPDF.Shell;
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
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF.Shell
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
            EditTool.FormField => Cursors.Cross,
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
            EditTool.Measure => Cursors.Cross,
            EditTool.Redact => Cursors.Cross,
            _ => Cursors.Arrow
        };

        private void SetTool(EditTool tool, bool restoringPane = false)
        {
            if (_doc is null) tool = EditTool.Select;
            // Re-clicking the tool that owns the visible annotate bar tucks the bar away (or brings it
            // back) instead of rebuilding it - no flicker, and a quick way to get it out of the way.
            bool reclickedAnnotTool = !restoringPane && tool == _currentTool && tool == _annotBarTool
                && (_textSettingsBar is not null || _drawSettingsBar is not null || _cropConfirmBar is not null);
            if (reclickedAnnotTool)
            {
                ToggleAnnotBarMinimized();
                return;
            }

            // A PDF whose owner restricts editing is view-only until its owner password is entered.
            // Choosing an editing tool asks for it; re-applying the tab's tool on open or a tab
            // switch quietly falls back to Select instead of prompting.
            if (tool != EditTool.Select && IsOwnerRestricted
                && (restoringPane || tool == _currentTool || !UnlockOwnerRestrictions()))
                tool = EditTool.Select;

            // Continuous view now supports annotation tools inline via per-page overlays.
            CommitActiveTextBox();
            ClearTextSelection();
            CancelShapePolygon();   // abandon an in-progress polygon when switching tools
            if (tool != EditTool.Draw) HideBrushPreview();   // drop the brush cursor when leaving Draw
            if (tool != EditTool.Measure) ActiveViewer.ClearMeasurementExt();
            _currentTool = tool;
            ActiveViewer.RefreshFormDesignMode();

            var map = new (Button btn, EditTool t)[]
            {
                (_toolSelectBtn, EditTool.Select),
                (_toolTextBtn, EditTool.Text),
                (_toolFormFieldBtn, EditTool.FormField),
                (_toolHighlightBtn, EditTool.Highlight),
                (_toolUnderlineBtn, EditTool.Line),          // the old Underline button is now the Line tool
                (_toolDrawBtn, EditTool.Draw),
                (_toolShapeBtn, EditTool.Shape),
                (_toolSignatureBtn, EditTool.Signature),
                (_toolImageBtn, EditTool.Image),
                (_toolCropBtn, EditTool.Crop),
                (_toolRotateBtn, EditTool.Rotate),
                (_toolMeasureBtn, EditTool.Measure),
                (ToolRedactBtn, EditTool.Redact)
            };
            foreach (var (btn, t) in map)
            {
                btn.Tag = t == tool ? "selected" : null;
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

            // Show/hide draw settings bar (the Measure bar is the draw bar with the Measure controls)
            if ((tool is EditTool.Draw or EditTool.Line or EditTool.Shape or EditTool.Measure) || tool == EditTool.Highlight
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
                _signatureDragOrigin = null;
                HideSignaturePopup();
                _pendingSignature = null;
            }

            // Dismiss crop confirm bar when switching away from Crop; entering Crop drops a default box + bar.
            if (tool != EditTool.Crop)
                HideCropConfirmBar();
            else if (_cropConfirmBar is null)
                ShowDefaultCropBox();

            // The Redact bar lives only while the Redact tool is active; leaving it drops the marks.
            if (tool == EditTool.Redact) ActiveViewer.ShowRedactionBar();
            else ActiveViewer.HideRedactionBar();

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
                (MiText, EditTool.Text), (MiFormField, EditTool.FormField),
                (MiUnderline, EditTool.Line), (MiHighlight, EditTool.Highlight),
                (MiDraw, EditTool.Draw), (MiImage, EditTool.Image), (MiCrop, EditTool.Crop),
                (MiSignature, EditTool.Signature), (MiMeasure, EditTool.Measure),
                (MiRedact, EditTool.Redact),
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
            // A toggle from the USER makes the state deliberate: a closed sidebar then stays
            // closed until the user opens it - SyncSidebarToDocState may only undo its own
            // automatic collapse (see _sidebarAutoCollapsed).
            if (!_sidebarAutoToggling) _sidebarAutoCollapsed = false;
            _sidebarCollapsed = !_sidebarCollapsed;
            RememberSidebarCollapsed();
            if (_sidebarCollapsed)
            {
                // Save current width before collapsing so expand restores it.
                if (_sidebarCol.ActualWidth > SbPx(SidebarRailSize)) RememberSidebarWidth(_sidebarCol.ActualWidth);
                _sidebarToggleBtn.ToolTip = ShortcutTip("Str_TT_ExpandSidebar", "F9");
                // Glide shut sliding, not squishing: freeze the content at its open width so
                // the shrinking border CLIPS it (thumbnails hold their size), then hide the
                // border once the strip width is reached. MinWidth drops first so the
                // animation isn't clamped at the readable floor.
                BeginSidebarSlide(_sidebarSide.IsStrip() ? SidebarContentPanel.ActualHeight : SidebarContentPanel.ActualWidth);
                _sidebarCol.MinWidth = SbPx(SidebarRailSize);
                AnimateSidebarWidth(SbPx(SidebarRailSize), () =>
                {
                    _sidebarBorder.Visibility = Visibility.Collapsed;
                    EndSidebarSlide();
                });
                // Splitter stays enabled so the user can grab it and drag the sidebar back open.
            }
            else
            {
                _sidebarBorder.Visibility = Visibility.Visible;
                double restore = SavedSidebarWidth();
                // Slide in at full size: content fixed at the target width from the first
                // frame, revealed by the growing border instead of reflowing up to size.
                BeginSidebarSlide(_sbSlideContentW > 0 ? _sbSlideContentW
                    : Math.Max(0, restore / Math.Max(0.01, _appScale) - 24));
                AnimateSidebarWidth(restore, () =>
                {
                    _sidebarCol.MinWidth = SbPx(SidebarMinOpen);   // open: clamp so the list can't be dragged below readable
                    EndSidebarSlide();
                });
                _sidebarToggleBtn.ToolTip = ShortcutTip("Str_TT_CollapseSidebar", "F9");
                SidebarSplitter.IsEnabled = true;
            }
            UpdateSidebarToggleGlyph();
        }

        // Sidebar slide freeze: while the track animates, the content keeps a FIXED width (a
        // fixed height for a top or bottom strip), anchored at the outer edge, and the border
        // clips it - so thumbnails and labels hold their size and slide out of view instead of
        // reflowing every frame. Restored to normal stretch layout when the glide lands.
        private double _sbSlideContentW;

        private void BeginSidebarSlide(double contentW)
        {
            if (contentW <= 0) return;
            _sbSlideContentW = contentW;
            switch (_sidebarSide)
            {
                case SidebarSide.Top:
                    SidebarContentPanel.Height = contentW;
                    SidebarContentPanel.VerticalAlignment = VerticalAlignment.Top;
                    break;
                case SidebarSide.Bottom:
                    SidebarContentPanel.Height = contentW;
                    SidebarContentPanel.VerticalAlignment = VerticalAlignment.Bottom;
                    break;
                default:
                    SidebarContentPanel.Width = contentW;
                    SidebarContentPanel.HorizontalAlignment = _sidebarSide == SidebarSide.Right
                        ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                    break;
            }
            _sidebarBorder.ClipToBounds = true;
        }

        private void EndSidebarSlide()
        {
            SidebarContentPanel.Width = double.NaN;
            SidebarContentPanel.Height = double.NaN;
            SidebarContentPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidebarContentPanel.VerticalAlignment = VerticalAlignment.Stretch;
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
                _sidebarCol.BeginAnimation(null);
                _sidebarCol.Width = new GridLength(target);
                onDone?.Invoke();
            };
            _sidebarCol.BeginAnimation(anim);
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
            _sidebarToggleBtn.ToolTip = ShortcutTip("Str_TT_CollapseSidebar", "F9");
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
            Point mouse = e.GetPosition(MainContentGrid);
            const double closeBuffer = 36;   // pull this far past the minimum edge to close on release
            double edge = SbPx(SidebarMinOpen);
            _sidebarWantClose = _sidebarSide switch
            {
                SidebarSide.Right => mouse.X > MainContentGrid.ActualWidth - edge + closeBuffer,
                SidebarSide.Top => mouse.Y < edge - closeBuffer,
                SidebarSide.Bottom => mouse.Y > MainContentGrid.ActualHeight - edge + closeBuffer,
                _ => mouse.X < edge - closeBuffer,
            };
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
                    double mid = SbPx((SidebarRailSize + SidebarMinOpen) / 2.0);
                    if (w < mid) { CollapseSidebarToStrip(); return; }
                    _sidebarCol.Width = new GridLength(SbPx(SidebarMinOpen));
                    w = SbPx(SidebarMinOpen);
                }
                _sidebarBorder.Visibility = Visibility.Visible;        // ensure the list shows when settled open
                SidebarContentPanel.Visibility = Visibility.Visible;
                _sidebarCol.MinWidth = SbPx(SidebarMinOpen);           // clamp future resizes so they can't clip
                RememberSidebarWidth(w);
                RememberSidebarCollapsed();
            }));
        }

        // Collapse to the 24px toggle strip without overwriting the saved width, so re-expand restores the
        // last good size rather than the thin dragged one. Mirrors SidebarToggle_Click's collapse branch.
        private void CollapseSidebarToStrip()
        {
            _sidebarCollapsed = true;
            RememberSidebarCollapsed();
            _sidebarToggleBtn.ToolTip = ShortcutTip("Str_TT_ExpandSidebar", "F9");
            _sidebarBorder.Visibility = Visibility.Collapsed;
            SidebarContentPanel.Visibility = Visibility.Visible;   // reset so the next border-show has content
            _sidebarCol.Width = new GridLength(SbPx(SidebarRailSize));
            _sidebarCol.MinWidth = SbPx(SidebarRailSize);
            UpdateSidebarToggleGlyph();   // splitter stays enabled so it can be dragged back open
        }

        // The rail auto-collapses when no PDF is open and re-opens when one is (2026-07-23): with
        // nothing loaded there are no thumbnails to show, so the strip gets out of the way. The empty
        // page-jump box + "/ -" hide alongside. Only fires on the open/close transition (FinishOpenFile /
        // ShowEmptyState) and at startup - NOT on tab switches - so a manual toggle mid-session sticks.
        // startup=true collapses instantly (no glide before the first paint); runtime transitions animate.
        //
        // A sidebar the USER closed stays closed: the auto-open may only undo this method's OWN
        // collapse. Materializing a lazy tab runs the document-open transition too, so without the
        // distinction a tab click was popping a deliberately closed sidebar back open.
        private bool _sidebarAutoCollapsed;   // the current collapse was made HERE, not by the user
        private bool _sidebarAutoToggling;    // this method is driving SidebarToggle_Click

        private void RememberSidebarCollapsed()
        {
            if (_sidebarAutoToggling) return;
            _sidebarAutoCollapsed = false;
            App.SetSetting("SidebarCollapsed", _sidebarCollapsed ? "1" : "0");
        }

        private void SyncSidebarToDocState(bool hasDoc, bool startup)
        {
            PageControlsRow?.Visibility = (hasDoc && _sidebarPanel == SidebarPanel.Pages)
                    ? Visibility.Visible : Visibility.Collapsed;
            SyncRailPanelAvailability(hasDoc);

            _sidebarAutoToggling = true;
            try
            {
                if (hasDoc && _sidebarCollapsed && _sidebarAutoCollapsed)
                {
                    _sidebarAutoCollapsed = false;
                    SidebarToggle_Click(this, new RoutedEventArgs());          // open the rail for the document
                }
                else if (!hasDoc && !_sidebarCollapsed)
                {
                    _sidebarAutoCollapsed = true;
                    if (startup) CollapseSidebarToStrip();                     // instant at launch
                    else SidebarToggle_Click(this, new RoutedEventArgs());     // animated on last-tab close
                }
            }
            finally { _sidebarAutoToggling = false; }
        }
    }
}
