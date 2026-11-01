using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// The dialog is not linked into this project, so this pins its wiring to the tested base.
public sealed class ExportImagesScaleTests
{
    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }

    [Fact]
    public void TheRestrictionNoticeIsAnnouncedToScreenReaders()
    {
        string notice = Read("Controls", "Viewer", "PdfViewer.Restriction.cs");
        Assert.Contains("AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Assertive)", notice, StringComparison.Ordinal);
        Assert.Contains("AutomationEvents.LiveRegionChanged", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRestrictionNoticeCanBeDismissedAndTheTitleStaysReadOnly()
    {
        string notice = Read("Controls", "Viewer", "PdfViewer.Restriction.cs");
        Assert.Contains("\"OverlayCloseButton\"", notice, StringComparison.Ordinal);
        Assert.Contains("_restrictionDismissed.TryGetValue(_doc", notice, StringComparison.Ordinal);
        Assert.Contains("\"Str_Restricted_TitleSuffix\"", notice, StringComparison.Ordinal);
        Assert.Contains("Str_Restricted_TitleSuffix", Read("Strings", "en-US.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void TabCloseIsOnlyRedOnHover()
    {
        string viewer = Read("Controls", "Viewer", "PdfViewer.xaml");
        int tag = viewer.IndexOf("x:Name=\"tabClose\"", StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource TabCloseButton}\"",
            viewer[tag..viewer.IndexOf("/>", tag, StringComparison.Ordinal)], StringComparison.Ordinal);
        string window = Read("Shell", "MainWindow.xaml");
        int style = window.IndexOf("x:Key=\"TabCloseButton\"", StringComparison.Ordinal);
        string body = window[style..window.IndexOf("</Style>", style, StringComparison.Ordinal)];
        Assert.DoesNotContain("TargetName=\"Bd\" Property=\"Background\"", body, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource DangerRed}\"/>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void OneTimesIsTheRealImageSizeWhenThePageIsOneImage()
    {
        Assert.Contains("OutputPixelDimensions.ScaleBase(", Read("Controls", "ExportImagesDialog.cs"), StringComparison.Ordinal);
        Assert.Contains("engineSession.PageImagePixels(previewPage, _pageRotations)",
            Read("Shell", "FileOperations.cs"), StringComparison.Ordinal);
    }
}
