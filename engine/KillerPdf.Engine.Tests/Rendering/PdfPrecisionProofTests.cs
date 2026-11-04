using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionProofTests
{
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
    [InlineData("default-gray")]
    [InlineData("class")]
    [InlineData("fallback")]
    [InlineData("reverse")]
    public void InitialProofSubsetDiagnosesResourceDefaultsAndUnusablePrintingConditions(string condition)
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
        var document = PageGroup(source, defaultGray: condition == "default-gray");

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
    public void InitialProofSubsetRequiresAnExplicitRgbPageGroup()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(1, 1).Build());
        var options = new PdfRenderOptions(1, 1)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
        };

        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document).RenderFrame(0, options));

        Assert.Contains("proof", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0 1 0 0 k 0 0 1 1 re f")]
    [InlineData("0 1 0 0 K 1 w 0 .5 m 1 .5 l S")]
    [InlineData("/DeviceCMYK cs 0 1 0 0 sc 0 0 1 1 re f")]
    [InlineData("/DeviceRGB cs .5 .5 .5 sc 0 0 1 1 re f")]
    [InlineData("BT ET")]
    public void InitialProofSubsetDiagnosesSourcesOutsideItsPathOnlyRgbContract(string content)
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
    [InlineData("warnings")]
    [InlineData("transparent")]
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
        bool includeReverse = true, bool wideGamut = false, bool perceptualTables = false)
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
            tags.Add(("B2A0", reverse));
        }
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
