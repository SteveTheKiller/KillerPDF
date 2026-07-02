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
        // Settings panel
        // ============================================================

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Toggle: clicking the gear while the panel is open closes it.
            if (SettingsOverlay.Visibility == Visibility.Visible) { SlideSettingsClosed(); return; }
            // Sync radio buttons to current theme before showing
            var cur = ThemeManager.Current;
            ThemeDarkRadio.IsChecked  = cur == Theme.Dark;
            ThemeLightRadio.IsChecked = cur == Theme.Light;
            ThemeHCRadio.IsChecked    = cur == Theme.Black;
            ThemeBloodRadio.IsChecked = cur == Theme.Blood;
            ThemeGreedRadio.IsChecked    = cur == Theme.Greed;
            ThemeCyanoticRadio.IsChecked = cur == Theme.Cyanotic;
            ThemeCurrentLabel.Text       = ThemeDisplayName(cur);
            UpdateAccentDotSelection();
            UpdateAccentRowsVisibility(animate: false);
            // Sync language picker
            var curLoc = KillerPDF.Services.LocaleManager.Current;
            LangEnRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.EnUS;
            LangEsRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Es;
            LangFrRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Fr;
            LangZhTWRadio.IsChecked = curLoc == KillerPDF.Services.Locale.ZhTW;
            LangZhCNRadio.IsChecked = curLoc == KillerPDF.Services.Locale.ZhCN;
            LangBnRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Bn;
            LangTrRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.TrTR;
            LangDeRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.De;
            LangCurrentLabel.Text   = LangDisplayName(curLoc);
            // Sync view mode radios
            ViewSingleRadio.IsChecked     = _viewMode == ViewMode.Single;
            ViewContinuousRadio.IsChecked = _viewMode == ViewMode.Continuous;
            ViewTwoPageRadio.IsChecked    = _viewMode == ViewMode.TwoPage;
            ViewGridRadio.IsChecked       = _viewMode == ViewMode.Grid;
            ViewCurrentLabel.Text         = ViewModeDisplayName(_viewMode);
            // Sync toolbar style picker
            ToolbarSmallRadio.IsChecked  = _toolbarStyle == ToolbarStyle.SmallIcons;
            ToolbarLargeRadio.IsChecked  = _toolbarStyle == ToolbarStyle.LargeIcons;
            ToolbarBesideRadio.IsChecked = _toolbarStyle == ToolbarStyle.TextBeside;
            ToolbarUnderRadio.IsChecked  = _toolbarStyle == ToolbarStyle.TextUnder;
            ToolbarOnlyRadio.IsChecked   = _toolbarStyle == ToolbarStyle.TextOnly;
            ToolbarCurrentLabel.Text     = ToolbarStyleName(_toolbarStyle);
            // Sync sidebar-side picker
            SidebarLeftRadio.IsChecked   = !_sidebarRight;
            SidebarRightRadio.IsChecked  = _sidebarRight;
            SidebarCurrentLabel.Text     = Loc(_sidebarRight ? "Str_Sidebar_Right" : "Str_Sidebar_Left");
            // Sync the Links section: confirm is ON unless the user has opted out ("Don't ask again").
            LinkConfirmCheck.IsChecked = App.GetSetting(SkipLinkConfirmSetting) != "1";
            // Sync the Selection section: box text-select is opt-in (default = flowing selection).
            BoxSelectCheck.IsChecked = App.GetSetting("BoxTextSelect") == "1";
            PositionSettingsPanel();
            SettingsOverlay.Visibility = Visibility.Visible;
            SlideSettingsOpen();
        }

        private const double SettingsPanelWidth = 228;

        // Expands the panel out of the sidebar (Width grows from the flush left edge). Clipped while
        // animating so it reveals left-to-right; clip is dropped at the end so the drop shadow shows.
        private void SlideSettingsOpen()
        {
            SettingsPanel.ClipToBounds = true;
            var anim = new DoubleAnimation(0, SettingsPanelWidth, new Duration(TimeSpan.FromMilliseconds(160)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += (_, _) =>
            {
                SettingsPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                SettingsPanel.Width = SettingsPanelWidth;
                SettingsPanel.ClipToBounds = false;   // reveal the right/bottom drop shadow
            };
            SettingsPanel.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        // Shrinks it back into the sidebar, then hides the overlay.
        private void SlideSettingsClosed()
        {
            if (SettingsOverlay.Visibility != Visibility.Visible) return;
            SettingsPanel.ClipToBounds = true;
            double from = SettingsPanel.ActualWidth > 0 ? SettingsPanel.ActualWidth : SettingsPanelWidth;
            var anim = new DoubleAnimation(from, 0, new Duration(TimeSpan.FromMilliseconds(140)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) =>
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                SettingsPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                SettingsPanel.Width = SettingsPanelWidth;
                SettingsPanel.ClipToBounds = false;
            };
            SettingsPanel.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        // ── Settings submenus: inline accordion sections that expand in place below their row.
        // Sections are independent: opening one does NOT collapse the others, so the user can keep
        // several expanded at once. Their open/closed state persists while the app runs.
        private void SettingsMenu_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Primitives.ToggleButton btn) return;
            var panel = SubmenuFor(btn);
            if (panel != null)
                panel.Visibility = btn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            // Expanding/collapsing a section changes the content height, so re-run the shock absorber once
            // layout has updated ExtentHeight (deferred to Render priority so the new height is in).
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                (Action)(() => { if (SettingsOverlay.Visibility == Visibility.Visible) PositionSettingsPanel(); }));
        }

        private System.Windows.Controls.StackPanel? SubmenuFor(System.Windows.Controls.Primitives.ToggleButton btn)
        {
            if (btn == LangMenuButton)    return LangSubmenu;
            if (btn == ThemeMenuButton)   return ThemeSubmenu;
            if (btn == ToolbarMenuButton) return ToolbarSubmenu;
            if (btn == ViewMenuButton)    return ViewSubmenu;
            if (btn == SidebarMenuButton) return SidebarSubmenu;
            return null;
        }

        // Non-modal Settings: a mouse-down anywhere outside the panel dismisses it WITHOUT swallowing the
        // click (it still reaches its target). The title bar is excluded so dragging the window keeps the
        // panel open; the gear is excluded so it can toggle itself closed.
        private void SettingsDismiss_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (SettingsOverlay.Visibility != Visibility.Visible) return;
            if (e.OriginalSource is not DependencyObject src) return;
            if (IsDescendantOf(src, SettingsPanel)) return;
            if (TitleBarBorder != null && IsDescendantOf(src, TitleBarBorder)) return;
            if (SettingsBtn != null && IsDescendantOf(src, SettingsBtn)) return;
            SlideSettingsClosed();
        }

        /// <summary>
        /// Pins the Settings panel's left edge flush against the sidebar's right edge (just past the
        /// splitter), bottom-anchored above the footer. Not draggable; tracks the sidebar's collapsed
        /// width and window resizes automatically.
        /// </summary>
        private void PositionSettingsPanel()
        {
            double edge = (_sidebarCol?.ActualWidth ?? 180) + 6;   // sidebar column + 6px splitter

            // Vertical "shock absorber". The panel's top can rise to just under the tab bar (the document
            // pane top); it normally keeps a small gap above the footer. As the window shrinks and the top
            // reaches the tab bar, that bottom gap collapses FIRST, and only once it's gone does the inner
            // ScrollViewer start scrolling. Growing the window reverses the order. Setting MaxHeight to
            // (avail - bottomGap) guarantees the top never climbs above the tab bar even if the content
            // estimate is a hair off - worst case the scrollbar shows a pixel early, never a broken layout.
            const double footer  = 28;   // footer band; the panel bottom can descend to the footer top
            const double gapPref = 8;    // preferred gap between the panel bottom and the footer
            const double chrome  = 37;   // card vertical chrome: 16 top pad + 20 bottom pad + 1 bottom border
            const double safety  = 8;    // px of slack added to MaxHeight WHILE a gap exists, so a sub-pixel
                                         // rounding can't trip the scrollbar before the bottom gap is gone
            double avail = Math.Max(160, DocPaneBorder.ActualHeight);   // tab bar -> footer

            // Full (unscrolled) height the content wants. ExtentHeight is the scroll content's real height
            // regardless of the viewport, so it tells us whether scrolling would be needed.
            double content = (SettingsScroll?.ExtentHeight ?? 0) > 0
                ? SettingsScroll!.ExtentHeight + chrome
                : 0;

            // bottomGap collapses accurately, so the top stays pinned to the tab bar through the squeeze.
            // maxHeight gets the safety slack ONLY while a gap remains: that stops a rounding-induced
            // scrollbar from showing with a gap still visible. Once the gap is 0, maxHeight = avail, so the
            // scrollbar engages exactly when content truly exceeds the space and the top never overshoots.
            double bottomGap = content <= 0
                ? gapPref
                : Math.Max(0, Math.Min(gapPref, avail - content));
            double maxHeight = bottomGap > 0 ? (avail - bottomGap + safety) : avail;

            double bottomMargin = footer + bottomGap;
            SettingsPanel.Margin = _sidebarRight
                ? new Thickness(0, 0, edge, bottomMargin)
                : new Thickness(edge, 0, 0, bottomMargin);
            SettingsPanel.MaxHeight = Math.Max(160, maxHeight);
        }

        // ── Quick fade in/out for the full-window overlay panels (Settings/Shortcuts/About) ──
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

        private void SettingsOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => SlideSettingsClosed();

        // While Settings is open the full-window overlay catches input. Let the wheel pass through to
        // the content behind it (document or sidebar under the cursor) so the user can keep reading
        // without the panel closing - only a click closes it.
        private void SettingsOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var fwd = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = this
            };
            Point sp = e.GetPosition(_sidebarBorder);
            bool overSidebar = _sidebarBorder is { IsVisible: true }
                               && sp.X >= 0 && sp.X <= _sidebarBorder.ActualWidth
                               && sp.Y >= 0 && sp.Y <= _sidebarBorder.ActualHeight;
            if (overSidebar) PageList.RaiseEvent(fwd);
            else            PagePreviewPanel.RaiseEvent(fwd);
        }

        private void SettingsOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => e.Handled = true;

        private void SettingsOverlayClose_Click(object sender, RoutedEventArgs e)
            => SlideSettingsClosed();

        // Links section. The checkbox is the re-armable inverse of the "Don't ask again" opt-out from the
        // open-link confirmation dialog: checked = confirm before opening (default), unchecked = skip it.
        // Reads/writes the same SkipLinkConfirm setting ConfirmOpenLink checks, so it takes effect on the
        // next link click with no restart.
        private void LinkConfirmCheck_Toggled(object sender, RoutedEventArgs e)
        {
            if (LinkConfirmCheck.IsChecked == true) App.RemoveSetting(SkipLinkConfirmSetting);
            else                                    App.SetSetting(SkipLinkConfirmSetting, "1");
        }

        // Selection section: opt in to the legacy box text-selection (drag a rectangle to copy the words
        // inside) instead of the default flowing character selection. Read live via BoxTextSelectMode.
        private void BoxSelectCheck_Toggled(object sender, RoutedEventArgs e)
        {
            if (BoxSelectCheck.IsChecked == true) App.SetSetting("BoxTextSelect", "1");
            else                                  App.RemoveSetting("BoxTextSelect");
        }

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
            ThemeManager.Apply(theme);
            if (ThemeCurrentLabel is not null) ThemeCurrentLabel.Text = ThemeDisplayName(theme);
            UpdateAccentDotSelection();
            UpdateAccentRowsVisibility(animate: true);
            // Intentionally leave the flyout open so the user can try another theme right away
            // without reopening the submenu.
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
            var ring = (System.Windows.Media.Brush)FindResource("TextPrimary");
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
        private void LangEsRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.Es);
        private void LangFrRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.Fr);
        private void LangZhTWRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(KillerPDF.Services.Locale.ZhTW);
        private void LangZhCNRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(KillerPDF.Services.Locale.ZhCN);
        private void LangBnRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.Bn);
        private void LangTrRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.TrTR);
        private void LangDeRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.De);

        private void SelectLocale(KillerPDF.Services.Locale loc)
        {
            KillerPDF.Services.LocaleManager.Apply(loc);
            ApplyToolNumberTooltips();   // re-append the numbers to the now-localized tool tooltips
            LangCurrentLabel.Text = LangDisplayName(loc);
            // The Theme and Toolbar picker labels are set imperatively (not DynamicResource), so the
            // language switch happening while the panel is open would leave them in the old language.
            if (ThemeCurrentLabel is not null)   ThemeCurrentLabel.Text   = ThemeDisplayName(ThemeManager.Current);
            if (ToolbarCurrentLabel is not null) ToolbarCurrentLabel.Text = ToolbarStyleName(_toolbarStyle);
            if (ViewCurrentLabel is not null)    ViewCurrentLabel.Text    = ViewModeDisplayName(_viewMode);
            LangMenuButton.IsChecked = false;   // collapses the inline language section after a pick

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
            KillerPDF.Services.Locale.Es   => "Español",
            KillerPDF.Services.Locale.Fr   => "Français",
            KillerPDF.Services.Locale.ZhTW => "中文 (繁體)",
            KillerPDF.Services.Locale.ZhCN => "中文 (简体)",
            KillerPDF.Services.Locale.Bn   => "বাংলা",
            KillerPDF.Services.Locale.TrTR => "Türkçe",
            KillerPDF.Services.Locale.De   => "Deutsch",
            _                              => "English",
        };

        private void ViewContinuousRadio_Checked(object sender, RoutedEventArgs e) => SelectViewMode(ViewMode.Continuous);
        private void ViewSingleRadio_Checked(object sender, RoutedEventArgs e)     => SelectViewMode(ViewMode.Single);
        private void ViewTwoPageRadio_Checked(object sender, RoutedEventArgs e)    => SelectViewMode(ViewMode.TwoPage);
        private void ViewGridRadio_Checked(object sender, RoutedEventArgs e)       => SelectViewMode(ViewMode.Grid);

        // ── Toolbar appearance (display-mode picker) ──────────────────────
        // Icon size and whether captions show, picked as one exclusive mode. Hover tooltips stay on
        // in every mode, so the text modes are about preference, not discoverability.
        private enum ToolbarStyle { SmallIcons, LargeIcons, TextBeside, TextUnder, TextOnly }
        private ToolbarStyle _toolbarStyle = ToolbarStyle.SmallIcons;   // default for new installs

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
            var mode = _toolbarStyle;
            bool large = mode == ToolbarStyle.LargeIcons;
            bool beside = mode == ToolbarStyle.TextBeside;
            bool under = mode == ToolbarStyle.TextUnder;
            bool textOnly = mode == ToolbarStyle.TextOnly;
            double glyphSize = (large || under) ? 20 : (beside ? 16 : 14);
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

            // Text under the icon: a large icon stacked over a small caption, while it still fits.
            if (under && withLabel)
            {
                btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = 52; btn.Padding = new Thickness(6, 4, 6, 4);
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

            // Icon only: the icon modes, or Text-beside / Text-under after a caption was shed.
            btn.Width = (large || under) ? 46 : (beside ? 40 : 36);
            btn.MinWidth = 0;
            btn.Height = under ? 52 : (beside ? 34 : (large ? 42 : 32));
            btn.Padding = (beside || under) ? new Thickness(8, 5, 8, 5) : new Thickness(10, 6, 10, 6);
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
            bool textMode = _toolbarStyle is ToolbarStyle.TextBeside or ToolbarStyle.TextUnder or ToolbarStyle.TextOnly;
            ApplySplitButtonChrome(textMode);
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

        private void ToolbarSmallRadio_Checked(object sender, RoutedEventArgs e)  => SelectToolbarStyle(ToolbarStyle.SmallIcons);
        private void ToolbarLargeRadio_Checked(object sender, RoutedEventArgs e)  => SelectToolbarStyle(ToolbarStyle.LargeIcons);
        private void ToolbarBesideRadio_Checked(object sender, RoutedEventArgs e) => SelectToolbarStyle(ToolbarStyle.TextBeside);
        private void ToolbarUnderRadio_Checked(object sender, RoutedEventArgs e)  => SelectToolbarStyle(ToolbarStyle.TextUnder);
        private void ToolbarOnlyRadio_Checked(object sender, RoutedEventArgs e)   => SelectToolbarStyle(ToolbarStyle.TextOnly);

        private void SelectToolbarStyle(ToolbarStyle style)
        {
            _toolbarStyle = style;
            App.SetSetting("ToolbarStyle", style.ToString());
            if (ToolbarCurrentLabel is not null) ToolbarCurrentLabel.Text = ToolbarStyleName(style);
            ApplyToolbarAppearance();
            // Leave the flyout open so the user can compare modes without reopening it.
        }

        private string ToolbarStyleName(ToolbarStyle style) => style switch
        {
            ToolbarStyle.LargeIcons => Loc("Str_Toolbar_LargeIcons"),
            ToolbarStyle.TextBeside => Loc("Str_Toolbar_TextBeside"),
            ToolbarStyle.TextUnder  => Loc("Str_Toolbar_TextUnder"),
            ToolbarStyle.TextOnly   => Loc("Str_Toolbar_TextOnly"),
            _                       => Loc("Str_Toolbar_SmallIcons"),
        };

        // ── Responsive toolbar overflow ───────────────────────────────────
        private bool _reflowingToolbar;
        private bool _reflowQueued;

        // ReflowToolbar decides which toolbar groups collapse into the overflow chevron. It must run
        // live during a window resize (deferring it leaves buttons overlapping mid-drag), so the cost is
        // kept low two ways: SizeChanged is coalesced to at most one reflow per render tick, and the
        // reflow re-measures only the two bars instead of forcing repeated whole-tree UpdateLayout passes.
        private void ToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => QueueReflowToolbar();

        private void QueueReflowToolbar()
        {
            if (_reflowQueued) return;
            _reflowQueued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, (Action)(() =>
            {
                _reflowQueued = false;
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
                    (GrpSignature,      new UIElement[] { MiSignature }),
                    (ToolImageBtn,      new UIElement[] { MiImage }),
                    (ToolCropBtn,       new UIElement[] { MiCrop }),
                    (GrpUndo,           new UIElement[] { MiUndo, MiClear }),
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
                bool textCaptions = _toolbarStyle is ToolbarStyle.TextBeside or ToolbarStyle.TextUnder;
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
