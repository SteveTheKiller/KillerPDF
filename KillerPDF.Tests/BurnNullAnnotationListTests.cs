using System.IO;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

// "/Annots null" is a legal way to say a page has no annotations. A save must add new native annotations to such a
// page, not draw them into it, and the saved file must open with them there.
public sealed class BurnNullAnnotationListTests
{
    [Fact]
    public void ATextBoxSavedOnAPageWithANullAnnotationListReopensAsAnAnnotation()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-null-annots-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, BurnFlattenedMarkupTests.Build(
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots null >>"
            ]));
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation
                {
                    PageIndex = 0, Position = new Point(10, 10), Width = 120, Height = 30,
                    Content = "Kept editable", FontName = "Segoe UI", FontSize = 12
                }]
            }, new Dictionary<int, (int w, int h)> { [0] = (200, 200) });

            Assert.False(PdfEngineBurn.FlattenedMarkup);
            PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(path));
            Assert.Equal([1], PdfCommentReader.ReadPageAnnotationCounts(reopened));
            PdfCommentInfo comment = Assert.Single(PdfCommentReader.Read(reopened, true, new List<int>()));
            Assert.Equal(0, comment.PageIndex);
            Assert.Equal("Kept editable", comment.Contents);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
