using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccProfileTransformTests
{
    [Theory]
    [InlineData("Perceptual", 0)]
    [InlineData("Saturation", 0)]
    [InlineData("Perceptual", 1)]
    [InlineData("Saturation", 1)]
    [InlineData("Perceptual", 2)]
    [InlineData("Saturation", 2)]
    public void PaintConversionSelectsRgbDestinationIntentAndRestoresIt(string intent, int kind)
    {
        byte[] reference = Render(true), actual = Render(false);
        Assert.Equal(reference[..4], actual[..4]);
        Assert.NotEqual((byte)0, actual[0]);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, actual[4..]);

        byte[] Render(bool reference)
        {
            PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
            KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
            PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
            string paint = kind switch { 0 => "/F Do", 1 => "/Source cs 0 0 0 0 sc 0 0 1 1 re f",
                _ => "/RelativeColorimetric ri /Image Do" };
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
                Encoding.ASCII.GetBytes($"/{intent} ri {paint} /RelativeColorimetric ri 1 1 1 rg 1 0 1 1 re f")).Build());
            var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
            var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
            var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
            var page = (PdfDictionary)source.Resolve(pageReference);
            var update = new PdfIncrementalUpdateBuilder(source);
            PdfArray Space(byte[] profile, int count) => new([Name("ICCBased"), update.AddObject(new PdfStream(
                new PdfDictionary([Entry("N", new PdfInteger(count))]), profile))]);
            byte[] sourceLut = Lut(true, true, false, 4);
            for (int cell = 0; cell < 16; cell++)
                BinaryPrimitives.WriteUInt16BigEndian(sourceLut.AsSpan(68 + cell * 6), 16320);
            var groupSpace = Space(Profile("CMYK", "Lab ", ("A2B0", sourceLut)), 4);
            byte[] relativeReverse = Lut(true, false, true);
            if (!reference) relativeReverse.AsSpan(64, 8 * 6).Clear();
            var pageSpace = Space(Profile("RGB ", "XYZ ", ("A2B0", Lut(true, false, true)),
                ("B2A0", Lut(true, false, true)), ("B2A1", relativeReverse), ("B2A2", Lut(true, false, true))), 3);
            var form = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 1, 1)),
                Entry("Resources", new PdfDictionary([])), Entry("Group", new PdfDictionary([
                    Entry("S", Name("Transparency")), Entry("I", new PdfBoolean(true)), Entry("CS", groupSpace)]))]),
                "0 0 0 0 k 0 0 1 1 re f"u8));
            var image = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
                Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", groupSpace),
                Entry("Intent", Name(intent))]), new byte[4]));
            update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Concat([Entry("Resources", new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("F", form), Entry("Image", image)])),
                    Entry("ColorSpace", new PdfDictionary([Entry("Source", groupSpace)]))])),
                    Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", pageSpace)]))])));
            var result = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(2, 1));
            Assert.Empty(result.Diagnostics);
            return result.Pixels.ToArray();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AvailableIntentSharesProfileStorageAndCachesFailures(bool saturation)
    {
        byte[] table = Lut(true, false, false), unsupported = "bad!\0\0\0\0"u8.ToArray();
        byte[] bytes = Profile("RGB ", "XYZ ", ("A2B0", saturation ? unsupported : table),
            ("A2B1", unsupported), ("A2B2", table), ("test", new byte[4 * 1024 * 1024]));
        Assert.Throws<FormatException>(() => new PdfIccProfileTransform(bytes));
        long start = GC.GetAllocatedBytesForCurrentThread();
        var available = PdfIccProfileTransform.ReadAvailable(bytes);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - start < 131072);
        Assert.Null(available.ForIntent(1));
        if (saturation) Assert.Null(available.ForIntent(0));
        Assert.Same(available, available.ForIntent(saturation ? 2 : 0));
        start = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 100; repeat++) _ = available.ForIntent(1);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void UnavailableRelativeTableDoesNotHideUsableIntent(bool output, bool saturation, bool relativeFirst)
    {
        Assert.Equal(Render(false), Render(true));

        byte[] Render(bool unavailableRelative)
        {
            PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
            KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
            string select = output ? "0.2 0.3 0.4 0.25 k " : "/Space cs 0.2 0.3 0.4 scn ";
            string intent = saturation ? "/Saturation ri " : "/Perceptual ri ";
            string content = (relativeFirst ? select + intent : intent + select) + "0 0 8 8 re f";
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8, Encoding.ASCII.GetBytes(content)).Build());
            var root = (PdfIndirectReference)source.Trailer[Name("Root")];
            var catalog = (PdfDictionary)source.Resolve(root);
            var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
            var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
            var page = (PdfDictionary)source.Resolve(pageReference);
            var update = new PdfIncrementalUpdateBuilder(source);
            int components = output ? 4 : 3;
            byte[] table = Lut(true, false, false, components), unsupported = "bad!\0\0\0\0"u8.ToArray();
            byte[] bytes = unavailableRelative
                ? Profile(output ? "CMYK" : "RGB ", "XYZ ", ("A2B0", saturation ? unsupported : table),
                    ("A2B1", unsupported), ("A2B2", table))
                : Profile(output ? "CMYK" : "RGB ", "XYZ ", ("A2B0", table));
            var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(components))]), bytes));
            if (output)
                update.ReplaceObject(root.ObjectNumber, new PdfDictionary(catalog.Append(Entry("OutputIntents",
                    new PdfArray([new PdfDictionary([Entry("S", Name("GTS_PDFX")), Entry("DestOutputProfile", profile)])])))));
            else
                update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                    .Append(Entry("Resources", new PdfDictionary([Entry("ColorSpace", new PdfDictionary([
                        Entry("Space", new PdfArray([Name("ICCBased"), profile]))]))])))));
            var renderer = new PdfPageRenderer(PdfDocument.Open(update.Build()));
            var rendered = renderer.Render(0, new PdfRenderOptions(8, 8));
            Assert.Equal(unavailableRelative && relativeFirst ? 1 : 0, rendered.Diagnostics.Count);
            return rendered.Pixels.ToArray();
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void NestedGroupPreservesInheritedCmykProfile(bool stroke, bool changeComponents, bool cmykGroup)
    {
        Assert.Equal(Render(false), Render(true));

        byte[] Render(bool inherited)
        {
            PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
            KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
            PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
            string select = stroke ? "/Space CS 0.2 0.3 0.4 0.25 SCN " : "/Space cs 0.2 0.3 0.4 0.25 scn ";
            string paint = stroke ? "8 w 0 4 m 8 4 l S" : "0 0 8 8 re f";
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8,
                Encoding.ASCII.GetBytes((inherited ? select : "") + "/Form Do")).Build());
            var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
            var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
            var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
            var page = (PdfDictionary)source.Resolve(pageReference);
            var update = new PdfIncrementalUpdateBuilder(source);
            byte[] forward = Lut(true, true, false, 4);
            for (int cell = 0; cell < 16; cell++)
                BinaryPrimitives.WriteUInt16BigEndian(forward.AsSpan(68 + cell * 6), (ushort)((cell & 1) == 0 ? 65280 : 0));
            var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(4))]),
                Profile("CMYK", "Lab ", ("A2B0", forward), ("B2A0", NeutralReverseLut()))));
            var space = new PdfArray([Name("ICCBased"), profile]);
            var spaces = new PdfDictionary([Entry("Space", space)]);
            PdfObject groupSpace = Name("DeviceRGB");
            if (cmykGroup)
            {
                var otherProfile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(4))]),
                    Profile("CMYK", "Lab ", ("A2B0", forward), ("B2A0", NeutralReverseLut()))));
                groupSpace = new PdfArray([Name("ICCBased"), otherProfile]);
            }
            string change = changeComponents ? stroke ? "0.4 0.1 0.2 0.5 SCN " : "0.4 0.1 0.2 0.5 scn " : "";
            var form = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 8, 8)),
                Entry("Resources", new PdfDictionary([Entry("ColorSpace", inherited ? new PdfDictionary([]) : spaces)])),
                Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")), Entry("I", new PdfBoolean(true)),
                    Entry("CS", groupSpace)]))]), Encoding.ASCII.GetBytes((inherited ? "" : select) + change + paint)));
            update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Concat([Entry("Resources", new PdfDictionary([Entry("ColorSpace", spaces),
                    Entry("XObject", new PdfDictionary([Entry("Form", form)]))])),
                    Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", space)]))])));
            var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(8, 8));
            Assert.Empty(rendered.Diagnostics);
            return rendered.Pixels.ToArray();
        }
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("stroke")]
    [InlineData("after-color")]
    [InlineData("graphics-state")]
    [InlineData("restore")]
    [InlineData("image")]
    [InlineData("image-override")]
    [InlineData("inline")]
    [InlineData("shading")]
    [InlineData("form")]
    [InlineData("default")]
    [InlineData("default-restore")]
    [InlineData("saturation")]
    [InlineData("unknown")]
    [InlineData("indexed")]
    [InlineData("separation")]
    [InlineData("device-n")]
    public void RenderHonorsSelectedIntent(string paint)
    {
        Assert.Equal(Render(true), Render(false));

        byte[] Render(bool reference)
        {
            PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
            KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
            PdfArray Numbers(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
            string select = "/Space cs 0.2 0.3 0.4 scn ";
            string fill = "0 0 8 8 re f";
            string content = paint switch
            {
                "stroke" => "/Perceptual ri /Space CS 0.2 0.3 0.4 SCN 8 w 0 4 m 8 4 l S",
                "after-color" => select + "/Perceptual ri " + fill,
                "graphics-state" => select + "/State gs " + fill,
                "restore" => select + "/Perceptual ri q /RelativeColorimetric ri Q " + fill,
                "image" => "/Perceptual ri 8 0 0 8 0 0 cm /Image Do",
                "image-override" => "8 0 0 8 0 0 cm /Image Do",
                "inline" => "/Perceptual ri 8 0 0 8 0 0 cm BI /W 1 /H 1 /BPC 8 /CS /Space ID abc EI",
                "shading" => "/Perceptual ri /Shade sh",
                "form" => select + "/Form Do",
                "default" => "0.2 0.3 0.4 rg /Perceptual ri " + fill,
                "default-restore" => "/Perceptual ri q /RelativeColorimetric ri 0.4 0.5 0.6 rg Q 0.2 0.3 0.4 rg " + fill,
                "saturation" => select + "/Saturation ri " + fill,
                "unknown" => "/Perceptual ri " + select + "/Unrecognized ri " + fill,
                "indexed" => "/Indexed cs 0 scn /Perceptual ri " + fill,
                "separation" => "/Spot cs 0.5 scn /Perceptual ri " + fill,
                "device-n" => "/Multi cs 0.5 scn /Perceptual ri " + fill,
                _ => "/Perceptual ri " + select + fill
            };
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8, Encoding.ASCII.GetBytes(content)).Build());
            var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
            var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
            var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
            var page = (PdfDictionary)source.Resolve(pageReference);
            var update = new PdfIncrementalUpdateBuilder(source);
            byte[] perceptual = Lut(true, false, paint == "unknown");
            byte[] profile = reference ? Profile("RGB ", "XYZ ", ("A2B0", perceptual))
                : Profile("RGB ", "XYZ ", ("A2B0", Lut(true, false, false)),
                    ("A2B1", Lut(true, false, true)), ("A2B2", Lut(true, false, false)));
            var profileReference = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(3))]), profile));
            var space = new PdfArray([Name("ICCBased"), profileReference]);
            var colorSpaces = new PdfDictionary([Entry("Space", space)]);
            if (paint is "default" or "default-restore") colorSpaces = new PdfDictionary(colorSpaces.Append(Entry("DefaultRGB", space)));
            var tint = new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
                Entry("C0", Numbers(0.2, 0.3, 0.4)), Entry("C1", Numbers(0.2, 0.3, 0.4)), Entry("N", new PdfInteger(1))]);
            colorSpaces = new PdfDictionary(colorSpaces.Concat([
                Entry("Indexed", new PdfArray([Name("Indexed"), space, new PdfInteger(0), new PdfString([51, 76, 102], PdfStringForm.Hexadecimal)])),
                Entry("Spot", new PdfArray([Name("Separation"), Name("Custom"), space, tint])),
                Entry("Multi", new PdfArray([Name("DeviceN"), new PdfArray([Name("Custom")]), space, tint]))]));
            var imageDictionary = new PdfDictionary([Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
                Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", space)]);
            if (paint == "image-override") imageDictionary = new PdfDictionary(imageDictionary.Append(Entry("Intent", Name("Perceptual"))));
            var image = update.AddObject(new PdfStream(imageDictionary, [51, 76, 102]));
            var form = update.AddObject(new PdfStream(new PdfDictionary([Entry("Subtype", Name("Form")),
                Entry("BBox", Numbers(0, 0, 8, 8)), Entry("Resources", new PdfDictionary([]))]),
                Encoding.ASCII.GetBytes("/Perceptual ri " + fill)));
            var function = new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
                Entry("C0", Numbers(0.2, 0.3, 0.4)), Entry("C1", Numbers(0.2, 0.3, 0.4)), Entry("N", new PdfInteger(1))]);
            var shading = new PdfDictionary([Entry("ShadingType", new PdfInteger(2)), Entry("ColorSpace", space),
                Entry("Coords", Numbers(0, 0, 8, 0)), Entry("Function", function)]);
            var resources = new PdfDictionary([Entry("ColorSpace", colorSpaces),
                Entry("XObject", new PdfDictionary([Entry("Image", image), Entry("Form", form)])),
                Entry("Shading", new PdfDictionary([Entry("Shade", shading)])),
                Entry("ExtGState", new PdfDictionary([Entry("State", new PdfDictionary([Entry("RI", Name("Perceptual"))]))]))]);
            update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources))));
            var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(8, 8));
            Assert.Empty(rendered.Diagnostics);
            return rendered.Pixels.ToArray();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelativeIntentRecoversAfterUnavailableAbsoluteIntent(bool output)
    {
        Assert.Equal(Render(false), Render(true));

        byte[] Render(bool switchIntent)
        {
            PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
            KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
            string select = output ? "0.2 0.3 0.4 0.1 k " : "/Space cs 0.2 0.3 0.4 scn ";
            string content = (switchIntent ? "/AbsoluteColorimetric ri " : "") + select
                + "/RelativeColorimetric ri 0 0 8 8 re f";
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8, Encoding.ASCII.GetBytes(content)).Build());
            var rootReference = (PdfIndirectReference)source.Trailer[Name("Root")];
            var catalog = (PdfDictionary)source.Resolve(rootReference);
            var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
            var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
            var page = (PdfDictionary)source.Resolve(pageReference);
            var update = new PdfIncrementalUpdateBuilder(source);
            int components = output ? 4 : 3;
            byte[] profile = Profile(output ? "CMYK" : "RGB ", "XYZ ",
                ("A2B0", Lut(true, false, false, components)), ("wtpt", Xyz(0, 1, 1)));
            var profileReference = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(components))]), profile));
            if (output)
                update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(catalog.Append(Entry("OutputIntents",
                    new PdfArray([new PdfDictionary([Entry("S", Name("GTS_PDFX")), Entry("DestOutputProfile", profileReference)])])))));
            else
                update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                    .Append(Entry("Resources", new PdfDictionary([Entry("ColorSpace", new PdfDictionary([
                        Entry("Space", new PdfArray([Name("ICCBased"), profileReference]))]))])))));
            var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(8, 8));
            Assert.Equal(switchIntent ? 1 : 0, rendered.Diagnostics.Count);
            return rendered.Pixels.ToArray();
        }
    }

    [Fact]
    public void IntentVariantsReuseEncodedStorageAndStableIdentities()
    {
        var profile = new PdfIccProfileTransform(Profile("RGB ", "XYZ ",
            ("A2B0", Lut(true, false, false)), ("A2B1", Lut(true, false, true)),
            ("wtpt", Xyz(0.75, 0.875, 0.5)), ("test", new byte[4 * 1024 * 1024])));
        long start = GC.GetAllocatedBytesForCurrentThread();
        var perceptual = profile.ForIntent(0);
        var saturation = profile.ForIntent(2);
        var absolute = profile.ForIntent(3);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 131072, $"Intent variants allocated {allocated} bytes.");
        Assert.NotNull(perceptual);
        Assert.NotNull(saturation);
        Assert.NotNull(absolute);
        Assert.Same(profile, absolute.ForIntent(1));
        Assert.Same(perceptual, saturation.ForIntent(0));
        Assert.Same(absolute, perceptual.ForIntent(3));
        var variants = new PdfIccProfileTransform?[32];
        Parallel.For(0, variants.Length, index => variants[index] = profile.ForIntent(3));
        Assert.All(variants, value => Assert.Same(absolute, value));
        start = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 100; repeat++) _ = profile.ForIntent(repeat % 4);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Fact]
    public void UnavailableAbsoluteIntentDoesNotRepeatExceptions()
    {
        var profile = new PdfIccProfileTransform(Profile("RGB ", "XYZ ",
            ("A2B0", Lut(true, false, true)), ("wtpt", Xyz(0, 1, 1))));
        Assert.Null(profile.ForIntent(3));
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 100; repeat++) _ = profile.ForIntent(3);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Theory]
    [InlineData("GRAY")]
    [InlineData("RGB ")]
    [InlineData("LUT")]
    public void AbsoluteIntentUsesMediaWhiteAndRelativeTables(string kind)
    {
        byte[] curve = new byte[12];
        "curv"u8.CopyTo(curve);
        var tags = new List<(string, byte[])> { ("wtpt", Xyz(0.75, 0.875, 0.5)) };
        if (kind == "GRAY") tags.Add(("kTRC", curve));
        else if (kind == "RGB ") tags.AddRange([
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
            ("rXYZ", Xyz(1, 0, 0)), ("gXYZ", Xyz(0, 1, 0)), ("bXYZ", Xyz(0, 0, 1))]);
        else tags.AddRange([
            ("A2B0", Lut(true, false, false)), ("A2B1", Lut(true, false, true)),
            ("B2A0", Lut(true, false, false)), ("B2A1", Lut(true, false, true))]);
        byte[] bytes = Profile(kind == "LUT" ? "RGB " : kind, "XYZ ", tags.ToArray());
        var relative = new PdfIccProfileTransform(bytes, 1);
        var absolute = new PdfIccProfileTransform(bytes, 3);
        double[] input = kind == "GRAY" ? [0.25] : [0.2, 0.3, 0.4];
        var expected = new double[3];
        var actual = new double[3];
        relative.ToXyz(input, expected);
        absolute.ToXyz(input, actual);
        Assert.Equal(expected[0] * (0.75 / 0.9642), actual[0], 12);
        Assert.Equal(expected[1] * 0.875, actual[1], 12);
        Assert.Equal(expected[2] * (0.5 / 0.8249), actual[2], 12);
        var restored = new double[input.Length];
        absolute.FromXyz(actual, restored);
        for (int channel = 0; channel < input.Length; channel++) Assert.Equal(input[channel], restored[channel], 12);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 100; repeat++)
        {
            absolute.ToXyz(input, actual);
            absolute.FromXyz(actual, restored);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AbsoluteIntentRejectsInvalidWhiteWithoutBreakingRelativeIntent(double white)
    {
        byte[] bytes = Profile("RGB ", "XYZ ", ("A2B0", Lut(true, false, true)), ("wtpt", Xyz(white, 1, 1)));
        _ = new PdfIccProfileTransform(bytes, 1);
        Assert.Throws<FormatException>(() => new PdfIccProfileTransform(bytes, 3));
    }

    [Fact]
    public void Render_RejectsReversibleLabBlendingProfile()
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("0.25 0.5 0.75 rg 0 0 1 1 re f")).Build());
        var options = new PdfRenderOptions(1, 1);
        byte[] expected = new PdfPageRenderer(source).Render(0, options).Pixels.ToArray();
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] table = Lut(false, true, true);
        byte[] bytes = Profile("Lab ", "Lab ", ("A2B0", table), ("B2A0", table));
        Assert.True(new PdfIccProfileTransform(bytes).CanConvertFromXyz);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([
            new(Name("N"), new PdfInteger(3)), new(Name("Alternate"), Name("DeviceRGB"))]), bytes));
        var group = new PdfDictionary([new(Name("S"), Name("Transparency")),
            new(Name("CS"), new PdfArray([Name("ICCBased"), profile]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Group"), group))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, options);
        Assert.Equal(expected, rendered.Pixels.ToArray());
        Assert.Contains(rendered.Diagnostics, message => message.Contains("ICC profile could not be used"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void LabInput_RejectsNonfiniteComponentsWithoutChangingOutput(double invalid)
    {
        var profile = new PdfIccProfileTransform(Profile("Lab ", "Lab ", ("A2B0", Lut(false, true, true))));
        for (int channel = 0; channel < 3; channel++)
        {
            double[] input = [50, 20, -30];
            input[channel] = invalid;
            double[] output = [7, 8, 9];
            Assert.Throws<ArgumentException>(() => profile.ToXyz(input, output));
            Assert.Equal(new double[] { 7, 8, 9 }, output);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_LabInputProfileMatchesNativeLab(bool image, bool explicitRange)
    {
        Assert.Equal(Render(false), Render(true));

        byte[] Render(bool profiled)
        {
            PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
            KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
            PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
            string expected = explicitRange ? image ? "50.19607843137255 20 -30" : "50 20 -30"
                : image ? "0.5019607843137255 0.5803921568627451 0.3843137254901961" : "1 1 0";
            string content = profiled && image ? "/Im Do"
                : $"/S cs {(profiled ? "50 20 -30" : expected)} scn 0 0 1 1 re f";
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, Encoding.ASCII.GetBytes(content)).Build());
            var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
            var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
            var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
            var page = (PdfDictionary)source.Resolve(reference);
            var update = new PdfIncrementalUpdateBuilder(source);
            PdfObject space = new PdfArray([Name("Lab"), new PdfDictionary([
                Entry("WhitePoint", new PdfArray([new PdfReal(0.9642), new PdfInteger(1), new PdfReal(0.8249)])),
                Entry("Range", Numbers(-128, 127, -128, 127))])]);
            if (profiled)
            {
                var entries = new List<KeyValuePair<PdfName, PdfObject>>
                {
                    Entry("N", new PdfInteger(3)), Entry("Alternate", Name("DeviceRGB"))
                };
                if (explicitRange) entries.Add(Entry("Range", Numbers(0, 100, -128, 127, -128, 127)));
                space = new PdfArray([Name("ICCBased"), update.AddObject(new PdfStream(new PdfDictionary(entries),
                    Profile("Lab ", "Lab ", ("A2B0", Lut(false, true, true))))) ]);
            }
            var imageReference = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
                Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", space)]), [128, 148, 98]));
            var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([Entry("S", space)])),
                Entry("XObject", new PdfDictionary([Entry("Im", imageReference)]))]);
            update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources))));
            var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(1, 1));
            Assert.Empty(rendered.Diagnostics);
            return rendered.Pixels.ToArray();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LabInput_ConvertsNaturalComponentsAndPreservesSameProfileValues(bool sixteen)
    {
        byte[] table = Lut(sixteen, true, true);
        var profile = new PdfIccProfileTransform(Profile("Lab ", "Lab ", ("A2B0", table), ("B2A0", table)));
        double[] values = [50, 20, -30];
        profile.ConvertTo(profile, values, values);
        Assert.Equal(new double[] { 50, 20, -30 }, values);
        profile.ToXyz(values, values);
        Assert.InRange(Math.Abs(values[1] - Math.Pow(66d / 116, 3)), 0, 1e-12);
        profile.FromXyz(values, values);
        Assert.InRange(Math.Abs(values[0] - 50), 0, 1e-10);
        Assert.InRange(Math.Abs(values[1] - 20), 0, 1e-10);
        Assert.InRange(Math.Abs(values[2] + 30), 0, 1e-10);
        profile.ConvertTo(profile, [-1, -200, 200], values);
        Assert.Equal(new double[] { 0, -128, 127 }, values);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_IccFallbackHonorsBothItsRangeAndTheLabAlternate(bool image, bool explicitRange)
    {
        Assert.Equal(Render(false), Render(true));

        byte[] Render(bool wrapped)
        {
            PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
            KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
            PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
            string referenceColor = explicitRange ? image ? "50.19607843137255 20 -30" : "100 20 -30"
                : image ? "1 1 1" : "1 1 0";
            string content = wrapped && image ? "/Im Do"
                : $"/S cs {(wrapped ? "120 100 -100" : referenceColor)} scn 0 0 1 1 re f";
            var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, Encoding.ASCII.GetBytes(content)).Build());
            var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
            var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
            var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
            var page = (PdfDictionary)source.Resolve(reference);
            var update = new PdfIncrementalUpdateBuilder(source);
            PdfObject space = new PdfArray([Name("Lab"), new PdfDictionary([
                Entry("WhitePoint", new PdfArray([new PdfReal(0.9642), new PdfInteger(1), new PdfReal(0.8249)])),
                Entry("Range", Numbers(-20, 20, -30, 30))])]);
            if (wrapped)
            {
                var entries = new List<KeyValuePair<PdfName, PdfObject>>
                {
                    Entry("N", new PdfInteger(3)), Entry("Alternate", space)
                };
                if (explicitRange) entries.Add(Entry("Range", Numbers(0, 100, -100, 100, -100, 100)));
                space = new PdfArray([Name("ICCBased"), update.AddObject(new PdfStream(new PdfDictionary(entries), []))]);
            }
            var imageReference = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
                Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", space)]), explicitRange ? [128, 255, 0] : [255, 255, 255]));
            var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([Entry("S", space)])),
                Entry("XObject", new PdfDictionary([Entry("Im", imageReference)]))]);
            update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources))));
            return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(1, 1)).Pixels.ToArray();
        }
    }

    [Theory]
    [InlineData("path", false, 64)]
    [InlineData("image", false, 64)]
    [InlineData("explicit", false, 64)]
    [InlineData("indexed", false, 64)]
    [InlineData("shading", false, 64)]
    [InlineData("path", true, 137)]
    [InlineData("image", true, 137)]
    [InlineData("explicit", true, 137)]
    [InlineData("indexed", true, 137)]
    [InlineData("shading", true, 137)]
    [InlineData("image-mid", false, 96)]
    [InlineData("image-mid", true, 165)]
    [InlineData("indexed-mid", false, 96)]
    [InlineData("indexed-mid", true, 165)]
    [InlineData("image-high", false, 191)]
    [InlineData("image-high", true, 225)]
    [InlineData("path-high", false, 191)]
    [InlineData("path-high", true, 225)]
    [InlineData("invalid-alternate-image", true, 137)]
    [InlineData("path-invalid-alternate", true, 137)]
    public void Render_HonorsIccComponentRange(string mode, bool supported, int expected)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        string content = mode.StartsWith("path", StringComparison.Ordinal)
            ? $"/S cs {(mode == "path-high" ? 1 : 0)} scn 0 0 1 1 re f"
            : mode == "shading" ? "/Sh sh" : "/Im Do";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, Encoding.ASCII.GetBytes(content)).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] curve = new byte[12];
        "curv"u8.CopyTo(curve);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("N", new PdfInteger(1)), Entry("Alternate", Name(mode.Contains("invalid-alternate") ? "InvalidSpace" : "DeviceGray")),
            Entry("Range", new PdfArray([new PdfReal(0.25), new PdfReal(0.75)]))]),
            supported ? Profile("GRAY", "XYZ ", ("kTRC", curve)) : []));
        var space = new PdfArray([Name("ICCBased"), profile]);
        PdfObject imageSpace = mode.StartsWith("indexed", StringComparison.Ordinal)
            ? new PdfArray([Name("Indexed"), space, new PdfInteger(0),
                new PdfString(mode == "indexed-mid" ? [64] : [0], PdfStringForm.Hexadecimal)]) : space;
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
            Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", imageSpace)
        };
        if (mode == "explicit") imageEntries.Add(Entry("Decode", Numbers(0, 1)));
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries),
            mode == "image-mid" ? [64] : mode == "image-high" ? [255] : [0]));
        var function = new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0)), Entry("C1", Numbers(0)), Entry("N", new PdfInteger(1))]);
        var shading = new PdfDictionary([Entry("ShadingType", new PdfInteger(2)), Entry("ColorSpace", space),
            Entry("Coords", Numbers(0, 0, 1, 0)), Entry("Function", function)]);
        var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([Entry("S", space)])),
            Entry("XObject", new PdfDictionary([Entry("Im", image)])),
            Entry("Shading", new PdfDictionary([Entry("Sh", shading)]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(1, 1));
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.All(pixels[..3], value => Assert.InRange((int)value, expected - 1, expected + 1));
        Assert.Equal((byte)255, pixels[3]);
    }

    [Theory]
    [InlineData("page", false, false)]
    [InlineData("page", true, false)]
    [InlineData("isolated", false, false)]
    [InlineData("isolated", true, false)]
    [InlineData("luminosity", false, false)]
    [InlineData("luminosity", true, false)]
    [InlineData("inherited", false, false)]
    [InlineData("inherited", true, false)]
    [InlineData("knockout", false, false)]
    [InlineData("knockout", true, false)]
    [InlineData("page", false, true)]
    [InlineData("page", true, true)]
    [InlineData("isolated", false, true)]
    [InlineData("isolated", true, true)]
    [InlineData("luminosity", false, true)]
    [InlineData("luminosity", true, true)]
    [InlineData("inherited", false, true)]
    [InlineData("inherited", true, true)]
    [InlineData("knockout", false, true)]
    [InlineData("knockout", true, true)]
    public void Render_BlendsInDeclaredProfile(string mode, bool image, bool gray)
        => RenderProfileBlend(mode, image, gray, false);

    [Theory]
    [InlineData("page", false, false)]
    [InlineData("page", true, false)]
    [InlineData("isolated", false, false)]
    [InlineData("luminosity", true, false)]
    [InlineData("page", false, true)]
    [InlineData("page", true, true)]
    [InlineData("isolated", false, true)]
    [InlineData("luminosity", true, true)]
    public void Render_BlendsInCalibratedSpace(string mode, bool image, bool gray)
        => RenderProfileBlend(mode, image, gray, true);

    [Theory]
    [InlineData(false, 2, 137)]
    [InlineData(true, 2, 137)]
    [InlineData(false, 3, 100)]
    [InlineData(true, 3, 100)]
    public void Render_CalibratedGammaControlsBlending(bool gray, double gamma, int expected)
        => RenderProfileBlend("page", false, gray, true, gamma, expected);

    private static void RenderProfileBlend(string mode, bool image, bool gray, bool calibrated,
        double gamma = 1, int expectedBlend = 188)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        string black = gray ? "0" : "0 0 0", white = gray ? "1" : "1 1 1";
        string content = $"/S cs {black} scn 0 0 1 1 re f /G gs "
            + (image ? "/Im Do" : $"{white} scn 0 0 1 1 re f");
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes(mode == "page" ? content : mode == "luminosity"
                ? "0 g 0 0 1 1 re f /M gs 1 g 0 0 1 1 re f"
                : mode is "inherited" or "knockout" ? $"/S cs {black} scn 0 0 1 1 re f /F Do" : "/F Do")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] curve = new byte[12];
        "curv"u8.CopyTo(curve);
        byte[] data = Profile("RGB ", "XYZ ", ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
            ("rXYZ", Xyz(0.4360747, 0.2225045, 0.0139322)),
            ("gXYZ", Xyz(0.3850649, 0.7168786, 0.0971045)),
            ("bXYZ", Xyz(0.1430804, 0.0606169, 0.7141733)));
        if (gray) data = Profile("GRAY", "XYZ ", ("kTRC", curve));
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(gray ? 1 : 3))]), data));
        var space = new PdfArray([Name("ICCBased"), profile]);
        if (calibrated)
        {
            PdfArray Reals(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
            var parameters = new List<KeyValuePair<PdfName, PdfObject>>
            {
                Entry("WhitePoint", Reals(0.9642, 1, 0.8249)),
                Entry("Gamma", gray ? new PdfReal(gamma) : Reals(gamma, gamma, gamma))
            };
            if (!gray) parameters.Add(Entry("Matrix", Reals(0.4360747, 0.2225045, 0.0139322,
                0.3850649, 0.7168786, 0.0971045, 0.1430804, 0.0606169, 0.7141733)));
            space = new PdfArray([Name(gray ? "CalGray" : "CalRGB"), new PdfDictionary(parameters)]);
        }
        var imageReference = update.AddObject(new PdfStream(new PdfDictionary([Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
            Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", space)]), gray ? [255] : [255, 255, 255]));
        var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([Entry("S", space)])),
            Entry("ExtGState", new PdfDictionary([Entry("G", new PdfDictionary([Entry("ca", new PdfReal(0.5))]))])),
            Entry("XObject", new PdfDictionary([Entry("Im", imageReference)]))]);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", space), Entry("I", new PdfBoolean(true))]);
        if (mode != "page")
        {
            bool inherited = mode is "inherited" or "knockout";
            var formGroup = inherited ? new PdfDictionary([Entry("S", Name("Transparency")),
                Entry("I", new PdfBoolean(false)), Entry("K", new PdfBoolean(mode == "knockout"))]) : group;
            string paint = image ? "/Im Do" : $"/S cs {white} scn 0 0 1 1 re f";
            string formContent = inherited ? "/G gs " + paint : content;
            if (mode == "knockout") formContent += " " + paint;
            var form = update.AddObject(new PdfStream(new PdfDictionary([Entry("Subtype", Name("Form")),
                Entry("BBox", Numbers(0, 0, 1, 1)), Entry("Resources", resources), Entry("Group", formGroup)]),
                Encoding.ASCII.GetBytes(formContent)));
            resources = mode != "luminosity"
                ? new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("F", form)])),
                    Entry("ColorSpace", new PdfDictionary([Entry("S", space)]))])
                : new PdfDictionary([Entry("ExtGState", new PdfDictionary([Entry("M", new PdfDictionary([
                    Entry("SMask", new PdfDictionary([Entry("S", Name("Luminosity")), Entry("G", form)]))]))]))]);
        }
        var pageEntries = page.Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources));
        if (mode is "page" or "inherited" or "knockout") pageEntries = pageEntries.Append(Entry("Group", group));
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(pageEntries));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(1, 1));
        byte[] pixels = rendered.Pixels.ToArray();
        int expected = mode == "luminosity" ? 128 : expectedBlend;
        Assert.All(pixels[..3], value => Assert.InRange((int)value, expected - 1, expected + 1));
        Assert.Equal((byte)255, pixels[3]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_ConvertsProfiledShadingOnlyWhenDestinationDiffers(bool sameProfile)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, Encoding.ASCII.GetBytes("/Sh sh")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] sourceLut = Lut(true, true, false, 4), destinationLut = Lut(true, true, false, 4);
        for (int cell = 0; cell < 16; cell++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(sourceLut.AsSpan(68 + cell * 6), 16320);
            BinaryPrimitives.WriteUInt16BigEndian(destinationLut.AsSpan(68 + cell * 6), (ushort)((cell & 1) == 0 ? 65280 : 0));
        }
        PdfArray Space(byte[] data) => new([Name("ICCBased"), update.AddObject(new PdfStream(
            new PdfDictionary([Entry("N", new PdfInteger(4))]), data))]);
        var sourceSpace = Space(Profile("CMYK", "Lab ", ("A2B0", sourceLut)));
        var destinationSpace = sameProfile ? sourceSpace : Space(Profile("CMYK", "Lab ",
            ("A2B0", destinationLut), ("B2A0", NeutralReverseLut())));
        var function = new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(0, 0, 0, 0)), Entry("N", new PdfInteger(1))]);
        var shading = new PdfDictionary([Entry("ShadingType", new PdfInteger(2)), Entry("ColorSpace", sourceSpace),
            Entry("Coords", Numbers(0, 0, 1, 0)), Entry("Function", function)]);
        var resources = new PdfDictionary([Entry("Shading", new PdfDictionary([Entry("Sh", shading)]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", new PdfDictionary([
                Entry("S", Name("Transparency")), Entry("CS", destinationSpace)])))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(1, 1));
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.InRange(pixels[0], (byte)58, (byte)60);
        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
        Assert.Equal((byte)255, pixels[3]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_CmykLuminosityMaskUsesItsDeclaredColorSpace(bool calibrated)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("/M gs 1 0 0 rg 0 0 2 1 re f")).Build());
        var catalogReference = (PdfIndirectReference)source.Trailer[Name("Root")];
        var catalog = (PdfDictionary)source.Resolve(catalogReference);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        byte[] lut = Lut(true, true, false, 4);
        for (int cell = 0; cell < 16; cell++) BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(68 + cell * 6), 16320);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(4))]),
            Profile("CMYK", "Lab ", ("A2B0", lut))));
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog.Append(Entry("OutputIntents",
            new PdfArray([new PdfDictionary([Entry("S", Name("GTS_PDFX")), Entry("DestOutputProfile", profile)])])))));
        var group = update.AddObject(new PdfStream(new PdfDictionary([Entry("Subtype", Name("Form")),
            Entry("BBox", new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(2), new PdfInteger(1)])),
            Entry("Resources", new PdfDictionary([])), Entry("Group", new PdfDictionary([
                Entry("S", Name("Transparency")), Entry("CS", calibrated
                    ? new PdfArray([Name("ICCBased"), profile]) : Name("DeviceCMYK"))]))]),
            Encoding.ASCII.GetBytes("0 0 0 0 k 0 0 1 1 re f")));
        var mask = new PdfDictionary([Entry("S", Name("Luminosity")), Entry("G", group),
            Entry("BC", new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(1)]))]);
        var resources = new PdfDictionary([Entry("ExtGState", new PdfDictionary([
            Entry("M", new PdfDictionary([Entry("SMask", mask)]))]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(2, 1, transparentBackground: true));
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.Equal(calibrated ? (byte)11 : (byte)255, pixels[3]);
        Assert.Equal(calibrated ? (byte)11 : (byte)0, pixels[7]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Render_RemovesRgbMatteWithIndependentMaskSamples(int bits)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 1,
            Encoding.ASCII.GetBytes("q 4 0 0 1 0 0 cm /Im Do Q")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var mask = update.AddObject(new PdfStream(new PdfDictionary([Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(4)), Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceGray")), Entry("Matte", new PdfArray([new PdfInteger(1), new PdfInteger(0), new PdfInteger(0)]))]),
            [0, 64, 192, 255]));
        byte[] samples = bits == 8 ? [128, 64, 0] : [128, 128, 64, 64, 0, 0];
        var image = update.AddObject(new PdfStream(new PdfDictionary([Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(bits)),
            Entry("ColorSpace", Name("DeviceRGB")), Entry("SMask", mask)]), samples));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Im", image)]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(4, 1, transparentBackground: true));
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.Equal((byte)0, pixels[3]);
        Assert.Equal(new byte[] { 0, 255, 0, 64, 0, 85, 86, 192, 0, 64, 128, 255 }, pixels[4..]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_RemovesMatteBeforeIccConversion(bool indexed, bool higherResolutionMask)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        byte[] gamma = new byte[14];
        "curv"u8.CopyTo(gamma);
        BinaryPrimitives.WriteUInt32BigEndian(gamma.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(gamma.AsSpan(12), 512);
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1,
            Encoding.ASCII.GetBytes("q 3 0 0 1 0 0 cm /Im Do Q")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(1))]),
            Profile("GRAY", "XYZ ", ("kTRC", gamma))));
        PdfObject space = new PdfArray([Name("ICCBased"), profile]);
        byte[] samples = higherResolutionMask ? [128] : [191, 127, 63];
        if (indexed)
        {
            space = new PdfArray([Name("Indexed"), space, new PdfInteger(samples.Length - 1), new PdfString(samples, PdfStringForm.Hexadecimal)]);
            samples = higherResolutionMask ? [0] : [0, 1, 2];
        }
        var mask = update.AddObject(new PdfStream(new PdfDictionary([Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(3)), Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceGray")), Entry("Matte", new PdfArray([new PdfInteger(1)]))]), [64, 128, 192]));
        var image = update.AddObject(new PdfStream(new PdfDictionary([Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(samples.Length)), Entry("Height", new PdfInteger(1)),
            Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", space), Entry("SMask", mask)]), samples));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Im", image)]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(3, 1, transparentBackground: true));
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.Equal((byte)0, pixels[0]);
        Assert.Equal((byte)0, pixels[4]);
        Assert.InRange(pixels[8], higherResolutionMask ? (byte)94 : (byte)0, higherResolutionMask ? (byte)96 : (byte)0);
        Assert.Equal(new byte[] { 64, 128, 192 }, new byte[] { pixels[3], pixels[7], pixels[11] });
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_UsesDocumentOutputProfileForDeviceCmykPathsAndImages(bool cmykPage)
    {
        byte[] lut = Lut(true, true, false, 4);
        for (int cell = 0; cell < 16; cell++) BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(68 + cell * 6), 16320);
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        var content = new PdfContentStreamBuilder().SetFillCmyk(0, 1, 0, 0).Rectangle(0, 0, 1, 1).Fill()
            .DrawImage(PdfImage.FromCmyk(1, 1, new byte[] { 0, 255, 0, 0 }), 1, 0, 1, 1);
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1, content).Build());
        var catalogReference = (PdfIndirectReference)source.Trailer[Name("Root")];
        var catalog = (PdfDictionary)source.Resolve(catalogReference);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([]), Profile("CMYK", "Lab ", ("A2B0", lut))));
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog.Append(Entry("OutputIntents",
            new PdfArray([new PdfDictionary([Entry("S", Name("GTS_PDFX")), Entry("DestOutputProfile", profile)])])))));
        if (cmykPage) update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Append(Entry("Group",
            new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))])))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(2, 1));
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.InRange(pixels[0], (byte)58, (byte)60);
        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
        Assert.Equal(pixels[..4], pixels[4..]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PreservesIccSourcePrecisionForPathsAndSixteenBitImages()
    {
        byte[] gamma = new byte[14];
        "curv"u8.CopyTo(gamma);
        BinaryPrimitives.WriteUInt32BigEndian(gamma.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(gamma.AsSpan(12), 51200);
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 1, Encoding.ASCII.GetBytes(
            "/S cs 0.999389638 scn 0 0 1 1 re f 1 scn 1 0 1 1 re f q 2 0 0 1 2 0 cm /Im Do Q")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(1))]),
            Profile("GRAY", "XYZ ", ("kTRC", gamma))));
        var space = new PdfArray([Name("ICCBased"), profile]);
        var image = update.AddObject(new PdfStream(new PdfDictionary([Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(1)),
            Entry("BitsPerComponent", new PdfInteger(16)), Entry("ColorSpace", space)]), [255, 215, 255, 255]));
        var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([Entry("S", space)])),
            Entry("XObject", new PdfDictionary([Entry("Im", image)]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(4, 1));
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.InRange(pixels[0], (byte)240, (byte)243);
        Assert.Equal((byte)255, pixels[4]);
        Assert.Equal(pixels[..8], pixels[8..]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false, 1, false, false)]
    [InlineData(true, 1, false, false)]
    [InlineData(false, 0, false, false)]
    [InlineData(true, 0, false, false)]
    [InlineData(false, 2, false, false)]
    [InlineData(true, 2, false, false)]
    [InlineData(false, 1, true, false)]
    [InlineData(true, 1, true, false)]
    [InlineData(false, 0, true, false)]
    [InlineData(true, 0, true, false)]
    [InlineData(false, 2, true, false)]
    [InlineData(true, 2, true, false)]
    [InlineData(false, 0, false, true)]
    [InlineData(true, 0, false, true)]
    [InlineData(false, 0, true, true)]
    [InlineData(true, 0, true, true)]
    public void Render_ConvertsIsolatedGroupIntoDifferentParentProfile(bool rgbParent, int intent, bool rgbGroup, bool unavailable)
    {
        int components = rgbGroup ? 3 : 4, tableOffset = 52 + components * 4;
        byte[] groupLut = Lut(true, true, false, components), pageLut = Lut(true, true, false, 4);
        for (int cell = 0; cell < 1 << components; cell++)
            BinaryPrimitives.WriteUInt16BigEndian(groupLut.AsSpan(tableOffset + cell * 6), 16320);
        for (int cell = 0; cell < 16; cell++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pageLut.AsSpan(68 + cell * 6), (ushort)((cell & 1) == 0 ? 65280 : 0));
        }
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        string intentName = intent == 0 ? "Perceptual" : intent == 2 ? "Saturation" : "RelativeColorimetric";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes($"/{intentName} ri /F Do")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfArray Space(byte[] profile, int count = 4) => new([Name("ICCBased"), update.AddObject(new PdfStream(
            new PdfDictionary([Entry("N", new PdfInteger(count))]), profile))]);
        byte[] relativeLut = groupLut.ToArray();
        if (intent != 1)
            for (int cell = 0; cell < 1 << components; cell++)
                BinaryPrimitives.WriteUInt16BigEndian(relativeLut.AsSpan(tableOffset + cell * 6), 48960);
        PdfArray groupSpace = Space(Profile(rgbGroup ? "RGB " : "CMYK", "Lab ",
            ("A2B0", unavailable ? Encoding.ASCII.GetBytes("bad!00000000") : groupLut),
            ("A2B1", relativeLut), ("A2B2", groupLut),
            ("B2A0", rgbGroup ? Lut(true, false, true) : NeutralReverseLut())), components);
        byte[] relativeReverse = NeutralReverseLut();
        if (intent != 1)
            for (int cell = 0; cell < 8; cell++)
                BinaryPrimitives.WriteUInt16BigEndian(relativeReverse.AsSpan(64 + cell * 8 + 6), 65535);
        PdfArray pageSpace = Space(Profile("CMYK", "Lab ", ("A2B0", pageLut),
            ("B2A0", NeutralReverseLut()), ("B2A1", relativeReverse), ("B2A2", NeutralReverseLut())));
        var form = new PdfStream(new PdfDictionary([Entry("Subtype", Name("Form")),
            Entry("BBox", new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1)])),
            Entry("Resources", new PdfDictionary([])),
            Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", groupSpace),
                Entry("I", new PdfBoolean(true))]))]), Encoding.ASCII.GetBytes(
                    "/RelativeColorimetric ri " + (rgbGroup ? "0 0 0 rg " : "0 0 0 0 k ") + "0 0 1 1 re f"));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("F", update.AddObject(form))]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", new PdfDictionary([
                Entry("S", Name("Transparency")), Entry("CS", rgbParent ? Name("DeviceRGB") : pageSpace)])))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(1, 1));
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.InRange(pixels[0], unavailable ? (byte)184 : (byte)58, unavailable ? (byte)186 : (byte)60);
        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
        Assert.Equal((byte)255, pixels[3]);
        if (unavailable) Assert.Contains("group rendering intent", Assert.Single(rendered.Diagnostics));
        else Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false, false, 0, "cmyk")]
    [InlineData(true, false, 0, "cmyk")]
    [InlineData(false, true, 0, "cmyk")]
    [InlineData(true, true, 0, "cmyk")]
    [InlineData(false, false, 1, "cmyk")]
    [InlineData(true, false, 2, "cmyk")]
    [InlineData(false, false, 0, "rgb")]
    [InlineData(true, false, 0, "rgb")]
    [InlineData(false, false, 0, "image")]
    [InlineData(true, false, 0, "image")]
    [InlineData(false, false, 0, "cmykGroup")]
    [InlineData(true, false, 0, "cmykGroup")]
    [InlineData(false, false, 0, "rgbGroup")]
    [InlineData(true, false, 0, "rgbGroup")]
    [InlineData(false, false, 0, "gray")]
    [InlineData(true, false, 0, "gray")]
    [InlineData(false, false, 0, "grayImage")]
    [InlineData(true, false, 0, "grayImage")]
    public void Render_CmykPageUsesItsIccProfileAfterBlending(bool transparent, bool named, int invalid, string sourceKind)
    {
        bool rgb = sourceKind != "cmyk";
        byte[] lut = Lut(true, true, false, 4);
        for (int cell = 0; cell < 16; cell++)
            BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(52 + 16 + cell * 6),
                (ushort)((cell & 1) == 0 ? 65280 : 0));
        byte[] profile = rgb ? Profile("CMYK", "Lab ", ("A2B0", lut), ("B2A0", NeutralReverseLut()))
            : Profile("CMYK", "Lab ", ("A2B0", lut));
        if (invalid == 1) profile[36] = 0;
        var content = new PdfContentStreamBuilder().SetOpacity(0.5);
        if (sourceKind is "gray" or "grayImage") content.SetFillGray(0.5);
        else if (rgb) content.SetFillRgb(0.5, 0.5, 0.5);
        else content.SetFillCmyk(0, 1, 0, 0);
        if (sourceKind == "image") content.DrawImage(PdfImage.FromRgb(1, 1, new byte[] { 128, 128, 128 }), 0, 0, 1, 1);
        else if (sourceKind == "grayImage") content.DrawImage(PdfImage.FromGray(1, 1, new byte[] { 128 }), 0, 0, 1, 1);
        else content.Rectangle(0, 0, 1, 1).Fill();
        content.SetFillCmyk(0, 1, 0, 1).Rectangle(1, 0, 1, 1).Fill();
        if (sourceKind is "cmykGroup" or "rgbGroup")
            content = new PdfContentStreamBuilder().DrawForm(new PdfFormXObject(2, 1, content,
                isolatedTransparencyGroup: true, transparencyGroupColorSpace: sourceKind == "cmykGroup"
                    ? PdfTransparencyGroupColorSpace.Cmyk : PdfTransparencyGroupColorSpace.Rgb), 0, 0);
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1, content).Build());
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var profileEntries = new List<KeyValuePair<PdfName, PdfObject>> { new(Name("N"), new PdfInteger(4)) };
        if (invalid == 2) profileEntries.Add(new(Name("Filter"), Name("UnknownFilter")));
        var profileReference = update.AddObject(new PdfStream(new PdfDictionary(profileEntries), profile));
        var colorSpace = new PdfArray([Name("ICCBased"), profileReference]);
        var group = new PdfDictionary([new(Name("S"), Name("Transparency")),
            new(Name("CS"), named ? Name("PageInk") : colorSpace)]);
        if (named)
        {
            PdfObject resourceValue = page[Name("Resources")];
            var resources = (PdfDictionary)(resourceValue is PdfIndirectReference resourceReference
                ? source.Resolve(resourceReference) : resourceValue);
            page = new PdfDictionary(page.Where(entry => !entry.Key.Equals(Name("Resources"))).Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Resources"), new PdfDictionary(resources.Append(
                    new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), new PdfDictionary([
                        new(Name("PageInk"), colorSpace)])))))));
        }
        update.ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Append(new KeyValuePair<PdfName, PdfObject>(Name("Group"), group))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(2, 1, transparentBackground: transparent));
        byte[] pixels = rendered.Pixels.ToArray();
        if (invalid != 0)
        {
            Assert.Equal(PdfDeviceCmykTests.RenderInk(0, transparent ? (byte)255 : (byte)128)[1], pixels[1]);
            Assert.Contains(rendered.Diagnostics, message => message.Contains("ICC profile could not be used"));
            return;
        }
        if (sourceKind is "gray" or "grayImage")
            Assert.InRange(pixels[1], transparent ? (byte)118 : (byte)183, transparent ? (byte)120 : (byte)186);
        else if (rgb) Assert.InRange(pixels[1], transparent ? (byte)126 : (byte)189, transparent ? (byte)129 : (byte)192);
        else Assert.Equal((byte)255, pixels[1]);
        Assert.Equal(transparent ? (byte)128 : (byte)255, pixels[3]);
        // The RGB group first maps the unprofiled magenta-plus-black ink to RGB (21, 0, 0).
        // The page's neutral ICC transform therefore retains a small nonzero shadow value.
        if (transparent && sourceKind == "rgbGroup") Assert.Equal((byte)6, pixels[4]);
        else Assert.InRange(pixels[4], transparent ? (byte)0 : (byte)118, transparent ? (byte)0 : (byte)120);
        Assert.Equal(pixels[4], pixels[5]);
        Assert.Equal(pixels[5], pixels[6]);
        Assert.Empty(rendered.Diagnostics);
    }

    private static byte[] NeutralReverseLut()
    {
        byte[] bytes = new byte[144];
        "mft2"u8.CopyTo(bytes);
        bytes[8] = 3;
        bytes[9] = 4;
        bytes[10] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(50), 2);
        for (int channel = 0; channel < 3; channel++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(54 + channel * 4), 65535);
        for (int cell = 0; cell < 8; cell++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(64 + cell * 8 + 6), (ushort)(cell < 4 ? 65535 : 0));
        for (int channel = 0; channel < 4; channel++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(130 + channel * 4), 65535);
        return bytes;
    }

    [Fact]
    public void CanConvertFromXyz_RejectsDescendingCurveBeforeWritingOutput()
    {
        byte[] curve = new byte[18];
        "curv"u8.CopyTo(curve);
        BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(14), 65535);
        var profile = new PdfIccProfileTransform(Profile("RGB ", "XYZ ",
            ("rTRC", new byte[] { 99, 117, 114, 118, 0, 0, 0, 0, 0, 0, 0, 0 }),
            ("gTRC", curve), ("bTRC", curve),
            ("rXYZ", Xyz(1, 0, 0)), ("gXYZ", Xyz(0, 1, 0)), ("bXYZ", Xyz(0, 0, 1))));
        Assert.False(profile.CanConvertFromXyz);
        double[] result = [0.9, 0.8, 0.7];
        Assert.Throws<NotSupportedException>(() => profile.FromXyz([0.2, 0.3, 0.4], result));
        Assert.Equal(new double[] { 0.9, 0.8, 0.7 }, result);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ConvertTo_PreservesSameProfileComponentsWithoutReverseTable(int components)
    {
        var profile = new PdfIccProfileTransform(Profile(components == 4 ? "CMYK" : "RGB ", "XYZ ",
            ("A2B0", Lut(true, false, false, components))));
        double[] source = components == 4 ? [0.123456789, 0.432198765, 0.987654321, 0.246813579]
            : [0.123456789, 0.432198765, 0.987654321];
        double[] result = new double[components];
        profile.ConvertTo(profile, source, result);
        Assert.Equal(source, result);
        profile.ConvertTo(profile, result, result);
        Assert.Equal(source, result);
        source[0] = double.NaN;
        Assert.Throws<ArgumentException>(() => profile.ConvertTo(profile, source, result));
    }

    [Fact]
    public void ConvertTo_UsesDestinationCurvesWithoutAllocatingPerColor()
    {
        byte[] identity = new byte[12];
        "curv"u8.CopyTo(identity);
        byte[] gamma = new byte[14];
        "curv"u8.CopyTo(gamma);
        BinaryPrimitives.WriteUInt32BigEndian(gamma.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(gamma.AsSpan(12), 512);
        var source = new PdfIccProfileTransform(Profile("GRAY", "XYZ ", ("kTRC", identity)));
        var target = new PdfIccProfileTransform(Profile("GRAY", "XYZ ", ("kTRC", gamma)));
        double[] input = [0.25], output = new double[1];
        source.ConvertTo(target, input, output);
        Assert.Equal(0.5, output[0]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++) source.ConvertTo(target, input, output);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        source.ConvertTo(target, input, input);
        Assert.Equal(0.5, input[0]);
    }

    [Fact]
    public void ToXyz_UsesRgbCurvesAndMatrixColumns()
    {
        byte[] gamma = new byte[14];
        "curv"u8.CopyTo(gamma);
        BinaryPrimitives.WriteUInt32BigEndian(gamma.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(gamma.AsSpan(12), 512);
        var profile = new PdfIccProfileTransform(Profile("RGB ", "XYZ ",
            ("rTRC", gamma), ("gTRC", gamma), ("bTRC", gamma),
            ("rXYZ", Xyz(0, 1, 0)), ("gXYZ", Xyz(1, 0, 0)), ("bXYZ", Xyz(0, 0, 1))));
        double[] result = new double[3];
        profile.ToXyz([0.5, 0.75, 1], result);
        Assert.Equal(new double[] { 0.5625, 0.25, 1 }, result);
        profile.FromXyz(result, result);
        Assert.Equal(new double[] { 0.5, 0.75, 1 }, result);
    }

    [Fact]
    public void ToXyz_MapsNeutralGrayOntoD50()
    {
        byte[] identity = new byte[12];
        "curv"u8.CopyTo(identity);
        var profile = new PdfIccProfileTransform(Profile("GRAY", "XYZ ", ("kTRC", identity)));
        double[] result = new double[3];
        profile.ToXyz([0.5], result);
        Assert.Equal(new double[] { 0.4821, 0.5, 0.41245 }, result);
        double[] gray = new double[1];
        profile.FromXyz(result, gray);
        Assert.Equal(0.5, gray[0]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Transform_UsesLutSpecificPcsEncoding(bool sixteen, bool lab)
    {
        var profile = new PdfIccProfileTransform(Profile("RGB ", lab ? "Lab " : "XYZ ",
            ("A2B0", Lut(sixteen, lab, false)), ("B2A0", Lut(sixteen, lab, true))));
        double[] xyz = new double[3];
        profile.ToXyz([0.2, 0.4, 0.6], xyz);
        double[] expected = lab ? [0.9642, 1, 0.8249] : [1, 1, 1];
        for (int channel = 0; channel < 3; channel++) Assert.Equal(expected[channel], xyz[channel], 10);
        double[] result = new double[3];
        profile.FromXyz(xyz, result);
        double scale = sixteen ? 65535d : 255;
        double[] encoded = lab ? sixteen ? [65280 / scale, 32768 / scale, 32768 / scale]
            : [1, 128 / scale, 128 / scale]
            : Enumerable.Repeat((sixteen ? 32768 : 128) / scale, 3).ToArray();
        for (int channel = 0; channel < 3; channel++) Assert.Equal(encoded[channel], result[channel], 10);
    }

    [Fact]
    public void Constructor_RejectsInvalidTagBounds()
    {
        byte[] profile = Profile("RGB ", "XYZ ", ("A2B0", Lut(true, false, false)));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(136), uint.MaxValue);
        Assert.Throws<FormatException>(() => new PdfIccProfileTransform(profile));
    }

    private static byte[] Profile(string space, string pcs, params (string Name, byte[] Data)[] tags)
    {
        int size = 132 + tags.Length * 12 + tags.Sum(tag => (tag.Data.Length + 3) & ~3);
        byte[] bytes = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)size);
        Encoding.ASCII.GetBytes(space).CopyTo(bytes, 16);
        Encoding.ASCII.GetBytes(pcs).CopyTo(bytes, 20);
        "acsp"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), (uint)tags.Length);
        int offset = 132 + tags.Length * 12;
        for (int index = 0; index < tags.Length; index++)
        {
            int entry = 132 + index * 12;
            Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(bytes, entry);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), (uint)tags[index].Data.Length);
            tags[index].Data.CopyTo(bytes, offset);
            offset += (tags[index].Data.Length + 3) & ~3;
        }
        return bytes;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        byte[] bytes = new byte[20];
        "XYZ "u8.CopyTo(bytes);
        double[] values = [x, y, z];
        for (int channel = 0; channel < 3; channel++)
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8 + channel * 4), (int)(values[channel] * 65536));
        return bytes;
    }

    private static byte[] Lut(bool sixteen, bool lab, bool identity, int inputs = 3)
    {
        int entries = sixteen ? 2 : 256, sample = sixteen ? 2 : 1, header = sixteen ? 52 : 48;
        int cells = 1 << inputs;
        byte[] bytes = new byte[header + sample * ((inputs + 3) * entries + cells * 3)];
        (sixteen ? "mft2"u8 : "mft1"u8).CopyTo(bytes);
        bytes[8] = (byte)inputs;
        bytes[9] = 3;
        bytes[10] = 2;
        for (int row = 0; row < 3; row++) BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12 + row * 16), 65536);
        if (sixteen)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(48), 2);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(50), 2);
        }
        int offset = header, maximum = sixteen ? 65535 : 255;
        void Write(int value)
        {
            if (sixteen) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), (ushort)value);
            else bytes[offset] = (byte)value;
            offset += sample;
        }
        for (int channel = 0; channel < inputs; channel++)
            for (int entry = 0; entry < entries; entry++) Write(entry * maximum / (entries - 1));
        for (int cell = 0; cell < cells; cell++)
            for (int channel = 0; channel < 3; channel++)
                Write(identity ? ((cell >> (2 - channel)) & 1) * maximum
                    : lab && channel == 0 ? sixteen ? 65280 : 255 : sixteen ? 32768 : 128);
        for (int channel = 0; channel < 3; channel++)
            for (int entry = 0; entry < entries; entry++) Write(entry * maximum / (entries - 1));
        return bytes;
    }
}
