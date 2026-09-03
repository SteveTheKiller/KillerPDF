using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerPDF.Controls;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

public partial class MainWindow
{
    private bool _comparisonActive;
    private bool _comparisonSyncing;
    private string? _comparisonLeftPath;
    private string? _comparisonRightPath;
    private CancellationTokenSource? _comparisonCts;
    private CancellationTokenSource? _comparisonReportCts;
    private string? _comparisonReport;
    private IReadOnlyList<DifferenceRegion> _comparisonRegions = [];
    private int _comparisonRegionIndex = -1;
    private int _comparisonRegionPage = -1;
    private int _comparisonRegionWidth;
    private int _comparisonRegionHeight;
    private bool _comparisonWasSplit;
    private PdfViewer? _comparisonPreviousFocusedViewer;
    private PdfViewer.DocumentSession? _comparisonPreviousLeftSession;
    private PdfViewer.DocumentSession? _comparisonPreviousRightSession;
    private ComparisonViewState _comparisonPreviousLeftView;
    private ComparisonViewState _comparisonPreviousRightView;

    private void ComparePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_comparisonActive)
        {
            EndComparison(closeSplit: true);
            return;
        }

        string? source = ActiveViewer.CurrentFilePathExt;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            SetStatus(Loc("Str_Compare_OpenOriginal"));
            return;
        }

        // Keep the overflow entry visible while its comparison choices are open.
        // Otherwise the parent click handler closes the anchor and dismisses both menus.
        if (ReferenceEquals(sender, MiCompare)) e.Handled = true;
        ShowComparisonMenu(sender as FrameworkElement ?? ComparePdfBtn, source);
    }

    private void ShowComparisonMenu(FrameworkElement anchor, string source)
    {
        var menu = MakeThemedMenu();
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        if (ReferenceEquals(anchor, MiCompare))
            menu.Closed += (_, _) => OverflowChevron.IsChecked = false;
        PopulateComparisonChoices(menu.Items, source);
        menu.IsOpen = true;
    }

    private MenuItem BuildComparisonContextItem()
    {
        var root = new MenuItem
        {
            Header = Loc("Str_TT_ComparePDFs"),
            Icon = BuildComparisonMenuIcon()
        };
        string? source = ActiveViewer.CurrentFilePathExt;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            root.IsEnabled = false;
        else
            PopulateComparisonChoices(root.Items, source);
        return root;
    }

    private static Grid BuildComparisonMenuIcon()
    {
        var icon = new Grid { Width = 14, Height = 13 };
        static Border MakeDocument(HorizontalAlignment alignment)
        {
            var line = new System.Windows.Shapes.Rectangle
            {
                Height = 1, Margin = new Thickness(1), VerticalAlignment = VerticalAlignment.Center
            };
            line.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(MenuItem), 1)
            });
            var document = new Border
            {
                Width = 6, Height = 11, HorizontalAlignment = alignment,
                VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(1), Child = line
            };
            document.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(MenuItem), 1)
            });
            return document;
        }
        icon.Children.Add(MakeDocument(HorizontalAlignment.Left));
        icon.Children.Add(MakeDocument(HorizontalAlignment.Right));
        return icon;
    }

    private void PopulateComparisonChoices(ItemCollection items, string source)
    {
        var openTabs = Viewer.OpenPdfTabsExt()
            .Concat(ViewerB.OpenPdfTabsExt())
            .Where(item => !string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(source),
                StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        items.Add(new MenuItem { Header = Loc("Str_Compare_OpenTabs"), IsEnabled = false });
        if (openTabs.Count == 0)
            items.Add(new MenuItem { Header = Loc("Str_Compare_NoOpenTabs"), IsEnabled = false });
        else
            foreach (var tab in openTabs)
            {
                string path = tab.Path;
                var item = new MenuItem { Header = tab.Title };
                item.Click += (_, _) => BeginComparison(source, path);
                items.Add(item);
            }

        items.Add(new Separator());
        var browse = new MenuItem { Header = Loc("Str_Compare_Browse") };
        browse.Click += (_, _) => BrowseForComparison(source);
        items.Add(browse);
    }

    private void BrowseForComparison(string source)
    {

        var dialog = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Filter = Loc("Str_Filter_Pdf") + "|*.pdf",
            Title = Loc("Str_Compare_SelectTitle")
        };
        if (dialog.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(dialog.FileName),
            StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(Loc("Str_Compare_ChooseDifferent"));
            return;
        }

        BeginComparison(source, dialog.FileName);
    }

    private void BeginComparison(string source, string comparison)
    {
        _comparisonWasSplit = _isSplit;
        _comparisonPreviousFocusedViewer = ActiveViewer;
        FocusPane(Viewer);
        _comparisonPreviousLeftSession = Viewer.ActiveSessionExt;
        _comparisonPreviousLeftView = Viewer.CaptureComparisonViewStateExt();
        if (_comparisonWasSplit)
        {
            FocusPane(ViewerB);
            _comparisonPreviousRightSession = ViewerB.ActiveSessionExt;
            _comparisonPreviousRightView = ViewerB.CaptureComparisonViewStateExt();
        }
        _comparisonActive = false;
        _comparisonLeftPath = source;
        _comparisonRightPath = comparison;

        if (!_isSplit) OpenSplit();
        FocusPane(Viewer);
        if (!string.Equals(Viewer.CurrentFilePathExt, source, StringComparison.OrdinalIgnoreCase))
            Viewer.OpenInNewTabExt(source);
        FocusPane(ViewerB);
        ViewerB.OpenInNewTabExt(comparison);
        if (ViewerB.PageCountExt == 0)
        {
            FocusPane(Viewer);
            EndComparison(closeSplit: false);
            return;
        }
        FocusPane(Viewer);

        _comparisonActive = true;
        _comparisonReport = null;
        ComparePdfBtn.Tag = "on";
        ComparePdfBtn.ToolTip = Loc("Str_TT_ExitComparison");
        ComparisonFilesText.Text = $"{Path.GetFileName(source)}  ↔  {Path.GetFileName(comparison)}";
        ComparisonResultText.Text = Loc("Str_Compare_Comparing");
        ComparisonBar.Visibility = Visibility.Visible;
        int page = Math.Max(0, Viewer.CurrentPageIndex);
        Viewer.EnterComparisonViewExt(page);
        ViewerB.EnterComparisonViewExt(Math.Min(page, Math.Max(0, ViewerB.PageCountExt - 1)));
        _ = RefreshComparisonAsync(page);
    }

    private void ComparisonClose_Click(object sender, RoutedEventArgs e)
        => EndComparison(closeSplit: true);

    private void ComparisonPreviousDifference_Click(object sender, RoutedEventArgs e)
        => CycleComparisonRegion(-1);

    private void ComparisonNextDifference_Click(object sender, RoutedEventArgs e)
        => CycleComparisonRegion(1);

    private void CycleComparisonRegion(int direction)
    {
        if (!_comparisonActive || _comparisonRegions.Count == 0) return;
        _comparisonRegionIndex = _comparisonRegionIndex < 0
            ? (direction > 0 ? 0 : _comparisonRegions.Count - 1)
            : (_comparisonRegionIndex + direction + _comparisonRegions.Count) % _comparisonRegions.Count;
        ShowSelectedComparisonRegion();
    }

    private void ShowSelectedComparisonRegion()
    {
        Viewer.ShowDifferenceRegionsExt(_comparisonRegionPage, _comparisonRegionWidth,
            _comparisonRegionHeight, _comparisonRegions, _comparisonRegionIndex);
        ViewerB.ShowDifferenceRegionsExt(_comparisonRegionPage, _comparisonRegionWidth,
            _comparisonRegionHeight, _comparisonRegions, _comparisonRegionIndex);
        ComparisonRegionText.Text = $"{_comparisonRegionIndex + 1}/{_comparisonRegions.Count}";
    }

    private void SetComparisonRegions(int page, int width, int height,
        IReadOnlyList<DifferenceRegion> regions)
    {
        _comparisonRegionPage = page;
        _comparisonRegionWidth = width;
        _comparisonRegionHeight = height;
        _comparisonRegions = regions;
        _comparisonRegionIndex = -1;
        ComparisonRegionNavigation.Visibility = regions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ComparisonRegionText.Text = regions.Count > 0 ? $"0/{regions.Count}" : string.Empty;
    }

    private async void ComparisonDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!_comparisonActive || _comparisonLeftPath is null || _comparisonRightPath is null) return;
        if (_comparisonReport is null)
        {
            ComparisonResultButton.IsEnabled = false;
            _comparisonReportCts?.Cancel();
            _comparisonReportCts?.Dispose();
            _comparisonReportCts = new CancellationTokenSource();
            try
            {
                _comparisonReport = await BuildComparisonReportAsync(
                    _comparisonLeftPath, _comparisonRightPath, _comparisonReportCts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _comparisonReport = string.Format(Loc("Str_Compare_Failed"), ex.Message);
            }
            finally
            {
                ComparisonResultButton.IsEnabled = true;
            }
        }
        if (_comparisonReport is not null)
        {
            ComparisonDetailsText.Text = _comparisonReport;
            ComparisonDetailsPopup.IsOpen = true;
        }
    }

    private void EndComparison(bool closeSplit)
    {
        bool restoreSplit = _comparisonWasSplit;
        PdfViewer? restoreFocus = _comparisonPreviousFocusedViewer;
        PdfViewer.DocumentSession? restoreLeft = _comparisonPreviousLeftSession;
        PdfViewer.DocumentSession? restoreRight = _comparisonPreviousRightSession;
        ComparisonViewState restoreLeftView = _comparisonPreviousLeftView;
        ComparisonViewState restoreRightView = _comparisonPreviousRightView;
        _comparisonActive = false;
        _comparisonSyncing = false;
        _comparisonCts?.Cancel();
        _comparisonCts?.Dispose();
        _comparisonCts = null;
        _comparisonReportCts?.Cancel();
        _comparisonReportCts?.Dispose();
        _comparisonReportCts = null;
        _comparisonReport = null;
        SetComparisonRegions(-1, 0, 0, []);
        _comparisonLeftPath = null;
        _comparisonRightPath = null;
        Viewer.ClearDifferenceRegionsExt();
        ViewerB.ClearDifferenceRegionsExt();
        ComparePdfBtn.Tag = null;
        ComparePdfBtn.ToolTip = Loc("Str_TT_ComparePDFs");
        ComparisonBar.Visibility = Visibility.Collapsed;
        ComparisonDetailsPopup.IsOpen = false;
        ComparisonFilesText.Text = string.Empty;
        ComparisonResultText.Text = string.Empty;
        if (restoreLeft is not null)
        {
            FocusPane(Viewer);
            Viewer.SwitchToTabExt(restoreLeft);
            Viewer.RestoreComparisonViewStateExt(restoreLeftView);
        }
        if (restoreSplit && restoreRight is not null)
        {
            FocusPane(ViewerB);
            ViewerB.SwitchToTabExt(restoreRight);
            ViewerB.RestoreComparisonViewStateExt(restoreRightView);
        }
        _comparisonPreviousLeftSession = null;
        _comparisonPreviousRightSession = null;
        _comparisonPreviousFocusedViewer = null;
        _comparisonWasSplit = false;

        if (closeSplit && _isSplit && !restoreSplit) CloseSplit();
        else
        {
            if (restoreSplit && ReferenceEquals(restoreFocus, ViewerB)) FocusPane(ViewerB);
            else FocusPane(Viewer);
            SetStatus(Loc("Str_Compare_Ended"));
        }
    }

    private void ComparisonZoomChanged(PdfViewer source)
    {
        if (!_comparisonActive || _comparisonSyncing || !ReferenceEquals(source, ActiveViewer)) return;
        PdfViewer other = ReferenceEquals(source, Viewer) ? ViewerB : Viewer;
        _comparisonSyncing = true;
        try { other.ApplyComparisonZoomExt(source); }
        finally { _comparisonSyncing = false; }
    }

    private void ComparisonScrolled(PdfViewer source, double horizontalRatio, double verticalRatio)
    {
        if (!_comparisonActive || _comparisonSyncing || !ReferenceEquals(source, ActiveViewer)) return;
        PdfViewer other = ReferenceEquals(source, Viewer) ? ViewerB : Viewer;
        _comparisonSyncing = true;
        try { other.ScrollToComparisonPositionExt(source, horizontalRatio, verticalRatio); }
        finally { _comparisonSyncing = false; }
    }

    private void ComparisonPageChanged(PdfViewer source, int pageIndex)
    {
        if (!_comparisonActive || _comparisonSyncing || pageIndex < 0
            || !ReferenceEquals(source, ActiveViewer)) return;
        PdfViewer other = ReferenceEquals(source, Viewer) ? ViewerB : Viewer;
        _comparisonSyncing = true;
        try
        {
            // Continuous scrolling already carries the page and position within it. A second
            // page-jump request would snap that position to the page top after layout.
            if (other.PageCountExt > 0 && source.CaptureComparisonViewStateExt().View != ViewMode.Continuous)
                other.NavigateToPageExt(Math.Min(pageIndex, other.PageCountExt - 1));
        }
        finally { _comparisonSyncing = false; }
        _ = RefreshComparisonAsync(pageIndex);
    }

    private async Task RefreshComparisonAsync(int pageIndex)
    {
        if (!_comparisonActive || _comparisonLeftPath is null || _comparisonRightPath is null) return;
        _comparisonCts?.Cancel();
        _comparisonCts = new CancellationTokenSource();
        CancellationToken token = _comparisonCts.Token;
        try
        {
            var result = await Task.Run(() => ComparePagePair(
                _comparisonLeftPath, _comparisonRightPath, pageIndex, token));
            if (token.IsCancellationRequested || !_comparisonActive) return;
            Viewer.ClearDifferenceRegionsExt();
            ViewerB.ClearDifferenceRegionsExt();
            SetComparisonRegions(-1, 0, 0, []);
            if (!result.LeftPresent || !result.RightPresent)
            {
                PdfViewer present = result.LeftPresent ? Viewer : ViewerB;
                int presentPage = Math.Min(pageIndex, Math.Max(0, present.PageCountExt - 1));
                present.ShowMissingComparisonPageExt(presentPage,
                    result.LeftPresent ? Loc("Str_Compare_MissingRight") : Loc("Str_Compare_MissingLeft"));
                SetComparisonStatus(string.Format(Loc("Str_Compare_PageOnlyOne"), pageIndex + 1,
                    result.LeftPresent ? Loc("Str_Compare_Original") : Loc("Str_Compare_Comparison")));
            }
            else if (result.Difference.DimensionsMatch)
            {
                SetComparisonRegions(pageIndex, result.Width, result.Height, result.Difference.Regions);
                Viewer.ShowDifferenceRegionsExt(pageIndex, result.Width, result.Height, result.Difference.Regions);
                ViewerB.ShowDifferenceRegionsExt(pageIndex, result.Width, result.Height, result.Difference.Regions);
                SetComparisonStatus(result.Difference.IsDifferent
                    ? string.Format(Loc(result.PageDimensionsMatch
                            ? "Str_Compare_Changed" : "Str_Compare_ChangedAndDimensions"),
                        pageIndex + 1, result.Difference.ChangedFraction.ToString("P2"))
                    : string.Format(Loc(result.PageDimensionsMatch
                            ? "Str_Compare_NoDifferences" : "Str_Compare_DimensionsDiffer"),
                        pageIndex + 1));
            }
            else SetComparisonStatus(string.Format(Loc("Str_Compare_DimensionsDiffer"), pageIndex + 1));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetComparisonStatus(string.Format(Loc("Str_Compare_Failed"), ex.Message)); }
    }

    private void SetComparisonStatus(string text)
    {
        ComparisonResultText.Text = text;
        SetStatus(text);
    }

    private static async Task<string> BuildComparisonReportAsync(
        string leftPath, string rightPath, CancellationToken token)
    {
        var leftInfo = PdfEngineIntegration.ReadPageInformation(leftPath);
        var rightInfo = PdfEngineIntegration.ReadPageInformation(rightPath);
        int pageCount = Math.Max(leftInfo.Count, rightInfo.Count);
        int changed = 0;
        int missing = 0;
        var lines = new List<string>
        {
            $"{Path.GetFileName(leftPath)}: {leftInfo.Count} pages",
            $"{Path.GetFileName(rightPath)}: {rightInfo.Count} pages",
            string.Empty
        };
        for (int page = 0; page < pageCount; page++)
        {
            token.ThrowIfCancellationRequested();
            var result = await Task.Run(() => ComparePagePair(leftPath, rightPath, page, token));
            token.ThrowIfCancellationRequested();
            if (!result.LeftPresent || !result.RightPresent)
            {
                missing++;
                lines.Add($"Page {page + 1}: exists only in the {(result.LeftPresent ? "original" : "comparison")} PDF");
                continue;
            }
            if (!result.Difference.IsDifferent && result.PageDimensionsMatch) continue;
            changed++;
            var details = new List<string>();
            if (!result.PageDimensionsMatch) details.Add("page dimensions differ");
            if (result.Difference.IsDifferent)
            {
                details.Add($"{result.Difference.ChangedFraction:P2} changed");
                details.Add($"{result.Difference.Regions.Count} changed region{(result.Difference.Regions.Count == 1 ? "" : "s")}");
            }
            lines.Add($"Page {page + 1}: {string.Join(", ", details)}");
        }
        if (changed == 0 && missing == 0) lines.Add("No visual or structural page differences were found.");
        lines.Insert(3, $"{changed} changed pages, {missing} missing pages, {pageCount - changed - missing} matching pages");
        lines.Insert(4, string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    private static (PageDifferenceResult Difference, int Width, int Height,
        bool LeftPresent, bool RightPresent, bool PageDimensionsMatch) ComparePagePair(
        string leftPath, string rightPath, int pageIndex, CancellationToken token)
    {
        var leftInfo = PdfEngineIntegration.ReadPageInformation(leftPath);
        var rightInfo = PdfEngineIntegration.ReadPageInformation(rightPath);
        bool leftPresent = pageIndex < leftInfo.Count;
        bool rightPresent = pageIndex < rightInfo.Count;
        if (!leftPresent || !rightPresent)
            return (new PageDifferenceResult(false, 0, 1, []), 1, 1, leftPresent, rightPresent, false);
        if (token.IsCancellationRequested) return CanceledComparison();
        (int lw, int lh) = ComparisonRenderSize(leftInfo[pageIndex]);
        (int rw, int rh) = ComparisonRenderSize(rightInfo[pageIndex]);
        bool pageDimensionsMatch = lw == rw && lh == rh;
        byte[] left = PdfiumInterop.RenderPageWithAnnotations(leftPath, pageIndex, lw, lh)
            ?? throw new InvalidOperationException("The original page could not be rendered.");
        if (token.IsCancellationRequested) return CanceledComparison();
        byte[] right = PdfiumInterop.RenderPageWithAnnotations(rightPath, pageIndex, lw, lh)
            ?? throw new InvalidOperationException("The comparison page could not be rendered.");
        return (PdfPageDifference.Compare(left, lw, lh, right, lw, lh), lw, lh,
            true, true, pageDimensionsMatch);
    }

    private static (PageDifferenceResult Difference, int Width, int Height,
        bool LeftPresent, bool RightPresent, bool PageDimensionsMatch) CanceledComparison()
        => (new PageDifferenceResult(true, 0, 0, []), 1, 1, true, true, true);

    private static (int Width, int Height) ComparisonRenderSize(PdfPageInformation page)
    {
        double width = page.Rotation is 90 or 270 ? page.Height : page.Width;
        double height = page.Rotation is 90 or 270 ? page.Width : page.Height;
        const int longEdge = 1600;
        double scale = longEdge / Math.Max(width, height);
        return ((int)Math.Max(1, Math.Round(width * scale)),
                (int)Math.Max(1, Math.Round(height * scale)));
    }
}
