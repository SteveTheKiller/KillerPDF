using KillerPDF.Presentation;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class WindowsPdfDisplayColorModeTests
{
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
