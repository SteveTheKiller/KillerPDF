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
        // Keyboard shortcuts
        // ============================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Keyboard view of the shortcuts overlay: holding Ctrl / Shift / Alt previews that layer.
            KbSyncLayerFromModifiers();

            // Don't intercept keys when typing in an editable TextBox (typewriter tool or form field).
            // The zoom ComboBox is editable-but-read-only; after using it, focus parks on its inner
            // TextBox and would otherwise swallow every shortcut (e.g. Ctrl+F) until the user clicked away.
            if (e.OriginalSource is TextBox tbSrc && !tbSrc.IsReadOnly) return;
            if (_activeTextBox is not null && _activeTextBox.IsFocused) return;

            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // An annotation selection copies the annotation(s); otherwise copy page text.
                if (_selectedAnnotation is not null || _selectedSet.Count > 0) CopySelectedAnnotations();
                else CopySelectedText();
                e.Handled = true;
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Internal annotation clipboard takes priority over an OS-clipboard image paste.
                if (_annotationClipboard.Count > 0) PasteAnnotations(PageList.SelectedIndex);
                else PasteFromClipboard();
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Prefer selecting all annotations (shows where everything is, makes stacked annotations
                // editable); fall back to selecting page text when there are none on screen.
                if (!SelectAllAnnotations()) SelectAllText();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.F3 && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
            {
                // F3 / Shift+F3 - next / previous search match, the Find Next convention (Acrobat and
                // Sumatra do the same). With the search bar closed, F3 opens it like Ctrl+F.
                if (_searchBar is null || _searchBar.Visibility != Visibility.Visible)
                    ToggleSearchBar();
                else if (Search.HasResults)
                {
                    if (Keyboard.Modifiers == ModifierKeys.Shift) SearchPrevResult();
                    else SearchNextResult();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _shapePolyPoints.Count > 0)
            {
                // Shapes tool (#127 Phase 3): abandon the in-progress polygon.
                CancelShapePolygon();
                e.Handled = true;
            }
            else if (e.Key == Key.Back && _shapePolyPoints.Count > 0)
            {
                // Remove the last placed polygon vertex.
                ShapePolyBackspace();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _shapePolyPoints.Count >= 3)
            {
                // Close the polygon from the keyboard.
                CommitShapePolygon();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                ApplyCrop([PageList.SelectedIndex]);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                HideCropConfirmBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && ShortcutOverlay.Visibility == Visibility.Visible)
            {
                FadeOverlayOut(ShortcutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && AboutOverlay.Visibility == Visibility.Visible)
            {
                FadeOverlayOut(AboutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _busyCts is not null)
            {
                // A cancellable long operation (OCR, repair) is running behind the busy overlay - offer to
                // cancel it instead of letting Escape fall through to the app-exit handler below.
                if (KillerDialog.Show(this, $"Cancel the current {_busyOpLabel}?", "KillerPDF",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _busyCts?.Cancel();
                e.Handled = true;
            }
            // #153: matched by the character the key TYPES, not its position. On a German layout
            // "?" is Shift+ss, so the US-positional OemQuestion check never fired (and the exact
            // modifier equality failed too, since typing "?" holds Shift). The VK test stays as a
            // fast path for layouts where it already worked.
            else if (Services.KeyLayout.IsCtrlChar(e.Key, '?')
                     || (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control))
            {
                if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
                else ShowShortcutsOverlayExclusive();
                e.Handled = true;
            }
            else if (e.Key == Key.F1)
            {
                // Toggle the shortcuts overlay (conventional Help key, alongside Ctrl+?).
                if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
                else ShowShortcutsOverlayExclusive();
                e.Handled = true;
            }
            else if (e.Key == Key.F12)
            {
                // Toggle the About dialog. (Moved off F2, which now belongs to rename-bookmark in the
                // outline panel, #133 - Windows convention. Document Info moved to F4 / Ctrl+D below.)
                if (AboutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(AboutOverlay);
                else ShowAboutOverlay();
                e.Handled = true;
            }
            else if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.None)
            {
                // Document Info - the advertised single-key shortcut (F1 help, F4 info, F5-F8 views,
                // F11 full screen, F12 about). Looked it up on the shortcuts overlay and pressed it?
                // The cheat sheet gets out of the way first.
                if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
                OpenDocumentInfo();
                e.Handled = true;
            }
            else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Compatibility alias: Ctrl+D is Document Properties in Acrobat, Foxit, and SumatraPDF.
                if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
                OpenDocumentInfo();
                e.Handled = true;
            }
            else if (e.Key == Key.F5) { SetViewMode(ViewMode.Continuous); e.Handled = true; }
            else if (e.Key == Key.F6) { SetViewMode(ViewMode.Single);     e.Handled = true; }
            else if (e.Key == Key.F7) { SetViewMode(ViewMode.TwoPage);    e.Handled = true; }
            else if (e.Key == Key.F8) { SetViewMode(ViewMode.Grid);       e.Handled = true; }
            // F10 = split pane. Matched as Key.System + SystemKey == Key.F10, NOT e.Key == Key.F10:
            // F10 is a system key in WPF (it activates the menu bar), so it never arrives as
            // Key.F10 and a plain check silently never fires. The Shift+F10 branch further down has
            // the same shape; F11 below does not need it, because F11 is not a system key.
            // Shift is excluded here because Shift+F10 is the context menu - without that guard the
            // two would both fire off one press.
            else if (e.Key == Key.System && e.SystemKey == Key.F10
                     && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                ToggleSplit();
                e.Handled = true;
            }
            else if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
            else if (e.Key == Key.Escape && _fullScreen) { ToggleFullScreen(); e.Handled = true; }
            // PgDn / PgUp navigate to the next / previous page - they never reorder pages (that's the
            // toolbar Move Up/Down buttons). Handled at the window level with e.Handled so it behaves the
            // same whether the page canvas or a sidebar thumbnail has focus; without this, a focused
            // PageList (ListBox) would page its own selection instead. The TextBox guard at the top of this
            // handler already exempts typing in a form field / typewriter box.
            else if (e.Key == Key.PageDown && Keyboard.Modifiers == ModifierKeys.None)
            {
                NavigatePageStep(1);   // one page; one SPREAD in Two-Page mode (#120)
                e.Handled = true;
            }
            else if (e.Key == Key.PageUp && Keyboard.Modifiers == ModifierKeys.None)
            {
                NavigatePageStep(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Print_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && (_selectedAnnotation is not null || _selectedSet.Count > 0))
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) Undo_Click(this, e);   // ignore key auto-repeat so one press = one undo
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                SaveAs_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveInPlace();
                e.Handled = true;
            }
            else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CloseTab(_active);
                e.Handled = true;
            }
            else if (e.Key == Key.W && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                CloseOtherTabs(_active);
                e.Handled = true;
            }
            else if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CloseAllTabs();
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                CycleTab(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CycleTab(1);
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Open_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                OcrPageToClipboard(PageList.SelectedIndex);
                e.Handled = true;
            }
            else if (e.Key == Key.I && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                BeginOcrRegion();
                e.Handled = true;
            }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.None && _doc is not null
                     && ShortcutOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible)
            {
                // Bare N = invert document colors (night mode), #135. Moved off Ctrl+I in 1.6.6 so
                // the conventional italic chord is free while editing text; single-key house style.
                // Same guards as the bare-key tool switches below (doc open, no overlay, not typing).
                ToggleDocInvert(!BitmapHelpers.DocInvert);
                e.Handled = true;
            }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Shift && _doc is not null
                     && ShortcutOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible)
            {
                // Shift+N pairs with bare N: toggles whether night mode inverts pictures too
                // (the moon's right-click option). Same guards as N.
                ToggleInvertImages(!BitmapHelpers.DocInvertImages);
                e.Handled = true;
            }
            // App-wide accessibility size (AppScale.cs), distinct from the Ctrl+wheel page zoom:
            // Ctrl+Shift +/- steps the whole-app size, Ctrl+Shift+0 resets it. Works with no
            // mouse; the wheel-over-logo gesture drives the same scale.
            else if ((e.Key == Key.OemPlus || e.Key == Key.Add) && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ApplyAppScale(_appScale + AppScaleStep, persist: true);
                e.Handled = true;
            }
            else if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ApplyAppScale(_appScale - AppScaleStep, persist: true);
                e.Handled = true;
            }
            else if ((e.Key == Key.D0 || e.Key == Key.NumPad0) && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ApplyAppScale(1.0, persist: true);
                e.Handled = true;
            }
            // Toolbar appearance, mirroring the bar's right-click menu top to bottom: Ctrl+Shift+1/2
            // pick the icon size, Ctrl+Shift+3..6 pick where the text goes. Number row only - the
            // numpad digits stay clear of the tool shortcuts' numpad mappings.
            else if (e.Key is >= Key.D1 and <= Key.D6 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                switch (e.Key)
                {
                    case Key.D1: SetToolbarIconSize(ToolbarIconSize.Small);   break;
                    case Key.D2: SetToolbarIconSize(ToolbarIconSize.Large);   break;
                    case Key.D3: SetToolbarLabelMode(ToolbarLabelMode.None);  break;
                    case Key.D4: SetToolbarLabelMode(ToolbarLabelMode.Beside);break;
                    case Key.D5: SetToolbarLabelMode(ToolbarLabelMode.Under); break;
                    case Key.D6: SetToolbarLabelMode(ToolbarLabelMode.Only);  break;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.B && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ToggleSidebarSide();   // left/right, pairing with Ctrl+B's collapse toggle
                e.Handled = true;
            }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                NewDocument();
                e.Handled = true;
            }
            else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SidebarToggle_Click(this, e);   // collapse / restore the sidebar
                e.Handled = true;
            }
            else if (e.Key == Key.F9 && Keyboard.Modifiers == ModifierKeys.None)
            {
                // Jog to the next view mode, wrapping (single-key house style; freed when the
                // Settings panel retired). F5-F8 still jump straight to a specific mode.
                CycleViewMode();
                e.Handled = true;
            }
            else if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.None && _doc is not null)
            {
                // First / last page (the Acrobat / Sumatra convention).
                RecordNavJump();
                PageList.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.None && _doc is not null)
            {
                RecordNavJump();
                PageList.SelectedIndex = _doc.PageCount - 1;
                e.Handled = true;
            }
            else if (e.Key == Key.D1 && Keyboard.Modifiers == ModifierKeys.Control && _doc is not null)
            {
                _fitMode = FitMode.None;
                SetTrueZoom(1.0);    // actual size (Acrobat Ctrl+1); Ctrl+0 stays the 100% reset
                e.Handled = true;
            }
            else if (e.Key == Key.D2 && Keyboard.Modifiers == ModifierKeys.Control && _doc is not null)
            {
                FitToWidth();        // (Acrobat Ctrl+2)
                e.Handled = true;
            }
            else if (e.Key == Key.D3 && Keyboard.Modifiers == ModifierKeys.Control && _doc is not null)
            {
                FitToPage();
                e.Handled = true;
            }
            else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) Redo_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                if (!e.IsRepeat) Redo_Click(this, e);   // Ctrl+Shift+Z, the editor-style redo
                e.Handled = true;
            }
            else if (e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                NavHistoryGo(back: true);    // retrace bookmark / link / jump-box jumps
                e.Handled = true;
            }
            else if (e.Key == Key.System && e.SystemKey == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                NavHistoryGo(back: false);
                e.Handled = true;
            }
            else if ((e.Key == Key.Apps
                      || (e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
                     && _doc is not null)
            {
                // Windows accessibility convention: the Menu key / Shift+F10 opens the context menu
                // at the current selection without the mouse.
                OpenContextMenuAtSelection();
                e.Handled = true;
            }
            // Bare-key tool switches. Only when a document is open, no modifier is held, and no
            // overlay is up (and not while typing - guarded at the top of this handler).
            else if (Keyboard.Modifiers == ModifierKeys.None && _doc is not null
                     && ShortcutOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility != Visibility.Visible
                     && TryToolShortcut(e.Key))
            {
                e.Handled = true;
            }
            // Left/Right move one page - one two-page SPREAD in Two-Page mode (#120), so a press
            // always changes what's on screen instead of stepping through both pages of a spread.
            else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (NavigatePageStep(-1)) e.Handled = true;
            }
            else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (NavigatePageStep(1)) e.Handled = true;
            }
            // Up/Down scroll the view like the mouse wheel instead of jumping a page; at the top/
            // bottom edge (or when the page fits the viewport, where there's nothing to scroll)
            // they flip to the previous/next page, so at fit-to-page zoom this behaves exactly
            // like the old page navigation. Left/Right and PgUp/PgDn stay hard page jumps.
            // Handled at the window level so a focused sidebar thumbnail doesn't move its own
            // selection instead (same reasoning as PgUp/PgDn above).
            else if ((e.Key == Key.Up || e.Key == Key.Down) && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_doc is not null)
                {
                    ScrollOrFlipByKey(up: e.Key == Key.Up);
                    e.Handled = true;
                }
            }
            // #153: "the key that types + or =", whatever position that is on this layout. The
            // Ctrl+Shift app-scale chords above are tested FIRST, so ignoring Shift here cannot
            // swallow them - which is why this ordering matters and must not be rearranged.
            else if (Services.KeyLayout.IsCtrlChar(e.Key, '+', '=')
                     || ((e.Key == Key.OemPlus || e.Key == Key.Add) && Keyboard.Modifiers == ModifierKeys.Control))
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(false); else SetZoom(_zoomLevel + ZoomStep);
                e.Handled = true;
            }
            else if (Services.KeyLayout.IsCtrlChar(e.Key, '-')
                     || ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && Keyboard.Modifiers == ModifierKeys.Control))
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(true); else SetZoom(_zoomLevel - ZoomStep);
                e.Handled = true;
            }
            else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SetTrueZoom(1.0);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _doc is not null && _currentTool != EditTool.Select)
            {
                // 1.6.6 (#127 Phase 3): Esc with nothing left to cancel drops back to the Select
                // tool, Acrobat-style. With Select already active it keeps falling through to the
                // app-exit handler below, unchanged.
                SetTool(EditTool.Select);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // No overlay active - ESC exits the app
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Space && !_spaceHeld)
            {
                _spaceHeld = true;
                PagePreviewPanel.Cursor = Cursors.Hand;
                e.Handled = true;
            }
        }

        // Full-window overlays are mutually exclusive: opening the shortcuts overlay dismisses
        // About instead of stacking - ShowAboutOverlay does the converse.
        private void ShowShortcutsOverlayExclusive()
        {
            if (AboutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(AboutOverlay);
            ApplyPersistedShortcutView();
            FadeOverlayIn(ShortcutOverlay);
        }

        // ── Jump history (Alt+Left / Alt+Right / mouse back-forward buttons) ─────────────────────
        // Page-granular: recorded at the long-jump sites (bookmark click, internal link, the page
        // jump box, Home/End) so a reader thrown 30 pages by a bookmark can retrace the hop.

        /// <summary>Records the CURRENT page onto the back stack. Call BEFORE performing a jump.</summary>
        private void RecordNavJump()
        {
            if (_doc is null) return;
            int cur = Math.Max(0, PageList.SelectedIndex);
            if (_navBack.Count > 0 && _navBack.Peek() == cur) { _navForward.Clear(); return; }
            _navBack.Push(cur);
            _navForward.Clear();   // a fresh jump invalidates the forward chain, like a browser
        }

        private void NavHistoryGo(bool back)
        {
            if (_doc is null) return;
            var from = back ? _navBack : _navForward;
            var to   = back ? _navForward : _navBack;
            if (from.Count == 0) return;
            int cur = Math.Max(0, PageList.SelectedIndex);
            int target = from.Pop();
            to.Push(cur);
            if (target >= 0 && target < _doc.PageCount)
                PageList.SelectedIndex = target;
        }

        // Mouse back / forward buttons (XButton1 / XButton2) retrace the same history, like a
        // browser. Registered on the window in the MainWindow constructor.
        private void NavHistory_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.XButton1)      { NavHistoryGo(back: true);  e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { NavHistoryGo(back: false); e.Handled = true; }
        }

        /// <summary>Keyboard access to the right-click menu (Menu key / Shift+F10): the selected
        /// annotation's menu at its bounds, or the page-level menu centered on the current page's
        /// canvas. Placement is set to Center just for this open and restored on close, so the
        /// mouse path keeps its open-at-cursor behavior.</summary>
        private void OpenContextMenuAtSelection()
        {
            if (_doc is null || _annotationCanvas.ContextMenu is not ContextMenu cm) return;
            int pg = Math.Max(0, PageList.SelectedIndex);
            Point pt;
            if (_selectedAnnotation is not null)
            {
                pg = _selectedAnnotation.PageIndex;
                var b = AnnotBounds(_selectedAnnotation);
                pt = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
            }
            else
                pt = _renderDims.TryGetValue(pg, out var rd)
                    ? new Point(rd.w / 2.0, rd.h / 2.0)
                    : new Point(0, 0);
            var canvas = VisibleCanvasForPage(pg) ?? CanvasForPage(pg);
            if (canvas is null) return;
            _activeCanvas = canvas;
            PopulateContextMenu(pt, pg);
            cm.PlacementTarget = canvas;
            var prevPlacement = cm.Placement;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            void Restore(object? s, RoutedEventArgs a) { cm.Placement = prevPlacement; cm.Closed -= Restore; }
            cm.Closed += Restore;
            cm.IsOpen = true;
        }

        // One Up/Down arrow press scrolls this many DIP; key auto-repeat makes holding the key a
        // smooth continuous scroll. Kept smaller than a wheel notch (144 DIP, see WheelScrollFactor)
        // for fine reading control.
        private const double ArrowScrollStep = 48.0;

        // Up/Down arrow behavior, mirroring PagePreview_PreviewMouseWheel exactly: Grid and
        // Continuous are one scroll over the whole document, so the keys always scroll; Single/
        // Two-Page scroll within the page and flip to the previous/next page at the edges.
        private void ScrollOrFlipByKey(bool up)
        {
            double step = up ? -ArrowScrollStep : ArrowScrollStep;
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.Continuous)
            {
                PagePreviewPanel.ScrollToVerticalOffset(PagePreviewPanel.VerticalOffset + step);
                return;
            }
            // NavigatePageByWheel treats positive delta as "previous page" (wheel-up), so reuse
            // the same convention here.
            if (PagePreviewPanel.ScrollableHeight <= 0)
            {
                NavigatePageByWheel(up ? 120 : -120);
                return;
            }
            bool atTop    = PagePreviewPanel.VerticalOffset <= 0;
            bool atBottom = PagePreviewPanel.VerticalOffset >= PagePreviewPanel.ScrollableHeight - 1;
            if ((atTop && up) || (atBottom && !up))
            {
                NavigatePageByWheel(up ? 120 : -120);
                return;
            }
            PagePreviewPanel.ScrollToVerticalOffset(PagePreviewPanel.VerticalOffset + step);
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            KbSyncLayerFromModifiers();   // releasing a modifier drops the keyboard view back a layer
            if (e.Key == Key.Space && _spaceHeld)
            {
                _spaceHeld = false;
                if (!_isPanning)
                    PagePreviewPanel.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        // Maps a bare key to an editing tool. Returns false for any other key so the caller's
        // shortcut chain continues. Mirrors the toolbar tool buttons exactly - Signature routes
        // through its button handler so the signature picker opens (a bare SetTool would only arm
        // the tool without showing the menu).
        private bool TryToolShortcut(Key key)
        {
            switch (key)
            {
                // Tools are reachable by their toolbar position (digits 1-9, 0 mirror the toolbar,
                // left to right); the original letter keys stay as fallbacks. Both the number-row and
                // numpad digits map. 1.6.6 remap (#127 Phase 3): Select is V-only (the Photoshop /
                // Illustrator / Figma convention) - its digit went to Text so Shapes could take 4 and
                // the digits keep mirroring the toolbar order.
                case Key.V: SetTool(EditTool.Select); return true;
                case Key.T: case Key.D1: case Key.NumPad1: SetTool(EditTool.Text); return true;
                case Key.H: case Key.D2: case Key.NumPad2: SetTool(EditTool.Highlight); return true;
                case Key.L: case Key.U: case Key.D3: case Key.NumPad3: SetTool(EditTool.Line); return true;
                case Key.D4: case Key.NumPad4: SetTool(EditTool.Shape); return true;
                case Key.D: case Key.D5: case Key.NumPad5: SetTool(EditTool.Draw); return true;
                case Key.I: case Key.D6: case Key.NumPad6: SetTool(EditTool.Image); return true;
                case Key.G: case Key.D7: case Key.NumPad7: ToolSignature_Click(this, new RoutedEventArgs()); return true;
                case Key.C: case Key.D8: case Key.NumPad8: SetTool(EditTool.Crop); return true;
                case Key.R: case Key.D9: case Key.NumPad9: ToolRotate_Click(this, new RoutedEventArgs()); return true;
                case Key.S: case Key.D0: case Key.NumPad0: ToolStamp_Click(this, new RoutedEventArgs()); return true;
                default: return false;
            }
        }

        // Appends each tool's key to its tooltip, e.g. "Highlight (2)". Digits mirror the toolbar
        // position (1-9, 0); Select shows its letter (V) since the 1.6.6 remap made it V-only.
        // Re-resolves the localized base text so a language switch keeps the right wording (from SelectLocale).
        private void ApplyToolNumberTooltips()
        {
            void Set(System.Windows.Controls.Button btn, string key, string n)
            {
                if (btn != null && TryFindResource(key) is string s) btn.ToolTip = $"{s} ({n})";
            }
            Set(ToolSelectBtn, "Str_TT_SelectTool", "V");
            Set(ToolTextBtn, "Str_TT_TextTool", "1");
            Set(ToolHighlightBtn, "Str_TT_HighlightTool", "2");
            Set(ToolUnderlineBtn, "Str_TT_LineTool", "3");   // repurposed to the Line tool
            Set(ToolShapeBtn, "Str_TT_ShapeTool", "4");
            Set(ToolDrawBtn, "Str_TT_DrawTool", "5");
            Set(ToolImageBtn, "Str_TT_ImageTool", "6");
            Set(ToolSignatureBtn, "Str_TT_SignatureTool", "7");
            Set(ToolCropBtn, "Str_TT_CropTool", "8");
            Set(_toolRotateBtn, "Str_TT_RotateTool", "9");
            // Not a tool, but the same treatment: the view-mode rail button advertises its jog
            // key (the wheel-over gesture cycles too). Sentence case, like every rail tooltip.
            Set(ViewModeBtn, "Str_TT_ViewMode", "F9");
        }

        // Opens the online help / how-to page in the user's default browser.
        private void OnlineHelp_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://killerpdf.net/help.html") { UseShellExecute = true }); }
            catch { }
        }
    }
}
