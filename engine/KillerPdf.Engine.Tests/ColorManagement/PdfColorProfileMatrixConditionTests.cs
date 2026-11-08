using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Tests.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfColorProfileMatrixConditionTests
{
    [Fact]
    public void ANearSingularMatrixHasNoReverseTransform()
    {
        // Columns (a,a,a), (a,a,a+e), (a,a+e,a) with a=1/2 and e one s15Fixed16 step: the
        // determinant is -a*e^2, about 1.2e-10, far above an absolute 1e-15 test but about
        // 1.8e-10 of the rows' Hadamard bound, so the inverse would amplify encoding error
        // by roughly ten orders of magnitude.
        int[][] columns = [[32768, 32768, 32768], [32768, 32768, 32769], [32768, 32769, 32768]];
        var profile = PdfColorProfile.Load(TestIccProfiles.MatrixRgb("mntr", columns));

        var reverse = profile.GetTransformSelection(PdfRenderingIntent.RelativeColorimetric,
            PdfColorTransformDirection.ConnectionToDevice);

        Assert.False(reverse.IsAvailable);
        Assert.True(profile.GetTransformSelection().IsAvailable);
    }

    [Fact]
    public void AnOrdinaryDisplayMatrixKeepsItsReverseTransform()
    {
        var profile = PdfColorProfile.Load(TestIccProfiles.MatrixRgb());

        Assert.True(profile.GetTransformSelection(PdfRenderingIntent.RelativeColorimetric,
            PdfColorTransformDirection.ConnectionToDevice).IsAvailable);
    }
}
