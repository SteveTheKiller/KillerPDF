using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

// Transform burns a page's annotations into the page it rasterizes and then drops the page's records. A picture the
// burn could not draw is in no page, so its record has to stay.
public sealed class TransformBakeTests
{
    private static ImageAnnotation UnreadablePicture() => new()
    {
        PageIndex = 0, Position = new Point(10, 10), Scale = 1, SourceWidth = 50, SourceHeight = 50,
        // A %PDF- asset with two pages: the reader refuses it, so the burn leaves it out.
        ImageData = Convert.ToBase64String(new PdfDocumentBuilder().AddBlankPage(50, 50).AddBlankPage(50, 50).Build())
    };

    [Fact]
    public void TheBurnNamesThePictureRecordItLeftOut()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-bake-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            var ink = new InkAnnotation { PageIndex = 0, Points = [new Point(10, 10), new Point(60, 60)] };
            var picture = UnreadablePicture();
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [ink, picture] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 200) }, null, 0);
            Assert.Equal(1, PdfEngineBurn.SkippedPictures);
            Assert.NotNull(PdfEngineBurn.SkippedPictureRecords);
            Assert.Same(picture, Assert.Single(PdfEngineBurn.SkippedPictureRecords!));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ABakedPageKeepsOnlyTheRecordsTheBurnLeftOut()
    {
        var ink = new InkAnnotation { PageIndex = 0, Points = [new Point(10, 10), new Point(60, 60)] };
        var picture = UnreadablePicture();
        var other = new InkAnnotation { PageIndex = 1, Points = [new Point(10, 10), new Point(60, 60)] };
        var annotations = new Dictionary<int, List<PageAnnotation>>
        {
            [0] = [ink, picture, new InkAnnotation { PageIndex = 0 }],
            [1] = [other]
        };
        var keep = new Dictionary<int, IReadOnlySet<int>> { [0] = new HashSet<int> { 1 } };

        PdfEngineBurn.RemapAfterBake(annotations, [0], keep);

        Assert.Same(picture, Assert.Single(annotations[0]));
        Assert.Same(other, Assert.Single(annotations[1]));
    }

    [Fact]
    public void ABakedPageWithNothingLeftOutLosesAllItsRecords()
    {
        var annotations = new Dictionary<int, List<PageAnnotation>>
        {
            [0] = [new InkAnnotation { PageIndex = 0 }]
        };
        PdfEngineBurn.RemapAfterBake(annotations, [0], new Dictionary<int, IReadOnlySet<int>>());
        Assert.Empty(annotations[0]);
    }

    // SOURCE PIN: the Transform flow lives in the window, which this project does not compile. Window check on SN02:
    // 1. Place a picture copied from a PDF that has two pages (or cannot be read) on a page that also has an ink stroke.
    // 2. Transform that page (any angle). 3. The status ends with the skipped-picture note and the picture record is
    // still on the page after the reload, while the ink is part of the page.
    [Fact]
    public void TransformKeepsTheUnburnedPicturesAndSaysSo()
    {
        string rotate = Source("Rotate.cs");
        Assert.Contains("PdfEngineBurn.RemapAfterBake(annotations, bakedAnnotationPages, unburnedByPage)", rotate);
        Assert.Contains("PicturesSkippedNote.Append(", rotate);
        Assert.Contains("skippedPictures", rotate);
        Assert.DoesNotContain("pageAnns.Clear()", rotate);
    }

    private static string Source(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "Shell", file));
    }
}
