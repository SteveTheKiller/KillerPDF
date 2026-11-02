using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PageNativeDpiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-native-dpi-" + Guid.NewGuid().ToString("N"));

    public PageNativeDpiTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void EachPageKeepsItsOwnImagesWholeNumberResolutionAndABlankPageTakesTheFallback()
    {
        PdfImage image = PdfImage.FromBitonal(500, 500, new byte[250000]);
        string path = Path.Combine(_folder, "scans.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .AddPage(340, 340, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 340, 340))
            .AddPage(720, 720, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 720, 720))
            .AddBlankPage()
            .Build());

        Dictionary<int, double> dpis = PdfEngineDocumentSession.Open(path).NativeDpis([0, 1, 2], null, 150);

        Assert.Equal(106, dpis[0]);
        Assert.Equal(50, dpis[1]);
        Assert.Equal(150, dpis[2]);
    }
}
