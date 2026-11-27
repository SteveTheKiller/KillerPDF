using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Synthetic bold draws glyphs with fill plus a thin stroke (text rendering mode 2). The stroke
// width is a page-space length, so the weight it adds must shrink with the zoom instead of
// holding a one-pixel floor that makes bold text heavier the further the page zooms out.
public sealed class PdfFillStrokeTextWeightTests
{
    private const int PageWidth = 400, PageHeight = 60;
    private const string Mixed = "Terms and Conditions: Promise to Pay. Late Fee.";
    // Straight-edged capitals are closed orthogonal outlines, the shape stroke adjustment snaps.
    private const string Rectilinear = "TELL THE FILE HELL FILL LIFE ELITE";

    // At 400% the 0.3 pt stroke is 1.2 pixels wide, so that render shows the true weight. With the
    // one-pixel floor the weight ratio was 44% heavier at 133% and over three times heavier at 25%.
    // Painted as two antialiased layers, fill and stroke also counted their overlap twice on edge
    // pixels, which left text up to 15% heavier below 50% even without the floor.
    [Theory]
    [InlineData(1.33, false, false)]
    [InlineData(1, false, false)]
    [InlineData(0.75, false, false)]
    [InlineData(0.5, false, false)]
    [InlineData(0.33, false, false)]
    [InlineData(0.25, false, false)]
    [InlineData(1, true, true)]
    [InlineData(0.5, true, true)]
    [InlineData(0.25, true, true)]
    public void Render_FillAndStrokeTextKeepsItsWeightWhenZoomedOut(double zoom, bool rectilinear, bool strokeAdjust)
    {
        string text = rectilinear ? Rectilinear : Mixed;
        double reference = Ink(text, 2, 0.3, 4, strokeAdjust) / Ink(text, 0, 0.3, 4, strokeAdjust);
        double zoomed = Ink(text, 2, 0.3, zoom, strokeAdjust) / Ink(text, 0, 0.3, zoom, strokeAdjust);
        Assert.True(reference > 1.05, $"The stroke should add weight at 400%, ratio {reference:F3}.");
        Assert.True(Math.Abs(zoomed - reference) < 0.04 * reference,
            $"Bold weight at {zoom:P0} is {zoomed:F3} times regular, at 400% it is {reference:F3}.");
    }

    [Fact]
    public void Render_StrokeOnlyTextKeepsAVisibleOutlineWhenZoomedOut()
    {
        // Outline text has nothing but its stroke, so it keeps the thinnest visible line.
        double ink = Ink(Mixed, 1, 0.05, 0.25, false);
        Assert.True(ink > 0.6 * Ink(Mixed, 0, 0.05, 0.25, false),
            $"Outline text all but vanished at 25%, ink {ink:F0}.");
    }

    [Fact]
    public void Render_FillAndStrokeTextInTwoColorsKeepsAVisibleOutlineWhenZoomedOut()
    {
        // White letters with a thin black outline are outline text on a white page, not synthetic bold, so the
        // outline keeps the thinnest visible line as stroke-only text does.
        double outlined = Ink(Mixed, 2, 0.05, 0.25, false, "1 g 0 G");
        double outlineOnly = Ink(Mixed, 1, 0.05, 0.25, false, "1 g 0 G");
        Assert.True(outlined > 0.6 * outlineOnly,
            $"A white-filled outline all but vanished at 25%, ink {outlined:F0} against {outlineOnly:F0} for the outline alone.");
    }

    [Fact]
    public void Render_ZeroWidthFillAndStrokeTextKeepsItsThinnestLine()
    {
        // A zero line width means the thinnest line the device can draw (ISO 32000-1 8.4.3.2).
        double bold = Ink(Mixed, 2, 0, 1, false), regular = Ink(Mixed, 0, 0, 1, false);
        Assert.True(bold > 1.05 * regular, $"A 0 w stroke added nothing: {bold:F0} against {regular:F0}.");
    }

    private static double Ink(string text, int mode, double lineWidth, double zoom, bool strokeAdjust, string colors = "0 g 0 G")
    {
        string content = string.Create(CultureInfo.InvariantCulture,
            $"/GS gs {colors} {lineWidth} w BT /F1 11 Tf {mode} Tr 10 25 Td ({text}) Tj ET");
        PdfDocument document = PdfDocument.Open(Pdf(content, strokeAdjust));
        int width = (int)Math.Round(PageWidth * zoom), height = (int)Math.Round(PageHeight * zoom);
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(width, height,
            includeAnnotations: false, includeFormFields: false));
        Assert.Empty(page.Diagnostics);
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        double ink = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4) ink += 255 - pixels[offset + 1];
        // Normalize to page area so every zoom reports ink in the same units.
        return ink / (zoom * zoom);
    }

    private static byte[] Pdf(string content, bool strokeAdjust)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /ExtGState << /GS 6 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /ExtGState /SA {(strokeAdjust ? "true" : "false")} >>",
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
