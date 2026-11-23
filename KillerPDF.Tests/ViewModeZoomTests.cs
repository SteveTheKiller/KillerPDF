using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ViewModeZoomTests
{
    [Fact]
    public void ApplyViewModeDoesNotUnconditionallyFitToPage()
    {
        string root = FindRepositoryRoot();
        string viewport = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        int start = viewport.IndexOf("internal void ApplyViewMode(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ApplyViewMode was not found.");

        Assert.DoesNotContain("else if (mode == ViewMode.Single || mode == ViewMode.TwoPage) FitToPage();", viewport[start..]);
        Assert.Contains("else if (_fitMode == FitMode.Width)     FitToWidth();", viewport[start..]);
        Assert.Contains("else if (_fitMode == FitMode.Page)      FitToPage();", viewport[start..]);
        Assert.Contains("else if (_active?.PendingZoom != null)  RestoreManualZoom();", viewport[start..]);
        Assert.Contains("SetTrueZoom(savedTrueZoom.Value);", viewport[start..]);
    }

    [Fact]
    public void ApplyViewModePreservesTrueZoomAndActiveFitMode()
    {
        string root = FindRepositoryRoot();
        string viewport = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        int start = viewport.IndexOf("internal void ApplyViewMode(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ApplyViewMode was not found.");

        string code = viewport[start..];
        Assert.Contains("FitMode savedFitMode = _fitMode;", code);
        Assert.Contains("savedTrueZoom = PersistableTrueZoomExt ??", code);
        Assert.Contains("modeSession.PendingZoom = new DeferredManualZoom(savedTrueZoom.Value);", code);
        Assert.Contains("SetupContinuousView(idx, fitDefault: false);", code);
    }

    // Reloads after a save or a page edit call SetupContinuousView without fitDefault and fit the page as
    // before #521; only the view-mode switch passes false to keep a custom zoom.
    [Fact]
    public void SetupContinuousViewStillFitsByDefault()
    {
        string root = FindRepositoryRoot();
        string viewport = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        Assert.Contains("internal void SetupContinuousView(int initialPage, bool fitDefault = true,", viewport);
    }

    // Grid fits its own columns, so a carried zoom must not be pending there (it could draw the grid at the
    // old zoom first); its exit zoom comes from PreGridTrueZoom. A fit mode re-fits in the new mode, so a
    // pending zoom or fit left from the old mode is dropped instead of replaying its steps.
    [Fact]
    public void ApplyViewModeQueuesTheZoomOnlyOutsideGridAndDropsItForAFit()
    {
        string root = FindRepositoryRoot();
        string viewport = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        int start = viewport.IndexOf("internal void ApplyViewMode(", StringComparison.Ordinal);
        string code = viewport[start..];
        Assert.Contains("if (mode != ViewMode.Grid && savedFitMode == FitMode.None && savedTrueZoom is > 0)", code);
        Assert.Contains("else if (mode == ViewMode.Grid || savedFitMode is FitMode.Width or FitMode.Page)", code);
    }

    // A document opened straight into Grid never recorded a pre-grid zoom, so leaving the grid kept its
    // tiny column zoom. With nothing recorded it fits the page, as before #521.
    [Fact]
    public void LeavingAGridTheDocumentOpenedInFitsThePage()
    {
        string code = ApplyViewModeSource();
        Assert.Contains("else savedFitMode = FitMode.Page;", code);
    }

    // The start screen's empty session must not carry a pre-grid zoom into the next file opened in it.
    [Fact]
    public void AnEmptySessionHoldsNoPreGridZoom()
    {
        string code = ApplyViewModeSource();
        Assert.Contains("if (mode == ViewMode.Grid && modeSession != null && HasReadDocument)", code);
        string root = FindRepositoryRoot();
        string tabs = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Tabs.cs"));
        int reuse = tabs.IndexOf("reuse the current empty tab", StringComparison.Ordinal);
        Assert.True(reuse > 0);
        string block = tabs[reuse..tabs.IndexOf("else", reuse, StringComparison.Ordinal)];
        Assert.Contains("target.PreGridFit = null;", block);
        Assert.Contains("target.PreGridTrueZoom = null;", block);
    }

    // Comparing from the grid goes through Continuous and back, which used to replace the user's pre-grid
    // zoom with the comparison's fit-width.
    [Fact]
    public void AComparisonRoundTripKeepsThePreGridZoom()
    {
        string root = FindRepositoryRoot();
        string api = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Api.cs"));
        Assert.Contains("FitMode? PreGridFit, double? PreGridTrueZoom)", api);
        int restore = api.IndexOf("internal void RestoreComparisonViewStateExt(", StringComparison.Ordinal);
        string body = api[restore..api.IndexOf("internal void ScrollToRatioExt(", restore, StringComparison.Ordinal)];
        Assert.Contains("_active.PreGridFit = state.PreGridFit;", body);
        Assert.Contains("_active.PreGridTrueZoom = state.PreGridTrueZoom;", body);
    }

    private static string ApplyViewModeSource()
    {
        string root = FindRepositoryRoot();
        string viewport = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        int start = viewport.IndexOf("internal void ApplyViewMode(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ApplyViewMode was not found.");
        return viewport[start..];
    }

    [Fact]
    public void DocumentSessionTracksPreGridFitAndTrueZoom()
    {
        string root = FindRepositoryRoot();
        string tabs = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Tabs.cs"));
        Assert.Contains("public FitMode? PreGridFit;", tabs);
        Assert.Contains("public double? PreGridTrueZoom;", tabs);

        string viewport = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        int start = viewport.IndexOf("internal void ApplyViewMode(", StringComparison.Ordinal);
        string code = viewport[start..];
        Assert.Contains("modeSession.PreGridFit = savedFitMode;", code);
        Assert.Contains("modeSession.PreGridTrueZoom = savedTrueZoom;", code);
        Assert.Contains("savedFitMode = preFit;", code);
        Assert.Contains("savedTrueZoom = modeSession.PreGridTrueZoom;", code);
    }

    private static string FindRepositoryRoot([CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
