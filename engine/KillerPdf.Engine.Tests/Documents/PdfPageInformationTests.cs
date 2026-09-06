using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageInformationTests
{
    [Fact]
    public void Read_ReturnsEffectiveCropGeometryAndNormalizedRotation()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(200, 300, ReadOnlyMemory<byte>.Empty)
            .AddPage(400, 500, ReadOnlyMemory<byte>.Empty)
            .Build();
        byte[] edited = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetCropBox(0, 10, 20, 100, 150)
            .SetRotation(0, 270)
            .Build();

        IReadOnlyList<PdfPageInformation> pages =
            PdfPageInformation.Read(PdfDocument.Open(edited));

        Assert.Equal(2, pages.Count);
        Assert.Equal(10, pages[0].Left);
        Assert.Equal(20, pages[0].Bottom);
        Assert.Equal(100, pages[0].Width);
        Assert.Equal(150, pages[0].Height);
        Assert.Equal(270, pages[0].Rotation);
        Assert.Equal(400, pages[1].Width);
        Assert.Equal(500, pages[1].Height);
        Assert.Equal(0, pages[1].Rotation);
    }
}
