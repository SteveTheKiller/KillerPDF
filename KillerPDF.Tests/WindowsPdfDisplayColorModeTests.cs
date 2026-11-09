using KillerPDF.Presentation;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class WindowsPdfDisplayColorModeTests
{
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
