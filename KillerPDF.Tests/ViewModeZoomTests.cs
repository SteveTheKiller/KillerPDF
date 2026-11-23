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

    [Fact]
    public void SetupContinuousViewDefaultsToPreservingZoom()
    {
        string root = FindRepositoryRoot();
        string viewport = File.ReadAllText(Path.Combine(root, "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        Assert.Contains("internal void SetupContinuousView(int initialPage, bool fitDefault = false,", viewport);
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
