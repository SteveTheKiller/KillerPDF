using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #169: rotating a page must keep its overlay annotations and turn them with the page.
// The mapping runs in render-dim space; a +90 turn takes frame (W, H) to (H, W), so
// round-trip tests swap the frame between passes exactly as the live reload does.
public sealed class AnnotationRotateTests
{
    private const double W = 800, H = 600;

    [Fact]
    public void Highlight_QuarterTurn_TurnsWithTheContent()
    {
        var ha = new HighlightAnnotation { Bounds = new Rect(10, 20, 100, 40) };
        AnnotationRotate.Remap([ha], 90, W, H);

        // Corners (10,20)-(110,60) map to (580,10)-(540,110): the region swaps its axes.
        Assert.Equal(540, ha.Bounds.X, 3);
        Assert.Equal(10, ha.Bounds.Y, 3);
        Assert.Equal(40, ha.Bounds.Width, 3);
        Assert.Equal(100, ha.Bounds.Height, 3);
    }

    [Fact]
    public void Highlight_FourQuarterTurns_IsIdentity()
    {
        var start = new Rect(10, 20, 100, 40);
        var ha = new HighlightAnnotation { Bounds = start };
        AnnotationRotate.Remap([ha], 90, W, H);
        AnnotationRotate.Remap([ha], 90, H, W);
        AnnotationRotate.Remap([ha], 90, W, H);
        AnnotationRotate.Remap([ha], 90, H, W);
        Assert.Equal(start, ha.Bounds);
    }

    [Fact]
    public void Highlight_TurnThenCounterTurn_IsIdentity()
    {
        var start = new Rect(33, 44, 55, 66);
        var ha = new HighlightAnnotation { Bounds = start };
        AnnotationRotate.Remap([ha], 90, W, H);
        AnnotationRotate.Remap([ha], -90, H, W);
        Assert.Equal(start, ha.Bounds);
    }

    [Fact]
    public void Highlight_TwoQuarterTurns_MatchHalfTurn()
    {
        var a = new HighlightAnnotation { Bounds = new Rect(10, 20, 100, 40) };
        var b = new HighlightAnnotation { Bounds = new Rect(10, 20, 100, 40) };
        AnnotationRotate.Remap([a], 90, W, H);
        AnnotationRotate.Remap([a], 90, H, W);
        AnnotationRotate.Remap([b], 180, W, H);
        Assert.Equal(b.Bounds, a.Bounds);
    }

    [Fact]
    public void TextBox_KeepsItsSizeAndTurnsAroundTheSameContent()
    {
        var ta = new TextAnnotation { Position = new Point(10, 70), Width = 100, Height = 40 };
        AnnotationRotate.Remap([ta], 90, W, H);

        // The 100x40 box at (10,70) covers (10..110, 70..110); turned it covers (490..530, 10..110).
        Assert.Equal(490, ta.Position.X, 3);
        Assert.Equal(10, ta.Position.Y, 3);
        Assert.Equal(100, ta.Width, 3);
        Assert.Equal(40, ta.Height, 3);
        Assert.Equal(new Size(40, 100), ta.FootprintSize);
    }

    // A placed picture is page content, often lifted from the page by Extract Image, so it turns with the page:
    // its pixels take the same quarter turn and its rectangle maps like a highlight's.
    [Fact]
    public void PlacedImage_TurnsWithThePage()
    {
        // Left pixel red, right pixel blue; a clockwise quarter turn puts red on top.
        var image = new ImageAnnotation
        {
            Position = new Point(10, 20),
            SourceWidth = 20,
            SourceHeight = 10,
            Scale = 1,
            ImageData = Png(2, 1, [0, 0, 255, 255, 255, 0, 0, 255]),
        };

        string bytes = image.ImageData;
        AnnotationRotate.Remap([image], 90, W, H);

        Assert.Equal(10, image.SourceWidth, 3);
        Assert.Equal(20, image.SourceHeight, 3);
        Assert.Equal(H - 30, image.Position.X, 3);
        Assert.Equal(10, image.Position.Y, 3);
        Assert.Equal(90, image.Rotation);
        Assert.Equal(bytes, image.ImageData);   // the turn is recorded, the picture's bytes are kept
        Assert.Null(image.CachedBitmap);
    }

    // A picture lifted from the page is kept as its own one-page PDF, which no image decoder reads. Rotating its
    // page must still work and turn it.
    [Fact]
    public void PlacedPdfPicture_TurnsWithoutDecodingItsBytes()
    {
        string pdf = Convert.ToBase64String("%PDF-1.7 not an image file"u8.ToArray());
        var image = new ImageAnnotation { Position = new Point(10, 20), SourceWidth = 20, SourceHeight = 10, Scale = 1, ImageData = pdf };

        AnnotationRotate.Remap([image], 180, W, H);

        Assert.Equal(180, image.Rotation);
        Assert.Equal(pdf, image.ImageData);
        Assert.Equal(new Point(W - 30, H - 30), image.Position);
    }

