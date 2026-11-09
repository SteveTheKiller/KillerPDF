using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPDF.Tests;

// A save must succeed whatever the editing layer cannot hold: an image too large for it is saved as page
// content or a plain stamp, and a document with very many notes is saved in chunks.
public sealed class PdfOverlayLimitsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "killerpdf-overlay-limits-" + Guid.NewGuid().ToString("N"));

    public PdfOverlayLimitsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string Blank(int pages)
    {
        var builder = new PdfDocumentBuilder();
        for (int index = 0; index < pages; index++) builder.AddBlankPage(400, 400);
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    // Noise does not compress, so the PNG is about the size of its pixels.
    private static string NoisePng(int side)
    {
        byte[] pixels = new byte[side * side * 4];
        new Random(7).NextBytes(pixels);
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        BitmapSource source = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static ImageAnnotation Image(string data, double x) => new()
    {
        PageIndex = 0, Position = new Point(x, 20), SourceWidth = 100, SourceHeight = 100, Scale = 1, ImageData = data
    };

    private static Dictionary<int, (int w, int h)> Dims(int pages) =>
        Enumerable.Range(0, pages).ToDictionary(page => page, _ => (400, 400));

    [Fact]
    public void ImageTooLargeForTheEditingLayerStillSaves()
    {
        string path = Blank(1);
        string data = NoisePng(1300);
        Assert.True(data.Length > 7 * 1024 * 1024);

        PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [Image(data, 20)] }, Dims(1));

        byte[] saved = File.ReadAllBytes(path);
        var comments = PdfCommentReader.Read(PdfDocument.Open(saved), true);
        Assert.Equal("Stamp", Assert.Single(comments).Subtype);
        Assert.Empty(PdfOverlayAnnotations.TakeEditableTail(saved).Records);
    }

    [Fact]
    public void ImagesThatTogetherExceedTheBatchLimitStillSaveWithTheEditsEditable()
    {
        string path = Blank(1);
        string data = NoisePng(1000);
        Assert.True(data.Length * 2 > 8 * 1024 * 1024 && data.Length < 7 * 1024 * 1024);
        var pair = new ReplacementPair(
            new TextAnnotation { PairId = "edit", Content = "Edited", Position = new Point(20, 200), Width = 150, Height = 40 },
            new CoverAnnotation { PairId = "edit", Bounds = new Rect(15, 195, 160, 50) });

        PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            { [0] = [pair.Cover, pair.Text, Image(data, 20), Image(data, 200)] }, Dims(1));

        OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(File.ReadAllBytes(path));
        Assert.Contains(extracted.Records, record => record.Annotation is CoverAnnotation);
        Assert.Contains(extracted.Records, record => record.Annotation is TextAnnotation { Content: "Edited" });
        // The image the batch could not hold is page content, not dropped: the second one's area is not blank.
        Assert.Single(extracted.Records, record => record.Annotation is ImageAnnotation);
        byte[] pixels = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path)))
            .Render(0, new PdfRenderOptions(400, 400)).Pixels.ToArray();
        bool inked = false;
        for (int y = 30; y < 110 && !inked; y++)
            for (int x = 210; x < 290 && !inked; x++)
                inked = pixels[(y * 400 + x) * 4] != 255 || pixels[(y * 400 + x) * 4 + 1] != 255;
        Assert.True(inked);
    }

    [Fact]
    public void MoreThanTenThousandNotesSave()
    {
        const int pages = 5, notes = 10001;
        string path = Blank(pages);
        var byPage = Enumerable.Range(0, pages).ToDictionary(page => page, _ => new List<PageAnnotation>());
        // A page's first plain text box starts its saved tail, and every markup after it joins the tail.
        for (int page = 0; page < pages; page++)
            byPage[page].Add(new TextAnnotation { PageIndex = page, Content = "Note", Position = new Point(5, 5), Width = 50, Height = 20 });
        for (int index = pages; index < notes; index++)
            byPage[index % pages].Add(new HighlightAnnotation
            {
                PageIndex = index % pages, Bounds = new Rect(index % 40 * 9, index / 40 % 40 * 9, 8, 8)
            });

        PdfEngineBurn.Burn(path, byPage, Dims(pages));

        Assert.Equal(notes, PdfOverlayAnnotations.TakeEditableTail(File.ReadAllBytes(path)).Records.Count);
    }

    [Theory]
    [InlineData(PdfSignatureCertificationPermission.FormFillingAndSignatures)]
    [InlineData(PdfSignatureCertificationPermission.FormFillingSignaturesAndAnnotations)]
    public void CertifiedDocumentKeepsItsOverlaysAsNativeAnnotations(PdfSignatureCertificationPermission permission)
    {
        string path = Blank(2);
        PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            { [0] = [new TextAnnotation { Content = "Note", Position = new Point(20, 20), Width = 100, Height = 30 }] }, Dims(2));
        byte[] certified = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(File.ReadAllBytes(path)), _ => new byte[64],
            new PdfSignatureOptions
            {
                FieldName = "Signature1", PageIndex = 1,
                CertificationPermission = permission
            });
        Assert.Single(PdfOverlayAnnotations.Read(PdfDocument.Open(certified)));

        OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(certified);

        Assert.Empty(extracted.Records);
        Assert.Equal(certified, extracted.Bytes);
    }
}
