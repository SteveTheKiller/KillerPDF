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
using KillerPDF.Features;
using KillerPDF.Services;
using KillerFamily.Accessibility;

namespace KillerPDF.Shell
{
    public partial class MainWindow : ISearchHost
    {
        // The search state machine (match list, cursor, next/prev) lives in
        // Features/Search/SearchController.cs; the bar UI, debounce, and highlight painting stay
        // here. Forwarders keep the bar buttons' and KeyboardShortcuts' call sites unchanged.
        private SearchController? _searchController;
        private SearchController Search => _searchController ??= new SearchController(this);

        private void SearchNextResult() => Search.Next();
        private void SearchPrevResult() => Search.Prev();

        // ---- ISearchHost -----------------------------------------------------------------------
        // (IShellServices - Window, Loc, SetStatus - is implemented once for the class in
        // Shell/About.cs.)

        string? ISearchHost.CurrentFile => _currentFile;
        long ISearchHost.SearchRevision => _documentEditRevision;
        Guid? ISearchHost.NativeSearchRevisionId => ActiveViewer.NativeDocumentRef?.CaptureReadRevision().Id;
        CurrentSearchSnapshot ISearchHost.CaptureSearchSnapshot() => ActiveViewer.CaptureCurrentSearchSnapshot();
        int ISearchHost.CurrentPageIndex => PageList.SelectedIndex;
        void ISearchHost.GoToPage(int pageIdx) => PageList.SelectedIndex = pageIdx;

        void ISearchHost.SetResultText(string text)
        {
            if (_searchStatus is null) return;
            _searchStatus.Text = text;
            _searchStatus.ToolTip = null;
        }

        void ISearchHost.SetResultNote(string text, string? tooltip)
        {
            if (_searchStatus is null) return;
            _searchStatus.Text = text;
            _searchStatus.ToolTip = tooltip;
        }

        void ISearchHost.SetResultCount(string text, string? tooltip)
        {
            if (_searchStatus is null) return;
            _searchStatus.Text = text;
            _searchStatus.ToolTip = tooltip;
        }

        void ISearchHost.ClearHighlights() => ClearSearchHighlights();
        void ISearchHost.RepaintHighlights() => HighlightSearchResultsOnCurrentPage();
        bool ISearchHost.IsPageOnScreen(int pageIdx) => ActiveViewer.IsPageOnScreen(pageIdx);
        void ISearchHost.ShowReplaceAvailable(bool available) => ShowReplaceToggle(available);

        // EPUB and DjVu tabs are read-only until they are converted to PDF, so their bar is Find alone.
        private void ShowReplaceToggle(bool available)
        {
            if (!available) SetReplaceRowVisible(false);
            _replaceToggleBtn?.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        }
        bool ISearchHost.IsCovered(int pageIdx, double left, double bottom, double right, double top)
            => ActiveViewer.IsPdfRectCovered(pageIdx, left, bottom, right, top);

        private void Search_Click(object sender, RoutedEventArgs e) => ToggleSearchBar();

        private void ToggleSearchBar()
        {
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                return;
            }
            ShowSearchBar();
        }

        // Ctrl+H: the find bar with its replace row open. An open bar keeps its search text. A read-only
        // EPUB or DjVu tab opens Find alone.
        private void ShowReplaceBar()
        {
            if (_searchBar is not { Visibility: Visibility.Visible }) ShowSearchBar();
            if (!Search.ReplaceAvailable) return;
            SetReplaceRowVisible(true);
            TextBox? target = string.IsNullOrEmpty(_searchBox?.Text) ? _searchBox : _replaceBox;
            target?.Focus();
            if (target is not null) Keyboard.Focus(target);
        }

