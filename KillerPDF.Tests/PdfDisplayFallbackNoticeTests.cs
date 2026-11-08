using KillerPDF.Presentation;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfDisplayFallbackNoticeTests
{
    [Fact]
    public void UnobservableDisplayReportsAVisibleFallback()
    {
        var display = new PdfDisplayDiscoverySnapshot(3, PdfDisplayDiscoveryStatus.Unavailable,
            null, null, "MonitorFromWindow", null);

        Assert.Equal("DisplayPresentationNotEstablished", PdfDisplayFallbackNotice.Reason(display, inverted: false));
    }

    [Fact]
    public void UnknownColorModeReportsAVisibleFallback()
    {
        Assert.NotNull(PdfDisplayFallbackNotice.Reason(Single(PdfDisplayColorMode.Unknown), inverted: false));
    }

    [Fact]
    public void ConventionalSdrNeedsNoFallbackNotice()
    {
        Assert.Null(PdfDisplayFallbackNotice.Reason(Single(PdfDisplayColorMode.ConventionalSdr), inverted: false));
    }

    [Fact]
    public void InvertedViewingIsADeliberateModeNotAFallback()
    {
        Assert.Null(PdfDisplayFallbackNotice.Reason(Single(PdfDisplayColorMode.Unknown), inverted: true));
    }

    [Fact]
    public void ViewportNotYetAttachedDoesNotFlashANotice()
    {
        var display = new PdfDisplayDiscoverySnapshot(0, PdfDisplayDiscoveryStatus.Unavailable,
            null, null, "ViewportWindowNotAttached", null);

        Assert.Null(PdfDisplayFallbackNotice.Reason(display, inverted: false));
        Assert.Null(PdfDisplayFallbackNotice.Reason(null, inverted: false));
    }

    private static PdfDisplayDiscoverySnapshot Single(PdfDisplayColorMode mode) =>
        new(1, PdfDisplayDiscoveryStatus.Available, "\\\\.\\DISPLAY1", new(0, 0, 1920, 1080), null, null)
        {
            TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget,
            [
                new PdfDisplayTarget("target", "Monitor")
                {
                    ColorState = new(mode, 15, 0, 0, 0, 8, null, null)
                }
            ])
        };
}
