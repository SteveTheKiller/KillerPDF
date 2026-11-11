using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

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
