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
using Microsoft.Win32;
using KillerPDF.Services;
using KillerFamily.Accessibility;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // ============================================================
        // Picker state sync + appearance handlers. (The Settings panel itself retired 2026-07-31:
        // every section moved to where the thing it configures lives - theme/language/view onto
        // rail flyouts, toolbar onto the bar's right-click menu, sidebar side onto the sidebar's.)
        // ============================================================

        // Syncs the view-mode radios and page navigation buttons to live state. The theme and
        // language menus are the kit's (Controls/ThemePicker.cs, Controls/LanguageMenu.cs) and
        // build their checked rows on every open.
        private void SyncPickerState()
        {
            SyncPageNavigationButtons();
            // Sync view mode radios. Against the PENDING mode while a fade-wrapped switch is in
            // flight (_viewMode lags until the fade-out lands), so wheel-cycling with the flyout
            // open moves the checkmark in step instead of one notch behind.
            var vm = _pendingViewMode ?? _viewMode;
            ViewSingleRadio.IsChecked     = vm == ViewMode.Single;
            ViewContinuousRadio.IsChecked = vm == ViewMode.Continuous;
            ViewTwoPageRadio.IsChecked    = vm == ViewMode.TwoPage;
            ViewTwoPageContinuousRadio.IsChecked = vm == ViewMode.TwoPageContinuous;
            ViewGridRadio.IsChecked       = vm == ViewMode.Grid;
            ApplyPageThumbnailLayout();
            // (The toolbar picker lives on the toolbar's own right-click menu, and the sidebar
            // side on the sidebar's - both build their checks fresh on open, nothing to sync here.)
        }

        // ── Quick fade in/out for the full-window overlay panels (Shortcuts/About) ──
        private static void FadePanelIn(UIElement el)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            el.Visibility = Visibility.Visible;
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(110)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        private static void FadePanelOut(UIElement el, Action? done = null)
        {
            if (el.Visibility != Visibility.Visible) { done?.Invoke(); return; }
            var anim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(90)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) =>
            {
                el.Visibility = Visibility.Collapsed;
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 1;
                done?.Invoke();
            };
            el.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // Fades an annotate (draw/text) settings bar out over ~90ms, then removes it from its parent -
        // so the bar dissolves when its tool is deselected and crossfades when switching tools, matching
        // the About/Settings overlays.
        private static void FadeOutAndRemoveBar(Border? bar)
        {
            if (bar is null) return;
            var anim = new DoubleAnimation(bar.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(90)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) =>
            {
                bar.BeginAnimation(UIElement.OpacityProperty, null);
                // Release the 98SE scroller inset before the bar leaves its area, so the document
                // slides back up to the pane top when the band disappears.
                if (bar.Parent is Grid area) SyncSe98BarInset(bar, area, removing: true);
                (bar.Parent as Panel)?.Children.Remove(bar);
            };
            bar.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // Collapses the visible annotate bar to a thin peek strip, or expands it back. Triggered by
        // re-clicking the already-active tool, so a second click tucks the bar away instead of the
        // old behavior of rebuilding it (which flickered).
        private void ToggleAnnotBarMinimized()
        {
            var bar = _textSettingsBar ?? _drawSettingsBar ?? _cropConfirmBar;
            if (bar is null) return;
            _annotBarMinimized = !_annotBarMinimized;
            bar.ClipToBounds = true;
            const double peek = 13;   // thin strip, just enough for the grip dots
            if (_annotBarMinimized)
            {
                // Freeze the current width so collapsing the content can't shrink the bar to the dots and
                // slide it to the corner - it stays a same-width strip in place.
                bar.Width = bar.ActualWidth;
                bar.Effect = null;   // minimized strips never carry a drop shadow
                _annotBarFullHeight = bar.ActualHeight > 0 ? bar.ActualHeight : bar.DesiredSize.Height;
                var anim = new DoubleAnimation(_annotBarFullHeight, peek, new Duration(TimeSpan.FromMilliseconds(120)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                anim.Completed += (_, _) =>
                {
                    _annotBarContent?.Visibility = Visibility.Collapsed;
                    _annotBarDots?.Visibility = Visibility.Visible;
                    bar.ClipToBounds = false;   // content is hidden now, nothing to clip
                };
                bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
            }
            else
            {
                // Show the full content again before growing back, and let the width track content again.
                bar.Width = double.NaN;
                bar.Effect = AnnotBarShadow();   // restore the drop shadow on the expanded bar
                _annotBarContent?.Visibility = Visibility.Visible;
                _annotBarDots?.Visibility = Visibility.Collapsed;
                double full = _annotBarFullHeight > 0 ? _annotBarFullHeight : bar.ActualHeight;
                var anim = new DoubleAnimation(peek, full, new Duration(TimeSpan.FromMilliseconds(120)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                anim.Completed += (_, _) =>
                {
                    bar.BeginAnimation(FrameworkElement.HeightProperty, null);
                    bar.Height = double.NaN;   // back to auto so it tracks its content again
                    bar.ClipToBounds = false;
                };
                bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
            }
        }

        // Confirm-before-opening-links (About card footer). One key, positive sense - see the
        // ConfirmLinksSetting comment in Links.cs. Writing the same value back on the init sync is
        // a harmless no-op, so this needs no change guard.
        private void LinkConfirmCheck_Toggled(object sender, RoutedEventArgs e)
            => App.SetSetting(ConfirmLinksSetting, LinkConfirmCheck.IsChecked == true ? "1" : "0");

        // Privacy section (#146): don't remember recently opened files. Turning it ON also clears
        // the existing list (matching the user's privacy expectation on a shared machine); the
        // guard makes the settings-open sync a no-op so opening the panel never wipes anything.
        private void NoRecentCheck_Toggled(object sender, RoutedEventArgs e)
        {
            bool off = NoRecentCheck.IsChecked == true;
            if ((App.GetSetting(App.NoRecentFilesSetting) == "1") == off) return;   // sync, not a user change
            if (off)
            {
                App.SetSetting(App.NoRecentFilesSetting, "1");
                App.ClearRecentFiles();
                PopulateRecentFilesList();   // start screen hides its Recent box immediately
            }
            else
            {
                App.RemoveSetting(App.NoRecentFilesSetting);
            }
        }

        // Invert document colors (#135): flips the display-only dark mode. Shared by the rail's
        // moon toggle and Ctrl+I. The state is baked into rendered pixels, so flush the render
        // caches and repaint IN PLACE - never through ApplyViewMode. The mode-switch rebuild
        // re-laid-out the whole view and restored scroll approximately, so pages visibly
        // shuffled before the new colors arrived. Invert changes pixels, never geometry:
        // layout and scroll stay exactly where they are and only the bitmaps re-render.
        private void ToggleDocInvert(bool on)
        {
            // Per pane: only the FOCUSED pane flips, so a split can read one document inverted
            // beside a normal one. The moon lights for the focused pane; FocusPane re-syncs it.
            if (ActiveViewer.DocInvert == on) return;
            ActiveViewer.DocInvert = on;
            App.SetSetting("DocInvert", on ? "1" : "0");
            DocInvertBtn.Tag = on ? "on" : null;   // lights the footer icon while active
            RepaintForInvertChange(bothPanes: false);   // per-pane toggle: the other pane is untouched
        }

        // Right-click on the moon: night-mode options. One checkable item - "Invert images too"
        // (default off since the #135 carve-out; some scanned documents ARE one full-page image,
        // where the carve-out makes night mode a no-op, so the old full inversion stays reachable).
        // Built fresh on each open so the caption follows the active language.
        private void DocInvertBtn_RightClick(object sender, MouseButtonEventArgs e)
        {
            var menu = MakeThemedMenu();
            var mi = new System.Windows.Controls.MenuItem
            {
                Header = Loc("Str_InvertImagesToo"),
                IsCheckable = true,
                IsChecked = BitmapHelpers.DocInvertImages,
                InputGestureText = GestureFor("Shift:N"),   // right-aligned in the family MenuItem template
            };
            mi.Click += (_, _2) => ToggleInvertImages(!BitmapHelpers.DocInvertImages);
            menu.Items.Add(mi);
            menu.PlacementTarget = DocInvertBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void ToggleInvertImages(bool on)
        {
            if (BitmapHelpers.DocInvertImages == on) return;
            BitmapHelpers.DocInvertImages = on;
            App.SetSetting("DocInvertImages", on ? "1" : "0");
            // Only repaint when night mode is actually showing in either pane; otherwise it just
            // takes effect the next time a moon is toggled on. Both panes: this option changes
            // how EVERY inverted pane renders.
            if (Viewer.DocInvert || ViewerB.DocInvert) RepaintForInvertChange(bothPanes: true);
        }

        // Shared by the moon toggle and its right-click option: the invert state is baked into
        // rendered pixels, so flush the render caches and repaint IN PLACE - never through
        // ApplyViewMode (see the comment above ToggleDocInvert).
        private void RepaintForInvertChange(bool bothPanes)
        {
            // Per-pane toggle flushes and repaints ONLY the focused pane; the other pane's
            // pixels are correct and repainting them read as a spurious refresh (2026-08-15).
            // The images-too option still touches every inverted pane.
            if (bothPanes) FlushAllRenderCaches();
            else ActiveViewer.FlushOwnRenderCaches();

            // The invert flag is GLOBAL, but everything below this block runs through the shared
            // fields and so repaints only the FOCUSED pane - the other pane's already-painted
            // tiles kept their old colors until any scroll or focus change forced a re-render,
            // which read as one pane being inverted and scrolling the other pane inverting it
            // (2026-08-01). Re-render it with its own session swapped in - the same
            // WithOwnSession idiom the cross-pane tab drag uses. BEFORE the _doc guard: the
            // focused pane being empty must not strand the other pane's stale pixels.
            if (bothPanes && IsSplit)
            {
                var other = ReferenceEquals(ActiveViewer, Viewer) ? ViewerB : Viewer;
                // PIXELS ONLY (RepaintPixelsExt, 2026-08-15): the full RenderActiveSessionExt ran
                // Bootstrap/ShowEmptyState under the swapped session, and their Host chrome
                // mutations kept wrecking the focused pane's sidebar - replaced thumbnails when
                // the other pane held a doc, cleared and disabled the sidebar when it was empty.
                other.WithOwnSession(other.RepaintPixelsExt);
            }

            if (_doc is null) return;
            if (_viewMode.IsContinuous())
            {
                // Null every slot's bitmap so the render pass (which skips filled slots)
                // repaints the window around the viewport; far slots stay empty scaffolds
                // until virtualization brings them back, exactly as after a long scroll.
                // Slot sizes are untouched, so scroll geometry cannot move.
                _continuousSharpenCts?.Cancel();
                _continuousSharpPages.Clear();
                foreach (var child in _continuousPanel.Children)
                    if (child is Border b && b.Child is Grid g
                        && g.Children.Count > 0 && g.Children[0] is System.Windows.Controls.Image img)
                        img.Source = null;
                _ = RenderContinuousPages(Math.Max(0, PageList.SelectedIndex));
                StartRerenderTimer();   // then re-sharpen the visible pages at the current zoom
            }
            else
            {
                // Single/Two-Page/Grid: RenderPage repaints the primary and streams the
                // secondary tiles back (grid anchors at page 0, like ApplyViewMode).
                // keepTiles: the tile set is unchanged, so existing tiles stay put and get
                // their bitmaps swapped in place - no clear-and-refill jitter in grid.
                ActiveViewer.RenderPage(_viewMode == ViewMode.Grid ? 0 : Math.Max(0, PageList.SelectedIndex),
                                        keepTiles: true);
            }
        }

        private void DocInvertBtn_Click(object sender, RoutedEventArgs e)
            => ToggleDocInvert(!ActiveViewer.DocInvert);

        private void OnThemeChanged()
        {
            _appliedSquared = null;
            UpdateWindowChrome();
            // The 98SE pane reaches the frame while rounded themes retain an 8px outer gutter.
            // Reapply the saved sidebar side so switching away from 98SE restores that right edge.
            ApplySidebarSide();
            // Refresh snapshot FindResource calls that were set as local values.
            // SetResourceReference bindings update automatically; sidebar tabs and
            // active tool button background still need an explicit refresh.
            SetTool(_currentTool);
            // The list panels use resource references throughout and need no refresh.
            if (_sidebarPanel == SidebarPanel.Outlines)
                SwitchSidebarToOutlinesTab();
            else if (_sidebarPanel == SidebarPanel.Pages)
                SwitchSidebarToPagesTab();
            // Re-evaluate the page-list edge overlays immediately. 98SE sets the theme
            // multiplier to zero; modern themes leave it at the default full strength.
            SyncPageListEdgeFades();
            RefreshSelectionAccent();
            // Both panes carry local border-thickness state. Rebuild both so an inactive pane cannot
            // retain the previous theme's right-edge geometry until it happens to receive focus.
            Viewer.RebuildTabStripExt();
            ViewerB.RebuildTabStripExt();
            // The signature popup is built from snapshot (FindResource) colors, so rebuild it in place
            // if it's open so it picks up the new theme without the user having to close and reopen it.
            if (_signaturePopup is not null) ShowSignaturePopup();

            // The keyboard's layer selectors use the current theme's actual button template
            // (98SE raised bevel versus the modern card button), so rebuild the lazy board when
            // that template family changes. Key labels and mappings remain table-driven.
            if (_kbBuilt)
            {
                var layer = _kbLayer;
                _kbBuilt = false;
                BuildKeyboardView();
                SetKbLayer(layer);
            }

            // Code-built annotate bars can be open in either pane. Refresh both panes so a bar
            // cannot retain the accent or control template from the theme that created it.
            RefreshOpenAnnotationBars();
        }

        // The rest of a language switch, after the kit's RelocalizeDynamicUi (Shell/Language.cs)
        // has rebuilt the toolbar and the shortcuts card. It also runs when a --lang-file
        // translation reloads.
        partial void RelocalizeAppUi()
        {
            ApplyShortcutTooltips();     // tooltips carry key names, which differ per language

            // The status bar text is a formatted string (not a DynamicResource), so it keeps the
            // language it was last set in. Re-set it in the new locale instead of leaving it stale.
            if (_doc is not null && PageList.SelectedIndex >= 0)
                SetStatus(string.Format(Loc("Str_PageOf"), PageList.SelectedIndex + 1, _doc.PageCount));
            else
                SetStatus(Loc("Str_Ready"));

            // The canvas right-click menu is built once with Loc() values captured at build time,
            // so rebuild it in the new language. (The sidebar menu is rebuilt on each open.)
            BuildContextMenu();

            // Refresh code-built annotate bars in both panes. A language change comes from a
            // window flyout, so the pane containing the visible bar is not guaranteed to be the
            // current ActiveViewer by the time these controls are rebuilt.
            RefreshOpenAnnotationBars();

            // Page thumbnails and outline tooltips snapshot Loc() strings when built; rebuild both
            // lists so their "Page N" labels switch to the new language immediately.
            RefreshPageList();
            RefreshOutlines();

            // A visible signature popup is built with Loc() too; rebuild it so its section headers and
            // pen labels switch immediately.
            RefreshSignaturePopupLanguage();

            if (_kbBuilt)
            {
                var layer = _kbLayer;
                _kbBuilt = false;
                BuildKeyboardView();
                SetKbLayer(layer);
            }

            // The fit-mode terms differ in length per language; resize the zoom box so the longest never clips.
            AdjustZoomBoxWidth();
        }

        private void RefreshOpenAnnotationBars()
        {
            var active = ActiveViewer;
            var other = ReferenceEquals(active, Viewer) ? ViewerB : Viewer;
            // Refresh the inactive pane first. BuildBarHost keeps a few drag/minimize references
            // at window scope, so finishing on the active pane leaves those references pointed at
            // the controls the shared toolbar is currently operating.
            if (!ReferenceEquals(other, active)) RefreshOpenAnnotationBars(other);
            RefreshOpenAnnotationBars(active);
        }

        private void RefreshOpenAnnotationBars(KillerPDF.Controls.PdfViewer viewer)
        {
            ((KillerPDF.Features.IViewerHost)this).RunWithViewerContext(viewer, () =>
            {
                var tool = _annotBarTool;
                if (tool == EditTool.Text)
                    ShowTextSettings();
                else if (tool is EditTool bt && bt is EditTool.Draw or EditTool.Highlight
                    or EditTool.Line or EditTool.Strikethrough or EditTool.Underline or EditTool.Shape)
                    ShowDrawSettings(bt);

                RebuildCropBarForLocale();
            });
        }

        // Keep the footer zoom box compact. Its popup has a wider minimum for localized fit labels.
        private void AdjustZoomBoxWidth()
        {
            if (ZoomBox is null) return;
            try
            {
                double pixelsPerDip = VisualTreeHelper.GetDpi(ZoomBox).PixelsPerDip;
                var typeface = new System.Windows.Media.Typeface(
                    ZoomBox.FontFamily, ZoomBox.FontStyle, ZoomBox.FontWeight, ZoomBox.FontStretch);
                double emSize = ZoomBox.FontSize > 0 ? ZoomBox.FontSize : 12;
                double max = 0;
                foreach (var item in ZoomBox.Items)
                {
                    string text = item is System.Windows.Controls.ComboBoxItem ci
                        ? ci.Content?.ToString() ?? ""
                        : item?.ToString() ?? "";
                    var ft = new System.Windows.Media.FormattedText(
                        text, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                        typeface, emSize, System.Windows.Media.Brushes.Black, pixelsPerDip);
                    if (ft.WidthIncludingTrailingWhitespace > max) max = ft.WidthIncludingTrailingWhitespace;
                }
                ZoomBox.Width = System.Math.Clamp(System.Math.Ceiling(max) + 28, 70, 220);
            }
            catch { /* best-effort; leave the XAML default width */ }
        }

        private void ViewContinuousRadio_Checked(object sender, RoutedEventArgs e) => SelectViewMode(ViewMode.Continuous);
        private void ViewSingleRadio_Checked(object sender, RoutedEventArgs e)     => SelectViewMode(ViewMode.Single);
        private void ViewTwoPageRadio_Checked(object sender, RoutedEventArgs e)    => SelectViewMode(ViewMode.TwoPage);
        private void ViewTwoPageContinuousRadio_Checked(object sender, RoutedEventArgs e) => SelectViewMode(ViewMode.TwoPageContinuous);
        private void ViewGridRadio_Checked(object sender, RoutedEventArgs e)       => SelectViewMode(ViewMode.Grid);

        // #193: book layout toggle (cover page alone in Two-Page). Global, persisted; both panes
        // re-run their current layout so an active Two-Page view re-pairs in place. Reached from
        // the page context menu while in Two-Page, and the bare B key.
        internal void ToggleBookMode()
        {
            App.SetSetting("TwoPageBook", Controls.PdfViewer.BookMode ? "0" : "1");
            Viewer.ReapplyViewMode();
            ViewerB.ReapplyViewMode();
        }

        // ── Toolbar appearance ────────────────────────────────────────────
        // The engine is the family one (Shell/ToolbarStyle.cs); KillerPDF's buttons, drawn glyphs,
        // picker rows, split buttons and caption shed order are in Shell/ToolbarStyle.Extras.cs.

        // #215: a reading view without the toolbar, while the window stays a normal window the
        // user can still switch away from (full screen was the wrong tool for that). On the
        // toolbar's right-click menu and Ctrl+F11; the tools all remain reachable by shortcut.
        private bool _toolbarHidden;

        internal void ToggleToolbarHidden() => SetToolbarHidden(!_toolbarHidden);

        private void SetToolbarHidden(bool hidden)
        {
            _toolbarHidden = hidden;
            ToolbarRowBorder.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            App.SetSetting("ToolbarHidden", hidden ? "1" : "0");
            SyncToolbarMenuChecks();
            SetStatus(Loc(hidden ? "Str_St_ToolbarHidden" : "Str_St_ToolbarShown"));
        }


        // ── Responsive toolbar overflow ───────────────────────────────────
        private bool _reflowingToolbar;
        private bool _reflowQueued;

        // ReflowToolbar decides which toolbar groups collapse into the overflow chevron. It must run
        // live during a window resize (deferring it leaves buttons overlapping mid-drag), so the cost is
        // kept low two ways: SizeChanged is coalesced to at most one reflow per render tick, and the
        // reflow re-measures only the two bars instead of forcing repeated whole-tree UpdateLayout passes.
        private void ToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => QueueReflowToolbar();

        /// <summary>Toolbar width the last completed reflow decided against. Reset to -1 by anything
        /// that changes what the toolbar CONTAINS (appearance mode, language), which is the only
        /// other thing that can change the answer.</summary>
        private double _lastReflowWidth = -1;
        internal void InvalidateToolbarReflow() => _lastReflowWidth = -1;

        private void QueueReflowToolbar()
        {
            if (_reflowQueued) return;
            _reflowQueued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, (Action)(() =>
            {
                _reflowQueued = false;
                // Skip when the width has not actually moved since the last decision. ReflowToolbar
                // re-expands every group and caption at the start of each pass and then sheds to
                // fit; that expand-then-shed is itself a layout change, which raises SizeChanged,
                // which runs it again - at a width sitting on the shed boundary it oscillates
                // forever. Measured at 57 reflows a second with the window pinned and nothing
                // moving, which is what stopped the app ever painting.
                if (ToolbarGrid != null
                    && Math.Abs(ToolbarGrid.ActualWidth - _lastReflowWidth) < 0.5) return;
                ReflowToolbar();
            }));
        }

        // The tab-strip and footer grain fades each allocate a gradient brush, run a TransformToVisual
        // query, and reset an OpacityMask. Several SizeChanged handlers (window, sidebar, doc pane, tab
        // strip) drive them, so during a live resize they fired multiple times per frame - synchronous
        // UI-thread work that widened the WPF frame/content desync and made the whole window thrash.
        // Coalesce every resize-driven fade refresh into a single pass per render tick.
        private bool _fadeRefreshQueued;
        private void ScheduleFadeRefresh()
        {
            if (_fadeRefreshQueued) return;
            _fadeRefreshQueued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, (Action)(() =>
            {
                _fadeRefreshQueued = false;
                UpdateTabStripFade();   // also refreshes the footer fade
            }));
        }

        // Collapses lower-priority button groups into the overflow popup when the toolbar runs
        // out of room, and restores them when there is space again. Keeps the left/right layout.
        private void ReflowToolbar()
        {
            if (_reflowingToolbar || ToolbarGrid is null || LeftBar is null || RightContainer is null) return;
            _reflowingToolbar = true;
            try
            {
                // The order in which buttons move to the More menu as the bar narrows is
                // ToolbarShedOrder (ToolbarLayout.cs); buttons the user put in the More menu stay
                // there at any width. Start fully expanded: every other button on the bar, and only
                // the user's choices in the menu.
                var units = _toolbarUnits.ToDictionary(unit => unit.Id);
                var chosen = _toolbarLayout.More.ToHashSet();
                foreach (ToolbarUnit unit in _toolbarUnits)
                {
                    bool inMenu = chosen.Contains(unit.Id);
                    unit.Bar.Visibility = inMenu ? Visibility.Collapsed : Visibility.Visible;
                    unit.Overflow.Visibility = inMenu ? Visibility.Visible : Visibility.Collapsed;
                }
                SyncToolbarSeparators();
                MeasureToolbarBars();

                double avail = ToolbarGrid.ActualWidth;

                // Text-beside / Text-under: each pass starts with ALL captions on, so widening the
                // window always restores them. Captions are only shed much later, as a last resort.
                bool textCaptions = _labelMode is LabelMode.Beside or LabelMode.Under;
                if (textCaptions && _toolbarButtons.Count > 0)
                {
                    RestoreToolbarCaptions();
                    ToolbarGrid.UpdateLayout();
                }
                MeasureToolbarBars();

                // Keep the ACTIVE tool on the bar no matter how narrow, so its selected state stays visible -
                // otherwise it vanishes into the overflow chevron and there's no way to tell what's active.
                string? activeTool = _currentTool switch
                {
                    EditTool.Text      => "text",
                    EditTool.FormField => "formfield",
                    EditTool.Line      => "line",
                    EditTool.Highlight => "highlight",
                    EditTool.Draw      => "draw",
                    EditTool.Shape     => "shape",
                    EditTool.Image     => "image",
                    EditTool.Crop      => "crop",
                    EditTool.Redact    => "redact",
                    EditTool.Signature => "signature",
                    EditTool.Measure   => "measure",
                    _ => null
                };

                // First defense against a narrow bar (and the long-standing behavior): move whole
                // low-priority groups into the More menu, KEEPING captions on whatever stays. This
                // is what runs at normal widths - captions stay, extras move to the chevron.
                if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width > avail)
                {
                    foreach (string[] group in ToolbarShedOrder)
                    {
                        bool moved = false;
                        foreach (string id in group)
                        {
                            if (id == activeTool || chosen.Contains(id) || !units.TryGetValue(id, out ToolbarUnit? unit)) continue;
                            unit.Bar.Visibility = Visibility.Collapsed;     // pull this button out of the bar
                            unit.Overflow.Visibility = Visibility.Visible;  // ...into the menu
                            moved = true;
                        }
                        if (!moved) continue;
                        OverflowChevron.Visibility = Visibility.Visible;
                        SyncToolbarSeparators();
                        MeasureToolbarBars();
                        if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width <= avail) break;
                    }
                }

                // Last resort, ONLY at the ultra-narrow width where everything collapsible is already
                // in the overflow menu and the remaining captioned buttons still overlap: shed captions
                // to icon-only in priority order (zoom and Select first, annotation tools last). Until
                // this point the toolbar keeps its full captions, exactly as it looked before.
                if (textCaptions && LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width > avail)
                {
                    // The kit sheds in ToolbarCaptionShedRank order (ToolbarStyle.Extras.cs).
                    ShedToolbarCaptions(() =>
                    {
                        ToolbarGrid.UpdateLayout();
                        MeasureToolbarBars();
                        return LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width <= avail;
                    });
                }

                // At large app sizes even the icon-only file controls can exceed the viewport.
                // Keep their existing commands reachable in More without losing split menus.
                foreach (string id in ToolbarLastResortShedOrder)
                {
                    if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width <= avail) break;
                    if (chosen.Contains(id) || id == activeTool || !units.TryGetValue(id, out ToolbarUnit? unit)
                        || unit.Bar.Visibility != Visibility.Visible) continue;
                    unit.Bar.Visibility = Visibility.Collapsed;
                    unit.Overflow.Visibility = Visibility.Visible;
                    OverflowChevron.Visibility = Visibility.Visible;
                    SyncToolbarSeparators();
                    MeasureToolbarBars();
                }

                // Shedding whole groups can leave room for individual tools. Restore every
                // tool that fits, in priority order, while preserving explicit More choices.
                foreach (string id in ToolbarShedOrder.Reverse().SelectMany(group => group))
                {
                    if (chosen.Contains(id) || !units.TryGetValue(id, out ToolbarUnit? unit)
                        || unit.Bar.Visibility == Visibility.Visible) continue;
                    unit.Bar.Visibility = Visibility.Visible;
                    unit.Overflow.Visibility = Visibility.Collapsed;
                    OverflowChevron.Visibility = _toolbarUnits.Any(item => item.Overflow.Visibility == Visibility.Visible)
                        ? Visibility.Visible : Visibility.Collapsed;
                    SyncToolbarSeparators();
                    MeasureToolbarBars();
                    if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width <= avail) continue;
                    unit.Bar.Visibility = Visibility.Collapsed;
                    unit.Overflow.Visibility = Visibility.Visible;
                    OverflowChevron.Visibility = Visibility.Visible;
                    SyncToolbarSeparators();
                    MeasureToolbarBars();
                }

                bool anyCollapsed = _toolbarUnits.Any(unit => unit.Overflow.Visibility == Visibility.Visible);
                OverflowChevron.Visibility = anyCollapsed ? Visibility.Visible : Visibility.Collapsed;
                if (!anyCollapsed) OverflowChevron.IsChecked = false;

                _lastReflowWidth = avail;   // this width is now decided - see QueueReflowToolbar

                // The toolbar does NOT get a say in how narrow the window may be. Deriving a floor
                // from it produced a minimum unrelated to anything the user can see, and a
                // different one in each mode. Reading panes and the footer viewport determine
                // the window minimum through SyncSplitMinWidth.
            }
            finally { _reflowingToolbar = false; }
        }

        private static readonly Size ToolbarMeasureBudget = new(double.PositiveInfinity, double.PositiveInfinity);

        // Re-measures ONLY the two toolbar bars to refresh their DesiredSize, instead of calling
        // ToolbarGrid.UpdateLayout() - which forces a synchronous Measure+Arrange of the ENTIRE visual
        // tree. The reflow only needs each bar's natural width to decide what fits, so a measure-only
        // pass on the bars is enough and far cheaper. This is what lets the reflow run live on every
        // resize frame (no deferral, so no mid-drag button overlap) without thrashing the window.
        // WPF re-arranges the bars with their real constraint on the next normal layout pass.
        private void MeasureToolbarBars()
        {
            LeftBar.InvalidateMeasure();
            RightContainer.InvalidateMeasure();
            LeftBar.Measure(ToolbarMeasureBudget);
            RightContainer.Measure(ToolbarMeasureBudget);
        }

        private void OverflowItem_Click(object sender, RoutedEventArgs e)
        {
            OverflowChevron.IsChecked = false;   // close the flyout after a choice is made
        }
    }
}
