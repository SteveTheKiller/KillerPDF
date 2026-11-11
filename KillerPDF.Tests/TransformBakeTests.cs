using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

// Transform and Straighten burn a page's annotations into the page they rasterize and then drop the page's records.
// A record cannot follow a page that was rotated, flipped or warped, so a baked page loses every record: a picture the
// burn could draw is in the page, and one no decoder could read is left out and reported.
public sealed class TransformBakeTests
{
    [Fact]
    public void ABakedPageLosesEveryRecordIncludingAnUnreadablePicture()
    {
        var ink = new InkAnnotation { PageIndex = 0, Points = [new Point(10, 10), new Point(60, 60)] };
        // Random bytes: neither GDI+ nor WIC can read them, so the burn leaves the picture out.
        var picture = new ImageAnnotation
        {
            PageIndex = 0, Position = new Point(10, 10), Scale = 1, SourceWidth = 50, SourceHeight = 50,
            ImageData = Convert.ToBase64String([7, 99, 1, 250, 13, 77, 3, 41, 200, 9, 8, 120])
        };
        var other = new InkAnnotation { PageIndex = 1, Points = [new Point(10, 10), new Point(60, 60)] };
        var annotations = new Dictionary<int, List<PageAnnotation>>
        {
            [0] = [ink, picture, new InkAnnotation { PageIndex = 0 }],
            [1] = [other]
        };

        PdfEngineBurn.RemapAfterBake(annotations, [0]);

        Assert.Empty(annotations[0]);
        Assert.Same(other, Assert.Single(annotations[1]));
    }

    [Fact]
    public void RemapAfterBakeHasNoKeepPath()
    {
        string burn = File.ReadAllText(Path.Combine(ServicesFolder(), "PdfEngineBurn.cs"));
        string remap = burn[burn.IndexOf("internal static void RemapAfterBake", StringComparison.Ordinal)..];
        remap = remap[..remap.IndexOf("internal static void Burn(", StringComparison.Ordinal)];
        Assert.DoesNotContain("keep", remap, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unburned", burn, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SkippedPictureRecords", burn);
    }

    // SOURCE PIN: the Transform flow lives in the window, which this project does not compile. Window check on SN02:
    // 1. Place a picture that cannot be read on a page that also has an ink stroke. 2. Transform that page (any angle).
    // 3. The status ends with the skipped-picture note and the page has no picture record after the reload.
    [Fact]
    public void TransformAndStraightenBakeEveryRecordAndStillSayWhatWasLeftOut()
    {
        foreach (string file in new[] { "Rotate.cs", "PageArrangement.cs" })
        {
            string text = Source(file);
            Assert.Contains("PdfEngineBurn.RemapAfterBake(annotations, bakedAnnotationPages)", text);
            Assert.Contains("PicturesSkippedNote.Append(", text);
            Assert.Contains("Str_Save_PicturesSkipped", text);
            Assert.DoesNotContain("unburnedByPage", text);
            Assert.DoesNotContain("Unburned", text);
        }
        Assert.DoesNotContain("pageAnns.Clear()", Source("Rotate.cs"));
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    private static string ServicesFolder() => Path.Combine(Root(), "Services");

    private static string Source(string file) => File.ReadAllText(Path.Combine(Root(), "Shell", file));
}
