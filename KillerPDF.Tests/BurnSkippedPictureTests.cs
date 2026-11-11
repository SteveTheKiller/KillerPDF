using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

public sealed class BurnSkippedPictureTests
{
    // A picture whose data starts like a PDF but cannot be read back is left out of the flattened page. The save
    // reports how many it left out instead of succeeding silently.
    [Fact]
    public void AFlattenedSaveReportsAPdfPictureItCouldNotRead()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-skipped-picture-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            var picture = new ImageAnnotation
            {
                PageIndex = 0, Position = new Point(10, 10), Scale = 1, SourceWidth = 50, SourceHeight = 50,
                ImageData = Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.7 this is not a picture"))
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [picture] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 200) }, flattenEditing: true);
            Assert.Equal(1, PdfEngineBurn.SkippedPictures);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ASaveWithNoUnreadablePictureReportsNone()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-skipped-picture-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new InkAnnotation { PageIndex = 0, Points = [new Point(10, 10), new Point(60, 60)] }]
            }, new Dictionary<int, (int w, int h)> { [0] = (200, 200) }, flattenEditing: true);
            Assert.Equal(0, PdfEngineBurn.SkippedPictures);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // The reader refuses a picture PDF that has more than one page, or that is over its size limit, with
    // InvalidDataException. Either one leaves the picture out and the save still succeeds, as it did in 1.9.2.
    [Fact]
    public void AMultiPagePdfPictureIsSkippedWithoutFailingTheSave()
    {
        byte[] twoPages = new PdfDocumentBuilder().AddBlankPage(50, 50).AddBlankPage(50, 50).Build();
        Assert.Equal(1, BurnPictureAndCountSkipped(twoPages));
    }

    [Fact]
    public void AnOversizedPdfPictureIsSkippedWithoutFailingTheSave()
    {
        byte[] huge = new byte[6 * 1024 * 1024];
        Encoding.ASCII.GetBytes("%PDF-1.7 ").CopyTo(huge, 0);
        Assert.Equal(1, BurnPictureAndCountSkipped(huge));
    }

    // SOURCE PIN: the only seam for a branch that can never change an output. RenderPdf starts by running
    // ReadPdfAsset on the same bytes, so a picture the asset reader refused is refused again; the retry only
    // parsed the file a second time. A refused PDF picture goes straight to the skipped count.
    [Fact]
    public void ARefusedPdfPictureIsNotReadAgainBeforeItIsCounted()
    {
        string burn = Source("Services", "PdfEngineBurn.cs");
        string draw = Between(burn, "private static void DrawImage(PdfContentStreamBuilder content, string data,", "private static void DrawNumber(");
        Assert.DoesNotContain("RenderPdf", draw);
        Assert.Equal(1, Count(draw, "ReadPdfAsset(bytes)"));
    }

    // GDI+ cannot read JPEG XR, but the overlay decodes it through WIC, so the burn falls back to WIC and draws it.
    private static byte[] JpegXrPicture()
    {
        byte[] pixels = new byte[4 * 4 * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        { pixels[offset] = 200; pixels[offset + 1] = 40; pixels[offset + 2] = 40; pixels[offset + 3] = 255; }
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(4, 4, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new System.Windows.Media.Imaging.WmpBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static (int Skipped, byte[] Output) BurnPictureAndRead(byte[] pictureBytes, bool allowPdfAsset = false)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-skipped-picture-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            var picture = new ImageAnnotation
            {
                PageIndex = 0, Position = new Point(10, 10), Scale = 1, SourceWidth = 50, SourceHeight = 50,
                ImageData = Convert.ToBase64String(pictureBytes)
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [picture] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 200) }, flattenEditing: true);
            return (PdfEngineBurn.SkippedPictures, File.ReadAllBytes(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void AJpegXrPictureTheOverlayCanDrawIsBurnedNotSkipped()
    {
        var (skipped, output) = BurnPictureAndRead(JpegXrPicture());
        Assert.Equal(0, skipped);
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(output).Replace("/Subtype/Image", "/Subtype /Image"));
    }

    // The image watermark is decoded from a file with the same chain as a placed picture, so a JPEG XR watermark the
    // stamp preview shows is burned too instead of silently missing from the output.
    [Fact]
    public void AJpegXrImageWatermarkIsBurnedNotDropped()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"kp-jxr-watermark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string image = Path.Combine(folder, "mark.jxr"), pdf = Path.Combine(folder, "page.pdf");
            File.WriteAllBytes(image, JpegXrPicture());
            File.WriteAllBytes(pdf, new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            PdfEngineBurn.Burn(pdf, new Dictionary<int, List<PageAnnotation>>(), new Dictionary<int, (int w, int h)>(),
                new StampSpec { WmEnabled = true, WmIsImage = true, WmImagePath = image, WmRange = "", WmScale = 1, WmOpacity = 1 });
            Assert.Contains("/Subtype /Image",
                Encoding.Latin1.GetString(File.ReadAllBytes(pdf)).Replace("/Subtype/Image", "/Subtype /Image"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void AJpegXrPictureIsBurnedOnAWorkerThreadToo()
    {
        var (skipped, output) = System.Threading.Tasks.Task.Run(() => BurnPictureAndRead(JpegXrPicture())).GetAwaiter().GetResult();
        Assert.Equal(0, skipped);
        Assert.Contains("/Image", Encoding.Latin1.GetString(output));
    }

    // SkippedPictures is thread-static: a print burn reads it on its worker, and the calling thread sees none of it.
    [Fact]
    public void TheSkippedCountBelongsToTheThreadThatBurned()
    {
        byte[] unreadable = Encoding.ASCII.GetBytes("not a picture at all, random bytes 12345");
        int onCaller = PdfEngineBurn.SkippedPictures;
        int onWorker = System.Threading.Tasks.Task.Run(() => BurnPictureAndRead(unreadable).Skipped).GetAwaiter().GetResult();
        Assert.Equal(1, onWorker);
        Assert.Equal(onCaller, PdfEngineBurn.SkippedPictures);
    }

    private static int BurnPictureAndCountSkipped(byte[] pictureBytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-skipped-picture-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            var picture = new ImageAnnotation
            {
                PageIndex = 0, Position = new Point(10, 10), Scale = 1, SourceWidth = 50, SourceHeight = 50,
                ImageData = Convert.ToBase64String(pictureBytes)
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [picture] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 200) }, flattenEditing: true);
            return PdfEngineBurn.SkippedPictures;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
