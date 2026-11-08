using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfRasterExtractionTests
{
    // Four by three sRGB frame. Row 0: red, green, blue, white. Row 1: half gray, black, yellow, cyan.
    // Row 2: magenta, quarter gray, three-quarter gray, red at half alpha.
    private static PdfRenderedFrame Frame() => new(4, 3,
    [
        1,0,0,1,  0,1,0,1,  0,0,1,1,  1,1,1,1,
        .5f,.5f,.5f,1,  0,0,0,1,  1,1,0,1,  0,1,1,1,
        1,0,1,1,  .25f,.25f,.25f,1,  .75f,.75f,.75f,1,  1,0,0,.5f
    ], PdfFrameColorEncoding.SrgbD65);

    [Fact]
    public void SelectedPixelsBecomeACanonicalTransparentImageOfTheirBoundingBox()
    {
        // A two by two square at (1,0) with its lower-right pixel subtracted: an L with a transparent hole.
        var selection = PdfRasterSelection.SelectRectangle(4, 3, 1, 0, 2, 2)
            .Subtract(PdfRasterSelection.SelectRectangle(4, 3, 2, 1, 1, 1));

        var result = PdfRasterExtraction.Extract(Frame(), selection);

        Assert.Equal((1, 0, 2, 2), (result.Left, result.Top, result.Width, result.Height));
        Assert.Equal((2, 2), (result.Image.Width, result.Image.Height));
        var (rgb, alpha) = Samples(result.Image);
        // Green, blue / black, then the hole; sRGB .5 is 127.5, which rounds away from zero to 128.
        Assert.Equal(new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0 }, rgb[..9]);
        Assert.Equal(new byte[] { 255, 255, 255, 0 }, alpha);
        // The burn, print and flatten path accepts the asset as a canonical detached image.
        byte[] drawn = new PdfDocumentBuilder().AddPage(2, 2,
            new PdfContentStreamBuilder().DrawImage(result.Image, 0, 0, 2, 2)).Build();
        Assert.Single(new PdfPageContentReader(PdfDocument.Open(drawn)).ReadImagePlacements(0));
    }

    [Fact]
    public void FuzzySeedCopiesTheConnectedRegionAndKeepsThePageTransparency()
    {
        // Seed on the half-alpha red at (3,2): tolerance .3 reaches nothing else, since its neighbors
        // differ by at least .25 in two channels and by .75 or more in another.
        var frame = Frame();
        var seeded = PdfRasterSelection.SelectConnected(frame, 3, 2, .3f);
        var single = PdfRasterExtraction.Extract(frame, seeded);
        Assert.Equal((3, 2, 1, 1), (single.Left, single.Top, single.Width, single.Height));
        var (rgb, alpha) = Samples(single.Image);
        Assert.Equal(new byte[] { 255, 0, 0 }, rgb);
        // Coverage 255 times page alpha 128 (0.5 quantized away from zero), divided by 255 and rounded.
        Assert.Equal(new byte[] { 128 }, alpha);

        // Seeding the quarter gray with tolerance .5 (inclusive) reaches the black above it, the half
        // gray beside that and the three-quarter gray to its right; the colors around them differ by .75.
        var gray = PdfRasterExtraction.Extract(frame, PdfRasterSelection.SelectConnected(frame, 1, 2, .5f));
        Assert.Equal((0, 1, 3, 2), (gray.Left, gray.Top, gray.Width, gray.Height));
        Assert.Equal(new byte[] { 255, 255, 0, 0, 255, 255 }, Samples(gray.Image).Alpha);
    }

    [Fact]
    public void FeatheringIsOffByDefaultAndAveragesTheMaskOverItsRadius()
    {
        var frame = new PdfRenderedFrame(5, 5, Enumerable.Repeat(1f, 100).ToArray(), PdfFrameColorEncoding.SrgbD65);
        var dot = PdfRasterSelection.SelectRectangle(5, 5, 2, 2, 1, 1);
        var hard = PdfRasterExtraction.Extract(frame, dot);
        Assert.Equal((2, 2, 1, 1), (hard.Left, hard.Top, hard.Width, hard.Height));
        Assert.Equal(new byte[] { 255 }, Samples(hard.Image).Alpha);

        var soft = PdfRasterExtraction.Extract(frame, dot, featherRadius: 1);
        Assert.Equal((1, 1, 3, 3), (soft.Left, soft.Top, soft.Width, soft.Height));
        // Each pixel of the three by three window sees one selected pixel of nine: 255 / 9 = 28.3.
        Assert.Equal(Enumerable.Repeat((byte)28, 9).ToArray(), Samples(soft.Image).Alpha);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfRasterExtraction.Extract(frame, dot, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfRasterExtraction.Extract(frame, dot, PdfRasterExtraction.MaximumFeatherRadius + 1));
    }

    [Fact]
    public void RefusesEmptyMismatchedAndOversizedSelections()
    {
        var frame = Frame();
        var empty = PdfRasterSelection.SelectRectangle(4, 3, 0, 0, 1, 1).Subtract(PdfRasterSelection.SelectRectangle(4, 3, 0, 0, 1, 1));
        Assert.Throws<ArgumentException>(() => PdfRasterExtraction.Extract(frame, empty));
        Assert.Throws<ArgumentException>(() => PdfRasterExtraction.Extract(frame, PdfRasterSelection.SelectRectangle(3, 3, 0, 0, 1, 1)));
        // 1,400 by 1,000 is 1.4 million pixels, above the 1,310,720 a five MiB detached image allows.
        var large = new PdfRenderedFrame(1400, 1000, new float[1400 * 1000 * 4], PdfFrameColorEncoding.SrgbD65);
        Assert.Throws<NotSupportedException>(() => PdfRasterExtraction.Extract(large,
            PdfRasterSelection.SelectRectangle(1400, 1000, 0, 0, 1400, 1000)));
    }

    [Fact]
    public void CancellationStopsExtractionWithoutTouchingItsInputs()
    {
        var frame = Frame();
        float[] pixels = frame.Pixels.ToArray();
        var selection = PdfRasterSelection.SelectRectangle(4, 3, 0, 0, 4, 3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfRasterExtraction.Extract(frame, selection, 0, cancellation.Token));
        Assert.Equal(pixels, frame.Pixels.ToArray());
        Assert.All(selection.Coverage.ToArray(), value => Assert.Equal(255, value));
    }

    [Fact]
    public void CutFillSamplesTheSurroundingColorAndCoversExactlyTheSelectedShape()
    {
        // A five by four page: white, with a black square at (1,1)-(2,2), a red pixel touching it at
        // (3,1) and a transparent pixel touching it at (1,3).
        float[] pixels = Enumerable.Repeat(1f, 5 * 4 * 4).ToArray();
        void Set(int x, int y, float r, float g, float b, float a)
        { int i = (y * 5 + x) * 4; pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a; }
        foreach (var (x, y) in new[] { (1, 1), (2, 1), (1, 2), (2, 2) }) Set(x, y, 0, 0, 0, 1);
        Set(3, 1, 1, 0, 0, 1);
        Set(1, 3, 0, 0, 0, 0);
        var frame = new PdfRenderedFrame(5, 4, pixels, PdfFrameColorEncoding.SrgbD65);
        var selection = PdfRasterSelection.SelectRectangle(5, 4, 1, 1, 2, 2);

        // The opaque unselected four-neighbors of the square: (1,0), (2,0), (0,1), (0,2), (3,2), (2,3)
        // are white and (3,1) is red; (1,3) is transparent and ignored. Red channel 7 * 255 / 7 = 255;
        // green and blue 6 * 255 / 7 = 218.57, which rounds to 219.
        Assert.Equal((255, 219, 219), PdfRasterExtraction.SampleFillColor(frame, selection, ringDistance: 1));
        // Three steps out are only (4,0) and (4,3), both white.
        Assert.Equal((255, 255, 255), PdfRasterExtraction.SampleFillColor(frame, selection));

        var copy = PdfRasterExtraction.Extract(frame, selection);
        var fill = PdfRasterExtraction.CreateFill(selection, copy, 10, 20, 30);
        var (rgb, alpha) = Samples(fill);
        Assert.Equal((2, 2), (fill.Width, fill.Height));
        Assert.Equal(new byte[] { 10, 20, 30, 10, 20, 30, 10, 20, 30, 10, 20, 30 }, rgb);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, alpha);
        // A selection with no opaque neighbor falls back to white.
        Assert.Equal((255, 255, 255), PdfRasterExtraction.SampleFillColor(frame, PdfRasterSelection.SelectRectangle(5, 4, 0, 0, 5, 4)));
    }

    [Fact]
    public void TheSampledFillLooksPastTheAntialiasedEdgeOfTheSelection()
    {
        // One row: white, white, white, edge gray .6, three selected blue, edge gray .6, white, white, white.
        float[] row = new float[11 * 4];
        for (int x = 0; x < 11; x++)
        {
            float v = x is 3 or 7 ? .6f : 1;
            (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]) = x is >= 4 and <= 6 ? (0f, 0f, 1f, 1f) : (v, v, v, 1f);
        }
        var frame = new PdfRenderedFrame(11, 1, row, PdfFrameColorEncoding.SrgbD65);
        var selection = PdfRasterSelection.SelectRectangle(11, 1, 4, 0, 3, 1);
        // The default ring is three pixels out, past one-pixel edge blending: x = 1 and x = 9, both white.
        Assert.Equal((255, 255, 255), PdfRasterExtraction.SampleFillColor(frame, selection));
        // The adjacent ring alone sees only the edge gray: .6 * 255 = 153.
        Assert.Equal((153, 153, 153), PdfRasterExtraction.SampleFillColor(frame, selection, ringDistance: 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractionFollowsTheContentCopyPermissionOfAUserPassword(bool copying)
    {
        byte[] plain = new PdfDocumentBuilder().AddBlankPage(10, 10).Build();
        byte[] encrypted = KillerPdf.Engine.Writing.PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(plain),
            new KillerPdf.Engine.Security.PdfPasswordEncryptionOptions { UserPassword = "read", OwnerPassword = "owner",
                AllowContentCopying = copying, AllowDocumentModification = true });
        Assert.Equal(copying, PdfRasterExtraction.AllowsExtraction(PdfDocument.Open(encrypted, "read")));
        Assert.True(PdfRasterExtraction.AllowsExtraction(PdfDocument.Open(encrypted, "owner")));
        Assert.True(PdfRasterExtraction.AllowsExtraction(PdfDocument.Open(plain)));
    }

    internal static (byte[] Rgb, byte[] Alpha) Samples(PdfImageAsset asset)
    {
        var document = PdfDocument.Open(asset.ToPdfBytes());
        var page = PdfPageTree.Read(document).Pages[0];
        var resources = Assert.IsType<PdfDictionary>(Resolve(document, page.Dictionary[Name("Resources")]));
        var objects = Assert.IsType<PdfDictionary>(Resolve(document, resources[Name("XObject")]));
        var image = Assert.IsType<PdfStream>(Resolve(document, objects[Name("Image")]));
        var mask = Assert.IsType<PdfStream>(Resolve(document, image.Dictionary[Name("SMask")]));
        return (document.DecodeStream(image), document.DecodeStream(mask));
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
