using KillerPDF.Presentation;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class WindowsPdfDisplayColorModeTests
{
    [Fact]
    public void AFreshCaptureOfTheSameDisplayKeepsItsGeneration()
    {
        Assert.True(WindowsPdfDisplayDiscovery.SameObservation(Observed(4, "monitor.icc"), Observed(5, "monitor.icc")));
    }

    [Theory]
    [InlineData("other.icc")]
    [InlineData(null)]
    public void AChangedProfileAssociationIsANewObservation(string? profile)
    {
        Assert.False(WindowsPdfDisplayDiscovery.SameObservation(Observed(4, "monitor.icc"), Observed(4, profile)));
    }

    [Fact]
    public void AnotherMonitorIsANewObservation()
    {
        Assert.False(WindowsPdfDisplayDiscovery.SameObservation(Observed(4, "monitor.icc"),
            Observed(4, "monitor.icc") with { MonitorIdentity = "\\\\.\\DISPLAY2" }));
    }

    // VIEW-007: every step of a window drag or resize sent a window-moved message, and each one queued a full display
    // capture on the UI thread (about 11 ms). Only a move onto another monitor captures at once; a move within one
    // monitor captures at most once per 250 ms.
    [Fact]
    public void AMoveOntoAnotherMonitorCapturesAtOnce()
    {
        Assert.True(WindowsPdfDisplayDiscovery.ShouldCaptureAfterMove(100, 200, lastCaptureTick: 1000, nowTick: 1001));
    }

    [Theory]
    [InlineData(1000, 1001)]
    [InlineData(1000, 1100)]
    [InlineData(1000, 1249)]
    public void MovesWithinOneMonitorInsideTheIntervalDoNotCapture(long lastCapture, long now)
    {
        Assert.False(WindowsPdfDisplayDiscovery.ShouldCaptureAfterMove(100, 100, lastCapture, now));
    }

    [Theory]
    [InlineData(1000, 1250)]
    [InlineData(1000, 5000)]
    public void AMoveWithinOneMonitorCapturesOnceTheIntervalHasPassed(long lastCapture, long now)
    {
        Assert.True(WindowsPdfDisplayDiscovery.ShouldCaptureAfterMove(100, 100, lastCapture, now));
    }

    private static PdfDisplayDiscoverySnapshot Observed(long generation, string? profile) =>
        new(generation, PdfDisplayDiscoveryStatus.Available, "\\\\.\\DISPLAY1", new PdfDisplayBounds(0, 0, 1920, 1080), null, null)
        {
            // Fresh instances each time, as Capture builds them.
            TargetAssociation = new(PdfDisplayTargetStatus.SingleTarget,
                [new PdfDisplayTarget("target:7330/0", "Monitor")
                {
                    ColorState = new(PdfDisplayColorMode.ConventionalSdr, 15, 0, 0, 0, 8, null, null),
                    StandardProfile = profile is null ? new(PdfDisplayProfileStatus.NoProfile, 1, null, null, null)
                        : new(PdfDisplayProfileStatus.Named, 1, profile, null, null),
                    SdrWhiteLevel = new(1000, 80, null)
                }])
        };

    [Fact]
    public void ADefaultProfileQueryThatFindsNoAssociationIsNoProfile()
    {
        // ColorProfileGetDisplayDefault reports a display without an association as
        // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND), 0x80070002 (observed on SN01).
        var result = WindowsPdfDisplayDiscovery.DefaultProfileResult(0, unchecked((int)0x80070002), null);
        Assert.Equal(PdfDisplayProfileStatus.NoProfile, result.Status);
        Assert.Null(result.FailureOperation);
        Assert.Null(result.HResult);
    }

    [Fact]
    public void ADefaultProfileQueryThatFailsOtherwiseStaysUnavailable()
    {
        // E_ACCESSDENIED is a real query failure, not an absent association.
        var result = WindowsPdfDisplayDiscovery.DefaultProfileResult(1, unchecked((int)0x80070005), null);
        Assert.Equal(PdfDisplayProfileStatus.Unavailable, result.Status);
        Assert.Equal("ColorProfileGetDisplayDefault", result.FailureOperation);
        Assert.Equal(unchecked((int)0x80070005), result.HResult);
    }

    [Theory]
    [InlineData(null, PdfDisplayProfileStatus.NoProfile)]
    [InlineData("", PdfDisplayProfileStatus.NoProfile)]
    [InlineData("monitor.icc", PdfDisplayProfileStatus.Named)]
    public void ASuccessfulDefaultProfileQueryIsNamedOnlyWithAName(string? name, PdfDisplayProfileStatus expected)
    {
        var result = WindowsPdfDisplayDiscovery.DefaultProfileResult(1, 0, name);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == PdfDisplayProfileStatus.Named ? name : null, result.Name);
    }

    // Bit layout from wingdi.h DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO: bit 0 advancedColorSupported,
    // bit 1 advancedColorEnabled, bit 2 wideColorEnforced, bit 3 advancedColorForceDisabled.
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(9u)]
    public void LegacyInformationWithAdvancedColorOffIsConventionalSdr(uint flags)
    {
        Assert.Equal(PdfDisplayColorMode.ConventionalSdr, WindowsPdfDisplayDiscovery.LegacyColorMode(flags));
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(5u)]
    [InlineData(7u)]
    public void LegacyInformationWithAdvancedColorOnStaysUnknown(uint flags)
    {
        Assert.Equal(PdfDisplayColorMode.Unknown, WindowsPdfDisplayDiscovery.LegacyColorMode(flags));
    }
}
