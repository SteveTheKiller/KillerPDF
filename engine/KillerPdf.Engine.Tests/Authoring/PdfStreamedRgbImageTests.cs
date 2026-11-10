using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

// A page-size photo is compressed row by row: every row goes through the adaptive PNG predictor straight into one Flate
// stream, so the only large buffers are the pixels and the compressed image. The written image must decode to the
// exact pixels, with predictor 15 over three colors, whatever the content (noise included, where whole-image Flate
// without prediction would otherwise be kept).
public sealed class PdfStreamedRgbImageTests
{
    [Theory]
    [InlineData("noise")]
    [InlineData("gradient")]
    public void AStreamedRgbImageDecodesToItsExactPixelsThroughThePngPredictor(string content)
    {
        const int width = 97, height = 41;
        var pixels = new byte[width * height * 3];
        var random = new Random(7);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++)
                    pixels[(y * width + x) * 3 + c] = content == "noise"
                        ? (byte)random.Next(256) : (byte)(x * 2 + y * c);

        PdfImage image = PdfImage.FromRgbRows(width, height, pixels);

        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 100, 100)).Build());
        var stream = ImageStream(document);
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(stream.Dictionary[Name("Filter")]).ValueAsLatin1());
        var parameters = Assert.IsType<PdfDictionary>(stream.Dictionary[Name("DecodeParms")]);
        Assert.Equal(15, Assert.IsType<PdfInteger>(parameters[Name("Predictor")]).Value);
        Assert.Equal(3, Assert.IsType<PdfInteger>(parameters[Name("Colors")]).Value);
        Assert.Equal(width, Assert.IsType<PdfInteger>(parameters[Name("Columns")]).Value);
        Assert.Equal(pixels, PdfStreamDecoder.Decode(stream, document.Resolve, pixels.Length));
    }

    // A 600 dpi letter photo (5100 x 6600) took about 5 s to encode with no way to stop it. The token is read before
    // the first row and then once per band of 32 rows (about 25 ms of that page), so a cancel stops the encode after
    // at most 32 more rows.
    [Fact]
    public void ACanceledEncodeStopsWithinThirtyTwoRowsOfTheCancel()
    {
        const int width = 64, height = 1000;
        using var cancellation = new CancellationTokenSource();
        var destination = new CancelingStream(cancellation, cancelOnWrite: 1);

        Assert.Throws<OperationCanceledException>(() => PdfPngPredictorEncoder.EncodeTo(destination, width, height, 3,
            Noise(width, height), cancellation.Token));

        Assert.InRange(destination.WritesAfterCancel, 0, 32);
    }

    [Fact]
    public void ACanceledTokenStopsTheStreamedImageBeforeItsFirstRow()
    {
        const int width = 64, height = 200;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var destination = new CancelingStream(cancellation, cancelOnWrite: 0);

        Assert.Throws<OperationCanceledException>(() => PdfPngPredictorEncoder.EncodeTo(destination, width, height, 3,
            Noise(width, height), cancellation.Token));
        Assert.Equal(0, destination.Writes);
        Assert.Throws<OperationCanceledException>(() => PdfImage.FromRgbRows(width, height, Noise(width, height),
            cancellation.Token));
    }

    // Uncanceled, the streamed rows are exactly the whole-image predictor's output, band edges included.
    [Fact]
    public void AnUncanceledStreamedEncodeWritesExactlyTheWholeImageEncoderBytes()
    {
        const int width = 37, height = 101;
        var pixels = Noise(width, height);
        using var cancellation = new CancellationTokenSource();
        using var destination = new MemoryStream();

        PdfPngPredictorEncoder.EncodeTo(destination, width, height, 3, pixels, cancellation.Token);

        Assert.Equal(PdfPngPredictorEncoder.Encode(width, height, 3, pixels), destination.ToArray());
    }

    private static byte[] Noise(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        new Random(11).NextBytes(pixels);
        return pixels;
    }

    // Counts row writes and cancels the token at the given write (1 = the first row; 0 = never).
    private sealed class CancelingStream(CancellationTokenSource cancellation, int cancelOnWrite) : Stream
    {
        internal int Writes { get; private set; }
        internal int WritesAfterCancel { get; private set; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            if (cancelOnWrite > 0 && Writes > cancelOnWrite) WritesAfterCancel++;
            if (Writes == cancelOnWrite) cancellation.Cancel();
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static PdfStream ImageStream(PdfDocument document)
    {
        PdfDictionary Dictionary(PdfObject? value) => Assert.IsType<PdfDictionary>(
            value is PdfIndirectReference reference ? document.Resolve(reference) : value);
        var catalog = Dictionary(document.Trailer[Name("Root")]);
        var pages = Dictionary(catalog[Name("Pages")]);
        var page = Dictionary(Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        var xobjects = Dictionary(Dictionary(page[Name("Resources")])[Name("XObject")]);
        return Assert.IsType<PdfStream>(document.Resolve(Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")])));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
