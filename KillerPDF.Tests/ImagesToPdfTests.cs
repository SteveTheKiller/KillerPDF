using System.IO;
using System.Security.Principal;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #479 and #480: images opened with KillerPDF, or saved as a PDF from Explorer's menu, become one PDF.
public sealed class ImagesToPdfTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-ImagesToPdf-" + Guid.NewGuid().ToString("N"));

    public ImagesToPdfTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void TheExplorerVerbCarriesItsFiles()
    {
        Assert.Equal(new[] { @"C:\Pics\a.jpg", @"C:\Pics\b.png" },
            ImagesToPdfPlan.VerbFiles(["--images-to-pdf", @"C:\Pics\a.jpg", @"C:\Pics\b.png"]));
        Assert.Equal(new[] { @"C:\Pics\a.jpg" }, ImagesToPdfPlan.VerbFiles(["--IMAGES-TO-PDF", @"C:\Pics\a.jpg"]));
        Assert.Empty(ImagesToPdfPlan.VerbFiles(["--images-to-pdf"])!);
        Assert.Null(ImagesToPdfPlan.VerbFiles([@"C:\Pics\a.jpg"]));
        Assert.Null(ImagesToPdfPlan.VerbFiles([]));
        Assert.Null(ImagesToPdfPlan.VerbFiles(["--merge", "out.pdf", @"C:\Pics\a.jpg"]));
    }

    [Fact]
    public void OpenWithPassesPlainImagePaths()
    {
        Assert.Equal(new[] { @"C:\Pics\a.JPG", @"C:\Pics\b.tif" },
            ImagesToPdfPlan.OpenWithImages([@"C:\Pics\a.JPG", @"C:\Pics\b.tif"]));
        foreach (string extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" })
            Assert.NotNull(ImagesToPdfPlan.OpenWithImages([@"C:\Pics\a" + extension]));
        // A PDF, a mixed launch or a command stays with the normal open.
        Assert.Null(ImagesToPdfPlan.OpenWithImages([@"C:\Docs\a.pdf"]));
        Assert.Null(ImagesToPdfPlan.OpenWithImages([@"C:\Pics\a.jpg", @"C:\Docs\a.pdf"]));
        Assert.Null(ImagesToPdfPlan.OpenWithImages(["--images-to-pdf", @"C:\Pics\a.jpg"]));
        Assert.Null(ImagesToPdfPlan.OpenWithImages(["/register-user"]));
        Assert.Null(ImagesToPdfPlan.OpenWithImages([]));
    }

    // Separate launches arrive in no fixed order, so pages follow the names the way Explorer sorts them.
    [Fact]
    public void PagesFollowFileNamesWithNumbersInOrder()
    {
        Assert.Equal(new[] { @"C:\P\scan 1.jpg", @"C:\P\scan 2.jpg", @"C:\P\Scan 10.jpg", @"C:\P\zebra.png" },
            ImagesToPdfPlan.Order([@"C:\P\zebra.png", @"C:\P\Scan 10.jpg", @"C:\P\scan 2.jpg", @"C:\P\scan 1.jpg"]));
        Assert.Equal(new[] { @"C:\P\img002.png", @"C:\P\img10.png" },
            ImagesToPdfPlan.Order([@"C:\P\img10.png", @"C:\P\img002.png"]));
    }

    [Fact]
    public void TheSameImageTwiceIsOnePage()
    {
        Assert.Equal(new[] { @"C:\P\a.jpg" }, ImagesToPdfPlan.Order([@"C:\P\a.jpg", @"c:\p\A.JPG"]));
    }

    [Fact]
    public void ThePdfIsNamedAfterTheFirstImageInItsFolder()
    {
        string path = ImagesToPdfPlan.OutputPath([@"C:\Pics\holiday.jpg", @"D:\Other\beach.png"], _ => false);
        Assert.Equal(@"C:\Pics\holiday.pdf", path);
        Assert.Equal("holiday.pdf", ImagesToPdfPlan.SuggestedName([@"C:\Pics\holiday.jpg"]));
    }

    [Fact]
    public void AnExistingPdfIsNeverReplaced()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Pics\holiday.pdf" };
        Assert.Equal(@"C:\Pics\holiday (2).pdf", ImagesToPdfPlan.OutputPath([@"C:\Pics\holiday.jpg"], taken.Contains));
        taken.Add(@"C:\Pics\holiday (2).pdf");
        Assert.Equal(@"C:\Pics\holiday (3).pdf", ImagesToPdfPlan.OutputPath([@"C:\Pics\holiday.jpg"], taken.Contains));
    }

    [Fact]
    public void ConvertWritesOnePdfBesideTheImagesAndKeepsAnExistingOne()
    {
        string b = WriteImage("b 10.png"), a = WriteImage("b 2.png");
        File.WriteAllText(Path.Combine(_folder, "b 2.pdf"), "keep");

        string output = ImagesToPdfCommand.Convert([b, a]);

        Assert.Equal(Path.Combine(_folder, "b 2 (2).pdf"), output);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_folder, "b 2.pdf")));
        byte[] pdf = File.ReadAllBytes(output);
        Assert.Equal(2, KillerPdf.Engine.Documents.PdfDocumentInformation
            .Read(KillerPdf.Engine.Documents.PdfDocument.Open(pdf)).PageCount);
    }

    [Fact]
    public void ConvertRefusesAMissingOrNonImageFile()
    {
        string text = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(text, "x");
        Assert.ThrowsAny<IOException>(() => ImagesToPdfCommand.Convert([Path.Combine(_folder, "gone.jpg")]));
        Assert.ThrowsAny<Exception>(() => ImagesToPdfCommand.Convert([text]));
        Assert.Empty(Directory.GetFiles(_folder, "*.pdf"));
    }

    [Fact]
    public void FailureNamesTheFirstImage()
    {
        Assert.Equal("KillerPDF could not make a PDF from a.jpg.\n\nBroken.",
            ImagesToPdfCommand.FailureMessage([@"C:\P\a.jpg", @"C:\P\b.jpg"], "Broken.", null));
        Assert.Equal("X a.jpg", ImagesToPdfCommand.FailureMessage([@"C:\P\a.jpg"], " ", "X {0}"));
    }

    // Explorer starts one process per selected file; the first gathers the rest so they make one PDF.
    [Fact]
    public void LaterLaunchesHandTheirImagesToTheFirst()
    {
        var channel = new SingleInstanceChannel("KillerPDF.Test." + Guid.NewGuid().ToString("N"),
            WindowsIdentity.GetCurrent().User!.Value, 0);
        var first = new ImageLaunchCollector(channel, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20));
        IReadOnlyList<string>? gathered = null;
        using var owning = new ManualResetEventSlim();
        var collector = new Thread(() => { gathered = first.Collect([@"C:\P\a.jpg"], owning.Set); }) { IsBackground = true };
        collector.Start();
        Assert.True(owning.Wait(TimeSpan.FromSeconds(10)));

        var later = new ImageLaunchCollector(channel, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20));
        Assert.Null(later.Collect([@"C:\P\b.jpg"]));
        Assert.Null(later.Collect([@"C:\P\c.jpg", @"C:\P\d.jpg"]));

        Assert.True(collector.Join(TimeSpan.FromSeconds(20)));
        Assert.Equal(new[] { @"C:\P\a.jpg", @"C:\P\b.jpg", @"C:\P\c.jpg", @"C:\P\d.jpg" }, gathered);
    }

    [Fact]
    public void ALoneLaunchKeepsItsOwnImages()
    {
        var channel = new SingleInstanceChannel("KillerPDF.Test." + Guid.NewGuid().ToString("N"),
            WindowsIdentity.GetCurrent().User!.Value, 0);
        var alone = new ImageLaunchCollector(channel, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { @"C:\P\a.jpg" }, alone.Collect([@"C:\P\a.jpg"]));
    }

    private string WriteImage(string name)
    {
        string path = Path.Combine(_folder, name);
        using var output = File.Create(path);
        BitmapHelpers.WritePng(output, new byte[] { 30, 60, 90, 255, 120, 150, 180, 255 }, 2, 1);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }
}
