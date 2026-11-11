using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionOverprintSourceTests
{
    private const string Content = ".8 .6 .4 .2 k 0 0 1 1 re f q /Over gs .2 0 .8 0 k 0 0 1 1 re f Q .5 .3 .6 .1 k 1 0 1 1 re f";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitBindingIgnoresWorkingProfileButRemovingItChangesAppearance(bool catalog)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(catalog)));
        var plain = new PdfRenderOptions(2, 1);
        var original = renderer.RenderFrame(0, plain);
        var changedWorking = plain with { WorkingCmykProfile = PdfColorProfile.Load(Source(alternate: true)) };
        var swapped = renderer.RenderFrame(0, changedWorking);
        Assert.Equal(original.Pixels.ToArray(), swapped.Pixels.ToArray());
        Assert.Null(swapped.AssumedCmykProfileSha256);
        var removed = new PdfPageRenderer(PdfDocument.Open(Page(catalog, removeSource: true))).RenderFrame(0, plain);
        Assert.NotNull(removed.AssumedCmykProfileSha256);
        Assert.True(Enumerable.Range(0, 3).Sum(channel => Math.Abs(original.Pixels[channel] - removed.Pixels[channel])) > .01);
        var otherSource = new PdfPageRenderer(PdfDocument.Open(Page(catalog, profile: Source(alternate: true))))
            .RenderFrame(0, plain);
        Assert.True(Enumerable.Range(0, 3).Sum(channel => Math.Abs(original.Pixels[channel] - otherSource.Pixels[channel])) > .01);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectiveSourceKindControlsTheOpmOneZeroException(bool catalog)
    {
        string literal = catalog ? ".2 .6 .8 .2" : ".2 0 .8 0";
        string content = ".8 .6 .4 .2 k 0 0 1 1 re f q /Over gs .2 0 .8 0 k 0 0 1 1 re f Q "
            + literal + " k 1 0 1 1 re f";
        var frame = new PdfPageRenderer(PdfDocument.Open(Page(catalog, content,
            settings: "/op true /OPM 1 /ca 1"))).RenderFrame(0, new PdfRenderOptions(2, 1));
        MatchStrip(frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelativeSourceBindingSurvivesUnpaintedIntentAndSavedStateChanges(bool catalog)
    {
        byte[] profile = Source(allIntents: true);
        var expected = new PdfPageRenderer(PdfDocument.Open(Page(catalog, profile: profile)))
            .RenderFrame(0, new PdfRenderOptions(2, 1));
        const string content = "/Perceptual ri .8 .6 .4 .2 k /RelativeColorimetric ri 0 0 1 1 re f "
            + "q /Saturation ri .1 .2 .3 .4 k Q q /Over gs .2 0 .8 0 k 0 0 1 1 re f Q .5 .3 .6 .1 k 1 0 1 1 re f";
        var actual = new PdfPageRenderer(PdfDocument.Open(Page(catalog, content, profile))).RenderFrame(0, new PdfRenderOptions(2, 1));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MixedOrStalePaintIntentIsRefusedEvenWithinTheSameProfile(bool catalog, bool stale)
    {
        string content = stale
            ? ".8 .6 .4 .2 k /Perceptual ri 0 0 1 1 re f /RelativeColorimetric ri /Over gs .2 0 .8 0 k 0 0 1 1 re f"
            : ".8 .6 .4 .2 k 0 0 1 1 re f /Perceptual ri /Over gs .2 0 .8 0 k 0 0 1 1 re f";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(catalog, content, Source(allIntents: true))));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrameRegion(0, new PdfRenderOptions(2, 1), 0, 0, 1, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitSourceRequiresActualA2B1InsteadOfIntentFallback(bool catalog)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(catalog, profile: Source(tag: "A2B0"))));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceBpcAndDirectIccPaintsRemainRefused(bool catalog)
    {
        var bpc = new PdfPageRenderer(PdfDocument.Open(Page(catalog, settings: "/op true /OPM 0 /UseBlackPtComp /ON")));
        Assert.Throws<NotSupportedException>(() => bpc.RenderFrame(0, new PdfRenderOptions(2, 1)));
        var direct = new PdfPageRenderer(PdfDocument.Open(Page(catalog,
            ".8 .6 .4 .2 k 0 0 1 1 re f /Other cs .2 0 .8 0 scn /Over gs 0 0 1 1 re f",
            extraSpaces: "/Other [/ICCBased 7 0 R]", otherProfile: Source(alternate: true))));
        Assert.Throws<NotSupportedException>(() => direct.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void NonUnitDefaultRangeRemainsRefused()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(false,
            profileEntries: "/Range [-1 1 0 1 0 1 0 1]")));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void InvalidDeclaredDefaultCannotBecomeWorkingFallbackBeforeAllocation()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(false, channels: 3)));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1, maximumPixelBytes: 8)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProofCompensationAndPrinterIdentityRemainSeparateFromTheSource(bool catalog, bool compensation)
    {
        var printer = PdfProofPaperWhiteTests.MisalignedPrinter(true);
        var options = new PdfRenderOptions(2, 1) { Proof = new PdfDocumentProofSettings(true, printer,
            new PdfProofOptions { RenderingIntent = PdfRenderingIntent.RelativeColorimetric,
                BlackPointCompensation = compensation, SimulateBlack = true }, gamutWarnings: true) };
        var frame = new PdfPageRenderer(PdfDocument.Open(Page(catalog))).RenderFrame(0, options);
        MatchStrip(frame);
        Assert.Null(frame.AssumedCmykProfileSha256);
        Assert.Equal(printer.ContentSha256, frame.PrinterGamutWarnings!.PrinterProfileSha256);
        Assert.NotEqual(PdfColorProfile.Load(Source()).ContentSha256, printer.ContentSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitSourcesRetainNativeStorageAndCancellationBoundaries(bool catalog)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(catalog)));
        var plain = new PdfRenderOptions(2, 1, maximumPixelBytes: 104);
        Assert.Equal(8, renderer.RenderFrame(0, plain).Pixels.Length);
        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1, maximumPixelBytes: 103)));
        var proof = new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(true), gamutWarnings: true);
        var options = new PdfRenderOptions(2, 1, maximumPixelBytes: 108) { Proof = proof };
        Assert.NotNull(renderer.RenderFrame(0, options).PrinterGamutWarnings);
        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1, maximumPixelBytes: 107) { Proof = proof }));
        Assert.NotNull(renderer.RenderFrameRegion(0, new PdfRenderOptions(2, 1, maximumPixelBytes: 54) { Proof = proof }, 0, 0, 1, 1));
        Assert.Throws<ArgumentException>(() => renderer.RenderFrameRegion(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 53) { Proof = proof }, 0, 0, 1, 1));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.RenderFrame(0, options, canceled.Token));
        Assert.NotNull(renderer.RenderFrame(0, options).PrinterGamutWarnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitSourceComposesNativeInkBeforeForwardConversionAndProof(bool catalog)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(catalog)));
        var options = new PdfRenderOptions(2, 1);
        var frame = renderer.RenderFrame(0, options);
        // Affine source: X=.125+.25C+.0625K, Y=.25+.25M+.125K,
        // Z=.0625+.375Y+.0625K. Native mean(.5,.3,.6,.1) gives PCS(.25625,.3375,.29375).
        // Independent Decimal60 Bradford inverse and sRGB matrix values are retained in task evidence.
        double[] expected = [.11321487947824696, .4057116896806186, .35409583879442286];
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], expected[channel] - 2e-6, expected[channel] + 2e-6);
        MatchStrip(frame);
        Assert.Null(frame.AssumedCmykProfileSha256);
        Assert.Equal(frame.Pixels.Slice(0, 4).ToArray(), renderer.RenderFrameRegion(0, options, 0, 0, 1, 1).Pixels.ToArray());
        var proof = options with { Proof = new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(true),
            new PdfProofOptions { RenderingIntent = PdfRenderingIntent.RelativeColorimetric, BlackPointCompensation = false }) };
        var proofed = renderer.RenderFrame(0, proof);
        MatchStrip(proofed);
        Assert.Equal(proofed.Pixels.Slice(0, 4).ToArray(), renderer.RenderFrameRegion(0, proof, 0, 0, 1, 1).Pixels.ToArray());
    }

    private static void MatchStrip(PdfRenderedFrame frame)
    {
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[channel], frame.Pixels[4 + channel] - 1e-7f, frame.Pixels[4 + channel] + 1e-7f);
    }

    private static byte[] Page(bool catalog, string content = Content, byte[]? profile = null, bool removeSource = false,
        string settings = "/op true /OPM 0 /ca .5", string profileEntries = "", int channels = 4,
        string extraSpaces = "", byte[]? otherProfile = null) => TestIccProfiles.Pdf(
        "<< /Type /Catalog /Pages 2 0 R " + (catalog && !removeSource ? "/OutputIntents [<< /S /GTS_PDFX /DestOutputProfile 6 0 R >>]" : "") + " >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2 1] /Resources << "
            + "/ColorSpace << " + (!catalog && !removeSource ? "/DefaultCMYK [/ICCBased 6 0 R] " : "") + extraSpaces
            + " >> /ExtGState << /Over 4 0 R >> >> /Contents 5 0 R >>",
        $"<< {settings} >>", TestIccProfiles.Stream("", Encoding.ASCII.GetBytes(content)),
        TestIccProfiles.Stream($"/N {channels} {profileEntries}", profile ?? Source()),
        otherProfile is null ? "<< >>" : TestIccProfiles.Stream("/N 4", otherProfile));

    private static byte[] Source(bool alternate = false, string tag = "A2B1", bool allIntents = false)
    {
        byte[] table = new byte[52 + 4 * 4 + 2 * 16 * 3 + 4 * 3];
        "mft2"u8.CopyTo(table); table[8] = 4; table[9] = 3; table[10] = 2;
        for (int axis = 0; axis < 3; axis++) BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(12 + axis * 16), 65536);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(50), 2);
        int offset = 52;
        void Append(int value) { BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(offset), (ushort)value); offset += 2; }
        for (int channel = 0; channel < 4; channel++) { Append(0); Append(65535); }
        for (int cell = 0; cell < 16; cell++)
        {
            int cyan = cell / 8, magenta = cell / 4 % 2, yellow = cell / 2 % 2, black = cell % 2;
            int shift = alternate ? 4096 : 0;
            Append(shift + 4096 + 8192 * cyan + 2048 * black);
            Append(shift + 8192 + 8192 * magenta + 4096 * black);
            Append(shift + 2048 + 12288 * yellow + 2048 * black);
        }
        for (int channel = 0; channel < 3; channel++) { Append(0); Append(65535); }
        var otherIntent = (byte[])table.Clone();
        // Non-relative variants deliberately disagree numerically, so an unused source
        // change cannot silently replace the fixed relative transform of accumulated plates.
        for (int entry = 68; entry < 164; entry += 2)
            BinaryPrimitives.WriteUInt16BigEndian(otherIntent.AsSpan(entry),
                (ushort)(BinaryPrimitives.ReadUInt16BigEndian(otherIntent.AsSpan(entry)) + 4096));
        return TestIccProfiles.Profile("prtr", "CMYK", "XYZ ", allIntents
            ? [("A2B1", table), ("A2B0", otherIntent), ("A2B2", otherIntent)] : [(tag, table)]);
    }
}