        private void ShowSearchBar()
        {
            if (_searchBar is null)
            {
                // Build search bar programmatically and inject into the preview area grid.
                // The find and replace boxes share one look.
                TextBox MakeFindTextBox()
                {
                    var box = new TextBox
                    {
                        Width = 200,
                        Height = 26,
                        FontFamily = UiKit.UiFont,
                        FontSize = 13,
                        SelectionBrush = AccentBrush(),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(6, 2, 6, 2),
                        VerticalContentAlignment = VerticalAlignment.Center
                    };
                    // Live (DynamicResource-style) brushes so the box recolors on a theme switch while the
                    // bar is open, instead of baking colors in at build time. Background uses the dark
                    // toolbar/titlebar tone (BgSidebar).
                    box.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
                    box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                    box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextBrush");
                    box.SetResourceReference(Control.BorderBrushProperty, "CardBorderBrush");

                    // Custom template so the default WPF blue focus/hover border never shows; keep our themed border.
                    var tbTemplate = new ControlTemplate(typeof(TextBox));
                    var tbBorder = new FrameworkElementFactory(typeof(Border));
                    tbBorder.SetValue(Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(Control.BackgroundProperty));
                    tbBorder.SetValue(Border.BorderBrushProperty, new System.Windows.TemplateBindingExtension(Control.BorderBrushProperty));
                    tbBorder.SetValue(Border.BorderThicknessProperty, new System.Windows.TemplateBindingExtension(Control.BorderThicknessProperty));
                    tbBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
                    var tbHost = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
                    tbHost.SetValue(ScrollViewer.PaddingProperty, new System.Windows.TemplateBindingExtension(Control.PaddingProperty));
                    tbHost.SetValue(ScrollViewer.VerticalAlignmentProperty, VerticalAlignment.Center);
                    tbBorder.AppendChild(tbHost);
                    tbTemplate.VisualTree = tbBorder;
                    box.Template = tbTemplate;
                    box.FocusVisualStyle = null;
                    return box;
                }
                _searchBox = MakeFindTextBox();
                _searchBox.KeyDown += SearchBox_KeyDown;
                _searchBox.TextChanged += SearchBox_TextChanged;
                _replaceBox = MakeFindTextBox();
                _replaceBox.KeyDown += ReplaceBox_KeyDown;

                // Wide enough for translated empty/error states while keeping the count stable.
                _searchStatus = new TextBlock
                {
                    FontFamily = UiKit.UiFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Width = 96,
                    Margin = new Thickness(2, 0, 2, 0)
                };
                _searchStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

                // Small VSCode-style prev / next / close buttons. Hover tooltips carry the shortcuts.
                Button SearchNavBtn(string glyph, string tip, Action onClick, bool danger = false)
                {
                    var b = new Button
                    {
                        Content    = glyph,
                        FontFamily = UiKit.IconFont,
                        FontSize   = 12,
                        Width = 26, Height = 24,
                        Padding    = new Thickness(0),   // ToolbarButton's 10,6 padding clips the glyph in a 26px button
                        // The close X uses the shared danger style so its glyph turns red on hover like
                        // every other close X (window chrome, tabs, overlay headers).
                        Style      = (Style)FindResource(danger ? "DangerCloseButton" : "ToolbarButton"),
                        ToolTip    = tip
                    };
                    b.Click += (_, _) => onClick();
                    return b;
                }
                var prevBtn  = SearchNavBtn("", Loc("Str_Search_PreviousTT"), SearchPrevResult); // ChevronUp
                var nextBtn  = SearchNavBtn("", Loc("Str_Search_NextTT"), SearchNextResult);     // ChevronDown
                var closeBtn = SearchNavBtn("", Loc("Str_Search_CloseTT"), CloseSearchBar, danger: true); // Cancel

                // Match case and Whole word, drawn the way the toolbar shows a selected tool.
                _matchCaseBtn = SearchNavBtn("", Loc("Str_Search_MatchCaseTT"), () => ToggleSearchOption(matchCase: true));
                _matchCaseBtn.Content = new TextBlock { Text = "Aa", FontFamily = UiKit.UiFont, FontSize = 12 };
                _wholeWordBtn = SearchNavBtn("", Loc("Str_Search_WholeWordTT"), () => ToggleSearchOption(matchCase: false));
                _wholeWordBtn.Content = new TextBlock { Text = "ab", FontFamily = UiKit.UiFont, FontSize = 12,
                    TextDecorations = TextDecorations.Underline };

                // The chevron at the left opens the replace row, as Ctrl+H does.
                _replaceToggleBtn = SearchNavBtn("", ShortcutTip("Str_Replace_ToggleTT", "Ctrl:H"),
                    () => SetReplaceRowVisible(_replaceBox?.Visibility != Visibility.Visible)); // ChevronRight

                Button ReplaceBtn(string label, string tip, Action onClick)
                {
                    var b = SearchNavBtn("", tip, onClick);
                    b.Content = label;
                    b.FontFamily = UiKit.UiFont;
                    b.Width = double.NaN;
                    b.Padding = new Thickness(8, 0, 8, 0);
                    b.Margin = new Thickness(2, 0, 0, 0);
                    return b;
                }
                _replaceActions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                _replaceActions.Children.Add(ReplaceBtn(Loc("Str_Replace_One"), Loc("Str_Replace_OneTT"), ReplaceCurrentMatch));
                _replaceActions.Children.Add(ReplaceBtn(Loc("Str_Replace_All"), Loc("Str_Replace_AllTT"), ReplaceAllMatches));
                _replaceBox.Margin = new Thickness(0, 4, 0, 0);
                _replaceBox.ToolTip = Loc("Str_Replace_WithTT");
                _replaceBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, Loc("Str_Replace_WithTT"));

                var searchIcon = new Glyph
                {
                    Text = "",  // Segoe MDL2 Search / magnifying glass
                    FontFamily = UiKit.IconFont,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    IsHitTestVisible = false
                };
                searchIcon.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

                // Drag grip: two columns of three dots on the left (same look as the sidebar splitter and
                // the annotate bars). Grabbing it moves the whole bar anywhere in the document area.
                var gripBrush = TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray;
                var gripDots = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 6, 0)
                };
                for (int gcol = 0; gcol < 2; gcol++)
                {
                    var colDots = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
                    for (int grow = 0; grow < 3; grow++)
                        colDots.Children.Add(new Ellipse { Width = 3, Height = 3, Margin = new Thickness(1.5), Fill = gripBrush });
                    gripDots.Children.Add(colDots);
                }
                var searchGrip = new Border
                {
                    Background = Brushes.Transparent,   // transparent yet hit-testable, so it can be grabbed
                    // No Cursor here: EnablePanelDrag sets the grab hand when it wires the drag,
                    // so the affordance has one source of truth.
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = gripDots,
                    ToolTip = Loc("Str_Search_DragTT")
                };

                // Find row on top, replace row under it with its box lined up under the find box.
                // The grip spans both rows so the bar drags from either.
                var panel = new Grid
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 6, 8, 6)
                };
                for (int c = 0; c < 5; c++) panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var findTail = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                findTail.Children.Add(_matchCaseBtn);
                findTail.Children.Add(_wholeWordBtn);
                findTail.Children.Add(_searchStatus);
                findTail.Children.Add(prevBtn);
                findTail.Children.Add(nextBtn);
                findTail.Children.Add(closeBtn);
                _replaceToggleBtn.Margin = new Thickness(0, 0, 4, 0);
                void Place(UIElement element, int row, int column)
                {
                    Grid.SetRow(element, row);
                    Grid.SetColumn(element, column);
                    panel.Children.Add(element);
                }
                Place(searchGrip, 0, 0);
                Grid.SetRowSpan(searchGrip, 2);
                Place(_replaceToggleBtn, 0, 1);
                Place(searchIcon, 0, 2);
                Place(_searchBox, 0, 3);
                Place(findTail, 0, 4);
                Place(_replaceBox, 1, 3);
                Place(_replaceActions, 1, 4);

                _searchBar = new Border
                {
                    BorderThickness = new Thickness(1),
                    // Free-floating like the Signatures popup: positioned by Left/Top margin (set after
                    // layout from the saved spot) and draggable by the grip, so it can sit anywhere.
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(4),
                    Child = GrainWrap(panel),
                    Margin = new Thickness(0),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.55 }
                };
                _searchBar.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
                // PaneBorderBrush, the same key the annotate settings bars use (AnnotationBars.cs).
                // The find bar is the same class of floating tool surface, so it takes the same
                // edge; on MenuBorderBrush the two sat side by side drawing different borders.
                _searchBar.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");

                // Add to the preview area grid (parent of ScrollViewer)
                var previewGrid = PagePreviewPanel.Parent as Grid;
                if (previewGrid is not null)
                {
                    // Above the annotate settings bars (ZIndex 100) so Ctrl+F is never hidden under
                    // the highlight/draw/text toolbar when both are open.
                    Panel.SetZIndex(_searchBar, 200);
                    previewGrid.Children.Add(_searchBar);
                    // Keep the whole bar on screen: when the preview area is resized, shrink the text box
                    // (not the buttons) so it never overflows, and re-clamp the floating bar back inside.
                    previewGrid.SizeChanged += (_, _) =>
                    {
                        FitSearchBox();
                        if (_searchBar is { Visibility: Visibility.Visible })
                        {
                            double cl = _searchBar.Margin.Left, ct = _searchBar.Margin.Top;
                            ClampPanelToBounds(_searchBar, previewGrid, ref cl, ref ct);
                            _searchBar.Margin = new Thickness(cl, ct, 0, 0);
                        }
                    };
                    // Place it (saved spot, or default near the old top-right position) and wire the grip
                    // for dragging once it's laid out and has a real width - mirrors the Signatures popup.
                    var bar = _searchBar;
                    var grip = searchGrip;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                    {
                        ApplySavedPanelPosition(bar, previewGrid, "SearchBar", fallbackRightInset: 16, fallbackTop: 6);
                        EnablePanelDrag(grip, bar, previewGrid, "SearchBar");
                    }));
                }
            }

            _searchBar.Visibility = Visibility.Visible;
            SetReplaceRowVisible(false);
            ShowReplaceToggle(Search.ReplaceAvailable);
            UpdateSearchOptionButtons();
            _searchBox!.Text = "";
            _searchStatus?.Text = "";
            FitSearchBox();
            _searchBox.Focus();
            Keyboard.Focus(_searchBox);
        }

        // Sizes the search text box to whatever width is left after the bar's other controls, capped at a comfortable
        // 200px, so the full bar fits the preview area.
        private void FitSearchBox()
        {
            if (_searchBar is null || _searchBox is null) return;
            double avail = (PagePreviewPanel.Parent as Grid)?.ActualWidth ?? 0;
            // Not laid out yet: there is no room to measure against, and the bar is fitted again once there is.
            if (avail <= 0) return;
            // The bar measures its own other controls, so nothing here goes stale when one of them changes. A pane
            // too narrow for it narrows the status text before the boxes (Features/Search/SearchController.cs).
            SearchBarFit.Fit(_searchBar, _replaceBox is null ? [_searchBox] : [_searchBox, _replaceBox], _searchStatus, avail);
        }

        private void CloseSearchBar()
        {
            if (_searchBar is { Visibility: Visibility.Visible } bar)
            {
                // Fade out rather than blink away. On completion, collapse and restore opacity so the
                // next open shows it cleanly.
                var fade = new DoubleAnimation(bar.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(150)))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                fade.Completed += (_, _) =>
                {
                    bar.Visibility = Visibility.Collapsed;
                    bar.BeginAnimation(UIElement.OpacityProperty, null);
                    bar.Opacity = 1;
                };
                bar.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            _searchDebounce?.Stop();
            Search.CancelPending();
            ClearSearchHighlights();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (TrySearchOptionKey(e)) return;
            if (e.Key == Key.Escape)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    SearchPrevResult();
                else
                    SearchNextResult();
                e.Handled = true;
            }
        }

        private System.Windows.Threading.DispatcherTimer? _searchDebounce;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // The text moved on, so a search still running is for something no longer asked.
            Search.CancelPending();
            var text = _searchBox?.Text ?? "";
            if (text.Length < 2)
            {
                _searchDebounce?.Stop();
                ClearSearchHighlights();
                Search.ClearMatches();
                _searchStatus?.Text = "";
                return;
            }
            // Debounce: wait for a brief pause in typing before searching, so the first keystrokes
            // on a large document don't start a whole-document read for each partial query.
            if (_searchDebounce is null)
            {
                _searchDebounce = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(250) };
                _searchDebounce.Tick += (_, _) =>
                {
                    _searchDebounce!.Stop();
                    var q = _searchBox?.Text ?? "";
                    if (q.Length >= 2) Search.Run(q);
                };
            }
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        // ---- Find and Replace (#422) -----------------------------------------------------------

        private TextBox? _replaceBox;
        private StackPanel? _replaceActions;
        private Button? _replaceToggleBtn;
        private Button? _matchCaseBtn;
        private Button? _wholeWordBtn;

        private void SetReplaceRowVisible(bool visible)
        {
            var state = visible ? Visibility.Visible : Visibility.Collapsed;
            _replaceBox?.Visibility = state;
            _replaceActions?.Visibility = state;
            _replaceToggleBtn?.Content = visible ? "\ue70d" : "\ue76c";   // ChevronDown / ChevronRight
        }

        // Alt+C and Alt+W flip Match case and Whole word from either box.
        private bool TrySearchOptionKey(KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Alt || e.Key != Key.System) return false;
            if (e.SystemKey == Key.C) ToggleSearchOption(matchCase: true);
            else if (e.SystemKey == Key.W) ToggleSearchOption(matchCase: false);
            else return false;
            e.Handled = true;
            return true;
        }

        private void ToggleSearchOption(bool matchCase)
        {
            if (matchCase) Search.MatchCase = !Search.MatchCase;
            else Search.WholeWord = !Search.WholeWord;
            UpdateSearchOptionButtons();
            string query = _searchBox?.Text ?? "";
            if (query.Length >= 2)
            {
                _searchDebounce?.Stop();
                Search.Run(query);
            }
        }

        private void UpdateSearchOptionButtons()
        {
            foreach (var (button, on) in new[] { (_matchCaseBtn, Search.MatchCase), (_wholeWordBtn, Search.WholeWord) })
            {
                if (button is null) continue;
                button.Tag = on ? "selected" : null;
                if (on)
                {
                    button.SetResourceReference(Control.BackgroundProperty, "SelectionBg");
                    button.SetResourceReference(Control.ForegroundProperty, "SelectionFg");
                }
                else
                {
                    button.ClearValue(Control.BackgroundProperty);
                    button.ClearValue(Control.ForegroundProperty);
                }
            }
        }

        // Enter replaces the current match, Ctrl+Alt+Enter replaces them all.
        private void ReplaceBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (TrySearchOptionKey(e)) return;
            if (e.Key == Key.Escape)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter || (e.Key == Key.System && e.SystemKey == Key.Enter))
            {
                if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) ReplaceAllMatches();
                else ReplaceCurrentMatch();
                e.Handled = true;
            }
        }

        // Replaces the highlighted match. With no match highlighted yet, the first press finds it,
        // so what gets replaced is always something the user has seen.
        private void ReplaceCurrentMatch()
        {
            string find = _searchBox?.Text ?? "";
            if (find.Length == 0 || _doc is null) return;
            if (!EnsureEditable()) return;
            // The match on show belongs to the older results until the running search lands.
            if (Search.IsSearching) return;
            if (!Search.ResultsCurrent || Search.LastQuery != find || Search.CurrentMatch is null)
            {
                _searchDebounce?.Stop();
                Search.Run(find);
                return;
            }
            if (Search.CurrentOccurrence is not { } occurrence) return;
            var result = ActiveViewer.ReplaceTextOccurrenceExt(find, _replaceBox?.Text ?? "",
                Search.LastMatchCase, Search.LastWholeWord, occurrence);
            if (result.Failed) return;
            if (result.Occurrences == 0)
            {
                // Nothing replaceable here (turned text, say): step past it rather than stall.
                Search.Next();
                SetStatus(ReplaceStatus(result));
                return;
            }
            AfterReplace(find, result);
        }

        private void ReplaceAllMatches()
        {
            string find = _searchBox?.Text ?? "";
            if (find.Length == 0 || _doc is null) return;
            if (!EnsureEditable()) return;
            var result = ActiveViewer.ReplaceTextExt(find, _replaceBox?.Text ?? "", Search.MatchCase, Search.WholeWord);
            if (!result.Failed) AfterReplace(find, result);
        }

        // Replaced text no longer reads as the search text, so the fresh search moves the cursor
        // on to the next match still showing.
        private void AfterReplace(string find, KillerPDF.Controls.TextReplaceResult result)
        {
            _searchDebounce?.Stop();
            Search.Run(find);
            ActiveViewer.ReportResult(ReplaceStatus(result));
        }

        private string ReplaceStatus(KillerPDF.Controls.TextReplaceResult result)
        {
            string message = result.Occurrences == 0
                ? Loc("Str_Replace_None")
                : string.Format(Loc("Str_Replace_Done"), result.Occurrences, result.Lines);
            if (result.SkippedTurnedLines > 0)
                message += " " + string.Format(Loc("Str_Replace_SkippedTurned"), result.SkippedTurnedLines);
            return message;
        }

        // Paints match highlights onto EVERY page that's currently on screen and has results -
        // the primary tile and all per-page overlays alike. Single Page shows the one page; Two-Page
        // and Grid show every visible tile with hits; Continuous shows them down the whole scroll.
        // Re-paints one page's match highlights onto its overlay using the in-memory page size (no
        // file I/O), so it's cheap enough to call at the tail of every RenderAllAnnotations - which is
        // what keeps highlights alive instead of being wiped by re-renders and continuous scrolling.
        private void ApplySearchHighlights(int page, Canvas canvas)
        {
            if (_searchBar is null || _searchBar.Visibility != Visibility.Visible) return;
            if (page < 0) return;
            if (!Search.TryGetPageRects(page, out var rects)) return;
            if (!_renderDims.TryGetValue(page, out var rd)) return;

            var (renderW, renderH) = rd;
            double pdfW, pdfH;
            int intrinsicRotation;
            if (ActiveViewer.NativeDocumentRef is { } native)
            {
                if (page >= native.PageCount) return;
                var geometry = native.GetNativePageGeometry(page);
                (pdfW, pdfH) = Features.SearchController.NativeHighlightPageSize(geometry);
                intrinsicRotation = geometry.IntrinsicRotation;
            }
            else
            {
                if (_doc is null || page >= EnsureEngineDocumentSession().PageCount) return;
                var pageInfo = EnsureEngineDocumentSession().Pages[page];
                pdfW = pageInfo.Width;
                pdfH = pageInfo.Height;
                intrinsicRotation = pageInfo.Rotation;
            }
            if (pdfW <= 0 || pdfH <= 0) return;
            // Match rects are unrotated page points; the canvas shows the page turned by its rotation.
            int rotation = _pageRotations.TryGetValue(page, out int stored) ? stored : intrinsicRotation;

            foreach (var (left, bottom, right, top) in rects)
            {
                bool isCurrent = Search.IsCurrentArea(page, new(left, bottom, right, top));
                AddSearchHighlight(canvas, RedactionGeometry.PageToCanvas(left, bottom, right, top,
                    pdfW, pdfH, renderW, renderH, rotation), isCurrent);
            }
        }

        // Repaints highlights on every page on screen right now (called when a search runs or the
        // current page changes); per-page re-renders keep them alive via ApplySearchHighlights.
        private void HighlightSearchResultsOnCurrentPage()
        {
            ClearSearchHighlights();
            foreach (var page in Search.PagesWithResults)
            {
                var canvas = VisibleCanvasForPage(page);
                if (canvas is not null) ApplySearchHighlights(page, canvas);
            }
        }

        private static void AddSearchHighlight(Canvas canvas, Rect match, bool isCurrent)
        {
            double cw = match.Width;
            double ch = match.Height;
            // A little breathing room so the box (and the current-match outline) wraps the whole word -
            // the engine's glyph bounds sit tight against the letters. Scales with text height (the
            // shorter side, so turned pages pad the same) so it looks consistent across font sizes.
            double pad = Math.Min(cw, ch) * 0.30;
            double cx = match.X - pad;
            double cy = match.Y - pad;
            var rect = new Rectangle
            {
                // The current match (search cursor) gets a brighter, more opaque fill; the others are dim.
                Fill = new SolidColorBrush(isCurrent
                    ? Color.FromArgb(150, 255, 190, 0)
                    : Color.FromArgb(70, 255, 165, 0)),
                StrokeThickness = isCurrent ? 3.5 : 1,
                RadiusX = pad * 0.6,
                RadiusY = pad * 0.6,
                Width = Math.Max(cw + pad * 2, 4),
                Height = Math.Max(ch + pad * 2, 4),
                IsHitTestVisible = false,
                Tag = "SearchHighlight"
            };
            if (isCurrent)
                // Bind the current-match outline to the on-page accent resource so it recolors live on a
                // theme switch (matching the selection chrome), rather than baking the color in at paint time.
                rect.SetResourceReference(Shape.StrokeProperty, "SelectionAccent");
            else
                rect.Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 165, 0));
            Canvas.SetLeft(rect, cx);
            Canvas.SetTop(rect, cy);
            canvas.Children.Add(rect);
        }

        // Removes only the highlight rectangles from every page overlay. Deliberately does NOT touch
        // the result counter - that's owned by SearchController's status updates and the empty-query path - so a
        // repaint (which clears then re-adds highlights) can't wipe the "3 / 14" count.
        private void ClearSearchHighlights()
        {
            foreach (var canvas in AllPageCanvases())
            {
                var toRemove = canvas.Children.OfType<Rectangle>()
                    .Where(r => r.Tag is string s && s == "SearchHighlight").ToList();
                foreach (var r in toRemove)
                    canvas.Children.Remove(r);
            }
        }
    }
}
