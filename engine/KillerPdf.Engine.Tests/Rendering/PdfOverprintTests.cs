using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfOverprintTests
{
    [Theory]
    [InlineData("fill", true, false, 1, 0)]
    [InlineData("fill", false, true, 1, 255)]
    [InlineData("fill", null, true, 1, 0)]
    [InlineData("fill", true, false, 0, 255)]
    [InlineData("stroke", false, true, 1, 0)]
    [InlineData("stroke", true, false, 1, 255)]
    [InlineData("stroke", true, true, 0, 255)]
    [InlineData("stencil", true, false, 1, 0)]
    [InlineData("image", true, true, 1, 255)]
    [InlineData("restored", true, true, 1, 255)]
    [InlineData("tiny", true, true, 1, 255)]
    [InlineData("axial", true, false, 1, 0)]
    [InlineData("radial", true, false, 1, 0)]
    [InlineData("axial", false, true, 1, 255)]
    [InlineData("radial", true, true, 0, 255)]
    [InlineData("function", true, false, 1, 0)]
    [InlineData("lattice", true, false, 1, 0)]
    [InlineData("coons", true, false, 1, 0)]
    [InlineData("tensor", true, false, 1, 0)]
    [InlineData("function", false, true, 1, 255)]
    [InlineData("lattice", false, true, 1, 255)]
    [InlineData("coons", true, true, 0, 255)]
    [InlineData("tensor", true, true, 0, 255)]
    [InlineData("separation", true, false, 0, 0)]
    [InlineData("separation", true, false, 1, 0)]
    [InlineData("separation", false, true, 1, 255)]
    [InlineData("spot", true, false, 0, 0)]
    [InlineData("spot", true, false, 1, 0)]
    [InlineData("spot", false, true, 1, 255)]
    [InlineData("spotimage", true, false, 1, 0)]
    [InlineData("spotimage", false, true, 1, 255)]
    [InlineData("devicen", true, false, 0, 0)]
    [InlineData("devicen", true, false, 1, 0)]
    [InlineData("namedzero", true, false, 1, 255)]
    [InlineData("namedimage", true, false, 0, 0)]
    [InlineData("namedimage", true, false, 1, 0)]
    [InlineData("namedimage", false, true, 1, 255)]
    [InlineData("namednone", true, false, 0, 0)]
    [InlineData("namednone", true, false, 1, 0)]
    [InlineData("namednone", false, true, 1, 255)]
    [InlineData("namednoneimage", true, false, 0, 0)]
    [InlineData("namednoneimage", true, false, 1, 0)]
    [InlineData("namednoneimage", false, true, 1, 255)]
    [InlineData("namedfive", true, false, 0, 255)]
    [InlineData("namedfive", true, false, 1, 255)]
    [InlineData("namedfiveimage", true, false, 0, 255)]
    [InlineData("namedfiveimage", true, false, 1, 255)]
    public void CmykPaint_HonorsSeparateOverprintSettings(string paint, bool? fill, bool stroke, int mode, int red)
    {
        var rendered = Render(paint, fill, stroke, mode);
        byte[] pixels = rendered.Pixels.ToArray();
        int center = (4 * 8 + 4) * 4;
        Assert.Equal(PdfDeviceCmykTests.RenderInk((byte)(255 - red), 255), pixels[center..(center + 4)]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData("fill", 1, 0)]
    [InlineData("stroke", 1, 0)]
    [InlineData("stencil", 1, 0)]
    [InlineData("fill", 0, 127)]
    [InlineData("stroke", 0, 127)]
    [InlineData("stencil", 0, 128)]
    [InlineData("image", 1, 127)]
    public void CmykPaint_CombinesOverprintWithOpacity(string paint, int mode, int red)
    {
        var rendered = Render(paint, true, true, mode, 0.5);
        byte[] pixels = rendered.Pixels.ToArray();
        int center = (4 * 8 + 4) * 4;
        Assert.Equal(PdfDeviceCmykTests.RenderInk((byte)(255 - red), 128), pixels[center..(center + 4)]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(0.5, 128)]
    [InlineData(0, 0)]
    public void RepaintingSameSpot_ReplacesItsPreviousTint(double secondTint, byte magenta)
    {
        var rendered = Render("spot", true, true, 1, secondSpotTint: secondTint,
            blankBackground: true);
        byte[] pixels = rendered.Pixels.ToArray();
        int center = (4 * 8 + 4) * 4;
        Assert.Equal(PdfDeviceCmykTests.RenderInk(0, magenta), pixels[center..(center + 4)]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void CmykImageBetweenSpotPaints_ClearsThePreviousSpotPlate()
    {
        var rendered = Render("spot", true, true, 1, secondSpotTint: 0.5,
            blankBackground: true, blankImageBetweenSpotPaints: true);
        byte[] pixels = rendered.Pixels.ToArray();
        int center = (4 * 8 + 4) * 4;
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(PdfDeviceCmykTests.RenderInk(0, 128), pixels[center..(center + 4)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GrayWhiteOverprint_PreservesNamedSpotButReplacesProcessInk(int mode)
    {
        var spot = Render("spot", true, true, mode, blankBackground: true);
        var overSpot = Render("spot", true, true, mode, blankBackground: true,
            grayWhiteOverlay: true);
        var withoutOverprint = Render("spot", false, true, mode, blankBackground: true,
            grayWhiteOverlay: true);
        var overProcess = Render("fill", true, true, mode, grayWhiteOverlay: true);
        int covered = (4 * 8 + 2) * 4;
        int uncovered = (4 * 8 + 6) * 4;

        Assert.Equal(spot.Pixels.Span[covered..(covered + 4)].ToArray(),
            overSpot.Pixels.Span[covered..(covered + 4)].ToArray());
        Assert.Equal(spot.Pixels.Span[uncovered..(uncovered + 4)].ToArray(),
            overSpot.Pixels.Span[uncovered..(uncovered + 4)].ToArray());
        Assert.Equal(PdfDeviceCmykTests.RenderInk(0, 0),
            withoutOverprint.Pixels.Span[covered..(covered + 4)].ToArray());
        Assert.Equal(PdfDeviceCmykTests.RenderInk(0, 0),
            overProcess.Pixels.Span[covered..(covered + 4)].ToArray());
        Assert.Empty(overSpot.Diagnostics);
        Assert.Empty(withoutOverprint.Diagnostics);
        Assert.Empty(overProcess.Diagnostics);
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(0.5d, 128)]
    public void CmykModeZeroOverprint_PreservesNamedSpotAndReplacesProcessInk(
        double blackTint, byte black)
    {
        var overprint = Render("spot", true, true, 0, cmykBlackOverlay: blackTint);
        var noOverprint = Render("spot", false, true, 0, cmykBlackOverlay: blackTint);
        int covered = (4 * 8 + 2) * 4;
        int uncovered = (4 * 8 + 6) * 4;

        Assert.Equal(PdfDeviceCmykTests.RenderInk(0, 255, 0, black),
            overprint.Pixels.Span[covered..(covered + 4)].ToArray());
        Assert.Equal(PdfDeviceCmykTests.RenderInk(255, 255, 0, 0),
            overprint.Pixels.Span[uncovered..(uncovered + 4)].ToArray());
        Assert.Equal(PdfDeviceCmykTests.RenderInk(0, 0, 0, black),
            noOverprint.Pixels.Span[covered..(covered + 4)].ToArray());
        Assert.Empty(overprint.Diagnostics);
        Assert.Empty(noOverprint.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpotImage_ReplacesEarlierTintOfSameColorant(bool indexed)
    {
        var rendered = Render("spotimage", true, true, 1, indexed: indexed,
            blankBackground: true, initialSpotBeforeImage: true, spotImageTint: 128);
        byte[] pixels = rendered.Pixels.ToArray();
        int center = (4 * 8 + 4) * 4;
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(PdfDeviceCmykTests.RenderInk(0, 128), pixels[center..(center + 4)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedBlackAndSpotImage_PreservesEarlierDifferentSpot(bool reducedImage)
    {
        var rendered = Render("mixedspotimage", true, true, 1, indexed: true,
            blankBackground: true, initialSpotBeforeImage: true, reducedImage: reducedImage);
        byte[] pixels = rendered.Pixels.ToArray();
        int center = (4 * 8 + 4) * 4;
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(PdfDeviceCmykTests.RenderInk(255, 128, 0, 128), pixels[center..(center + 4)]);
    }

    [Fact]
    public void MixedBlackAndSpotImage_AveragesBothColorantsDuringReduction()
    {
        var rendered = Render("mixedspotimage", true, true, 1, indexed: true,
            blankBackground: true, initialSpotBeforeImage: true,
            reducedImage: true, alternatingImageSamples: true);
        byte[] pixels = rendered.Pixels.ToArray();
        int center = (4 * 8 + 4) * 4;
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(PdfDeviceCmykTests.RenderInk(255, 64, 0, 64), pixels[center..(center + 4)]);
    }

    [Theory]
    [InlineData("group-inherited", 255, 0)]
    [InlineData("group-inherited", 255, 0, true)]
    [InlineData("group-repaint", 255, 0, true)]
    [InlineData("group-to-cmyk", 0, 255, true)]
    [InlineData("group-return", 0, 255, true)]
    [InlineData("group-stroke", 255, 0, true)]
    [InlineData("group-repaint", 255, 0)]
    [InlineData("group-to-cmyk", 0, 255)]
    [InlineData("group-return", 0, 255)]
    [InlineData("group-stroke", 255, 0)]
    [InlineData("group-precision", 255, 0)]
    public void NamedColor_FollowsGroupColorSpace(string paint, byte green, byte red, bool indexed = false)
    {
        var rendered = Render(paint, false, false, 0, indexed: indexed);
        byte[] pixels = rendered.Pixels.ToArray();
        int center = (4 * 8 + 4) * 4;
        byte[] expected = green == 0 ? PdfDeviceCmykTests.RenderInk((byte)(255 - red), 255)
            : RenderRgbGroupReference();
        Assert.Equal(expected, pixels[center..(center + 4)]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData("namedimage", true, 0, 0)]
    [InlineData("namedimage", true, 1, 0)]
    [InlineData("namednoneimage", true, 0, 0)]
    [InlineData("namednoneimage", true, 1, 0)]
    [InlineData("namednoneimage", false, 1, 255)]
    [InlineData("namedfiveimage", true, 0, 255)]
    [InlineData("namedfiveimage", true, 1, 255)]
    public void IndexedNamedImage_PreservesNamedInks(string paint, bool overprint, int mode, byte red)
    {
        var rendered = Render(paint, overprint, false, mode, indexed: true);
        byte[] pixels = rendered.Pixels.ToArray();
        int center = (4 * 8 + 4) * 4;
        Assert.Equal(PdfDeviceCmykTests.RenderInk((byte)(255 - red), 255), pixels[center..(center + 4)]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData("separation", false, false)]
    [InlineData("separation", false, true)]
    [InlineData("devicen", false, false)]
    [InlineData("namednone", false, true)]
    [InlineData("namedstroke", false, false)]
    [InlineData("namedstencil", false, true)]
    [InlineData("namedimage", false, false)]
    [InlineData("namednoneimage", true, true)]
    [InlineData("noneinitial", false, false)]
    [InlineData("namedtext", false, false)]
    [InlineData("namedtextstroke", false, true)]
    public void NoneColorant_DoesNotPaint(string paint, bool indexed, bool rgb)
    {
        var rendered = Render(paint, false, false, 0, indexed: indexed, none: true, rgb: rgb);
        byte[] pixels = rendered.Pixels.ToArray();
        byte[] background = PdfDeviceCmykTests.RenderInk(255, 0);
        for (int index = 0; index < pixels.Length; index += 4)
            Assert.Equal(background, pixels[index..(index + 4)]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void NoneText_PreservesTextClipping()
    {
        var invisible = Render("namedclip", false, false, 0, none: true);
        var reference = Render("clipreference", false, false, 0);
        Assert.Equal(reference.Pixels.ToArray(), invisible.Pixels.ToArray());
        Assert.Empty(invisible.Diagnostics);
        Assert.Empty(reference.Diagnostics);
        byte[] pixels = invisible.Pixels.ToArray();
        byte backgroundGreen = PdfDeviceCmykTests.RenderInk(255, 0)[1];
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), index => pixels[index * 4 + 1] < backgroundGreen);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), index => pixels[index * 4 + 1] == backgroundGreen);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 1, 0)]
    [InlineData(true, 1, 0)]
    [InlineData(false, 1, 1)]
    [InlineData(false, 1, 0, "namedimage", true)]
    [InlineData(false, 1, 1, "namedimage", false)]
    [InlineData(true, 0, 1, "namedimage", true)]
    [InlineData(false, 1, 0.5, "group-inherited")]
    [InlineData(false, 1, 0.5, "group-to-cmyk")]
    public void RegistrationColor_AppliesAllOutputComponents(bool rgb, int mode, double tint = 0.5,
        string paint = "separation", bool indexed = false)
    {
        var rendered = Render(paint, true, true, mode, rgb: rgb, registration: true, registrationTint: tint, indexed: indexed);
        bool referenceRgb = paint == "group-inherited" || paint != "group-to-cmyk" && rgb;
        var reference = paint == "group-inherited"
            ? Render("rgbgrayreference", false, false, 0, registrationTint: tint)
            : Render("allreference", false, false, 0, rgb: referenceRgb, registrationTint: tint);
        Assert.Equal(reference.Pixels.ToArray(), rendered.Pixels.ToArray());
        Assert.Empty(rendered.Diagnostics);
        Assert.Empty(reference.Diagnostics);
    }

    private static byte[] RenderRgbGroupReference()
    {
        var content = new PdfContentStreamBuilder().DrawForm(new PdfFormXObject(1, 1,
            new PdfContentStreamBuilder().SetFillCmyk(1, 0, 0, 0).Rectangle(0, 0, 1, 1).Fill(),
            isolatedTransparencyGroup: true, transparencyGroupColorSpace: PdfTransparencyGroupColorSpace.Rgb), 0, 0);
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, content).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        var dictionary = (PdfDictionary)source.Resolve(page.Reference);
        var group = new PdfDictionary([new(new PdfName("S"u8), new PdfName("Transparency"u8)),
            new(new PdfName("CS"u8), new PdfName("DeviceCMYK"u8))]);
        var update = new PdfIncrementalUpdateBuilder(source).ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(dictionary.Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Group"u8), group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(1, 1)).Pixels.ToArray();
    }

    private static readonly string[] sourceArray = ["Custom"];

    private static PdfRenderedPage Render(string paint, bool? fill, bool stroke, int mode, double opacity = 1,
        bool indexed = false, bool none = false, bool rgb = false, bool registration = false,
        double registrationTint = 0.5, double? secondSpotTint = null, bool blankBackground = false,
        bool blankImageBetweenSpotPaints = false, bool initialSpotBeforeImage = false,
        byte? spotImageTint = null, bool reducedImage = false, bool alternatingImageSamples = false,
        bool grayWhiteOverlay = false, double? cmykBlackOverlay = null)
    {
        PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        string tintNumber = registrationTint.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string grayNumber = (1 - registrationTint).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string operation = paint switch
        {
            "stroke" => "0 1 0 0 K 8 w 0 4 m 8 4 l S",
            "allreference" => (rgb ? grayNumber + " g" : $"{tintNumber} {tintNumber} {tintNumber} {tintNumber} k") + " 0 0 8 8 re f",
            "rgbgrayreference" => $"{grayNumber} {grayNumber} {grayNumber} rg 0 0 8 8 re f",
            "image" or "stencil" or "namedimage" or "spotimage" or "mixedspotimage" or "namednoneimage" or "namedfiveimage" => "8 0 0 8 0 0 cm /Im Do",
            "tiny" => "0.0001 1 0 0 k 0 0 8 8 re f",
            "separation" or "spot" or "devicen" => "/Named cs 1 scn 0 0 8 8 re f",
            "namedzero" => "/Named cs 0 1 scn 0 0 8 8 re f",
            "namedstroke" => "/Named CS 1 SCN 8 w 0 4 m 8 4 l S",
            "noneinitial" => "/Named cs 0 0 8 8 re f",
            "namedtext" => "/Named cs 1 scn BT /F 5 Tf 0 2 Td (HH) Tj ET",
            "namedtextstroke" => "/Named CS 1 SCN 0.5 w BT /F 5 Tf 1 Tr 0 2 Td (HH) Tj ET",
            "namedclip" => "/Named cs 1 scn BT /F 7 Tf 4 Tr 0 1 Td (H) Tj ET 0 1 0 0 k 0 0 8 8 re f",
            "clipreference" => "BT /F 7 Tf 7 Tr 0 1 Td (H) Tj ET 0 1 0 0 k 0 0 8 8 re f",
            "namedstencil" => "/Named cs 1 scn 8 0 0 8 0 0 cm /Im Do",
            "namednone" => "/Named cs 1 0.75 scn 0 0 8 8 re f",
            "namedfive" => "/Named cs 0 1 0 0 0.75 scn 0 0 8 8 re f",
            "group-inherited" or "group-repaint" or "group-to-cmyk" => "/Named cs 1 scn /Fm Do",
            "group-return" => "/Named cs 1 scn /Fm Do 0 0 8 8 re f",
            "group-stroke" => "/Named CS 1 SCN /Fm Do",
            "group-precision" => "/Named cs 0.0001 scn /Fm Do",
            "axial" or "radial" or "function" or "lattice" or "coons" or "tensor" => "/Sh sh",
            _ => "0 1 0 0 k 0 0 8 8 re f"
        };
        string select = paint == "restored" ? "q /O gs Q" : "/O gs";
        if (registration) operation = operation.Replace("1 scn", tintNumber + " scn", StringComparison.Ordinal);
        if (initialSpotBeforeImage)
            operation = (paint == "mixedspotimage" ? "/SpotA cs 1 scn" : "/Named cs 1 scn")
                + " 0 0 8 8 re f q " + operation + " Q";
        if (secondSpotTint is double repaintTint)
            operation += (blankImageBetweenSpotPaints ? " q 8 0 0 8 0 0 cm /Im Do Q" : "")
                + " /Named cs " + repaintTint.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " scn 0 0 8 8 re f";
        if (grayWhiteOverlay) operation += " q 1 g 0 0 4 8 re f Q";
        if (cmykBlackOverlay is double blackTint)
            operation += " q 0 0 0 " + blackTint.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " k 0 0 4 8 re f Q";
        if (indexed && paint.StartsWith("group-", StringComparison.Ordinal))
            operation = operation.Replace("/Named", "/IndexedNamed", StringComparison.Ordinal);
        string backdrop = blankBackground ? "0 0 0 0 k 0 0 8 8 re f"
            : "1 0 0 0 k 0 0 8 8 re f 0 1 0 0 k";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8,
            Encoding.ASCII.GetBytes($"{backdrop} {select} {operation}")).Build());
        var root = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)root[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var settings = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("OP", new PdfBoolean(stroke)), Entry("OPM", new PdfInteger(mode)),
            Entry("ca", new PdfReal(opacity)), Entry("CA", new PdfReal(opacity))
        };
        if (fill.HasValue) settings.Add(Entry("op", new PdfBoolean(fill.Value)));
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(reducedImage ? 16 : 1)),
            Entry("Height", new PdfInteger(reducedImage ? 16 : 1)),
            Entry("BitsPerComponent", new PdfInteger(paint is "stencil" or "namedstencil" ? 1 : 8))
        };
        byte[] imageSamples = paint == "mixedspotimage"
                ? alternatingImageSamples ? [128, 128, 0, 0] : [128, 128]
                : spotImageTint is byte tint ? [tint]
                : blankImageBetweenSpotPaints ? [0, 0, 0, 0]
                : paint is "stencil" or "namedstencil" ? [0]
                : paint is "namedimage" or "spotimage" ? [255]
                : paint == "namednoneimage" ? [255, 192]
                : paint == "namedfiveimage" ? [0, 255, 0, 0, 192]
                : [0, 255, 0, 0];
        if (registration && paint == "namedimage") imageSamples = [(byte)Math.Round(registrationTint * 255)];
        if (paint is "stencil" or "namedstencil") imageEntries.Add(Entry("ImageMask", new PdfBoolean(true)));
        else imageEntries.Add(Entry("ColorSpace", indexed
            ? new PdfArray([Name("Indexed"), Name("Named"), new PdfInteger(alternatingImageSamples ? 1 : 0),
                new PdfString(imageSamples, PdfStringForm.Hexadecimal)])
            : Name(paint is "namedimage" or "spotimage" or "mixedspotimage" or "namednoneimage" or "namedfiveimage" ? "Named" : "DeviceCMYK")));
        byte[] reducedSamples = new byte[256];
        if (alternatingImageSamples)
            for (int index = 0; index < reducedSamples.Length; index++)
                reducedSamples[index] = (byte)(index % 2);
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries),
            indexed ? reducedImage ? reducedSamples : [0] : imageSamples));
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        var function = new PdfDictionary([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 1, 0, 0)), Entry("C1", Numbers(0, 1, 0, 0)),
            Entry("N", new PdfInteger(1))]);
        PdfObject shading = new PdfDictionary([
            Entry("ShadingType", new PdfInteger(paint == "radial" ? 3 : 2)),
            Entry("ColorSpace", Name("DeviceCMYK")), Entry("Function", function),
            Entry("Coords", paint == "radial" ? Numbers(4, 4, 0, 4, 4, 8) : Numbers(0, 0, 8, 0))]);
        if (paint == "function")
        {
            var calculator = new PdfStream(new PdfDictionary([
                Entry("FunctionType", new PdfInteger(4)), Entry("Domain", Numbers(0, 8, 0, 8)),
                Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))]),
                Encoding.ASCII.GetBytes("{ pop pop 0 1 0 0 }"));
            shading = new PdfDictionary([
                Entry("ShadingType", new PdfInteger(1)), Entry("ColorSpace", Name("DeviceCMYK")),
                Entry("Domain", Numbers(0, 8, 0, 8)), Entry("Function", update.AddObject(calculator))]);
        }
        if (paint is "lattice" or "coons" or "tensor")
        {
            int type = paint == "lattice" ? 5 : paint == "coons" ? 6 : 7;
            byte[] boundary = [0, 0, 85, 0, 170, 0, 255, 0,
                255, 85, 255, 170, 255, 255, 170, 255,
                85, 255, 0, 255, 0, 170, 0, 85];
            byte[] points = type == 7 ? [.. boundary, 85, 85, 170, 85, 170, 170, 85, 170] : boundary;
            byte[] colors = [0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0];
            byte[] samples = type == 5
                ? [0, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0,
                    0, 255, 0, 255, 0, 0, 255, 255, 0, 255, 0, 0]
                : [0, .. points, .. colors];
            shading = new PdfStream(new PdfDictionary([
                Entry("ShadingType", new PdfInteger(type)), Entry("ColorSpace", Name("DeviceCMYK")),
                Entry("BitsPerCoordinate", new PdfInteger(8)), Entry("BitsPerComponent", new PdfInteger(8)),
                Entry("BitsPerFlag", new PdfInteger(8)), Entry("VerticesPerRow", new PdfInteger(2)),
                Entry("Decode", Numbers(0, 8, 0, 8, 0, 1, 0, 1, 0, 1, 0, 1))]), samples);
        }
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 8, 8)),
            Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")),
                Entry("CS", Name(paint == "group-to-cmyk" ? "DeviceCMYK" : "DeviceRGB")),
                Entry("I", new PdfBoolean(true))]))]),
            Encoding.ASCII.GetBytes(paint == "group-stroke" ? "8 w 0 4 m 8 4 l S"
                : paint == "group-repaint" ? "1 scn 0 0 8 8 re f" : "0 0 8 8 re f")));
        string[] colorants = paint switch
        {
            "namedzero" => ["Cyan", "Magenta"],
            "mixedspotimage" => ["Black", "Custom"],
            "namednone" or "namednoneimage" => ["Magenta", "None"],
            "namedfive" or "namedfiveimage" => ["Cyan", "Magenta", "Yellow", "Black", "None"],
            _ => ["Magenta"]
        };
        if (none) Array.Fill(colorants, "None");
        if (registration) Array.Fill(colorants, "All");
        var resources = new PdfDictionary([
            Entry("ColorSpace", new PdfDictionary([Entry("Named", new PdfArray([
                Name(paint is "separation" or "spot" || registration ? "Separation" : "DeviceN"),
                paint is "separation" or "spot" || registration
                    ? Name(paint == "spot" ? "Custom" : colorants[0])
                    : new PdfArray((paint == "spotimage" ? sourceArray : colorants)
                        .Select(value => (PdfObject)Name(value))),
                Name("DeviceCMYK"), update.AddObject(new PdfStream(new PdfDictionary([
                    Entry("FunctionType", new PdfInteger(4)),
                    Entry("Domain", Numbers([.. Enumerable.Range(0, colorants.Length * 2).Select(index => index % 2)])),
                    Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))]),
                    Encoding.ASCII.GetBytes(none ? "{ " + string.Concat(Enumerable.Repeat("pop ", colorants.Length)) + "0 1 0 0 }"
                        : paint == "mixedspotimage" ? "{ 0 exch 0 4 -1 roll }"
                        : paint is "namednone" or "namednoneimage" ? "{ pop pop 1 0 0 0 }"
                        : paint is "namedfive" or "namedfiveimage" ? "{ pop pop pop pop pop 1 0 0 0 }"
                        : paint == "group-precision" ? "{ 10000 mul 0 0 0 }"
                        : paint.StartsWith("group-", StringComparison.Ordinal)
                        ? "{ 0 0 0 }" : paint == "namedzero" ? "{ 0 0 }" : "{ 0 exch 0 0 }")))
            ])), Entry("SpotA", new PdfArray([Name("Separation"), Name("FirstSpot"),
                Name("DeviceCMYK"), update.AddObject(new PdfStream(new PdfDictionary([
                    Entry("FunctionType", new PdfInteger(4)), Entry("Domain", Numbers(0, 1)),
                    Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))]),
                    Encoding.ASCII.GetBytes("{ 0 0 0 }")))])),
            Entry("IndexedNamed", new PdfArray([Name("Indexed"), Name("Named"),
                new PdfInteger(1), new PdfString([0, 255], PdfStringForm.Hexadecimal)]))])),
            Entry("Font", new PdfDictionary([Entry("F", new PdfDictionary([
                Entry("Type", Name("Font")), Entry("Subtype", Name("Type1")), Entry("BaseFont", Name("Helvetica"))]))])),
            Entry("Shading", new PdfDictionary([Entry("Sh", update.AddObject(shading))])),
            Entry("ExtGState", new PdfDictionary([Entry("O", new PdfDictionary(settings))])),
            Entry("XObject", new PdfDictionary([Entry("Im", image), Entry("Fm", form)]))]);
        var group = new PdfDictionary([Entry("S", Name("Transparency")),
            Entry("CS", Name(rgb || paint == "group-to-cmyk" ? "DeviceRGB" : "DeviceCMYK"))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(8, 8));
    }
}
