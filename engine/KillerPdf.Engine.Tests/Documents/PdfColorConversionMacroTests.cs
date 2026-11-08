using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfColorConversionMacroTests
{
    // Synthetic affine CMYK profile (A2B1 only), as in PdfWorkingCmykByteRenderTests.
    private const string AffineCmyk =
        "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=";

    [Fact]
    public void ProfiledImagesFailSafelyInsteadOfTreatingProfileSamplesAsDeviceColors()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().DrawImage(PdfImage.FromCmyk(1, 1, new byte[] { 255, 0, 0, 0 }), 0, 0, 1, 1)).Build());
        PdfName Name(string name) => new(System.Text.Encoding.ASCII.GetBytes(name));
        var catalog = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var page = (PdfDictionary)document.Resolve((PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0]);
        var resources = (PdfDictionary)page[Name("Resources")];
        var images = (PdfDictionary)resources[Name("XObject")];
        var reference = (PdfIndirectReference)images.Values.Single();
        var image = (PdfStream)document.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(document);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([new(Name("N"), new PdfInteger(4))]),
            KillerPdf.Engine.Tests.Rendering.PdfBlackPointControlTests.CmykProfile()));
        var dictionary = image.Dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        dictionary[Name("ColorSpace")] = new PdfArray([Name("ICCBased"), profile]);
        byte[] source = update.ReplaceObject(reference.ObjectNumber,
            new PdfStream(new PdfDictionary(dictionary), image.EncodedData.Span)).Build();

        Assert.Throws<NotSupportedException>(() => PdfColorConversionMacro.Execute(
            PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale), source, new Dictionary<string, string>()));
    }

    // ISO 32000 DeviceRGB to DeviceGray: 0.3 R + 0.59 G + 0.11 B. Pure blue is 0.11 * 255 = 28.05,
    // so it becomes 28; Rec. 601 integer weights (29/256) would give 29.
    [Theory]
    [InlineData(0, 0, 1, 28)]
    [InlineData(0, 1, 0, 150)]
    [InlineData(1, 0, 0, 77)]
    public void RgbPaintBecomesGrayWithTheSpecificationWeights(double red, double green, double blue, int expected)
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillRgb(red, green, blue).Rectangle(0, 0, 1, 1).Fill()).Build();

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            source, new Dictionary<string, string>());

        var rendered = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, new PdfRenderOptions(1, 1));
        Assert.Equal(new byte[] { (byte)expected, (byte)expected, (byte)expected, 255 }, rendered.Pixels.ToArray());
    }

    // Untagged CMYK paint becomes the gray of its working-profile display color, the same color
    // managed and byte views show for it.
    [Fact]
    public void UntaggedCmykPaintBecomesTheGrayOfItsWorkingProfileColor()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillCmyk(1, 0, 0, 0).Rectangle(0, 0, 1, 1).Fill()).Build();
        byte[] shown = new PdfPageRenderer(PdfDocument.Open(source)).RenderFrame(0, new PdfRenderOptions(1, 1)).ToSrgbBgra32();
        int expected = (int)Math.Round(0.3 * shown[2] + 0.59 * shown[1] + 0.11 * shown[0], MidpointRounding.AwayFromZero);

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            source, new Dictionary<string, string>());

        byte gray = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, new PdfRenderOptions(1, 1)).Pixels.Span[0];
        Assert.InRange(gray, expected - 1, expected + 1);
    }

    // With a chosen working CMYK profile, untagged CMYK paint becomes the gray of the color that
    // profile shows, the same color every view shows with that choice.
    [Fact]
    public void UntaggedCmykPaintBecomesTheGrayOfTheChosenWorkingProfileColor()
    {
        var chosen = KillerPdf.Engine.ColorManagement.PdfColorProfile.Load(Convert.FromBase64String(AffineCmyk));
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().SetFillCmyk(1, 0, 0, 0).Rectangle(0, 0, 1, 1).Fill()).Build();
        byte[] shown = new PdfPageRenderer(PdfDocument.Open(source))
            .RenderFrame(0, new PdfRenderOptions(1, 1) { WorkingCmykProfile = chosen }).ToSrgbBgra32();
        int expected = (int)Math.Round(0.3 * shown[2] + 0.59 * shown[1] + 0.11 * shown[0], MidpointRounding.AwayFromZero);

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            source, new Dictionary<string, string>(), chosen);

        byte gray = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, new PdfRenderOptions(1, 1)).Pixels.Span[0];
        Assert.InRange(gray, expected - 1, expected + 1);
    }

    [Theory]
    [InlineData(PdfTransparencyGroupColorSpace.Rgb)]
    [InlineData(PdfTransparencyGroupColorSpace.Gray)]
    public void OrdinaryRgbAndGrayTransparencyGroupsRemainConvertible(PdfTransparencyGroupColorSpace space)
    {
        var form = new PdfFormXObject(1, 1,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill(),
            isolatedTransparencyGroup: true, transparencyGroupColorSpace: space);
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1, new PdfContentStreamBuilder().DrawForm(form, 0, 0)).Build();

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            source, new Dictionary<string, string>());

        var rendered = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, new PdfRenderOptions(1, 1));
        Assert.Equal(new byte[] { 77, 77, 77, 255 }, rendered.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NamedDeviceColorsConvertInPagesAndReusableForms(bool form, bool indirectType)
    {
        byte[] content = "/Cs1 cs 1 0 0 sc 0 0 1 1 re f"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            form ? "/Fm1 Do"u8.ToArray() : content).Build());
        var root = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[new PdfName("Root"u8)]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)root[new PdfName("Pages"u8)]);
        var pageReference = (PdfIndirectReference)((PdfArray)pages[new PdfName("Kids"u8)])[0];
        var page = ((PdfDictionary)document.Resolve(pageReference)).ToDictionary(entry => entry.Key, entry => entry.Value);
        var update = new PdfIncrementalUpdateBuilder(document);
        var resources = new PdfDictionary([new(new PdfName("ColorSpace"u8), new PdfDictionary([
            new(new PdfName("Cs1"u8), update.AddObject(new PdfName("DeviceRGB"u8)))]))]);
        if (form)
        {
            var reusable = update.AddObject(new PdfStream(new PdfDictionary([
                new(new PdfName("Type"u8), indirectType ? update.AddObject(new PdfName("XObject"u8)) : new PdfName("XObject"u8)),
                new(new PdfName("Subtype"u8), indirectType ? update.AddObject(new PdfName("Form"u8)) : new PdfName("Form"u8)),
                new(new PdfName("BBox"u8), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1)])),
                new(new PdfName("Resources"u8), resources)]), content));
            resources = new PdfDictionary([new(new PdfName("XObject"u8), new PdfDictionary([
                new(new PdfName("Fm1"u8), reusable)]))]);
        }
        page[new PdfName("Resources"u8)] = resources;
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page));

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            update.Build(), new Dictionary<string, string>());

        var rendered = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, new PdfRenderOptions(1, 1));
        Assert.Equal(new byte[] { 77, 77, 77, 255 }, rendered.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostScriptXObjectsFailSafelyRatherThanKeepingColoredPaint(bool indirectType)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        var root = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[new PdfName("Root"u8)]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)root[new PdfName("Pages"u8)]);
        var pageReference = (PdfIndirectReference)((PdfArray)pages[new PdfName("Kids"u8)])[0];
        var page = ((PdfDictionary)document.Resolve(pageReference)).ToDictionary(entry => entry.Key, entry => entry.Value);
        var update = new PdfIncrementalUpdateBuilder(document);
        var ps = update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Type"u8), indirectType ? update.AddObject(new PdfName("XObject"u8)) : new PdfName("XObject"u8)),
            new(new PdfName("Subtype"u8), indirectType ? update.AddObject(new PdfName("PS"u8)) : new PdfName("PS"u8))]), "1 0 0 setrgbcolor"u8));
        page[new PdfName("Resources"u8)] = new PdfDictionary([
            new(new PdfName("XObject"u8), new PdfDictionary([new(new PdfName("PS1"u8), ps)]))]);
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page));

        Assert.Throws<NotSupportedException>(() => PdfColorConversionMacro.Execute(
            PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale), update.Build(), new Dictionary<string, string>()));
    }

    [Fact]
    public void ZeroThresholdAlsoConvertsImplicitBlackPaintToWhite()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1,
            new PdfContentStreamBuilder().Rectangle(0, 0, 1, 1).Fill()).Build();
        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.BlackAndWhite, 0),
            source, new Dictionary<string, string>());

        var rendered = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, new PdfRenderOptions(1, 1));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rendered.Pixels.ToArray());
    }

    [Theory]
    [InlineData("XFA")]
    [InlineData("DefaultRGB")]
    [InlineData("TR")]
    public void UnsupportedAlternateColorDefinitionsFailSafely(string feature)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        var root = (PdfIndirectReference)document.Trailer[new PdfName("Root"u8)];
        var catalog = ((PdfDictionary)document.Resolve(root)).ToDictionary(entry => entry.Key, entry => entry.Value);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (feature == "XFA")
            catalog[new PdfName("AcroForm"u8)] = new PdfDictionary([
                new(new PdfName("XFA"u8), update.AddObject(new PdfStream(new PdfDictionary([]), "<xdp/>"u8)))]);
        else
        {
            var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[new PdfName("Pages"u8)]);
            var pageReference = (PdfIndirectReference)((PdfArray)pages[new PdfName("Kids"u8)])[0];
            var page = ((PdfDictionary)document.Resolve(pageReference)).ToDictionary(entry => entry.Key, entry => entry.Value);
            page[new PdfName("Resources"u8)] = feature == "DefaultRGB"
                ? new PdfDictionary([new(new PdfName("ColorSpace"u8), new PdfDictionary([
                    new(new PdfName("DefaultRGB"u8), new PdfName("DeviceRGB"u8))]))])
                : new PdfDictionary([new(new PdfName("ExtGState"u8), new PdfDictionary([
                    new(new PdfName("GS1"u8), new PdfDictionary([
                        new(new PdfName("TR"u8), new PdfDictionary([]))]))]))]);
            update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page));
        }
        update.ReplaceObject(root.ObjectNumber, new PdfDictionary(catalog));

        Assert.Throws<NotSupportedException>(() => PdfColorConversionMacro.Execute(
            PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale), update.Build(), new Dictionary<string, string>()));
    }

    [Fact]
    public void RichTextFieldsFailSafelyInsteadOfKeepingUnconvertedXmlColors()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(100, 100)
            .AddTextField(0, "Name", 10, 10, 80, 20, "Ada",
                richTextValue: "<body xmlns=\"http://www.w3.org/1999/xhtml\"><p style=\"color:#ff0000\">Ada</p></body>").Build();

        Assert.Throws<NotSupportedException>(() => PdfColorConversionMacro.Execute(
            PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale), source, new Dictionary<string, string>()));
    }

    [Fact]
    public void UnsupportedGroupSoftMaskFailsWithoutFlattening()
    {
        var mask = new PdfSoftMask(new PdfFormXObject(10, 10,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 0, 10, 10).Fill(),
            isolatedTransparencyGroup: true));
        byte[] source = new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder().SetGraphicsState(new PdfGraphicsState(softMask: mask))
                .SetFillRgb(0, 1, 0).Rectangle(0, 0, 10, 10).Fill()).Build();

        Assert.Throws<NotSupportedException>(() => PdfColorConversionMacro.Execute(
            PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale), source, new Dictionary<string, string>()));
    }

    [Fact]
    public void UnsupportedOverprintingFailsBeforeReturningAnAlteredDocument()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder().SetGraphicsState(new PdfGraphicsState(fillOverprint: true))
                .SetFillCmyk(1, 0, 0, 0).Rectangle(0, 0, 10, 10).Fill()).Build();

        Assert.Throws<NotSupportedException>(() => PdfColorConversionMacro.Execute(
            PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale), source, new Dictionary<string, string>()));
    }

    [Fact]
    public void GrayscaleFollowsDeviceColorSpaceSelectionAndRestoredGraphicsState()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(3, 1,
            "/DeviceRGB cs 1 0 0 scn 0 0 1 1 re f q /DeviceCMYK cs 1 0 0 0 sc 1 0 1 1 re f Q 0 1 0 sc 2 0 1 1 re f"u8.ToArray()).Build();

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            source, new Dictionary<string, string>());
        var rendered = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, new PdfRenderOptions(3, 1));

        // Red 0.3 * 255 = 77; working-profile cyan RGB (0, 174, 239) gives 0.59 * 174 + 0.11 * 239 = 129;
        // green 0.59 * 255 = 150.45, so 150 (ISO 32000 weights).
        Assert.Equal(new byte[] { 77, 77, 77, 255, 129, 129, 129, 255, 150, 150, 150, 255 }, rendered.Pixels.ToArray());
    }

    [Fact]
    public void GrayscaleConvertsCmykPathsAndImagesToTheSameDisplayTone()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(2, 1,
            new PdfContentStreamBuilder().SetFillCmyk(1, 0, 0, 0).Rectangle(0, 0, 1, 1).Fill()
                .DrawImage(PdfImage.FromCmyk(1, 1, new byte[] { 255, 0, 0, 0 }), 1, 0, 1, 1)).Build();

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            source, new Dictionary<string, string>());
        var rendered = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, new PdfRenderOptions(2, 1));

        Assert.Equal(new byte[] { 129, 129, 129, 255, 129, 129, 129, 255 }, rendered.Pixels.ToArray());
    }

    [Fact]
    public void BlackAndWhiteUsesTheConfiguredThresholdWithoutChangingImageOpacity()
    {
        PdfImage image = PdfImage.FromRgba(2, 1, new byte[] { 255, 0, 0, 128, 0, 255, 0, 64 });
        byte[] source = new PdfDocumentBuilder().AddPage(40, 20,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 40, 20)).Build();
        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.BlackAndWhite, 120),
            source, new Dictionary<string, string>());
        var rendered = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0,
            new PdfRenderOptions(40, 20, transparentBackground: true));

        Assert.Equal(new byte[] { 0, 0, 0, 128 }, rendered.Pixels.Slice((10 * 40 + 10) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 64 }, rendered.Pixels.Slice((10 * 40 + 30) * 4, 4).ToArray());
    }

    [Fact]
    public void GrayscaleConvertsImagePixelsWhileKeepingTheirSoftMaskAndText()
    {
        PdfImage image = PdfImage.FromRgba(2, 1, new byte[] { 255, 0, 0, 128, 0, 255, 0, 64 });
        byte[] source = new PdfDocumentBuilder().AddPage(40, 20,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 40, 20).BeginText()
                .SetFont(PdfStandardFont.Helvetica, 6).MoveText(0, 15).ShowLatin1Text("Live").EndText()).Build();

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            source, new Dictionary<string, string>());
        PdfDocument converted = PdfDocument.Open(result.Data);
        var rendered = new PdfPageRenderer(converted).Render(0, new PdfRenderOptions(40, 20, transparentBackground: true));

        // Away from the smoothed blend between the image's two samples.
        foreach ((int x, byte alpha) in new[] { (5, (byte)128), (30, (byte)64) })
        {
            byte[] pixel = rendered.Pixels.Slice((10 * 40 + x) * 4, 4).ToArray();
            Assert.Equal(pixel[0], pixel[1]);
            Assert.Equal(pixel[1], pixel[2]);
            Assert.Equal(alpha, pixel[3]);
        }
        var content = new PdfPageContentReader(converted).Read(0);
        Assert.Single(content.Images);
        Assert.Equal("Live", string.Concat(content.TextRuns.Select(run => run.Text)));
    }

    [Fact]
    public void GrayscaleUpdatesColoredWidgetAppearancesAndPreservesLaterEditing()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(100, 100)
            .AddTextField(0, "Name", 10, 10, 80, 20, "Ada", appearanceStyle: new PdfFormFieldAppearanceStyle
            {
                TextColor = new PdfRgbColor(1, 0, 0), BackgroundColor = new PdfRgbColor(0, 1, 0),
                BorderColor = new PdfRgbColor(0, 0, 1)
            }).Build();

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            source, new Dictionary<string, string>());
        PdfDocument converted = PdfDocument.Open(result.Data);
        var originalAppearance = new PdfPageRenderer(converted).Render(0, new PdfRenderOptions(100, 100));
        // The green background: 0.59 * 255 = 150.45, so 150 (ISO 32000 weights).
        Assert.Equal(new byte[] { 150, 150, 150, 255 }, originalAppearance.Pixels.Slice((80 * 100 + 80) * 4, 4).ToArray());

        PdfDocument edited = PdfDocument.Open(new PdfIncrementalPageEditor(converted).SetTextFieldValue("Name", "Grace").Build());
        var editedAppearance = new PdfPageRenderer(edited).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 150, 150, 150, 255 }, editedAppearance.Pixels.Slice((80 * 100 + 80) * 4, 4).ToArray());
        byte minimum = 255;
        for (int y = 73; y < 87; y++)
            for (int x = 13; x < 85; x++)
                minimum = Math.Min(minimum, editedAppearance.Pixels.Span[(y * 100 + x) * 4]);
        // The red text converts to 77. No pixel of these small glyphs is fully inside a stem:
        // the fullest one covers 0.9908 of its area exactly, so its coverage byte is
        // floor(256 * 0.9908) = 253, and over the 150 background it paints
        // (77 * 253 + 150 * 2 + 127) / 255 = 78.
        Assert.Equal(78, minimum);
    }

    [Fact]
    public void GrayscaleKeepsSelectableTextAndEditableFormFields()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 50, 100, 50).Fill()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 10).MoveText(10, 40)
                .ShowLatin1Text("Selectable").EndText())
            .AddTextField(0, "Name", 10, 10, 80, 20, "Ada").Build();

        var result = PdfColorConversionMacro.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            source, new Dictionary<string, string>());
        PdfDocument converted = PdfDocument.Open(result.Data);
        var rendered = new PdfPageRenderer(converted).Render(0, new PdfRenderOptions(100, 100));

        Assert.Equal(new byte[] { 77, 77, 77, 255 }, rendered.Pixels.Slice((5 * 100 + 5) * 4, 4).ToArray());
        Assert.Equal("Selectable", string.Concat(new PdfPageContentReader(converted).Read(0).TextRuns.Select(run => run.Text)));
        Assert.Empty(new PdfPageContentReader(converted).Read(0).Images);
        Assert.Equal("Ada", Assert.Single(PdfFormWidgetReader.ReadPage(converted, 0)).Value);
        PdfDocument edited = PdfDocument.Open(new PdfIncrementalPageEditor(converted).SetTextFieldValue("Name", "Grace").Build());
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(edited, 0)).Value);
    }
}
