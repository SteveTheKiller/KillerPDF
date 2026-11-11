using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// A shape with no pixels stores no bits, but the decoder still holds an object and an array for it, about 64 bytes. The
// 32 MiB symbol storage limit counts that overhead, so it bounds how many tiny shapes a page can define.
public sealed class DjvuJb2SymbolOverheadTests
{
    [Fact]
    public void ManyEmptyLibraryShapesAreRefusedAtTheStorageLimit()
    {
        // 32 MiB at 64 bytes a shape is 524,288 shapes; the record limit alone would allow 4,000,000.
        var writer = new DjvuTestJb2Writer();
        writer.StartOfData(16, 16);
        for (int i = 0; i < 600_000; i++) writer.EmptyLibraryShape();
        var document = DjvuDocument.Open(DjvuJb2Tests.Page(16, 16, 1, DjvuJb2Tests.Chunk("Sjbz", writer.Finish())));

        var error = Assert.Throws<InvalidDataException>(() => document.RenderPage(0));

        Assert.Contains("storage limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFewEmptyLibraryShapesStillDecode()
    {
        var writer = new DjvuTestJb2Writer();
        writer.StartOfData(16, 16);
        for (int i = 0; i < 1000; i++) writer.EmptyLibraryShape();
        var document = DjvuDocument.Open(DjvuJb2Tests.Page(16, 16, 1, DjvuJb2Tests.Chunk("Sjbz", writer.Finish())));

        Assert.Equal(16, document.RenderPage(0).Width);
    }
}
