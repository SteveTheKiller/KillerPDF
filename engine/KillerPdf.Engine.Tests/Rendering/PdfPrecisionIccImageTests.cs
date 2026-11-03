using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionIccImageTests
{
    private const string RgbProfileBase64 =
        "AAAB/AAAAAACQAAAbW50clJHQiBYWVogB+oACwADAAAAAAAAYWNzcE1TRlQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAPbWAAEAAAAA0y10ZXN0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJZGVzYwAAAPAAAAByY3BydAAAAWQAAAAjd3RwdAAAAYgAAAAUclhZWgAAAZwAAAAUZ1hZWgAAAbAAAAAUYlhZWgAAAcQAAAAUclRSQwAAAdgAAAAMZ1RSQwAAAeQAAAAMYlRSQwAAAfAAAAAMZGVzYwAAAAAAAAAYU3ludGhldGljIFhZWi1iYXNpcyBSR0IAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB0ZXh0AAAAAFB1YmxpYyBkb21haW4gdGVzdCBmaXh0dXJlAABYWVogAAAAAAAA9tYAAQAAAADTLVhZWiAAAAAAAAD21gAAAAAAAAAAWFlaIAAAAAAAAAAAAAEAAAAAAABYWVogAAAAAAAAAAAAAAAAAADTLWN1cnYAAAAAAAAAAGN1cnYAAAAAAAAAAGN1cnYAAAAAAAAAAA==";
    private const string GrayProfileBase64 =
        "AAABaAAAAAACQAAAbW50ckdSQVlYWVogB+oACwADAAAAAAAAYWNzcE1TRlQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAPbWAAEAAAAA0y10ZXN0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEZGVzYwAAALQAAABwY3BydAAAASQAAAAjd3RwdAAAAUgAAAAUa1RSQwAAAVwAAAAMZGVzYwAAAAAAAAAWU3ludGhldGljIGxpbmVhciBHcmF5AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAHRleHQAAAAAUHVibGljIGRvbWFpbiB0ZXN0IGZpeHR1cmUAAFhZWiAAAAAAAAD21gABAAAAANMtY3VydgAAAAAAAAAA";

    [Fact]
    public void NaturalIccImageRetainsAdjacentSixteenBitLevelsAndExtendedGamut()
    {
        byte[] profile = Convert.FromBase64String(RgbProfileBase64);
        Assert.Equal("044A5A9A7551EC7229EF682863DF9E7137A24D8F123BFFA14801B883D195D640",
            Convert.ToHexString(SHA256.HashData(profile)));
        var renderer = Renderer(profile);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        // Exact fixed16 diagonal matrix and identity TRCs give D50 PCS XYZ.
        // Independent Decimal60 Bradford inversion and the standard XYZ-to-sRGB
        // matrix give these literals, allowing rounded matrix/float storage error.
        AssertPixel(frame, 0, 1.51082417f, -0.47186967f, 0.03469589f);
        AssertPixel(frame, 1, 1.51087027f, -0.47188407f, 0.03469695f);
        Assert.True(frame.Pixels[0] > 1f);
        Assert.True(frame.Pixels[1] < 0f);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
    }

    [Fact]
    public void NaturalGrayIccImageRetainsAdjacentSixteenBitLevels()
    {
        byte[] profile = Convert.FromBase64String(GrayProfileBase64);
        Assert.Equal("1CA9DB10A5ECD4E8C3D19310E500F6CD6C2974074465823D7A5ECBCD33EA7FD1",
            Convert.ToHexString(SHA256.HashData(profile)));
        var renderer = Renderer(profile, components: 1);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        // Oracle uses the profile's quantized D50 white. The tolerance also covers
        // the existing Gray PCS approximation's rounded D50 white, chosen before rendering.
        AssertPixel(frame, 0, 0.50001085f, 0.50000629f, 0.50001152f, 0.000006f);
        AssertPixel(frame, 1, 0.50002611f, 0.50002155f, 0.50002678f, 0.000006f);
        Assert.True(frame.Pixels[4] > frame.Pixels[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NaturalEightBitIccImagesUseProfileTrcs(bool gray)
    {
        var renderer = Renderer(Convert.FromBase64String(gray ? GrayProfileBase64 : RgbProfileBase64),
            components: gray ? 1 : 3, bits: 8);

        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true));

        if (gray)
            AssertPixel(frame, 0, 0.50196402f, 0.50195944f, 0.50196469f, 0.000006f);
        else
            AssertPixel(frame, 0, 1.51672582f, -0.47371291f, 0.03483142f);
    }

    [Fact]
    public void InvalidProfileFallbackDoesNotBecomePreciseDeviceRendering()
    {
        var renderer = Renderer([0, 1, 2]);

        Assert.NotEmpty(renderer.Render(0, new PdfRenderOptions(2, 1)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void UsableForwardLutsAndCmykRemainOutsideThePrecisionImageSlice(int components)
    {
        var renderer = Renderer(WithForwardLut(Convert.FromBase64String(RgbProfileBase64), components),
            components: components);

        Assert.Empty(renderer.Render(0, new PdfRenderOptions(2, 1)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData(4, false, null)]
    [InlineData(4, true, null)]
    [InlineData(2, false, "2 0 0 1 0.5 0 cm /Image Do")]
    public void IccImageScalingFilteringAndUnalignedEdgesRemainRejected(int width, bool interpolate, string? content)
    {
        var renderer = Renderer(Convert.FromBase64String(RgbProfileBase64), width: width,
            interpolate: interpolate, content: content);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(width, 1)));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("soft")]
    [InlineData("matte")]
    public void IccImageMasksAndMatteRemainRejected(string mask)
    {
        var renderer = Renderer(Convert.FromBase64String(RgbProfileBase64), mask: mask);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData("Perceptual")]
    [InlineData("Saturation")]
    [InlineData("AbsoluteColorimetric")]
    public void UntestedRenderingIntentsRemainRejected(string intent)
    {
        var renderer = Renderer(Convert.FromBase64String(RgbProfileBase64), intent: intent);

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void BlackPointCompensationIsNotEnabledThroughTheIccImageGate()
    {
        var renderer = Renderer(Convert.FromBase64String(RgbProfileBase64),
            content: "/Bpc gs 2 0 0 1 0 0 cm /Image Do");

        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void DefaultDeviceProfileSubstitutionIsNotClassifiedAsTheDirectIccImageSlice()
    {
        var renderer = Renderer(Convert.FromBase64String(RgbProfileBase64), defaultSpace: true);

        Assert.Empty(renderer.Render(0, new PdfRenderOptions(2, 1)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void OtherColorConvertersAreNotAdmittedByTheIccImageGate()
    {
        var calibrated = new PdfArray([Name("CalRGB"), new PdfDictionary([
            Entry("WhitePoint", new PdfArray([new PdfReal(0.9642), new PdfInteger(1), new PdfReal(0.8249)]))])]);
        var renderer = Renderer(Convert.FromBase64String(RgbProfileBase64), overrideSpace: calibrated);

        Assert.Empty(renderer.Render(0, new PdfRenderOptions(2, 1)).Diagnostics);
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void CompatibilityCachePreservesTheLaterExtendedPrecisionImage()
    {
        var renderer = Renderer(Convert.FromBase64String(RgbProfileBase64));
        var options = new PdfRenderOptions(2, 1, transparentBackground: true);

        var compatibility = renderer.Render(0, options);
        var frame = renderer.RenderFrame(0, options);

        Assert.Equal(new byte[] { 52, 0, 255, 255, 52, 0, 255, 255 }, compatibility.Pixels.ToArray());
        AssertPixel(frame, 0, 1.51082417f, -0.47186967f, 0.03469589f);
        Assert.Equal(compatibility.Pixels.ToArray(), frame.ToSrgbBgra32());
    }

    private static PdfPageRenderer Renderer(byte[] profile, int components = 3, int bits = 16,
        int width = 2, string? content = null, bool interpolate = false, string? mask = null,
        string? intent = null, bool defaultSpace = false, PdfObject? overrideSpace = null)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, 1,
            Encoding.ASCII.GetBytes(content ?? $"{width} 0 0 1 0 0 cm /Image Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var icc = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(components))]), profile));
        byte[] samples = Enumerable.Range(0, 2).SelectMany(pixel => Enumerable.Range(0, components)
            .SelectMany(channel => bits == 16
                ? channel == 0 ? new byte[] { 128, (byte)pixel } : [0, 0]
                : new byte[] { channel == 0 ? (byte)(128 + pixel) : (byte)0 })).ToArray();
        var iccSpace = new PdfArray([Name("ICCBased"), icc]);
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(1)),
            Entry("ColorSpace", overrideSpace ?? (defaultSpace ? Name("DeviceRGB") : iccSpace)),
            Entry("BitsPerComponent", new PdfInteger(bits)), Entry("Interpolate", new PdfBoolean(interpolate))
        };
        if (intent is not null) imageEntries.Add(Entry("Intent", Name(intent)));
        if (mask == "key")
            imageEntries.Add(Entry("Mask", new PdfArray(Enumerable.Repeat<PdfObject>(new PdfInteger(0), components * 2))));
        else if (mask is "soft" or "matte")
        {
            var maskEntries = new List<KeyValuePair<PdfName, PdfObject>> {
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(2)), Entry("Height", new PdfInteger(1)),
                Entry("ColorSpace", Name("DeviceGray")), Entry("BitsPerComponent", new PdfInteger(16))
            };
            if (mask == "matte")
                maskEntries.Add(Entry("Matte", new PdfArray(Enumerable.Repeat<PdfObject>(new PdfInteger(1), components))));
            var soft = update.AddObject(new PdfStream(new PdfDictionary(maskEntries), [255, 255, 255, 255]));
            imageEntries.Add(Entry("SMask", soft));
        }
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries), samples));
        var resourceEntries = new List<KeyValuePair<PdfName, PdfObject>> {
            Entry("XObject", new PdfDictionary([Entry("Image", image)])),
            Entry("ExtGState", new PdfDictionary([Entry("Bpc",
                new PdfDictionary([Entry("UseBlackPtComp", new PdfBoolean(true))]))]))
        };
        if (defaultSpace) resourceEntries.Add(Entry("ColorSpace", new PdfDictionary([Entry("DefaultRGB", iccSpace)])));
        var resources = new PdfDictionary(resourceEntries);
        var document = PdfDocument.Open(update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(original.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
        return new PdfPageRenderer(document);
    }

    private static byte[] WithForwardLut(byte[] original, int components)
    {
        byte[] lut = new byte[48 + components * 256 + (1 << components) * 3 + 3 * 256];
        "mft1"u8.CopyTo(lut);
        lut[8] = (byte)components;
        lut[9] = 3;
        lut[10] = 2;
        for (int axis = 0; axis < 3; axis++)
            BinaryPrimitives.WriteInt32BigEndian(lut.AsSpan(12 + (axis * 3 + axis) * 4), 65536);
        int cursor = 48;
        for (int channel = 0; channel < components; channel++)
            for (int entry = 0; entry < 256; entry++) lut[cursor++] = (byte)entry;
        for (int cell = 0; cell < 1 << components; cell++)
            for (int channel = 0; channel < 3; channel++)
                lut[cursor++] = (byte)((cell >> (components - 1 - channel) & 1) * 255);
        for (int channel = 0; channel < 3; channel++)
            for (int entry = 0; entry < 256; entry++) lut[cursor++] = (byte)entry;
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(128));
        int tableEnd = 132 + count * 12;
        byte[] result = new byte[original.Length + 12 + lut.Length];
        original.AsSpan(0, tableEnd).CopyTo(result);
        original.AsSpan(tableEnd).CopyTo(result.AsSpan(tableEnd + 12));
        for (int index = 0; index < count; index++)
        {
            int offset = 132 + index * 12 + 4;
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset),
                BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(offset)) + 12);
        }
        "A2B0"u8.CopyTo(result.AsSpan(tableEnd));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(tableEnd + 4), (uint)(original.Length + 12));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(tableEnd + 8), (uint)lut.Length);
        lut.CopyTo(result.AsSpan(original.Length + 12));
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(128), (uint)(count + 1));
        if (components == 4)
        {
            "CMYK"u8.CopyTo(result.AsSpan(16));
            "prtr"u8.CopyTo(result.AsSpan(12));
        }
        return result;
    }

    private static void AssertPixel(PdfRenderedFrame frame, int pixel, float red, float green, float blue,
        float tolerance = 0.000002f)
    {
        int offset = pixel * 4;
        float[] expected = [red, green, blue];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], expected[channel] - tolerance, expected[channel] + tolerance);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
