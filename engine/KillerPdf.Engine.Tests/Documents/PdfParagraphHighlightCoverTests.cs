using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfParagraphHighlightCoverTests
{
    [Fact]
    public void AColorSelectedInsideTheFormUsesItsLocalResources()
    {
        var source = PdfDocument.Open(Fixture("form-local"));
        var pixels = new PdfPageRenderer(source).Render(0, new PdfRenderOptions(300, 300)).Pixels.Span;
        int offset = (47 * 300 + 155) * 4;
        Assert.InRange(pixels[offset], 0, 1);
        Assert.InRange(pixels[offset + 1], 254, 255);
        Assert.InRange(pixels[offset + 2], 254, 255);
        Assert.NotNull(Plan(source));
    }

    [Theory]
    [InlineData("inherited-alias")]
    [InlineData("inherited-default")]
    public void InheritedBlackFillCannotBecomeAFormLocalYellowHighlight(string kind)
    {
        var source = PdfDocument.Open(Fixture(kind));
        var pixels = new PdfPageRenderer(source).Render(0, new PdfRenderOptions(300, 300)).Pixels.Span;
        int offset = (47 * 300 + 155) * 4;
        Assert.InRange(pixels[offset], 0, 1);
        Assert.InRange(pixels[offset + 1], 0, 1);
        Assert.InRange(pixels[offset + 2], 0, 1);
        Assert.ThrowsAny<NotSupportedException>(() => Plan(source));
    }

    [Fact]
    public void FullStrengthMultiplyBlackCoverStillRefusesReplacement()
    {
        var source = PdfDocument.Open(Fixture("multiply-black"));
        Assert.ThrowsAny<NotSupportedException>(() => Plan(source));
    }

    [Fact]
    public void NonneutralTransferFunctionStillRefusesReplacement()
    {
        var source = PdfDocument.Open(Fixture("transfer"));
        Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
            new PdfContentBounds(30, 247, 150, 259), new PdfContentBounds(30, 247, 200, 261),
            "Replacement words sit here today", PdfStandardFontSubstitutes.OcrTrainingFonts()[0],
            10, 12, PdfParagraphAlignment.Left));
    }

    [Fact]
    public void UnsupportedDefaultRgbDoesNotBypassOpaqueCoverRefusal()
    {
        var source = PdfDocument.Open(Fixture("default-rgb"));
        Assert.ThrowsAny<NotSupportedException>(() => Plan(source));
    }

    [Theory]
    [InlineData("overprint")]
    [InlineData("intent")]
    public void UntrackedPaintEffectsKeepFullStrengthCoverConservative(string effect)
    {
        var source = PdfDocument.Open(Fixture(effect));
        Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
            new PdfContentBounds(30, 247, 150, 259), new PdfContentBounds(30, 247, 200, 261),
            "Replacement words sit here today", PdfStandardFontSubstitutes.OcrTrainingFonts()[0],
            10, 12, PdfParagraphAlignment.Left));
    }

    [Fact]
    public void FullStrengthMultiplyHighlightKeepsParagraphEditable()
    {
        var source = PdfDocument.Open(Fixture("multiply-opaque"));
        Assert.NotNull(Plan(source));
    }

    [Fact]
    public void LaterTranslucentMultiplyHighlightKeepsParagraphEditable()
    {
        var source = PdfDocument.Open(Fixture("highlight"));
        var edited = PdfDocument.Open(PdfParagraphEditor.Apply(source, Plan(source)));
        string text = new PdfPageContentReader(edited).Read(0).Text;
        Assert.Contains("Replacement words sit here today", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Old words", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SameReplacementWithoutLaterPaintIsEditable()
    {
        var source = PdfDocument.Open(Fixture("none"));
        Assert.NotNull(Plan(source));
    }

    [Fact]
    public void OpaqueLaterPaintStillRefusesCoveredReplacement()
    {
        var source = PdfDocument.Open(Fixture("opaque"));
        var error = Assert.ThrowsAny<NotSupportedException>(() => Plan(source));
        Assert.Contains("cover the new box", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoringGraphicsStateRestoresOpaqueCoverRefusal()
    {
        var source = PdfDocument.Open(Fixture("restored"));
        Assert.ThrowsAny<NotSupportedException>(() => Plan(source));
    }

    [Fact]
    public void SoftMaskedMultiplyFillStillRefusesCoveredReplacement()
    {
        var source = PdfDocument.Open(Fixture("softmask"));
        Assert.ThrowsAny<NotSupportedException>(() => Plan(source));
    }

    [Fact]
    public void UnknownGraphicsStateDoesNotInheritHighlightExemption()
    {
        var source = PdfDocument.Open(Fixture("unknown"));
        Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
            new PdfContentBounds(30, 247, 150, 259), new PdfContentBounds(30, 247, 200, 261),
            "Replacement words sit here today", PdfStandardFontSubstitutes.OcrTrainingFonts()[0],
            10, 12, PdfParagraphAlignment.Left));
    }

    [Fact]
    public void PatternedMultiplyFillStillRefusesCoveredReplacement()
    {
        var source = PdfDocument.Open(Fixture("pattern"));
        Assert.ThrowsAny<NotSupportedException>(() => Plan(source));
    }

    [Fact]
    public void MultiplyStateIsInheritedByAnOrdinaryForm()
    {
        var source = PdfDocument.Open(Fixture("form"));
        Assert.NotNull(Plan(source));
    }

    [Fact]
    public void EditedBlackTextAndYellowHighlightRemainVisible()
    {
        var highlighted = PdfDocument.Open(Fixture("multiply-opaque"));
        var edited = PdfDocument.Open(PdfParagraphEditor.Apply(highlighted, Plan(highlighted)));
        var plain = PdfDocument.Open(Fixture("none"));
        var plainEdited = PdfDocument.Open(PdfParagraphEditor.Apply(plain, Plan(plain)));
        var blank = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 300, Array.Empty<byte>()).Build());
        var shown = CountPaint(edited);
        Assert.True(shown.Black > 20);
        Assert.True(shown.Yellow > 100);
        Assert.Equal(0, CountPaint(plainEdited).Yellow);
        Assert.Equal(0, CountPaint(blank).Black);
    }

    private static (int Black, int Yellow) CountPaint(PdfDocument document)
    {
        ReadOnlySpan<byte> pixels = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(600, 600)).Pixels.Span;
        int black = 0, yellow = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] < 70 && pixels[index + 1] < 70 && pixels[index + 2] < 70) black++;
            if (pixels[index] < 120 && pixels[index + 1] > 180 && pixels[index + 2] > 180) yellow++;
        }
        return (black, yellow);
    }

    private static PdfParagraphPlan Plan(PdfDocument source)
    {
        var paragraph = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        return PdfParagraphEditor.PlanFitted(source, 0, paragraph, 300, 300,
            "Replacement words sit here today", PdfStandardFontSubstitutes.OcrTrainingFonts()[0],
            10, 12, PdfParagraphAlignment.Left);
    }

    public static byte[] Fixture(string kind)
    {
        bool inherited = kind is "inherited-alias" or "inherited-default";
        bool scoped = inherited || kind == "form-local";
        string formPaint = kind == "form-local" ? "/Ink cs 1 1 0 sc 60 247 100 12 re f"
            : inherited ? "60 247 100 12 re f" : "1 1 0 rg 60 247 100 12 re f";
        const string blackSpace = "[/CalRGB << /WhitePoint [0.95047 1 1.08883] /Matrix [0 0 0 0 0 0 0 0 0] >>]";
        string paint = kind == "none" ? "" : kind == "multiply-black"
            ? "q /GS1 gs 0 0 0 rg 60 247 100 12 re f Q" : kind == "form"
            ? "q /GS1 gs /Fm Do Q" : scoped
            ? "q /GS1 gs " + (kind != "inherited-default" ? "/Ink cs 1 1 0 sc " : "1 1 0 rg ") + "/Fm Do Q" : kind == "pattern"
            ? "q /GS1 gs /Pattern cs /P1 scn 60 247 100 12 re f Q" : kind == "unknown"
            ? "q /GS1 gs /Missing gs 1 1 0 rg 60 247 100 12 re f Q" : kind == "restored"
            ? "q /GS1 gs Q q 1 1 0 rg 60 247 100 12 re f Q" : kind is "highlight" or "softmask" or "multiply-opaque" or "transfer" or "default-rgb" or "overprint" or "intent"
            ? "q /GS1 gs 1 1 0 rg 60 247 100 12 re f Q"
            : "q 1 1 0 rg 60 247 100 12 re f Q";
        string content = "BT /F1 10 Tf 30 250 Td (Old words sit here today) Tj ET\n" + paint;
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> /ExtGState << /GS1 6 0 R >> /Pattern << /P1 8 0 R >> /XObject << /Fm 9 0 R >> "
                + (kind == "default-rgb" ? "/ColorSpace << /DefaultRGB [ /CalRGB << /WhitePoint [0.95047 1 1.08883] >> ] >> " : "")
                + (scoped ? "/ColorSpace << /" + (kind != "inherited-default" ? "Ink" : "DefaultRGB") + " " + blackSpace + " >> " : "")
                + ">> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /ExtGState /ca " + (scoped || kind is "multiply-opaque" or "multiply-black" or "transfer" or "default-rgb" or "overprint" or "intent" ? "1" : "0.31") + " /BM /Multiply "
                + (kind == "softmask" ? "/SMask << /S /Alpha /G 7 0 R >> " : "")
                + (kind == "transfer" ? "/TR2 << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0] /N 1 >> " : "")
                + (kind == "overprint" ? "/op true " : "")
                + (kind == "intent" ? "/RI /AbsoluteColorimetric " : "") + ">>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 300 300] /Group << /S /Transparency /CS /DeviceGray >> /Length 20 >>\nstream\n1 g 0 0 300 300 re f\nendstream",
            "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Length 23 >>\nstream\n1 1 0 rg 0 0 10 10 re f\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 300 300] "
                + (scoped ? "/Resources << /ColorSpace << /" + (kind != "inherited-default" ? "Ink" : "DefaultRGB") + " /DeviceRGB >> >> " : "")
                + $"/Length {formPaint.Length} >>\nstream\n{formPaint}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
