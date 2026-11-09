using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
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
