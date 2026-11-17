using KillerPdf.Engine.Filters;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfJpeg2000TileLengthTests
{
    // The JPXDecode stream of corpus regression/pdfjs/test/pdfs/bug_jpx.pdf. Its TLM marker
    // declares one-byte tile-parts, and OpenJPEG 2.5.4 corrupts the native heap when it seeks by them.
    private static readonly byte[] BogusTileLengths = Convert.FromBase64String(
        "/0//UQApAAAAAACAAAAAQAAAAAAAAAAAAAAAQAAAAEAAAAAAAAAAAAABBwEB/1IADAAAAAEAAAQEAAH/XAAEQED/ZAAlAAFDcmVhdGVkIGJ5IE9wZW5KUEVHIHZlcnNpb24gMi41LjT/VQAMAEAAAAEAAAABAP+QAAoAAAAAABsAAP+T34BQEVBUr/9/6Ylgf/+QAAoAAAAAAA4BAP+T/5AACgAAAAAADgIA/5P/kAAKAAEAAAAbAAH/k9+AUBFQVK//f+mJYH//2Q==");

    [Fact]
    public void TileLengthMarkersAreRemovedFromTheMainHeader()
    {
        byte[] cleaned = PdfJpeg2000Decoder.WithoutTileLengthMarkers(BogusTileLengths);

        byte[] expected = [.. BogusTileLengths.AsSpan(0, 104), .. BogusTileLengths.AsSpan(118)];
        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public void CodestreamWithoutTileLengthMarkersIsUnchanged()
    {
        byte[] cleaned = PdfJpeg2000Decoder.WithoutTileLengthMarkers(BogusTileLengths);

        Assert.Equal(cleaned, PdfJpeg2000Decoder.WithoutTileLengthMarkers(cleaned));
    }

    // Before the fix the second or third decode ended the test process with heap corruption.
    [Fact]
    public void BogusTileLengthsDecodeRepeatedly()
    {
        byte[]? first = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Jpeg2000DecodedImage image = PdfJpeg2000Decoder.DecodeImage(BogusTileLengths, 1_000_000, -1);
            Assert.Equal(128, image.Width);
            Assert.Equal(64, image.Height);
            first ??= image.Samples;
            Assert.Equal(first, image.Samples);
        }
    }
}
