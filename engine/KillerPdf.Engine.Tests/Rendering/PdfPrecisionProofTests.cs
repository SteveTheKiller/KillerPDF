using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionProofTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProofPreservesExtendedIccRgbUntilPrinterConversion(bool resourceDefault)
    {
        var renderer = PdfPrecisionIccImageTests.Renderer(
            Convert.FromBase64String(PdfPrecisionIccImageTests.RgbProfileBase64), defaultSpace: resourceDefault);
        var options = new PdfRenderOptions(2, 1, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = renderer.RenderFrame(0, options);

        // The fixture's red matrix column contributes X only, giving PCS Y=0.
        // Its reference RGB has red above1 and green below0. Clipping that RGB
        // before the printer inverse would falsely raise Y above printer black0.25.
        foreach (int offset in new[] { 0, 4 })
        {
            double luminance = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
                + .0722 * frame.Pixels[offset + 2];
            Assert.InRange(luminance, .25 - .0001, .25 + .0001);
            Assert.Equal(1f, frame.Pixels[offset + 3]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProofUsesGrayIccBeforePrinterConversion(bool resourceDefault)
    {
        var renderer = PdfPrecisionIccImageTests.Renderer(
            Convert.FromBase64String(PdfPrecisionIccImageTests.GrayProfileBase64), components: 1,
            defaultSpace: resourceDefault);
        var options = new PdfRenderOptions(2, 1, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = renderer.RenderFrame(0, options);

        // Identity gray TRC gives linear samples32768/65535 and32769/65535.
        // Above the fixture printer's middle node, its inverse/forward keeps that Y.
        // Tolerance covers quantized ICC white and matrix constants, not byte rounding.
        double first = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        double second = .2126 * frame.Pixels[4] + .7152 * frame.Pixels[5] + .0722 * frame.Pixels[6];
        Assert.InRange(first, .5000076295109483 - .0001, .5000076295109483 + .0001);
        Assert.InRange(second, .500022888532845 - .0001, .500022888532845 + .0001);
        Assert.True(second > first);
        Assert.Equal(1f, frame.Pixels[3]);
        Assert.Equal(1f, frame.Pixels[7]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ProofConvertsSixteenBitImageAfterDecode(bool halfOpacity, bool gray)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes(halfOpacity ? "/Half gs /Image Do" : "/Image Do")).Build());
        var page = PdfPageTree.Read(document).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(document);
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(16)),
            Entry("ColorSpace", Name(gray ? "DeviceGray" : "DeviceRGB")),
            Entry("Decode", new PdfArray(Enumerable.Range(0, gray ? 2 : 6).Select(index =>
                (PdfObject)new PdfReal(index % 2 == 0 ? 0 : 65535d / 65536))))]),
            gray ? new byte[] { 128, 0 } : new byte[] { 128, 0, 128, 0, 128, 0 }));
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("Image", image)])),
            Entry("ExtGState", new PdfDictionary([Entry("Half", new PdfDictionary([
                Entry("ca", new PdfReal(.5))]))]))]);
        document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(page.Dictionary.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
        var options = new PdfRenderOptions(2, 1, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(document).RenderFrame(0, options);

        // Decode maps sample32768 exactly to encoded gray0.5 before the printer transform.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
        Assert.Equal(halfOpacity ? .5f : 1f, frame.Pixels[3]);
        Assert.Equal(0f, frame.Pixels[7]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProofPreservesSingleByteWordSpacing(bool setByDoubleQuote)
    {
        var renderer = PdfPrecisionTextTests.SimpleFontRenderer(
            setByDoubleQuote
                ? ".5 g BT /F1 10 Tf 4 TL 1 0 0 1 2 6 Tm (A) Tj 3 0 (A A) \" ET"
                : ".5 g BT /F1 10 Tf 3 Tw 1 0 0 1 2 2 Tm (A A) Tj ET");
        var options = new PdfRenderOptions(30, 16, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = renderer.RenderFrame(0, options);

        // A advances6; the one-byte space advances5 plus Tw3, placing the next A at16.
        foreach (int x in new[] { 7, 21 })
        {
            int offset = (9 * 30 + x) * 4;
            double luminance = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
                + .0722 * frame.Pixels[offset + 2];
            Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
            Assert.Equal(1f, frame.Pixels[offset + 3]);
        }
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 14) * 4 + 3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProofPreservesQuotedTextAtTheNextLineOrigin(bool doubleQuote)
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(30, 16,
            new PdfContentStreamBuilder().SetFillGray(.5).BeginText().SetFont(font, 10)
                .SetTextLeading(4).SetTextMatrix(1, 0, 0, 1, 2, 6).ShowUnicodeText("A")
                .MoveToNextTextLine().ShowUnicodeText(doubleQuote ? "AA" : "A").EndText()).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var reference = Assert.IsType<PdfIndirectReference>(page.Dictionary[Name("Contents")]);
        var stream = Assert.IsType<PdfStream>(source.Resolve(reference));
        string raw = Encoding.ASCII.GetString(source.DecodeStream(stream));
        Assert.Contains("T*\n", raw);
        raw = raw.Replace("T*\n", "", StringComparison.Ordinal);
        int lastShow = raw.LastIndexOf(" Tj", StringComparison.Ordinal);
        Assert.True(lastShow > 0);
        raw = raw.Remove(lastShow, 3).Insert(lastShow, doubleQuote ? " \"" : " '");
        if (doubleQuote)
        {
            int operand = raw.LastIndexOf('<', lastShow);
            Assert.True(operand > 0);
            raw = raw.Insert(operand, "0 2 ");
        }
        source = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber, new PdfStream(new PdfDictionary([]), Encoding.ASCII.GetBytes(raw))).Build());
        var options = new PdfRenderOptions(30, 16, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // Quote resets the advanced x coordinate and moves the baseline from6 to2.
        foreach (int y in new[] { 3, 13 })
        {
            int offset = (y * 30 + 7) * 4;
            double luminance = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
                + .0722 * frame.Pixels[offset + 2];
            Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
            Assert.Equal(1f, frame.Pixels[offset + 3]);
        }
        if (doubleQuote)
        {
            // Double quote sets character spacing2: glyph advance6 places the next origin at10.
            int offset = (13 * 30 + 15) * 4;
            double luminance = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
                + .0722 * frame.Pixels[offset + 2];
            Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
            Assert.Equal(1f, frame.Pixels[offset + 3]);
        }
        else Assert.Equal(0f, frame.Pixels[(13 * 30 + 14) * 4 + 3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProofPreservesLineOriginsAndExplicitOrMoveDefinedLeading(bool moveDefinesLeading)
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder().SetFillGray(.5).BeginText().SetFont(font, 10);
        if (!moveDefinesLeading) content.SetTextLeading(4);
        content.SetTextMatrix(1, 0, 0, 1, 2, 10).MoveText(0, -4).ShowUnicodeText("A")
            .MoveToNextTextLine().ShowUnicodeText("A").EndText();
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(30, 16, content).Build());
        if (moveDefinesLeading)
        {
            var page = PdfPageTree.Read(source).Pages[0];
            var reference = Assert.IsType<PdfIndirectReference>(page.Dictionary[Name("Contents")]);
            var stream = Assert.IsType<PdfStream>(source.Resolve(reference));
            string raw = Encoding.ASCII.GetString(source.DecodeStream(stream));
            Assert.Contains("0 -4 Td\n", raw);
            byte[] replaced = Encoding.ASCII.GetBytes(raw.Replace("0 -4 Td\n", "0 -4 TD\n", StringComparison.Ordinal));
            source = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
                .ReplaceObject(reference.ObjectNumber, new PdfStream(new PdfDictionary([]), replaced)).Build());
        }
        var options = new PdfRenderOptions(30, 16, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // Both forms set origins (2,6), then (2,2), independent of glyph advance.
        foreach (int y in new[] { 3, 13 })
        {
            int offset = (y * 30 + 7) * 4;
            double luminance = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
                + .0722 * frame.Pixels[offset + 2];
            Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
            Assert.Equal(1f, frame.Pixels[offset + 3]);
        }
        Assert.Equal(0f, frame.Pixels[(13 * 30 + 14) * 4 + 3]);
    }

    [Fact]
    public void ProofPreservesCharacterSpacingHorizontalScaleAndTextRise()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(30, 16,
            new PdfContentStreamBuilder().SetFillGray(.5).BeginText().SetFont(font, 10)
                .SetCharacterSpacing(8).SetHorizontalTextScale(50).SetTextRise(4)
                .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("AA")
                .SetTextRise(0).ShowUnicodeText("A").EndText()).Build());
        var options = new PdfRenderOptions(30, 16, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // (6-point glyph advance + 8-point spacing) * 50% gives origins x2,9,16.
        // Glyph widths are5; the first two baselines rise to6, then reset to2.
        foreach (var (x, y) in new[] { (4, 5), (11, 5), (18, 11) })
        {
            int offset = (y * 30 + x) * 4;
            double luminance = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
                + .0722 * frame.Pixels[offset + 2];
            Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
            Assert.Equal(1f, frame.Pixels[offset + 3]);
        }
        Assert.Equal(0f, frame.Pixels[(5 * 30 + 7) * 4 + 3]);
        Assert.Equal(0f, frame.Pixels[(13 * 30 + 4) * 4 + 3]);
    }

    [Fact]
    public void ProofConvertsGlyphStrokesWithoutFillingTheirInterior()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(16, 16,
            new PdfContentStreamBuilder().SetLineWidth(2).SetStrokeGray(.5)
                .BeginText().SetFont(font, 10).SetTextRenderingMode(PdfTextRenderingMode.Stroke)
                .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("A").EndText()).Build());
        var options = new PdfRenderOptions(16, 16, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // The two-point stroke at the triangle's horizontal base fully covers (7,14).
        int offset = (14 * 16 + 7) * 4;
        double luminance = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
            + .0722 * frame.Pixels[offset + 2];
        Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
        Assert.Equal(0f, frame.Pixels[(9 * 16 + 7) * 4 + 3]);
    }

    [Fact]
    public void ProofRetainsPositionedTextArraySpacingAndConvertsBothGlyphs()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(30, 16,
            new PdfContentStreamBuilder().SetFillGray(.5).BeginText().SetFont(font, 10)
                .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowPositionedUnicodeText(["A", "A"], [-800]).EndText()).Build());
        var options = new PdfRenderOptions(30, 16, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // Six-point advance plus the eight-point TJ adjustment puts the second
        // triangle at x16. Independent interior samples have the same proof color.
        foreach (int x in new[] { 7, 21 })
        {
            int offset = (9 * 30 + x) * 4;
            double luminance = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
                + .0722 * frame.Pixels[offset + 2];
            Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
            Assert.Equal(1f, frame.Pixels[offset + 3]);
        }
        Assert.Equal(0f, frame.Pixels[(9 * 30 + 14) * 4 + 3]);
    }

    [Fact]
    public void ProofTransformsEmbeddedGlyphColorAfterTextPainting()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(16, 16,
            new PdfContentStreamBuilder().SetFillGray(.5).BeginText().SetFont(font, 10)
                .SetTextMatrix(1, 0, 0, 1, 2, 2).ShowUnicodeText("A").EndText()).Build());
        var options = new PdfRenderOptions(16, 16, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                gamutWarnings: true, options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // The independent synthetic glyph is the triangle (0,0),(1000,0),(500,1000).
        // Pixel(7,9) lies wholly inside its translated 10-point outline.
        // Encoded gray1/2 and the fixture printer give linear Y=.3570205702411163.
        int offset = (9 * 16 + 7) * 4;
        double luminance = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
            + .0722 * frame.Pixels[offset + 2];
        Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(PdfGamutClassification.NotEvaluated, frame.PrinterGamutWarnings!.GetClassification(0, 0));
    }

    [Theory]
    [InlineData(".5 g 0 0 1 1 re f")]
    [InlineData(".5 G 1 w 0 .5 m 1 .5 l S")]
    public void ProofHonorsCalibratedDefaultGrayBeforePrinterConversion(string content)
    {
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes(content)).Build()), defaultGray: true);
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: GrayPrinter(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // CalGray gamma2 gives Y=.25. It remains inside the gray printer range.
        // Treating the same token as assumed sRGB would instead give Y=.21404114.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, .25 - .0001, .25 + .0001);
        Assert.Equal(1f, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(0, .5, .1250019, PdfGamutClassification.Outside)]
    [InlineData(.5, .5, .21404114048223255, PdfGamutClassification.Inside)]
    [InlineData(0, .0001, .1250019, PdfGamutClassification.Outside)]
    [InlineData(.5, .0001, .21404114048223255, PdfGamutClassification.Inside)]
    public void TransparentProofTransformsStraightColorAndLeavesUnpaintedPixelsUnevaluated(
        double gray, double alpha, double expectedLuminance, PdfGamutClassification expectedClassification)
    {
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            new PdfContentStreamBuilder().SetGraphicsState(new PdfGraphicsState(fillOpacity: alpha))
                .SetFillRgb(gray, gray, gray).Rectangle(0, 0, 1, 1).Fill()).Build()));
        var options = new PdfRenderOptions(2, 1, transparentBackground: true)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: GrayPrinter(),
                gamutWarnings: true, options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // Independent references: black maps to Y=8192/65535; encoded gray1/2
        // has linear Y=.21404114048223255 and remains above the printer black.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, expectedLuminance - .0001, expectedLuminance + .0001);
        Assert.Equal((float)alpha, frame.Pixels[3]);
        Assert.Equal(0f, frame.Pixels[7]);
        var mask = Assert.IsType<PdfGamutWarningMask>(frame.PrinterGamutWarnings);
        Assert.Equal(expectedClassification, mask.GetClassification(0, 0));
        Assert.Equal(PdfGamutClassification.NotEvaluated, mask.GetClassification(1, 0));
        Assert.Equal(expectedClassification == PdfGamutClassification.Outside ? 1 : 0, mask.WarningPixelCount);
        Assert.Equal(0, mask.BoundaryPixelCount);
        Half[] half = frame.ToPremultipliedLinearHalfRgba();
        Assert.Equal((Half)alpha, half[3]);
        Assert.Equal(new Half[4], half.AsSpan(4, 4).ToArray());
    }

    [Fact]
    public void ProofGamutWarningsReturnASeparateMaskWithoutChangingColorOrByteExports()
    {
        var printer = GrayPrinter();
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("0 g 0 0 1 1 re f")).Build()));
        var renderer = new PdfPageRenderer(source);
        PdfRenderOptions Options(bool warnings) => new(2, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: printer,
                gamutWarnings: warnings, options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };
        var normal = renderer.RenderFrame(0, Options(false));

        var warned = renderer.RenderFrame(0, Options(true));

        var mask = Assert.IsType<PdfGamutWarningMask>(warned.PrinterGamutWarnings);
        Assert.Equal(2, mask.Width);
        Assert.Equal(1, mask.Height);
        Assert.Equal(printer.ContentSha256, mask.PrinterProfileSha256);
        Assert.Equal(PdfGamutClassification.Outside, mask.GetClassification(0, 0));
        Assert.Equal(PdfGamutClassification.Inside, mask.GetClassification(1, 0));
        Assert.Equal(1, mask.WarningPixelCount);
        Assert.Equal(0, mask.BoundaryPixelCount);
        Assert.Equal(normal.Pixels.ToArray(), warned.Pixels.ToArray());
        Assert.Equal(normal.ToSrgbBgra32(), warned.ToSrgbBgra32());
        Assert.Throws<ArgumentException>(() => warned.ToPremultipliedLinearHalfRgba(maximumCombinedPixelBytes: 49));
        Assert.Equal(normal.ToPremultipliedLinearHalfRgba(maximumCombinedPixelBytes: 48),
            warned.ToPremultipliedLinearHalfRgba(maximumCombinedPixelBytes: 50));
        Assert.Null(normal.PrinterGamutWarnings);
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.GetClassification(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.GetClassification(0, 1));
    }

    [Fact]
    public void ProofGamutRegionOwnsLocalClassificationsAndCountsBothMaskPlanes()
    {
        var printer = GrayPrinter();
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("0 g 0 0 1 1 re f")).Build()));
        var renderer = new PdfPageRenderer(source);
        PdfRenderOptions Options(long limit) => new(2, 1, maximumPixelBytes: limit)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: printer,
                gamutWarnings: true, options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };
        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0, Options(75)));
        var full = renderer.RenderFrame(0, Options(76));
        Assert.Throws<ArgumentException>(() => renderer.RenderFrameRegion(0, Options(37), 1, 0, 1, 1));
        var region = renderer.RenderFrameRegion(0, Options(38), 1, 0, 1, 1);
        var mask = Assert.IsType<PdfGamutWarningMask>(region.PrinterGamutWarnings);
        Assert.Equal(1, mask.Width);
        Assert.Equal(1, mask.Height);
        Assert.Equal(PdfGamutClassification.Inside, mask.GetClassification(0, 0));
        Assert.Equal(0, mask.WarningPixelCount);
        Assert.Equal(full.Pixels.Slice(4, 4).ToArray(), region.Pixels.ToArray());
        Assert.Equal(PdfGamutClassification.Outside, full.PrinterGamutWarnings!.GetClassification(0, 0));
        Assert.Throws<OperationCanceledException>(() => renderer.RenderFrameRegion(0, Options(38),
            1, 0, 1, 1, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void PrinterGamutCheckKeepsAnInRangeNeutralInside()
    {
        var result = new PdfPrinterGamutCheck(GrayPrinter()).EvaluateXyz([.4821, .5, .41245]);

        Assert.InRange(result.DeltaE76, 0, .000000001);
        Assert.Equal(PdfGamutClassification.Inside, result.Classification);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void PrinterGamutCheckRejectsNonfiniteIntendedColors(double value)
    {
        var check = new PdfPrinterGamutCheck(GrayPrinter());

        Assert.Throws<ArgumentException>(() => check.EvaluateXyz([value, 0, 0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void PrinterGamutCheckRequiresExactlyThreeXyzChannels(int channels)
    {
        var check = new PdfPrinterGamutCheck(GrayPrinter());

        Assert.Throws<ArgumentException>(() => check.EvaluateXyz(new double[channels]));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("fallback")]
    [InlineData("reverse")]
    public void PrinterGamutCheckRequiresAnActualColorimetricPrinterPair(string condition)
    {
        var printer = Printer(condition == "class" ? "scnr" : "prtr",
            perceptualOnly: condition == "fallback", includeReverse: condition != "reverse");

        Assert.Throws<NotSupportedException>(() => new PdfPrinterGamutCheck(printer));
    }

    [Fact]
    public void PrinterGamutCheckRejectsAMissingPrinter()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfPrinterGamutCheck(null!));
    }

    [Theory]
    [InlineData(.23990960485386035, .18418651851244413, .04143416994136701, 50)]
    [InlineData(-.005337119193078847, -.005535282299397269, -.004566054368772807, 47.00029500625617)]
    public void PrinterGamutCheckUsesChromaticDifferenceAndUnclippedIntendedColor(
        double x, double y, double z, double expectedDifference)
    {
        var check = new PdfPrinterGamutCheck(GrayPrinter());
        double[] intended = [x, y, z];

        var result = check.EvaluateXyz(intended);

        // Independent Lab[50,30,40] loses chroma30/40 to neutral gray, giving50.
        // Lab[-5,0,0] compared with printer-black L42.000295 gives47.000295.
        Assert.InRange(result.DeltaE76, expectedDifference - .000001, expectedDifference + .000001);
        Assert.Equal(PdfGamutClassification.Outside, result.Classification);
        Assert.Equal(new[] { x, y, z }, intended);
    }

    [Theory]
    [InlineData(.10878026497312473, .11281919204845958, .09306455152077431, 1.949, PdfGamutClassification.Inside)]
    [InlineData(.10877444289581614, .11281315380192506, .09305957057120798, 1.95, PdfGamutClassification.Boundary)]
    [InlineData(.10848360382303521, .11251151609939350, .09281074963038970, 2, PdfGamutClassification.Boundary)]
    [InlineData(.10819328363957597, .11221041655214268, .09256237261386250, 2.05, PdfGamutClassification.Boundary)]
    [InlineData(.10818748252539598, .11220440004708150, .09255740959883753, 2.051, PdfGamutClassification.Outside)]
    public void PrinterGamutBoundaryBandIncludesBothEdges(double x, double y, double z,
        double expectedDifference, PdfGamutClassification classification)
    {
        var check = new PdfPrinterGamutCheck(GrayPrinter());

        var result = check.EvaluateXyz([x, y, z]);

        Assert.InRange(result.DeltaE76, expectedDifference - .0000000001, expectedDifference + .0000000001);
        Assert.Equal(classification, result.Classification);
    }

    [Fact]
    public void PrinterGamutCheckReportsIndependentBlackRoundTripDifference()
    {
        var check = new PdfPrinterGamutCheck(GrayPrinter());

        var result = check.EvaluateXyz([0, 0, 0]);

        // Independent CIE1976 neutral difference:116*cuberoot(8192/65535)-16.
        Assert.InRange(result.DeltaE76, 42.00029500625617 - .0000001, 42.00029500625617 + .0000001);
        Assert.Equal(PdfGamutClassification.Outside, result.Classification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsoluteProofDoesNotRequireCompensationEndpointsForAnIdentityPolicy(bool compensation)
    {
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(1, 1).Build()));
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: GrayPrinter(collapsed: true),
                options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.AbsoluteColorimetric,
                    BlackPointCompensation = compensation, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // Absolute source BPC is an identity, even for a valid constant printer curve.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, .1250019073777371 - .00003, .1250019073777371 + .00003);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsoluteProofUsesPrinterMediaWhiteDuringTheSourceConversion(bool compensation)
    {
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillRgb(.7353569830524495, .7353569830524495, .7353569830524495)
                .Rectangle(0, 0, 1, 1).Fill()).Build()));
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: GrayPrinter(tintedPaper: true),
                options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.AbsoluteColorimetric,
                    BlackPointCompensation = compensation, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // Absolute source Y1/2 divided by media-white Y58982/65536 gives this
        // relative printer appearance when paper simulation is off.
        Assert.InRange(frame.Pixels[0], .5555593231833441 - .00005, .5555593231833441 + .00005);
        Assert.InRange(frame.Pixels[1], .5555593231833441 - .00005, .5555593231833441 + .00005);
        Assert.InRange(frame.Pixels[2], .5555593231833441 - .00005, .5555593231833441 + .00005);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(PdfRenderingIntent.Perceptual)]
    [InlineData(PdfRenderingIntent.Saturation)]
    [InlineData(PdfRenderingIntent.AbsoluteColorimetric)]
    public void ProofDiagnosesIntentFallbackAndMissingAbsoluteWhite(PdfRenderingIntent intent)
    {
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(1, 1).Build()));
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { RenderingIntent = intent, BlackPointCompensation = false, SimulateBlack = true })
        };

        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(source).RenderFrame(0, options));

        Assert.Contains(intent == PdfRenderingIntent.AbsoluteColorimetric ? "media-white" : "fallback", error.Message);
    }

    [Theory]
    [InlineData(PdfRenderingIntent.Perceptual, 1)]
    [InlineData(PdfRenderingIntent.Saturation, .5)]
    public void IntentProofUsesTheActualRequestedPrinterInverse(PdfRenderingIntent intent, double expected)
    {
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes(".5 g 0 0 1 1 re f")).Build()));
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true,
                printerProfile: Printer(perceptualTables: true, perceptualReverseWhite: true, saturationReverseHalf: true),
                options: new PdfProofOptions { RenderingIntent = intent,
                    BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // B2A0 returns zero ink, B2A2 returns half black; A2B1 gives Y1 or Y1/2.
        // B2A1 would instead produce the existing approximately .357 luminance.
        Assert.InRange(frame.Pixels[0], expected - .0001, expected + .0001);
        Assert.InRange(frame.Pixels[1], expected - .0001, expected + .0001);
        Assert.InRange(frame.Pixels[2], expected - .0001, expected + .0001);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(0, false, 0, 0, 0)]
    [InlineData(1, false, .7084205909186849, .9649073041724270, .8354998544865850)]
    [InlineData(.7353569830524495, false, .3036079422444264, .4135304998637930, .3580703254788740)]
    [InlineData(0, true, 0, 0, 0)]
    [InlineData(1, true, .7084205909186849, .9649073041724270, .8354998544865850)]
    [InlineData(.7353569830524495, true, .3542102954593425, .4824536520862135, .4177499272432925)]
    public void PaperOnlyViewingKeepsPaperTintAndMapsPrinterBlackToReferenceBlack(
        double encoded, bool compensation, double red, double green, double blue)
    {
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillRgb(encoded, encoded, encoded).Rectangle(0, 0, 1, 1).Fill()).Build()));
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: GrayPrinter(tintedPaper: true),
                options: new PdfProofOptions { BlackPointCompensation = compensation, SimulatePaper = true, SimulateBlack = false })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // Preserve the independently calculated paper RGB endpoint. Interior values
        // scale by (.5-p)/(1-p), where p=8192/65535, or by .5 with source BPC.
        Assert.InRange(frame.Pixels[0], red - .000005, red + .000005);
        Assert.InRange(frame.Pixels[1], green - .000005, green + .000005);
        Assert.InRange(frame.Pixels[2], blue - .000005, blue + .000005);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Fact]
    public void PaperAndBlackSimulationPreserveTheSelectedPrinterPaperWhite()
    {
        var source = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(1, 1).Build()));
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: GrayPrinter(tintedPaper: true),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(source).RenderFrame(0, options);

        // Independent Decimal50 Bradford adaptation from quantized wtpt to D65,
        // then inversion of the standard sRGB primary matrix. ICC D50 is [.9642,1,.8249].
        Assert.InRange(frame.Pixels[0], .7084205909186849 - .000005, .7084205909186849 + .000005);
        Assert.InRange(frame.Pixels[1], .9649073041724270 - .000005, .9649073041724270 + .000005);
        Assert.InRange(frame.Pixels[2], .8354998544865850 - .000005, .8354998544865850 + .000005);
        Assert.Equal(1, frame.Pixels[3]);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
    }

    [Theory]
    [InlineData(.25, false, 0)]
    [InlineData(.25, true, .050876088171556791)]
    [InlineData(.7353569830524495, false, .428570182934272710)]
    [InlineData(.7353569830524495, true, .5)]
    public void DisablingBlackSimulationNormalizesPrinterAppearanceToReferenceBlack(
        double encoded, bool compensation, double expectedLuminance)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillRgb(encoded, encoded, encoded)
                .Rectangle(0, 0, 1, 1).Fill()).Build());
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: GrayPrinter(),
                options: new PdfProofOptions { BlackPointCompensation = compensation, SimulateBlack = false })
        };

        PdfRenderedFrame frame = new PdfPageRenderer(PageGroup(source)).RenderFrame(0, options);

        // Independent normalization (printerY-p)/(1-p), p=8192/65535.
        // The low sample clips to printer black without first-leg compensation;
        // the interior sample distinguishes normalization from copying original pixels.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, expectedLuminance - .00003, expectedLuminance + .00003);
        Assert.InRange(Math.Abs(frame.Pixels[0] - frame.Pixels[1]), 0, .00005);
        Assert.InRange(Math.Abs(frame.Pixels[1] - frame.Pixels[2]), 0, .00005);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void CompensationRejectsCollapsedPrinterBlackAndWhiteEndpoints(bool lut, bool sourceCompensation)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes(".25 g 0 0 1 1 re f")).Build());
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true,
                printerProfile: lut ? Printer(wideGamut: true, perceptualTables: true) : GrayPrinter(collapsed: true),
                options: new PdfProofOptions { BlackPointCompensation = sourceCompensation, SimulateBlack = sourceCompensation })
        };

        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(PageGroup(source)).RenderFrame(0, options));

        Assert.Contains("proof", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(.25, false, .125001907377737087)]
    [InlineData(.25, true, .169518387487931350)]
    [InlineData(.7353569830524495, false, .5)]
    [InlineData(.7353569830524495, true, .562500953688868543)]
    public void SourceCompensationPreservesShadowsBeforeThePrinterInverse(
        double encoded, bool compensation, double expectedLuminance)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillRgb(encoded, encoded, encoded)
                .Rectangle(0, 0, 1, 1).Fill()).Build());
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: GrayPrinter(),
                options: new PdfProofOptions { BlackPointCompensation = compensation, SimulateBlack = true })
        };

        PdfRenderedFrame frame = new PdfPageRenderer(PageGroup(source)).RenderFrame(0, options);

        // Independent encoded curve p=8192/65535: printer Y=p+(1-p)*device.
        // Without source BPC, the low sample clips to p; with it, Y becomes p+(1-p)*sourceY.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, expectedLuminance - .00003, expectedLuminance + .00003);
        Assert.InRange(Math.Abs(frame.Pixels[0] - frame.Pixels[1]), 0, .00005);
        Assert.InRange(Math.Abs(frame.Pixels[1] - frame.Pixels[2]), 0, .00005);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Fact]
    public void ProofRetainsPrinterAppearanceOutsideTheReferenceRgbGamut()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes(".5 g 0 0 1 1 re f")).Build());
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(wideGamut: true),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        PdfRenderedFrame frame = new PdfPageRenderer(PageGroup(source)).RenderFrame(0, options);

        // Synthetic forward PCS is approximately XYZ [.2,.7,.1], a green outside sRGB.
        // Preserve its negative red and above-unity green for downstream presentation.
        Assert.True(frame.Pixels[0] < 0);
        Assert.True(frame.Pixels[1] > 1);
        Assert.Equal(1, frame.Pixels[3]);
    }

    [Fact]
    public void ProofRegionMatchesTheFullFrameAndHonorsExactPixelStorageAndCancellation()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1,
            new PdfContentStreamBuilder().SetFillRgb(0, 0, 0).Rectangle(1, 0, 1, 1).Fill()
                .SetGraphicsState(new PdfGraphicsState(fillOpacity: .5))
                .SetFillRgb(1, 1, 1).Rectangle(1, 0, 1, 1).Fill()).Build());
        var renderer = new PdfPageRenderer(PageGroup(source));
        var proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
            options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true });
        PdfRenderOptions Options(long limit) => new(3, 1, maximumPixelBytes: limit) { Proof = proof };

        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0, Options(107)));
        PdfRenderedFrame full = renderer.RenderFrame(0, Options(108));
        Assert.Throws<ArgumentException>(() => renderer.RenderFrameRegion(0, Options(35), 1, 0, 1, 1));
        PdfRenderedFrame region = renderer.RenderFrameRegion(0, Options(36), 1, 0, 1, 1);

        Assert.Equal(full.Pixels.Slice(4, 4).ToArray(), region.Pixels.ToArray());
        double luminance = .2126 * region.Pixels[0] + .7152 * region.Pixels[1] + .0722 * region.Pixels[2];
        Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
        Assert.Equal(1, region.Pixels[3]);
        Assert.Throws<OperationCanceledException>(() => renderer.RenderFrame(0, Options(108), new CancellationToken(true)));
        Assert.Throws<OperationCanceledException>(() => renderer.RenderFrameRegion(0, Options(36),
            1, 0, 1, 1, new CancellationToken(true)));
    }

    [Fact]
    public void DisabledProofRetainsNormalFloatAndByteOutputEvenWithUnsupportedSelections()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("0 1 0 0 k 0 0 1 1 re f")).Build());
        var renderer = new PdfPageRenderer(PageGroup(source));
        var normal = new PdfRenderOptions(1, 1);
        var disabled = normal with
        {
            Proof = new PdfDocumentProofSettings(printerProfile: Printer("scnr"), gamutWarnings: true)
        };

        Assert.Equal(renderer.RenderFrame(0, normal).Pixels.ToArray(), renderer.RenderFrame(0, disabled).Pixels.ToArray());
        Assert.Equal(renderer.Render(0, normal).Pixels.ToArray(), renderer.Render(0, disabled).Pixels.ToArray());
    }

    [Theory]
    [InlineData("class")]
    [InlineData("fallback")]
    [InlineData("reverse")]
    public void ProofDiagnosesUnusablePrintingConditions(string condition)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes(".5 g 0 0 1 1 re f")).Build());
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true,
                printerProfile: Printer(condition == "class" ? "scnr" : "prtr",
                    perceptualOnly: condition == "fallback", includeReverse: condition != "reverse"),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };
        var document = PageGroup(source);

        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, options));

        Assert.Contains("proof", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cached")]
    [InlineData("uncached")]
    [InlineData("into")]
    [InlineData("region")]
    [InlineData("region-into")]
    public void ByteRenderingRejectsEnabledProofWithoutReturningCachedNormalPixelsOrTouchingDestination(string kind)
    {
        var renderer = new PdfPageRenderer(PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(1, 1).Build())));
        renderer.Render(0, new PdfRenderOptions(1, 1));
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };
        byte[] destination = [37, 41, 43, 47];
        Action render = kind switch
        {
            "cached" => () => renderer.Render(0, options),
            "uncached" => () => renderer.Render(0, options with { CacheResult = false }),
            "into" => () => renderer.RenderInto(0, options, destination),
            "region" => () => renderer.RenderRegion(0, options, 0, 0, 1, 1),
            _ => () => renderer.RenderRegionInto(0, options, 0, 0, 1, 1, destination)
        };

        var error = Assert.Throws<NotSupportedException>(render);

        Assert.Contains("proof", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 37, 41, 43, 47 }, destination);
    }

    [Fact]
    public void ProofConvertsPagesWithoutExplicitGroupsAfterCompositing()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillRgb(0, 0, 0).Rectangle(0, 0, 1, 1).Fill()
                .SetGraphicsState(new PdfGraphicsState(fillOpacity: .5))
                .SetFillRgb(1, 1, 1).Rectangle(0, 0, 1, 1).Fill()).Build());
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var frame = new PdfPageRenderer(document).RenderFrame(0, options);

        // Completed encoded gray1/2 gives linear Y=.21404114048223255.
        // The fixture printer's inverse/forward pair maps this to Y=.3570205702411163.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
        Assert.Equal(1f, frame.Pixels[3]);
    }

    [Theory]
    [InlineData("0 1 0 0 k 0 0 1 1 re f")]
    [InlineData("0 1 0 0 K 1 w 0 .5 m 1 .5 l S")]
    [InlineData("/DeviceCMYK cs 0 1 0 0 sc 0 0 1 1 re f")]
    [InlineData("/DeviceRGB cs .5 .5 .5 sc 0 0 1 1 re f")]
    public void ProofDiagnosesUnportedSourceColorOperators(string content)
    {
        var document = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes(content)).Build()));
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, options));

        Assert.Contains("proof", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("compensation")]
    [InlineData("black")]
    [InlineData("paper")]
    [InlineData("intent")]
    [InlineData("printing")]
    public void UnavailableProofSelectionsAreDiagnosedInsteadOfSilentlyIgnored(string selection)
    {
        var document = PageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(1, 1).Build()));
        var options = new PdfRenderOptions(1, 1, transparentBackground: selection == "transparent")
        {
            ForPrinting = selection == "printing",
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                gamutWarnings: selection == "warnings", options: new PdfProofOptions
                {
                    BlackPointCompensation = selection == "compensation",
                    SimulateBlack = selection != "black",
                    SimulatePaper = selection == "paper",
                    RenderingIntent = selection == "intent" ? PdfRenderingIntent.Perceptual
                        : PdfRenderingIntent.RelativeColorimetric
                })
        };

        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, options));

        Assert.Contains("proof", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProofConvertsTheCompletedRgbPageGroupBeforeReturningLinearReferenceColor()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillRgb(0, 0, 0).Rectangle(0, 0, 1, 1).Fill()
                .SetGraphicsState(new PdfGraphicsState(fillOpacity: .5))
                .SetFillRgb(1, 1, 1).Rectangle(0, 0, 1, 1).Fill()).Build());
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        PdfRenderedFrame frame = new PdfPageRenderer(PageGroup(source)).RenderFrame(0, options);

        // Independent example: completed encoded gray1/2 has linear Y=.21404114048223255.
        // Printer inverse K=1-Y and forward Y=.25+.5*(1-K) below the middle LUT node
        // give Y=.3570205702411163. Allow only the fixture's ICC node quantization.
        double luminance = .2126 * frame.Pixels[0] + .7152 * frame.Pixels[1] + .0722 * frame.Pixels[2];
        Assert.InRange(luminance, .3570205702411163 - .0001, .3570205702411163 + .0001);
        Assert.Equal(1, frame.Pixels[3]);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
    }

    private static PdfDocument PageGroup(PdfDocument source, bool defaultGray = false)
    {
        var catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(pages[Name("Kids")])));
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page));
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB"))]);
        IEnumerable<KeyValuePair<PdfName, PdfObject>> entries = original;
        if (defaultGray)
        {
            var white = new PdfArray([new PdfReal(.9642), new PdfInteger(1), new PdfReal(.8249)]);
            var parameters = new PdfDictionary([Entry("WhitePoint", white), Entry("Gamma", new PdfInteger(2))]);
            var colorSpaces = new PdfDictionary([Entry("DefaultGray", new PdfArray([Name("CalGray"), parameters]))]);
            entries = original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", new PdfDictionary([Entry("ColorSpace", colorSpaces)])));
        }
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source).ReplaceObject(page.ObjectNumber,
            new PdfDictionary(entries.Append(Entry("Group", group)))).Build());
    }

    private static PdfColorProfile Printer(string profileClass = "prtr", bool perceptualOnly = false,
        bool includeReverse = true, bool wideGamut = false, bool perceptualTables = false,
        bool perceptualReverseWhite = false, bool saturationReverseHalf = false)
    {
        byte[] forward = wideGamut ? Table(4, 3, _ => [6554, 22938, 3277]) : Table(4, 3, cell => (cell % 3) switch
        {
            0 => [31595, 32768, 27030],
            1 => [15798, 16384, 13515],
            _ => [7899, 8192, 6758]
        });
        byte[] reverse = Table(3, 4, cell => [0, 0, 0, (ushort)((cell / 3) % 3 == 0 ? 65535 : 0)]);
        List<(string Name, byte[] Data)> tags = [(perceptualOnly ? "A2B0" : "A2B1", forward)];
        if (includeReverse) tags.Add((perceptualOnly ? "B2A0" : "B2A1", reverse));
        if (perceptualTables)
        {
            tags.Add(("A2B0", forward));
            tags.Add(("B2A0", perceptualReverseWhite ? Table(3, 4, _ => [0, 0, 0, 0]) : reverse));
        }
        if (saturationReverseHalf) tags.Add(("B2A2", Table(3, 4, _ => [0, 0, 0, 32768])));
        int offset = 132 + 12 * tags.Count;
        byte[] data = new byte[offset + tags.Sum(tag => (tag.Data.Length + 3) & ~3)];
        Write32(data, 0, data.Length);
        data[8] = 4;
        Encoding.ASCII.GetBytes(profileClass).CopyTo(data, 12);
        "CMYK"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, tags.Count);
        for (int index = 0; index < tags.Count; index++)
        {
            int entry = 132 + 12 * index;
            Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(data, entry);
            Write32(data, entry + 4, offset);
            Write32(data, entry + 8, tags[index].Data.Length);
            tags[index].Data.CopyTo(data, offset);
            offset += (tags[index].Data.Length + 3) & ~3;
        }
        return PdfColorProfile.Load(data);
    }

    private static byte[] Table(int inputs, int outputs, Func<int, ushort[]> vertex)
    {
        int cells = (int)Math.Pow(3, inputs);
        byte[] data = new byte[52 + 4 * inputs + 2 * cells * outputs + 4 * outputs];
        "mft2"u8.CopyTo(data);
        data[8] = (byte)inputs; data[9] = (byte)outputs; data[10] = 3;
        for (int axis = 0; axis < 3; axis++) Write32(data, 12 + axis * 16, 65536);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50), 2);
        int offset = 52;
        void Append(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
            offset += 2;
        }
        for (int channel = 0; channel < inputs; channel++) { Append(0); Append(65535); }
        for (int cell = 0; cell < cells; cell++)
            foreach (ushort value in vertex(cell)) Append(value);
        for (int channel = 0; channel < outputs; channel++) { Append(0); Append(65535); }
        return data;
    }

    private static PdfColorProfile GrayPrinter(bool collapsed = false, bool tintedPaper = false)
    {
        byte[] data = new byte[192];
        Write32(data, 0, data.Length);
        data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 156); Write32(data, 140, 16);
        "wtpt"u8.CopyTo(data.AsSpan(144));
        Write32(data, 148, 172); Write32(data, 152, 20);
        "curv"u8.CopyTo(data.AsSpan(156));
        Write32(data, 164, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(168), 8192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(170), collapsed ? (ushort)8192 : (ushort)65535);
        "XYZ "u8.CopyTo(data.AsSpan(172));
        Write32(data, 180, tintedPaper ? 52429 : 63190);
        Write32(data, 184, tintedPaper ? 58982 : 65536);
        Write32(data, 188, tintedPaper ? 45875 : 54061);
        return PdfColorProfile.Load(data);
    }

    private static void Write32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
