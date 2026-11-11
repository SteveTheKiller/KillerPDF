using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionWidgetFilteringTests
{
    [Fact]
    public void ExcludedTextWidgetPreservesPrecisePageColorsWithViewerOptions()
    {
        var document = PdfDocument.Open(DocumentWithTextWidget());
        var frame = new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(2, 1, includeAnnotations: true, includeFormFields: false)
            {
                IncludeSignedSignatureFields = true,
                IncludePushButtonFields = true
            });

        Assert.InRange(frame.Pixels[0], 0.2141336f, 0.2141339f);
        Assert.InRange(frame.Pixels[4], 0.2142261f, 0.2142265f);
        Assert.True(frame.Pixels[4] - frame.Pixels[0] > 0.00008f);
        Assert.Equal(1f, frame.Pixels[3]);
        Assert.Equal(1f, frame.Pixels[7]);
    }

    [Fact]
    public void SavedWidgetAppearanceRetainsFractionalColors()
    {
        byte[] bytes = DocumentWithTextWidget(appearanceContent:
            ".5001 g 0 0 60 60 re f .5002 g 60 0 60 60 re f");
        byte[] preserved = bytes.ToArray();
        var renderer = new PdfPageRenderer(PdfDocument.Open(bytes));
        var frame = renderer.RenderFrame(0,
            new PdfRenderOptions(600, 300, includeAnnotations: false, includeFormFields: true));

        AssertPixel(frame, 200, 180, .2141336f, .2141339f);
        AssertPixel(frame, 260, 180, .2142261f, .2142265f);
        Assert.True(frame.Pixels[(180 * 600 + 260) * 4] - frame.Pixels[(180 * 600 + 200) * 4] > .00008f);
        Assert.Equal(preserved, bytes);
    }

    private static void AssertPixel(PdfRenderedFrame frame, int x, int y, float low, float high)
    {
        int offset = (y * frame.Width + x) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], low, high);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    [Theory]
    [InlineData(4, true, false, true)]
    [InlineData(4, false, false, false)]
    [InlineData(2, true, false, false)]
    [InlineData(32, true, false, false)]
    [InlineData(0, true, true, false)]
    [InlineData(4, true, true, true)]
    public void WidgetVisibilityAndRegionRetainPrecisePaint(int flags, bool included, bool printing, bool visible)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithTextWidget(
            ".5002 g 0 0 120 60 re f", flags: flags)));
        var options = new PdfRenderOptions(600, 300, includeAnnotations: false, includeFormFields: included)
        {
            ForPrinting = printing
        };
        var frame = renderer.RenderFrameRegion(0, options, 180, 150, 120, 60);

        Assert.Equal(120, frame.Width);
        Assert.Equal(60, frame.Height);
        if (visible) AssertPixel(frame, 20, 30, .2142261f, .2142265f);
        else AssertPixel(frame, 20, 30, .2141336f, .2141339f);
    }

    [Theory]
    [InlineData("/FT /Sig /V << >>", true, false, true)]
    [InlineData("/FT /Sig /V << >>", false, true, false)]
    [InlineData("/FT /Sig", true, false, false)]
    [InlineData("/FT /Btn /Ff 65536", false, true, true)]
    [InlineData("/FT /Btn /Ff 65536", true, false, false)]
    [InlineData("/FT /Btn /Ff 0", false, true, false)]
    [InlineData("/FT /Tx /V (FIELD)", true, true, false)]
    public void ViewerWidgetOptionsSelectOnlyTheirFieldKinds(string field, bool signatures, bool buttons, bool visible)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithTextWidget(
            ".5002 g 0 0 120 60 re f", field: field)));
        var options = new PdfRenderOptions(600, 300, includeAnnotations: false, includeFormFields: false)
        {
            IncludeSignedSignatureFields = signatures,
            IncludePushButtonFields = buttons
        };
        var frame = renderer.RenderFrameRegion(0, options, 180, 150, 120, 60);

        if (visible) AssertPixel(frame, 20, 30, .2142261f, .2142265f);
        else AssertPixel(frame, 20, 30, .2141336f, .2141339f);
    }

    [Theory]
    [InlineData("BT /Missing 12 Tf (A) Tj ET")]
    [InlineData("BX .5 g 0 0 120 60 re f EX")]
    public void UnsupportedSavedWidgetPaintReportsPrecisionFailure(string appearance)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithTextWidget(appearance)));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(600, 300)));
    }

    [Fact]
    public void HiddenUnsupportedWidgetPaintDoesNotRejectThePage()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithTextWidget(
            "BT /Missing 12 Tf (A) Tj ET", flags: 2)));
        var frame = renderer.RenderFrameRegion(0, new PdfRenderOptions(600, 300), 180, 150, 120, 60);
        AssertPixel(frame, 20, 30, .2141336f, .2141339f);
    }

    [Fact]
    public void RequestedWidgetRegenerationStillReportsPrecisionFailure()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithTextWidget(needAppearances: true)));
        var error = Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(600, 300)));
        Assert.Contains("form-field", error.Message);
    }

    [Fact]
    public void WidgetPrinterProofRetainsHalfAlphaAndConvertsTheSourceColor()
    {
        byte[] bytes = DocumentWithTextWidget("/Half gs .25 g 0 0 120 60 re f", pageContent: "");
        byte[] preserved = bytes.ToArray();
        var renderer = new PdfPageRenderer(PdfDocument.Open(bytes));
        var options = new PdfRenderOptions(600, 300, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = true, SimulateBlack = true })
        };
        var frame = renderer.RenderFrameRegion(0, options, 200, 180, 1, 1);

        // Encoded .25 has Y=.05087608817. Compensating printer black 8192/65535 gives Y=.16951838749.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, .16948838749, .16954838749);
        Assert.Equal(.5f, frame.Pixels[3]);
        Assert.Equal(preserved, bytes);
    }

    private static PdfColorProfile Printer()
    {
        byte[] data = new byte[192];
        Write32(0, data.Length); data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12)); "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20)); "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132)); Write32(136, 156); Write32(140, 16);
        "wtpt"u8.CopyTo(data.AsSpan(144)); Write32(148, 172); Write32(152, 20);
        "curv"u8.CopyTo(data.AsSpan(156)); Write32(164, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(168), 8192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(170), 65535);
        "XYZ "u8.CopyTo(data.AsSpan(172));
        Write32(180, 63190); Write32(184, 65536); Write32(188, 54061);
        return PdfColorProfile.Load(data);
        void Write32(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }

    private static byte[] DocumentWithTextWidget(string appearanceContent = "q Q\n", int flags = 4,
        string field = "/FT /Tx /V (FIELD)", bool needAppearances = false,
        string pageContent = "0.5001 g 0 0 300 300 re f\n0.5002 g 300 0 300 300 re f\n")
    {
        string content = pageContent;
        string appearance = appearanceContent + "\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /Resources << >> /Contents 4 0 R /Annots [5 0 R] >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
            $"<< /Type /Annot /Subtype /Widget {field} /Rect [180 90 300 150] /T (overlayField)"
                + $" /DA (/Helv 12 Tf 0 g) /F {flags} /MK << /BG [0 1 0] >> /BS << /W 0 >> /P 3 0 R /AP << /N 6 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 120 60] /Resources << /Font << /Helv 8 0 R >> /ExtGState << /Half << /ca .5 >> >> >> /Length {Encoding.ASCII.GetByteCount(appearance)} >>\nstream\n{appearance}endstream",
            $"<< /Fields [5 0 R] /DR << /Font << /Helv 8 0 R >> >> /DA (/Helv 12 Tf 0 g) /NeedAppearances {(needAppearances ? "true" : "false")} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(text.ToString()));
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            text.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
