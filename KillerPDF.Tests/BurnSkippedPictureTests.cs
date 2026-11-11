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
}
