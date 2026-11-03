using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionTextTests
{
    [Fact]
    public void EmbeddedGlyphFillsRetainSubByteGrayAndRgbSourceColors()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillGray(0.5)
            .BeginText().SetFont(TestFont(), 10)
            .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("A")
            .SetFillRgb(0.5001, 0.5001, 0.5001)
            .SetTextMatrix(1, 0, 0, 1, 16, 2).ShowUnicodeText("A").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // The synthetic glyph is the triangle (0,0), (1000,0), (500,1000).
        // At 10 points these samples are wholly inside its two translated copies.
        AssertGray(frame, 7, 9, 0.21404113f, 0.21404116f);
        AssertGray(frame, 21, 9, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[3]);
        AssertPaintedExtents(frame, 2, 4, 25, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void PositionedGlyphArrayPreservesPreciseColorAndPdfSpacing()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(0.5001, 0.5001, 0.5001)
            .BeginText().SetFont(TestFont(), 10)
            .SetTextMatrix(1, 0, 0, 1, 2, 2)
            .ShowPositionedUnicodeText(["A", "A"], [-800]).EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // The 600-unit advance is six points, then -800 adds eight points.
        // The second glyph therefore begins at x=16, leaving this gap unpainted.
        AssertGray(frame, 7, 9, 0.21413370f, 0.21413373f);
        AssertGray(frame, 21, 9, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 14) * 4 + 3]);
        AssertPaintedExtents(frame, 2, 4, 25, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void StrokedGlyphsRetainSubByteColorsWithoutFillingTheirInteriors()
    {
        var content = new PdfContentStreamBuilder()
            .SetLineWidth(2).SetStrokeGray(0.5)
            .BeginText().SetFont(TestFont(), 10)
            .SetTextRenderingMode(PdfTextRenderingMode.Stroke)
            .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("A")
            .SetStrokeRgb(0.5001, 0.5001, 0.5001)
            .SetTextMatrix(1, 0, 0, 1, 16, 2).ShowUnicodeText("A").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // At each triangle's horizontal base, a two-point stroke covers this whole pixel.
        AssertGray(frame, 7, 14, 0.21404113f, 0.21404116f);
        AssertGray(frame, 21, 14, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 7) * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 21) * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void RelativeTextMoveUsesTheLineOriginAndPreservesSubByteColors()
    {
        var content = new PdfContentStreamBuilder().SetFillGray(0.5)
            .BeginText().SetFont(TestFont(), 10).MoveText(2, 2).ShowUnicodeText("A")
            .SetFillRgb(0.5001, 0.5001, 0.5001).MoveText(14, 0).ShowUnicodeText("A").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // Td moves the line origin from (0,0) to (2,2), then to (16,2).
        // The first glyph's six-point advance does not move the line origin.
        AssertGray(frame, 7, 9, 0.21404113f, 0.21404116f);
        AssertGray(frame, 21, 9, 0.21413370f, 0.21413373f);
        AssertPaintedExtents(frame, 2, 4, 25, 13);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 14) * 4 + 3]);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void NextTextLineResetsTheAdvanceAtZeroLeading()
    {
        var content = new PdfContentStreamBuilder().SetFillGray(0.5)
            .BeginText().SetFont(TestFont(), 10).SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("A")
            .SetFillRgb(0.5001, 0.5001, 0.5001).MoveToNextTextLine().ShowUnicodeText("A").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // Default leading is zero. T* resets x from the advanced 8 back to line origin 2.
        AssertGray(frame, 7, 9, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 14) * 4 + 3]);
        AssertPaintedExtents(frame, 2, 4, 11, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void ExplicitLeadingMovesTheNextLineByTheSpecifiedDistance()
    {
        var content = new PdfContentStreamBuilder().SetFillGray(0.5)
            .BeginText().SetFont(TestFont(), 10).SetTextLeading(4)
            .SetTextMatrix(1, 0, 0, 1, 2, 6).ShowUnicodeText("A")
            .SetFillRgb(0.5001, 0.5001, 0.5001).MoveToNextTextLine().ShowUnicodeText("A").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // TL=4 and T* put the two triangle origins at (2,6) and (2,2).
        AssertGray(frame, 7, 3, 0.21404113f, 0.21404116f);
        AssertGray(frame, 7, 9, 0.21413370f, 0.21413373f);
        AssertPaintedExtents(frame, 2, 0, 11, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void TextMoveWithLeadingSetsTheFollowingLineDistance()
    {
        var content = new PdfContentStreamBuilder().SetFillGray(0.5)
            .BeginText().SetFont(TestFont(), 10).SetTextMatrix(1, 0, 0, 1, 2, 10)
            .MoveText(0, -4).ShowUnicodeText("A").SetFillRgb(0.5001, 0.5001, 0.5001)
            .MoveToNextTextLine().ShowUnicodeText("A").EndText();
        var renderer = Renderer(content, "0 -4 Td\n", "0 -4 TD\n");
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // TD moves (2,10) to (2,6) and sets leading to 4; T* then moves to (2,2).
        AssertGray(frame, 7, 3, 0.21404113f, 0.21404116f);
        AssertGray(frame, 7, 9, 0.21413370f, 0.21413373f);
        AssertPaintedExtents(frame, 2, 0, 11, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void CharacterSpacingAddsToTheUnscaledGlyphAdvance()
    {
        var content = new PdfContentStreamBuilder().SetFillRgb(0.5001, 0.5001, 0.5001)
            .BeginText().SetFont(TestFont(), 10).SetCharacterSpacing(8)
            .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("AA").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // A has a six-point advance, plus Tc=8, so origins are x=2 and x=16.
        AssertGray(frame, 7, 9, 0.21413370f, 0.21413373f);
        AssertGray(frame, 21, 9, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 14) * 4 + 3]);
        AssertPaintedExtents(frame, 2, 4, 25, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void HorizontalScaleChangesBothGlyphWidthAndAdvance()
    {
        var content = new PdfContentStreamBuilder().SetFillGray(0.5)
            .BeginText().SetFont(TestFont(), 10).SetHorizontalTextScale(50)
            .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("A")
            .SetFillRgb(0.5001, 0.5001, 0.5001).ShowUnicodeText("A").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // Tz=50 halves the ten-point triangle width and the six-point advance.
        // The two origins are (2,2) and (5,2), with five-point-wide glyphs.
        AssertGray(frame, 4, 11, 0.21404113f, 0.21404116f);
        AssertGray(frame, 8, 11, 0.21413370f, 0.21413373f);
        AssertPaintedExtents(frame, 2, 4, 9, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void TextRiseDisplacesTheGlyphWithoutMovingItsTextOrigin()
    {
        var content = new PdfContentStreamBuilder().SetFillRgb(0.5001, 0.5001, 0.5001)
            .BeginText().SetFont(TestFont(), 10).SetTextRise(4)
            .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("A")
            .SetTextRise(0).SetFillGray(0.5).ShowUnicodeText("A").EndText();
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // Ts=4 raises the triangle baseline from y=2 to y=6.
        // Resetting rise leaves the next advanced text origin at (8,2).
        AssertGray(frame, 7, 5, 0.21413370f, 0.21413373f);
        AssertGray(frame, 13, 13, 0.21404113f, 0.21404116f);
        Assert.Equal(0f, frame.Pixels[(13 * 30 + 7) * 4 + 3]);
        AssertPaintedExtents(frame, 2, 0, 17, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void WordSpacingAppliesToAnExplicitSingleByteSpace()
    {
        var renderer = SimpleFontRenderer("0.5 g BT /F1 10 Tf 3 Tw 1 0 0 1 2 2 Tm "
            + "(A ) Tj 0.5001 0.5001 0.5001 rg (A) Tj ET");
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);
        Assert.Empty(renderer.Render(0, options).Diagnostics);

        var frame = renderer.RenderFrame(0, options);

        // One-byte A advances 6, space advances 5+Tw(3), so the next A starts at 16.
        AssertGray(frame, 7, 9, 0.21404113f, 0.21404116f);
        AssertGray(frame, 21, 9, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 14) * 4 + 3]);
        AssertPaintedExtents(frame, 2, 4, 25, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void QuoteShowsTextAtTheNextLineOrigin()
    {
        var renderer = SimpleFontRenderer("0.5 g BT /F1 10 Tf 4 TL 1 0 0 1 2 6 Tm "
            + "(A) Tj 0.5001 0.5001 0.5001 rg (A) ' ET");
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);
        Assert.Empty(renderer.Render(0, options).Diagnostics);

        var frame = renderer.RenderFrame(0, options);

        // Quote resets the advanced x and moves down by leading 4 to origin (2,2).
        AssertGray(frame, 7, 3, 0.21404113f, 0.21404116f);
        AssertGray(frame, 7, 9, 0.21413370f, 0.21413373f);
        AssertPaintedExtents(frame, 2, 0, 11, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void DoubleQuoteSetsBothSpacingValuesBeforeTheNextLine()
    {
        var renderer = SimpleFontRenderer("0.5 g BT /F1 10 Tf 4 TL 1 0 0 1 2 6 Tm "
            + "(A) Tj 0.5001 0.5001 0.5001 rg 3 2 (A A) \" ET");
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);
        Assert.Empty(renderer.Render(0, options).Diagnostics);

        var frame = renderer.RenderFrame(0, options);

        // Double quote sets Tw=3 and Tc=2, then starts the next line at (2,2).
        // First A advances 6+2; the space advances 5+2+3, so second A starts at x=20.
        AssertGray(frame, 7, 3, 0.21404113f, 0.21404116f);
        AssertGray(frame, 7, 11, 0.21413370f, 0.21413373f);
        AssertGray(frame, 25, 9, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(11 * 30 + 14) * 4 + 3]);
        AssertPaintedExtents(frame, 2, 0, 29, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    [Fact]
    public void WordSpacingDoesNotApplyToTwoByteCidCode32()
    {
        var content = new PdfContentStreamBuilder().BeginText().SetFont(TestFont(), 10)
            .SetWordSpacing(12).SetTextRenderingMode(PdfTextRenderingMode.Invisible);
        for (int code = 1; code < 32; code++)
            content.ShowUnicodeText("A", $"registration-{code}");
        content.SetTextRenderingMode(PdfTextRenderingMode.Fill).SetTextMatrix(1, 0, 0, 1, 2, 2)
            .SetFillGray(0.5).ShowUnicodeText("A", "cid-space")
            .SetFillRgb(0.5001, 0.5001, 0.5001).ShowUnicodeText("A", "cid-space").EndText();
        Assert.Contains("<0020> Tj", Encoding.ASCII.GetString(content.Build()));
        var renderer = Renderer(content);
        var options = new PdfRenderOptions(30, 16, transparentBackground: true);

        var frame = renderer.RenderFrame(0, options);

        // CID 0x0020 is a two-byte code for triangle A, not a one-byte word space.
        // Its six-point advance puts the next glyph at (8,2), with no Tw addition.
        AssertGray(frame, 7, 9, 0.21404113f, 0.21404116f);
        AssertGray(frame, 13, 9, 0.21413370f, 0.21413373f);
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 23) * 4 + 3]);
        AssertPaintedExtents(frame, 2, 4, 17, 13);
        Assert.Equal(renderer.Render(0, options).Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    private static PdfPageRenderer SimpleFontRenderer(string content)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(30, 16, Encoding.ASCII.GetBytes(content)).Build());
        var page = PdfPageTree.Read(document).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(document);
        byte[] fontBytes = TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true);
        var fontFile = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Length1", new PdfInteger(fontBytes.Length))]), fontBytes));
        var descriptor = update.AddObject(new PdfDictionary([
            Entry("Type", Name("FontDescriptor")), Entry("FontName", Name("KillerTest")),
            Entry("Flags", new PdfInteger(32)), Entry("FontBBox", Integers(0, 0, 1000, 1000)),
            Entry("ItalicAngle", new PdfInteger(0)), Entry("Ascent", new PdfInteger(1000)),
            Entry("Descent", new PdfInteger(0)), Entry("CapHeight", new PdfInteger(1000)),
            Entry("StemV", new PdfInteger(80)), Entry("FontFile2", fontFile)]));
        // Code 32 explicitly names space and has width 500; code 65 names triangle A with width 600.
        // The fixture's empty glyph 0 supplies the blank space outline.
        var encoding = new PdfDictionary([Entry("BaseEncoding", Name("WinAnsiEncoding")),
            Entry("Differences", new PdfArray([new PdfInteger(32), Name("space"), new PdfInteger(65), Name("A")]))]);
        var font = update.AddObject(new PdfDictionary([
            Entry("Type", Name("Font")), Entry("Subtype", Name("TrueType")), Entry("BaseFont", Name("KillerTest")),
            Entry("Encoding", encoding), Entry("FirstChar", new PdfInteger(32)), Entry("LastChar", new PdfInteger(65)),
            Entry("Widths", new PdfArray(Enumerable.Range(32, 34)
                .Select(code => (PdfObject)new PdfInteger(code == 65 ? 600 : 500)))), Entry("FontDescriptor", descriptor)]));
        var resources = new PdfDictionary([Entry("Font", new PdfDictionary([Entry("F1", font)]))]);
        return new PdfPageRenderer(PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(page.Dictionary.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build()));
    }

    private static PdfArray Integers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);

    private static TrueTypeFont TestFont() => TrueTypeFont.Load(
        TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

    private static PdfPageRenderer Renderer(PdfContentStreamBuilder content,
        string? original = null, string? replacement = null)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(30, 16, content).Build());
        if (original is null) return new PdfPageRenderer(document);
        var page = PdfPageTree.Read(document).Pages[0];
        var reference = Assert.IsType<PdfIndirectReference>(page.Dictionary[Name("Contents")]);
        var stream = Assert.IsType<PdfStream>(document.Resolve(reference));
        string raw = Encoding.ASCII.GetString(document.DecodeStream(stream));
        Assert.Contains(original, raw);
        byte[] replaced = Encoding.ASCII.GetBytes(raw.Replace(original, replacement, StringComparison.Ordinal));
        return new PdfPageRenderer(PdfDocument.Open(new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(reference.ObjectNumber, new PdfStream(new PdfDictionary([]), replaced)).Build()));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static void AssertGray(PdfRenderedFrame frame, int x, int y, float minimum, float maximum)
    {
        int offset = (y * frame.Width + x) * 4;
        // Independent literal sRGB decoding ranges, before byte quantization.
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], minimum, maximum);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    private static void AssertPaintedExtents(PdfRenderedFrame frame,
        int expectedLeft, int expectedTop, int expectedRight, int expectedBottom)
    {
        int left = frame.Width, top = frame.Height, right = -1, bottom = -1;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                if (frame.Pixels[(y * frame.Width + x) * 4 + 3] > 0)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
        Assert.Equal((expectedLeft, expectedTop, expectedRight, expectedBottom),
            (left, top, right, bottom));
    }
}
