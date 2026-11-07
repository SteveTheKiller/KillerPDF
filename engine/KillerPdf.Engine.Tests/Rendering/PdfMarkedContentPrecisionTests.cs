using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfMarkedContentPrecisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedOptionalContentRetainsOuterVisibilityAndRestoresFollowingPaint(bool visible)
    {
        var outer = new PdfOptionalContentGroup("Outer", initiallyVisible: visible);
        var inner = new PdfOptionalContentGroup("Inner");
        var content = new PdfContentStreamBuilder().BeginOptionalContent(outer)
            .BeginOptionalContent(inner).SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill()
            .EndMarkedContent().EndMarkedContent().SetFillRgb(0, 1, 0).Rectangle(1, 0, 1, 1).Fill();
        byte[] source = new PdfDocumentBuilder().AddPage(2, 1, content).Build();
        byte[] original = source.ToArray();

        var frame = new PdfPageRenderer(PdfDocument.Open(source)).RenderFrame(0,
            new PdfRenderOptions(2, 1, transparentBackground: true));

        Assert.Equal(visible ? 1 : 0, frame.Pixels[3]);
        Assert.All(frame.Pixels.ToArray(), value => Assert.True(float.IsFinite(value)));
        if (visible) Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels[..4].ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, frame.Pixels[4..].ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void NestedMarkersKeepExactRegionPixelsAndLeaveSourceUntouched()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(2, 1, Encoding.ASCII.GetBytes(
            "/Artifact BMC 1 0 0 rg 0 0 1 1 re f /Span << /MCID 7 >> BDC 0 1 0 rg 1 0 1 1 re f EMC EMC")).Build();
        byte[] original = source.ToArray();
        var renderer = new PdfPageRenderer(PdfDocument.Open(source));
        var options = new PdfRenderOptions(2, 1, transparentBackground: true);

        Assert.Equal(new float[] { 1, 0, 0, 1, 0, 1, 0, 1 }, renderer.RenderFrame(0, options).Pixels.ToArray());
        Assert.Equal(new float[] { 0, 1, 0, 1 }, renderer.RenderFrameRegion(0, options, 1, 0, 1, 1).Pixels.ToArray());
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("1 i")]
    public void MarkersDoNotMakeOtherUnsupportedOperatorsImplicitlySupported(string operation)
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes($"/Artifact BMC {operation} 1 0 0 rg 0 0 1 1 re f EMC")).Build();

        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(PdfDocument.Open(source)).RenderFrame(0,
            new PdfRenderOptions(1, 1, transparentBackground: true)));
    }

    [Fact]
    public void MarkedContentPropertiesRetainTheirExactFloatPaint()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("/Span << /MCID 0 >> BDC 0 1 0 rg 0 0 1 1 re f EMC")).Build();
        byte[] original = source.ToArray();

        var frame = new PdfPageRenderer(PdfDocument.Open(source)).RenderFrame(0,
            new PdfRenderOptions(1, 1, transparentBackground: true));

        Assert.Equal(new float[] { 0, 1, 0, 1 }, frame.Pixels.ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void ArtifactMarkedContentRetainsItsExactFloatPaint()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("/Artifact BMC 1 0 0 rg 0 0 1 1 re f EMC")).Build();
        byte[] original = source.ToArray();
        var document = PdfDocument.Open(source);

        var frame = new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(1, 1, transparentBackground: true));

        Assert.Equal(new float[] { 1, 0, 0, 1 }, frame.Pixels.ToArray());
        Assert.Equal(original, source);
    }
}
