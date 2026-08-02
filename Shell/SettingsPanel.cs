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
        // Picker state sync + appearance handlers. (The Settings panel itself retired 2026-07-31:
        // every section moved to where the thing it configures lives - theme/language/view onto
        // rail flyouts, toolbar onto the bar's right-click menu, sidebar side onto the sidebar's.)
        // ============================================================

        // Syncs every picker's radios and accent dots to live state before a flyout shows -
        // exactly ONE sync implementation, shared by all the rail flyouts (Shell/RailFlyouts.cs).
        private void SyncPickerState()
        {
            var cur = ThemeManager.Current;
            ThemeDarkRadio.IsChecked  = cur == Theme.Dark;
            ThemeLightRadio.IsChecked = cur == Theme.Light;
            ThemeHCRadio.IsChecked    = cur == Theme.Black;
            ThemeBloodRadio.IsChecked = cur == Theme.Blood;
            ThemeGreedRadio.IsChecked    = cur == Theme.Greed;
            ThemeCyanoticRadio.IsChecked = cur == Theme.Cyanotic;
            UpdateAccentDotSelection();
            UpdateAccentRowsVisibility(animate: false);
            // Sync language picker
            var curLoc = KillerPDF.Services.LocaleManager.Current;
            LangEnRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.EnUS;
            LangCsRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.CsCZ;
            LangEsRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Es;
            LangFrRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Fr;
            LangZhTWRadio.IsChecked = curLoc == KillerPDF.Services.Locale.ZhTW;
            LangZhCNRadio.IsChecked = curLoc == KillerPDF.Services.Locale.ZhCN;
            LangBnRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Bn;
            LangTrRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.TrTR;
            LangDeRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.De;
            LangJaRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.JaJP;
            // Sync view mode radios. Against the PENDING mode while a fade-wrapped switch is in
            // flight (_viewMode lags until the fade-out lands), so wheel-cycling with the flyout
            // open moves the checkmark in step instead of one notch behind.
            var vm = _pendingViewMode ?? _viewMode;
            ViewSingleRadio.IsChecked     = vm == ViewMode.Single;
            ViewContinuousRadio.IsChecked = vm == ViewMode.Continuous;
            ViewTwoPageRadio.IsChecked    = vm == ViewMode.TwoPage;
            ViewGridRadio.IsChecked       = vm == ViewMode.Grid;
            // (The toolbar picker lives on the toolbar's own right-click menu, and the sidebar
            // side on the sidebar's - both build their checks fresh on open, nothing to sync here.)
        }

        // ── Quick fade in/out for the full-window overlay panels (Shortcuts/About) ──
        private static void FadeOverlayIn(UIElement el)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            el.Visibility = Visibility.Visible;
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(110)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        private static void FadeOverlayOut(UIElement el)
        {
            if (el.Visibility != Visibility.Visible) return;
            var anim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(90)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) =>
            {
                el.Visibility = Visibility.Collapsed;
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 1;
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
                (bar.Parent as Panel)?.Children.Remove(bar);
            };
            bar.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // Collapses the visible annotate bar to a thin peek strip, or expands it back. Triggered by
        // re-clicking the already-active tool, so a second click tucks the bar away instead of the
        // old behaviour of rebuilding it (which flickered).
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
                    if (_annotBarContent is not null) _annotBarContent.Visibility = Visibility.Collapsed;
                    if (_annotBarDots is not null) _annotBarDots.Visibility = Visibility.Visible;
                    bar.ClipToBounds = false;   // content is hidden now, nothing to clip
                };
                bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
            }
            else
            {
                // Show the full content again before growing back, and let the width track content again.
                bar.Width = double.NaN;
                bar.Effect = AnnotBarShadow();   // restore the drop shadow on the expanded bar
                if (_annotBarContent is not null) _annotBarContent.Visibility = Visibility.Visible;
                if (_annotBarDots is not null) _annotBarDots.Visibility = Visibility.Collapsed;
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
            if (BitmapHelpers.DocInvert == on) return;
            BitmapHelpers.DocInvert = on;
            App.SetSetting("DocInvert", on ? "1" : "0");
            DocInvertBtn.Tag = on ? "on" : null;   // lights the rail icon in the accent while active
            RepaintForInvertChange();
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
                InputGestureText = "Shift+N",   // right-aligned in the family MenuItem template
            };
            mi.Click += (_, _2) => ToggleInvertImages(!BitmapHelpers.DocInvertImages);
            menu.Items.Add(mi);
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void ToggleInvertImages(bool on)
        {
            if (BitmapHelpers.DocInvertImages == on) return;
            BitmapHelpers.DocInvertImages = on;
            App.SetSetting("DocInvertImages", on ? "1" : "0");
            // Only repaint when night mode is actually showing; otherwise it just takes effect
            // the next time the moon is toggled on.
            if (BitmapHelpers.DocInvert) RepaintForInvertChange();
        }

        // Shared by the moon toggle and its right-click option: the invert state is baked into
        // rendered pixels, so flush the render caches and repaint IN PLACE - never through
        // ApplyViewMode (see the comment above ToggleDocInvert).
        private void RepaintForInvertChange()
        {
            FlushAllRenderCaches();

            // The invert flag is GLOBAL, but everything below this block runs through the shared
            // fields and so repaints only the FOCUSED pane - the other pane's already-painted
            // tiles kept their old colors until any scroll or focus change forced a re-render,
            // which read as "one pane is inverted and scrolling the other pane inverts it"
            // (Steve, 2026-08-01). Re-render it with its own session swapped in - the same
            // WithOwnSession idiom the cross-pane tab drag uses. BEFORE the _doc guard: the
            // focused pane being empty must not strand the other pane's stale pixels.
            if (IsSplit)
            {
                var other = ReferenceEquals(ActiveViewer, Viewer) ? ViewerB : Viewer;
                other.WithOwnSession(other.RenderActiveSessionExt);
            }

            if (_doc is null) return;
            if (_viewMode == ViewMode.Continuous)
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
                RenderPage(_viewMode == ViewMode.Grid ? 0 : Math.Max(0, PageList.SelectedIndex),
                           keepTiles: true);
            }
        }

        private void DocInvertBtn_Click(object sender, RoutedEventArgs e)
            => ToggleDocInvert(!BitmapHelpers.DocInvert);

        private void OnThemeChanged()
        {
            // Refresh snapshot FindResource calls that were set as local values.
            // SetResourceReference bindings update automatically; sidebar tabs and
            // active tool button background still need an explicit refresh.
            SetTool(_currentTool);
            if (_sidebarShowingOutlines)
                SwitchSidebarToOutlinesTab();
            else
                SwitchSidebarToPagesTab();
            RefreshSelectionAccent();
            RebuildTabStrip();   // tab divider bevel is derived from BgCanvas; refresh for the new theme
            // The signature popup is built from snapshot (FindResource) colors, so rebuild it in place
            // if it's open so it picks up the new theme without the user having to close and reopen it.
            if (_signaturePopup is not null) ShowSignaturePopup();

            // The crop bar's buttons snapshot accent colors (UiKit), so rebuild it in the new theme.
            RebuildCropBarForLocale();
        }

        private void ThemeDarkRadio_Checked(object sender, RoutedEventArgs e)     => SelectTheme(Theme.Dark);
        private void ThemeLightRadio_Checked(object sender, RoutedEventArgs e)    => SelectTheme(Theme.Light);
        private void ThemeHCRadio_Checked(object sender, RoutedEventArgs e)       => SelectTheme(Theme.Black);
        private void ThemeBloodRadio_Checked(object sender, RoutedEventArgs e)    => SelectTheme(Theme.Blood);
        private void ThemeGreedRadio_Checked(object sender, RoutedEventArgs e)    => SelectTheme(Theme.Greed);
        private void ThemeCyanoticRadio_Checked(object sender, RoutedEventArgs e) => SelectTheme(Theme.Cyanotic);

        private void SelectTheme(Theme theme)
        {
            bool wasOpen = ThemeFlyout is not null && ThemeFlyout.IsOpen;
            ThemeManager.Apply(theme);
            UpdateAccentDotSelection();
            UpdateAccentRowsVisibility(animate: true);
            // Intentionally leave the flyout open so the user can try another theme right away
            // without reopening the submenu. The theme swap's side effects (tab strip rebuild,
            // tool re-select) can still knock the popup closed behind our back, so check once
            // layout has settled and quietly reopen it in place if that happened.
            if (wasOpen)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() => { if (ThemeFlyout is not null && !ThemeFlyout.IsOpen) ThemeFlyout.IsOpen = true; }));
        }

        // Each theme family has its own picker row beneath its radio. Clicking a swatch sets that
        // family's accent (independently remembered). Switching themes animates the rows' heights so
        // the picker slides to the selected theme while the total menu height stays fixed.
        private void AccentDot_Click(object sender, MouseButtonEventArgs e)      => HandleAccentDot(sender, Theme.Dark);
        private void AccentDotLight_Click(object sender, MouseButtonEventArgs e) => HandleAccentDot(sender, Theme.Light);
        private void AccentDotBlack_Click(object sender, MouseButtonEventArgs e) => HandleAccentDot(sender, Theme.Black);

        private void HandleAccentDot(object sender, Theme family)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
            if (!Enum.TryParse<DarkAccent>(tag, out var accent)) return;
            ThemeManager.ApplyAccent(family, accent);   // persists for that family; reapplies if active
            UpdateAccentDotSelection();
        }

        // Ring each family's own selected swatch (Dark, Light, and Black remember independently).
        private void UpdateAccentDotSelection()
        {
            if (DarkAccentRow is null) return;
            var ring = (System.Windows.Media.Brush)FindResource("TextBrush");
            void RingRow(Border[] dots, DarkAccent chosen)
            {
                foreach (var dot in dots)
                {
                    bool sel = dot.Tag is string t && Enum.TryParse<DarkAccent>(t, out var a) && a == chosen;
                    dot.BorderBrush = sel ? ring : System.Windows.Media.Brushes.Transparent;
                }
            }
            RingRow([AccentDotRed, AccentDotOrange, AccentDotGreen, AccentDotTeal, AccentDotBlue, AccentDotPurple], ThemeManager.DarkAccentChoice);
            RingRow([AccentDotLightRed, AccentDotLightOrange, AccentDotLightGreen, AccentDotLightTeal, AccentDotLightBlue, AccentDotLightPurple], ThemeManager.LightAccentChoice);
            RingRow([AccentDotBlackRed, AccentDotBlackOrange, AccentDotBlackGreen, AccentDotBlackTeal, AccentDotBlackBlue, AccentDotBlackPurple], ThemeManager.BlackAccentChoice);
        }

        // Slide the picker to the active theme. Each row animates its height; because the outgoing row
        // shrinks by the same amount the incoming one grows, the combined height is constant - so the
        // menu doesn't change height, the picker just slides into place under the selected theme.
        private void UpdateAccentRowsVisibility(bool animate)
        {
            var cur = ThemeManager.Current;
            SlideRow(DarkAccentRow,  cur == Theme.Dark,         animate);
            SlideRow(LightAccentRow, cur == Theme.Light,        animate);
            SlideRow(BlackAccentRow, cur == Theme.Black, animate);
        }

        private const double AccentRowHeight = 26;   // 18px swatch + 8px breathing room
        // Expand and collapse MUST share one duration (and stay linear): when switching between neutral themes
        // one row opens while another closes, and equal linear durations keep their heights summing to a
        // constant, so the panel height never dips/jumps mid-animation.
        private const double AccentRowSlideMs = 160;

        // Slides the picker row open/closed by animating its Height. Each call clears any in-flight
        // height animation first so rapid theme clicking can't leave a held animation that strands
        // the wrong row visible under the wrong heading.
        private static void SlideRow(FrameworkElement? row, bool show, bool animate)
        {
            if (row is null) return;
            row.BeginAnimation(HeightProperty, null);   // drop any leftover/held animation
            if (show)
            {
                row.Visibility = Visibility.Visible;
                if (animate)
                {
                    row.Height = 0;
                    row.BeginAnimation(HeightProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(0, AccentRowHeight, TimeSpan.FromMilliseconds(AccentRowSlideMs)));
                }
                else row.Height = AccentRowHeight;
            }
            else if (animate && row.Visibility == Visibility.Visible && row.ActualHeight > 0.5)
            {
                var h = new System.Windows.Media.Animation.DoubleAnimation(AccentRowHeight, 0, TimeSpan.FromMilliseconds(AccentRowSlideMs));
                h.Completed += (_, __) => { row.BeginAnimation(HeightProperty, null); row.Height = 0; row.Visibility = Visibility.Collapsed; };
                row.BeginAnimation(HeightProperty, h);
            }
            else
            {
                row.Height = 0;
                row.Visibility = Visibility.Collapsed;
            }
        }

        // Localized display name for each theme, shown on the picker row.
        private string ThemeDisplayName(Theme t) => t switch
        {
            Theme.Light        => Loc("Str_Theme_Light"),
            Theme.Black        => Loc("Str_Theme_Black"),
            Theme.Blood        => Loc("Str_Theme_Blood"),
            Theme.Greed        => Loc("Str_Theme_Greed"),
            Theme.Cyanotic     => Loc("Str_Theme_Cyanotic"),
            _                  => Loc("Str_Theme_Dark"),
        };

        private void LangEnRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.EnUS);
        private void LangCsRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.CsCZ);
        private void LangEsRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.Es);
        private void LangFrRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.Fr);
        private void LangZhTWRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(KillerPDF.Services.Locale.ZhTW);
        private void LangZhCNRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(KillerPDF.Services.Locale.ZhCN);
        private void LangBnRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.Bn);
        private void LangTrRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.TrTR);
        private void LangDeRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.De);
        private void LangJaRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.JaJP);

        private void SelectLocale(KillerPDF.Services.Locale loc)
        {
            KillerPDF.Services.LocaleManager.Apply(loc);
            ApplyToolNumberTooltips();   // re-append the numbers to the now-localized tool tooltips
            BuildToolbarMenu();   // the toolbar right-click picker's items carry Loc() captions
            LangFlyout.IsOpen = false;   // a pick closes the rail flyout, like the accordion used to collapse

            // The status bar text is a formatted string (not a DynamicResource), so it keeps the
            // language it was last set in. Re-set it in the new locale instead of leaving it stale.
            if (_doc is not null && PageList.SelectedIndex >= 0)
                SetStatus(string.Format(Loc("Str_PageOf"), PageList.SelectedIndex + 1, _doc.PageCount));
            else
                SetStatus(Loc("Str_Ready"));

            // The canvas right-click menu is built once with Loc() values captured at build time,
            // so rebuild it in the new language. (The sidebar menu is rebuilt on each open.)
            BuildContextMenu();

            // Toolbar captions are built with Loc() at apply time (they don't auto-update like a
            // DynamicResource), so rebuild the toolbar on every language change. Harmless for the
            // icon-only modes; refreshes the captions for Text-beside / Text-under / Text-only.
            ApplyToolbarAppearance();

            // The annotate bars (text / draw) also capture Loc() values when built, so rebuild whichever
            // one is currently showing in the new language.
            if (_annotBarTool == EditTool.Text)
                ShowTextSettings();
            else if (_annotBarTool is EditTool bt &&
                     bt is EditTool.Draw or EditTool.Highlight or EditTool.Line
                        or EditTool.Strikethrough or EditTool.Underline)
                ShowDrawSettings(bt);

            // The crop bar is built once with Loc() snapshots; rebuild it in the new language if it's showing.
            RebuildCropBarForLocale();

            // Page thumbnails and outline tooltips snapshot Loc() strings when built; rebuild both
            // lists so their "Page N" labels switch to the new language immediately.
            RefreshPageList();
            RefreshOutlines();

            // A visible signature popup is built with Loc() too; rebuild it so its section headers and
            // pen labels switch immediately.
            RefreshSignaturePopupLanguage();

            // The fit-mode terms differ in length per language; resize the zoom box so the longest never clips.
            AdjustZoomBoxWidth();
        }

        // Size the editable zoom ComboBox to its widest item in the CURRENT language, so localized fit-mode
        // terms (e.g. French "Ajuster a la largeur") are never clipped. Re-run on locale change and at load.
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
                // measured text + editable text-box left margin (5) + chevron column (18) + borders (2),
                // plus a 3px safety margin so the longest term never clips.
                ZoomBox.Width = System.Math.Ceiling(max) + 28;
            }
            catch { /* best-effort; leave the XAML default width */ }
        }

        // Native name (autonym) for each language, shown in the picker regardless of UI locale.
        private static string LangDisplayName(KillerPDF.Services.Locale loc) => loc switch
        {
            KillerPDF.Services.Locale.CsCZ => "Čeština",
            KillerPDF.Services.Locale.Es   => "Español",
            KillerPDF.Services.Locale.Fr   => "Français",
            KillerPDF.Services.Locale.ZhTW => "中文 (繁體)",
            KillerPDF.Services.Locale.ZhCN => "中文 (简体)",
            KillerPDF.Services.Locale.Bn   => "বাংলা",
            KillerPDF.Services.Locale.TrTR => "Türkçe",
            KillerPDF.Services.Locale.De   => "Deutsch",
            KillerPDF.Services.Locale.JaJP => "日本語",
            _                              => "English",
        };

        private void ViewContinuousRadio_Checked(object sender, RoutedEventArgs e) => SelectViewMode(ViewMode.Continuous);
        private void ViewSingleRadio_Checked(object sender, RoutedEventArgs e)     => SelectViewMode(ViewMode.Single);
        private void ViewTwoPageRadio_Checked(object sender, RoutedEventArgs e)    => SelectViewMode(ViewMode.TwoPage);
        private void ViewGridRadio_Checked(object sender, RoutedEventArgs e)       => SelectViewMode(ViewMode.Grid);

        // ── Toolbar appearance (right-click picker on the bar) ────────────
        // Hover tooltips stay on in every mode, so the text modes are about preference, not
        // discoverability.
        // TWO AXES, NOT ONE (family standard, Steve 2026-07-30; KillerUI/Shell/ToolbarStyle.cs is
        // the reference). The old five-way ToolbarStyle could not express "large icons WITH text" -
        // icon size and text placement were never one axis, they only looked like one. The old
        // enum survives solely so an existing install's saved setting migrates (InitToolbarStyle).
        private enum ToolbarStyle { SmallIcons, LargeIcons, TextBeside, TextUnder, TextOnly }
        private enum ToolbarIconSize { Small, Large }
        private enum ToolbarLabelMode { None, Beside, Under, Only }
        // Large icons with the text underneath is the family default for new installs; migration
        // keeps whatever an existing install was on.
        private ToolbarIconSize  _toolbarIconSize  = ToolbarIconSize.Large;
        private ToolbarLabelMode _toolbarLabelMode = ToolbarLabelMode.Under;

        // Each toolbar icon button paired with its glyph and label-resource key, built once so the
        // appearance can be rebuilt without re-walking the tree.
        private readonly List<(Button btn, string glyph, string labelKey)> _toolbarButtons = [];

        // Maps each toolbar glyph (Segoe MDL2 Assets code point) to its caption string key. Buttons
        // whose glyph isn't listed keep their icon with no caption.
        private static readonly Dictionary<string, string> _toolbarLabelKeys = new()
        {
            [""] = "Str_Lbl_New",
            [""] = "Str_Lbl_Open",
            [""] = "Str_Lbl_Close",
            [""] = "Str_Lbl_Save",
            [""] = "Str_Lbl_Flatten",
            [""] = "Str_Lbl_Ocr",
            [""] = "Str_Lbl_Print",
            [""] = "Str_Lbl_Merge",
            [""] = "Str_Lbl_Extract",
            [""] = "Str_Lbl_Delete",
            [""] = "Str_Lbl_MoveUp",
            [""] = "Str_Lbl_MoveDown",
            [""] = "Str_Lbl_Select",
            [""] = "Str_Lbl_Text",
            [""] = "Str_Lbl_Highlight",
            [""] = "Str_Lbl_Strike",
            [""] = "Str_Lbl_Underline",
            [""] = "Str_Lbl_Draw",
            [""] = "Str_Lbl_Crop",
            [""] = "Str_Lbl_Rotate",
            [""] = "Str_Lbl_Image",
            [""] = "Str_Lbl_Signature",
            [""] = "Str_Lbl_Undo",
            [""] = "Str_Lbl_Clear",
            [""] = "Str_Lbl_ZoomOut",
            [""] = "Str_Lbl_ZoomIn",
            [""] = "Str_Lbl_Highlight",   // current highlighter glyph (see ToolHighlightBtn)
            [""] = "Str_Lbl_Line",   // repurposed ToolUnderlineBtn glyph = the Line tool
            [""] = "Str_Lbl_ZoomOut",   // boxed minus (RemoveFrom) - new zoom-out glyph
            [""] = "Str_Lbl_ZoomIn",    // boxed plus  (AddTo)      - new zoom-in glyph
            [""] = "Str_Lbl_Search",    // magnifier - toolbar search button
            [""] = "Str_Lbl_Stamp",     // page-number / watermark stamp tool
            [""] = "Str_Lbl_Shape",   // Shapes tool (rect / ellipse / polygon)
        };

        // Walks LeftBar + RightBar once and records each icon button with its glyph + label key.
        private void IndexToolbarButtons()
        {
            _toolbarButtons.Clear();
            foreach (Panel? bar in new Panel?[] { LeftBar, RightBar })
            {
                if (bar is null) continue;
                foreach (var btn in DescendantButtons(bar))
                    if (btn.Content is string g && g.Length > 0 && _toolbarLabelKeys.TryGetValue(g, out var key))
                        _toolbarButtons.Add((btn, g, key));
            }
        }

        private static IEnumerable<Button> DescendantButtons(DependencyObject root)
        {
            foreach (var obj in LogicalTreeHelper.GetChildren(root))
            {
                if (obj is Button b) yield return b;
                if (obj is DependencyObject d)
                    foreach (var nested in DescendantButtons(d)) yield return nested;
            }
        }

        // Rebuilds one toolbar button's content and size for the current mode. withLabel=false forces
        // icon-only (used when Text-beside has to shed captions to fit a narrow window). Deliberately
        // never touches Foreground/Background, so theme accents, the dirty-save tint, and the
        // active-tool highlight survive (the caption TextBlocks inherit the button's foreground and
        // the template's drop shadow).
        private void SetToolbarButton(Button btn, string glyph, string key, bool withLabel)
        {
            bool large = _toolbarIconSize == ToolbarIconSize.Large;
            bool beside = _toolbarLabelMode == ToolbarLabelMode.Beside;
            bool under = _toolbarLabelMode == ToolbarLabelMode.Under;
            bool textOnly = _toolbarLabelMode == ToolbarLabelMode.Only;
            // The size axis ALONE decides the glyph - the old engine let the text mode force
            // 16/20, which is exactly the coupling the two-axis split removes.
            double glyphSize = large ? 20 : 14;
            btn.FontSize = glyphSize;

            // Text only: caption, no icon (nothing to shed - there'd be nothing left).
            if (textOnly)
            {
                btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = 34; btn.Padding = new Thickness(8, 5, 8, 5);
                btn.Content = new TextBlock
                {
                    Text = Loc(key),
                    FontFamily = UiKit.UiFont,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }

            // Text beside the icon, while it still fits.
            if (beside && withLabel)
            {
                btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = 34; btn.Padding = new Thickness(8, 5, 8, 5);
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontFamily = UiKit.IconFont,
                    FontSize = glyphSize,
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = Loc(key),
                    FontFamily = UiKit.UiFont,
                    FontSize = 12,
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                btn.Content = row;
                return;
            }

            // Text under the icon: the icon stacked over a small caption, while it still fits.
            if (under && withLabel)
            {
                btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = large ? 56 : 52; btn.Padding = new Thickness(6, 4, 6, 4);
                var col = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
                col.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontFamily = UiKit.IconFont,
                    FontSize = glyphSize,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                col.Children.Add(new TextBlock
                {
                    Text = Loc(key),
                    FontFamily = UiKit.UiFont,
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                });
                btn.Content = col;
                return;
            }

            // Icon only: the None mode, or Beside/Under after a caption was shed. The box is the
            // size axis's alone (family numbers).
            btn.Width = large ? 46 : 36;
            btn.MinWidth = 0;
            btn.Height = large ? 42 : 32;
            btn.Padding = new Thickness(10, 6, 10, 6);
            btn.Content = glyph;
        }

        // Order in which Text-beside buttons shed their captions when the bar runs short of room:
        // lowest rank sheds first. Zoom and Select go first (their icons are obvious); the annotation
        // tools keep their captions longest because that is where the labels earn their space.
        private static int LabelStripRank(string key) => key switch
        {
            "Str_Lbl_ZoomOut" or "Str_Lbl_ZoomIn" => 0,
            "Str_Lbl_Select" => 1,
            "Str_Lbl_Undo" or "Str_Lbl_Clear" => 2,
            "Str_Lbl_New" or "Str_Lbl_Open" or "Str_Lbl_Close"
                or "Str_Lbl_Save" or "Str_Lbl_Flatten" or "Str_Lbl_Ocr" or "Str_Lbl_Print" => 3,
            "Str_Lbl_MoveUp" or "Str_Lbl_MoveDown" or "Str_Lbl_Delete"
                or "Str_Lbl_Merge" or "Str_Lbl_Extract" => 4,
            _ => 5,   // annotation tools keep their labels longest
        };

        // Rebuilds every toolbar button for the current mode (captions on where applicable), then
        // lets ReflowToolbar shed captions and/or collapse groups to fit the current width.
        private void ApplyToolbarAppearance()
        {
            if (_toolbarButtons.Count == 0) return;
            foreach (var (btn, glyph, key) in _toolbarButtons)
                SetToolbarButton(btn, glyph, key, withLabel: true);
            // Open / Save / OCR are split buttons (main half + overlapping dropdown chevron). Their chrome
            // is applied from one place (ApplySplitButtonChrome) so the three never drift apart again.
            bool textMode = _toolbarLabelMode != ToolbarLabelMode.None;
            ApplySplitButtonChrome(textMode);
            SyncToolbarMenuChecks();
            InvalidateToolbarReflow();   // the buttons themselves changed, so the last decision is void
            ReflowToolbar();
        }

        // Single source of truth for the split-button chrome shared by Open, Save, and OCR. Each entry is
        // (main half, dropdown chevron, split style, plain style). In icon modes the chevron overlaps the
        // main half (-6) for the connected split look; with a caption the button widens, so the chevron sits
        // clear of the text (margin 1) and the main half drops its split (hover-inset) style.
        private void ApplySplitButtonChrome(bool textMode)
        {
            var chevMargin = textMode ? new Thickness(1, 0, 0, 0) : new Thickness(-6, 0, 0, 0);
            var splits = new (Button? Main, Button? Chevron, string Split, string Plain)[]
            {
                (OpenFileBtn, OpenRecentBtn, "ToolbarSplitMain",       "ToolbarButton"),
                (SaveAsBtn,   SaveMenuBtn,   "ToolbarSplitMainAccent", "ToolbarButtonAccent"),
                (OcrBtn,      OcrMenuBtn,    "ToolbarSplitMain",       "ToolbarButton"),
            };
            foreach (var s in splits)
            {
                if (s.Chevron is not null) s.Chevron.Margin = chevMargin;
                if (s.Main is not null) s.Main.Style = (Style)FindResource(textMode ? s.Plain : s.Split);
            }
        }

        /// <summary>Restores the saved axes, migrating the retired five-way "ToolbarStyle" key so
        /// an existing install keeps the bar it was left on instead of silently resetting. Only
        /// falls back to migration when the new keys are absent. Called once from the ctor.</summary>
        private void InitToolbarStyle()
        {
            bool haveNew = false;
            if (Enum.TryParse<ToolbarIconSize>(App.GetSetting("ToolbarIconSize"), out var s)) { _toolbarIconSize = s; haveNew = true; }
            if (Enum.TryParse<ToolbarLabelMode>(App.GetSetting("ToolbarLabels"), out var l)) { _toolbarLabelMode = l; haveNew = true; }
            if (!haveNew && Enum.TryParse<ToolbarStyle>(App.GetSetting("ToolbarStyle"), out var old))
            {
                switch (old)
                {
                    case ToolbarStyle.SmallIcons: _toolbarIconSize = ToolbarIconSize.Small; _toolbarLabelMode = ToolbarLabelMode.None; break;
                    case ToolbarStyle.LargeIcons: _toolbarIconSize = ToolbarIconSize.Large; _toolbarLabelMode = ToolbarLabelMode.None; break;
                    // The old text modes never said what size the icon was, so they keep the default size.
                    case ToolbarStyle.TextBeside: _toolbarLabelMode = ToolbarLabelMode.Beside; break;
                    case ToolbarStyle.TextUnder:  _toolbarLabelMode = ToolbarLabelMode.Under;  break;
                    case ToolbarStyle.TextOnly:   _toolbarLabelMode = ToolbarLabelMode.Only;   break;
                }
            }
        }

        private void SetToolbarIconSize(ToolbarIconSize size)
        {
            _toolbarIconSize = size;
            App.SetSetting("ToolbarIconSize", size.ToString());
            ApplyToolbarAppearance();
        }

        private void SetToolbarLabelMode(ToolbarLabelMode mode)
        {
            _toolbarLabelMode = mode;
            App.SetSetting("ToolbarLabels", mode.ToString());
            ApplyToolbarAppearance();
        }

        /// <summary>
        /// The right-click picker on the toolbar: TWO radio groups, separated - icon size, then
        /// where the text goes. One flat list of five could not express "large icons WITH text",
        /// which is the whole reason for the split. Items stay open on click so modes can be
        /// compared without reopening (the behavior the old Settings flyout had).
        /// </summary>
        private void BuildToolbarMenu()
        {
            ToolbarMenu.Items.Clear();
            ToolbarMenu.Items.Add(new MenuItem { Header = Loc("Str_Toolbar_Header"), IsEnabled = false });
            ToolbarMenu.Items.Add(new Separator());

            foreach (var (size, key, gesture) in new[]
                     { (ToolbarIconSize.Small, "Str_Toolbar_SmallIcons", "Ctrl+Shift+1"),
                       (ToolbarIconSize.Large, "Str_Toolbar_LargeIcons", "Ctrl+Shift+2") })
            {
                var mi = new MenuItem { Header = Loc(key), Tag = size, IsCheckable = true,
                                        IsChecked = size == _toolbarIconSize, StaysOpenOnClick = true,
                                        InputGestureText = gesture };
                var v = size;
                // Checking one unchecks the rest through SyncToolbarMenuChecks, so this behaves as
                // a radio group without needing RadioButton plumbing inside a menu.
                mi.Click += (_, _2) => SetToolbarIconSize(v);
                ToolbarMenu.Items.Add(mi);
            }

            ToolbarMenu.Items.Add(new Separator());

            foreach (var (mode, key, gesture) in new[]
                     { (ToolbarLabelMode.None,   "Str_Toolbar_TextNone",   "Ctrl+Shift+3"),
                       (ToolbarLabelMode.Beside, "Str_Toolbar_TextBeside", "Ctrl+Shift+4"),
                       (ToolbarLabelMode.Under,  "Str_Toolbar_TextUnder",  "Ctrl+Shift+5"),
                       (ToolbarLabelMode.Only,   "Str_Toolbar_TextOnly",   "Ctrl+Shift+6") })
            {
                var mi = new MenuItem { Header = Loc(key), Tag = mode, IsCheckable = true,
                                        IsChecked = mode == _toolbarLabelMode, StaysOpenOnClick = true,
                                        InputGestureText = gesture };
                var v = mode;
                mi.Click += (_, _2) => SetToolbarLabelMode(v);
                ToolbarMenu.Items.Add(mi);
            }
        }

        private void SyncToolbarMenuChecks()
        {
            if (ToolbarMenu is null) return;
            foreach (var item in ToolbarMenu.Items)
            {
                if (item is not MenuItem mi) continue;
                if (mi.Tag is ToolbarIconSize sz)
                {
                    mi.IsChecked = sz == _toolbarIconSize;
                    // Text-only has no icon to size. Grey the choice rather than hiding it, so the
                    // setting stays visible and comes back when text moves off Only.
                    mi.IsEnabled = _toolbarLabelMode != ToolbarLabelMode.Only;
                }
                else if (mi.Tag is ToolbarLabelMode lb) mi.IsChecked = lb == _toolbarLabelMode;
            }
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
                // Order in which buttons move to the overflow menu as the bar narrows: FIRST entry
                // goes first. Lowest-value / most-redundant first - page move/delete and merge/extract
                // (all reachable from the sidebar right-click), then signature/image/crop, then
                // undo-clear, with the text-markup tools (draw, strike, underline, highlight, text)
                // kept on the bar the longest. Zoom, Select, and the file basics never collapse here;
                // they only shed their captions later (see LabelStripRank). Edit this list to retune.
                var order = new (UIElement bar, UIElement[] items)[]
                {
                    (GrpPageEdit,       new UIElement[] { MiDelete, MiMoveUp, MiMoveDown }),
                    (GrpPageOps,        new UIElement[] { MiMerge, MiExtract }),
                    // Stamp goes before signature, image and the markup tools: it is the most
                    // occasional of them, and inserting an image or typing text is reached for far
                    // more often (Steve, 2026-08-01).
                    (ToolStampBtn,      new UIElement[] { MiStamp }),
                    (GrpSignature,      new UIElement[] { MiSignature }),
                    (ToolImageBtn,      new UIElement[] { MiImage }),
                    (ToolCropBtn,       new UIElement[] { MiCrop }),
                    (GrpUndo,           new UIElement[] { MiUndo, MiClear }),
                    (ToolShapeBtn,      new UIElement[] { MiShape }),
                    (ToolDrawBtn,       new UIElement[] { MiDraw }),
                    (ToolUnderlineBtn,  new UIElement[] { MiUnderline }),   // now the Line tool
                    (ToolHighlightBtn,  new UIElement[] { MiHighlight }),
                    (ToolTextBtn,       new UIElement[] { MiText }),
                };

                // Start fully expanded (everything in the bar, nothing in the popup).
                foreach (var (grp, items) in order)
                {
                    grp.Visibility = Visibility.Visible;
                    foreach (var it in items) it.Visibility = Visibility.Collapsed;
                }
                MeasureToolbarBars();

                double avail = ToolbarGrid.ActualWidth;

                // Text-beside / Text-under: each pass starts with ALL captions on, so widening the
                // window always restores them. Captions are only shed much later, as a last resort.
                bool textCaptions = _toolbarLabelMode is ToolbarLabelMode.Beside or ToolbarLabelMode.Under;
                if (textCaptions && _toolbarButtons.Count > 0)
                {
                    foreach (var (btn, glyph, key) in _toolbarButtons)
                        SetToolbarButton(btn, glyph, key, withLabel: true);
                    ToolbarGrid.UpdateLayout();
                }

                // Keep the ACTIVE tool on the bar no matter how narrow, so its selected state stays visible -
                // otherwise it vanishes into the overflow chevron and there's no way to tell what's active.
                UIElement? activeToolBar = _currentTool switch
                {
                    EditTool.Text      => ToolTextBtn,
                    EditTool.Line      => ToolUnderlineBtn,   // repurposed to the Line tool
                    EditTool.Highlight => ToolHighlightBtn,
                    EditTool.Draw      => ToolDrawBtn,
                    EditTool.Shape     => ToolShapeBtn,
                    EditTool.Image     => ToolImageBtn,
                    EditTool.Crop      => ToolCropBtn,
                    EditTool.Signature => GrpSignature,
                    _ => null
                };

                // First defence against a narrow bar (and the long-standing behavior): collapse whole
                // low-priority groups into the overflow menu, KEEPING captions on whatever stays. This
                // is what runs at normal widths - captions stay, extras move to the chevron.
                if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width > avail)
                {
                    foreach (var (grp, items) in order)
                    {
                        if (ReferenceEquals(grp, activeToolBar)) continue;   // never collapse the active tool
                        grp.Visibility = Visibility.Collapsed;          // pull this group out of the bar
                        foreach (var it in items) it.Visibility = Visibility.Visible;  // ...into the popup
                        MeasureToolbarBars();
                        if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width + 30 <= avail) break;
                    }
                }

                // Last resort, ONLY at the ultra-narrow width where everything collapsible is already
                // in the overflow menu and the remaining captioned buttons still overlap: shed captions
                // to icon-only in priority order (zoom and Select first, annotation tools last). Until
                // this point the toolbar keeps its full captions, exactly as it looked before.
                if (textCaptions && LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width > avail)
                {
                    foreach (var (btn, glyph, key) in _toolbarButtons.OrderBy(x => LabelStripRank(x.labelKey)))
                    {
                        if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width <= avail) break;
                        if (!btn.IsVisible) continue;   // already collapsed into the overflow menu
                        SetToolbarButton(btn, glyph, key, withLabel: false);
                        ToolbarGrid.UpdateLayout();
                    }
                }

                bool anyCollapsed = order.Any(o => o.bar.Visibility != Visibility.Visible);
                OverflowChevron.Visibility = anyCollapsed ? Visibility.Visible : Visibility.Collapsed;
                if (!anyCollapsed) OverflowChevron.IsChecked = false;

                _lastReflowWidth = avail;   // this width is now decided - see QueueReflowToolbar

                // The toolbar does NOT get a say in how narrow the window may be. Deriving a floor
                // from it produced a minimum unrelated to anything the user can see, and a
                // different one in each mode. The only floor is the pane minimum (SyncSplitMinWidth).
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
