using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PrimaryRenderSessionTests
{
    [Fact]
    public void ZoomReusesSnapshotAndReturnsIndependentPixels()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        var retained = new PdfPrimaryRenderSession();
        try
        {
            File.WriteAllBytes(path, Document(100, 200));
            var first = retained.Render(path, 0, 0, 128);
            byte[] original = (byte[])first.Pixels.Clone();
            using var fresh = PdfPageRenderSession.OpenEngineFirst(path, 256, 256);
            var expected = fresh.RenderPage(0, includeFormFields: false);
            File.Delete(path);
            var zoomed = retained.Render(path, 0, 0, 256);
            Assert.Equal(expected.Width, zoomed.Width);
            Assert.Equal(expected.Height, zoomed.Height);
            Assert.Equal(expected.Pixels, zoomed.Pixels);
            Assert.Equal(original, first.Pixels);
            Assert.NotSame(first.Pixels, zoomed.Pixels);
        }
        finally { retained.Clear(); File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevisionOrExplicitClearReloadsRewrittenFile(bool clear)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        var retained = new PdfPrimaryRenderSession();
        try
        {
            File.WriteAllBytes(path, Document(100, 200));
            Assert.Equal(64, retained.Render(path, 0, 0, 128).Width);
            File.WriteAllBytes(path, Document(200, 100));
            if (clear) retained.Clear();
            var changed = retained.Render(path, clear ? 0 : 1, 0, 128);
            Assert.Equal(128, changed.Width);
            Assert.Equal(64, changed.Height);
        }
        finally { retained.Clear(); File.Delete(path); }
    }

    [Fact]
    public void DifferentPathReplacesSnapshotAtSameRevision()
    {
        string first = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        string second = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        var retained = new PdfPrimaryRenderSession();
        try
        {
            File.WriteAllBytes(first, Document(100, 200));
            File.WriteAllBytes(second, Document(200, 100));
            Assert.Equal(64, retained.Render(first, 0, 0, 128).Width);
            Assert.Equal(128, retained.Render(second, 0, 0, 128).Width);
            Assert.Equal(64, retained.Render(first, 0, 0, 128).Width);
        }
        finally { retained.Clear(); File.Delete(first); File.Delete(second); }
    }

    private static byte[] Document(double width, double height) => new PdfDocumentBuilder()
        .AddPage(width, height, new PdfContentStreamBuilder().SetFillRgb(0.8, 0.1, 0.3)
            .Rectangle(10, 10, width / 2, height / 2).Fill()).Build();
}
