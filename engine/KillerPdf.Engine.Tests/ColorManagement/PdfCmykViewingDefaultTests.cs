using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfCmykViewingDefaultTests
{
    [Fact]
    public void LoadsTheApprovedBundledSourceViewingProfileIdentity()
    {
        Assert.Equal("CGATS001Compat-v2-micro", PdfCmykViewingDefault.ProfileName);
        Assert.Equal("CGATS TR 001-1995 (North American web printing)", PdfCmykViewingDefault.PrintingCondition);
        Assert.Equal(PdfRenderingIntent.Perceptual, PdfCmykViewingDefault.EffectiveIntent);

        var profile = PdfCmykViewingDefault.LoadProfile();

        Assert.Equal("73E1BA37D2BAD5BAB2A964F40A9EED96209666EFC067C3322626214BBEF234A0",
            profile.ContentSha256);
    }
}