    // Everything on a page turns with it: a free text box keeps its place on the content and reads sideways.
    [Fact]
    public void FreeText_TurnsWithThePage()
    {
        var text = new TextAnnotation { Position = new Point(10, 20), Width = 200, Height = 28 };

        AnnotationRotate.Remap([text], 90, W, H);

        Assert.Equal(90, text.Rotation);
        Assert.Equal(H - 48, text.Position.X, 3);   // the 200x28 box at (10,20) maps to 28x200 at (H-48, 10)
        Assert.Equal(10, text.Position.Y, 3);
    }

    [Fact]
    public void Signature_TurnsItsStrokesWithThePage()
    {
        var signature = new SignatureAnnotation
        {
            Position = new Point(10, 20), SourceWidth = 40, SourceHeight = 10, Scale = 1,
            Strokes = [[new Point(0, 0), new Point(40, 10)]],
            Vectors = [new SignatureVector { Data = "M0,0 L40,0" }],
        };

        AnnotationRotate.Remap([signature], 90, W, H);

        Assert.Equal((10.0, 40.0), (signature.SourceWidth, signature.SourceHeight));
        Assert.Equal(new Point(H - 30, 10), signature.Position);
        // A clockwise quarter turn in the signature's own 40x10 space: (x, y) goes to (10 - y, x).
        Assert.Equal(new[] { new Point(10, 0), new Point(0, 40) }, signature.Strokes[0]);
        var turned = System.Windows.Media.Geometry.Parse(signature.Vectors[0].Data).Bounds;
        Assert.Equal(new Rect(10, 0, 0, 40), turned);
    }

    [Fact]
    public void PlacedImage_FourQuarterTurnsComeBackToTheStart()
    {
        byte[] start = [0, 0, 255, 255, 255, 0, 0, 255];
        var image = new ImageAnnotation
        {
            Position = new Point(10, 20), SourceWidth = 20, SourceHeight = 10, Scale = 1,
            ImageData = Png(2, 1, start),
        };
        double w = W, h = H;
        for (int turn = 0; turn < 4; turn++)
        {
            AnnotationRotate.Remap([image], 90, w, h);
            (w, h) = (h, w);
        }
        Assert.Equal(new Point(10, 20), image.Position);
        Assert.Equal((20.0, 10.0), (image.SourceWidth, image.SourceHeight));
        Assert.Equal(0, image.Rotation);
        Assert.Equal(start, Pixels(image.ImageData).Pixels);
    }

    private static string Png(int width, int height, byte[] bgra)
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static (int Width, int Height, byte[] Pixels) Pixels(string base64)
    {
        using var stream = new System.IO.MemoryStream(Convert.FromBase64String(base64));
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return (bgra.PixelWidth, bgra.PixelHeight, pixels);
    }

    public static TheoryData<double, double, int> QuarterTurnFrames => new()
    {
        { 1191, 842, 90 },
        { 1191, 842, -90 },
        { 842, 1191, 90 },
        { 842, 1191, -90 },
    };

    [Theory]
    [MemberData(nameof(QuarterTurnFrames))]
    public void UprightItemsNearEveryEdge_UsePostRotationBounds(double oldW, double oldH, int delta)
    {
        const double itemW = 152.906;
        const double itemH = 91.25;
        Point[] positions =
        [
            new(0, 0),
            new(oldW - itemW, 0),
            new(0, oldH - itemH),
            new(oldW - itemW, oldH - itemH),
        ];

        foreach (Point position in positions)
        {
            var text = new TextAnnotation
            {
                Position = position,
                Width = itemW,
                Height = itemH,
            };
            AnnotationRotate.Remap([text], delta, oldW, oldH);

            // The box turns with the page, so its footprint swaps and stays inside the turned frame.
            AssertInsideRotatedFrame(text.Position, itemH, itemW, oldH, oldW);
        }
    }

    private static void AssertInsideRotatedFrame(
        Point position, double width, double height, double frameWidth, double frameHeight)
    {
        Assert.InRange(position.X, 0, Math.Max(0, frameWidth - width));
        Assert.InRange(position.Y, 0, Math.Max(0, frameHeight - height));
        Assert.InRange(position.X + width, 0, frameWidth);
        Assert.InRange(position.Y + height, 0, frameHeight);
    }

    [Fact]
    public void InkPoints_MapPerPoint()
    {
        var ia = new InkAnnotation { Points = [new Point(0, 0), new Point(100, 50)] };
        AnnotationRotate.Remap([ia], 90, W, H);
        Assert.Equal(new Point(H, 0), ia.Points[0]);
        Assert.Equal(new Point(H - 50, 100), ia.Points[1]);
    }
}
