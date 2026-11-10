using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// A book's pages share their letter shapes through Djbz dictionaries; an open document decodes each one once.
public sealed class DjvuJb2DictionaryCacheTests
{
    [Fact]
    public void AnOpenDocumentDecodesASharedDictionaryOnceForEveryRenderThatUsesIt()
    {
        var document = DjvuDocument.Open(Convert.FromHexString(DjvuJb2Tests.DictionaryBundle));
        var fresh = DjvuDocument.Open(Convert.FromHexString(DjvuJb2Tests.DictionaryBundle)).RenderPage(0).Pixels.ToArray();

        var first = document.RenderPage(0).Pixels.ToArray();
        var second = document.RenderPage(0).Pixels.ToArray();
        document.ConvertToPdf();

        Assert.Equal(fresh, first);
        Assert.Equal(fresh, second);
        Assert.Equal(1, document.DictionaryCacheState.Decodes);
    }

    [Fact]
    public void AReadSnapshotKeepsItsDictionariesAcrossThumbnailsAndZoomedRegions()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Convert.FromHexString(DjvuJb2Tests.DictionaryBundle));
        var fresh = PdfReadSnapshot.OpenDjvu(Convert.FromHexString(DjvuJb2Tests.DictionaryBundle));
        var whole = fresh.RenderPage(0, new PdfRenderOptions(128, 80)).Pixels.ToArray();
        var crop = fresh.RenderPageRegion(0, new PdfRenderOptions(128, 80), 16, 8, 64, 40).Pixels.ToArray();

        snapshot.RenderPage(0, new PdfRenderOptions(32, 20));
        var resized = snapshot.RenderPage(0, new PdfRenderOptions(128, 80)).Pixels.ToArray();
        var region = snapshot.RenderPageRegion(0, new PdfRenderOptions(128, 80), 16, 8, 64, 40).Pixels.ToArray();

        Assert.Equal(whole, resized);
        Assert.Equal(crop, region);
        Assert.Equal(1, snapshot.DjvuDictionaryCacheState.Decodes);
    }

    [Fact]
    public void TheCacheCountsTheShapesItKeeps()
    {
        // The dictionary defines G1 (padded to 8 x 11, kept as 7 x 9 because a library shape loses its empty edge
        // rows and columns, 11.5.10), G2 refined from it (7 x 9) and G3 (7 x 9): three shapes of one byte by nine
        // rows. Each shape also costs 64 bytes of object and array headers.
        var document = DjvuDocument.Open(Convert.FromHexString(DjvuJb2Tests.DictionaryBundle));

        document.RenderPage(0);

        Assert.Equal(3 * (9 + 64), document.DictionaryCacheState.Bytes);
    }

    [Fact]
    public void ADictionaryAndTheDictionaryItRequiresAreEachDecodedOnce()
    {
        var document = DjvuDocument.Open(Convert.FromHexString(DjvuJb2Tests.NestedDictionaryBundle));

        document.RenderPage(0);
        document.RenderPage(0);

        Assert.Equal(2, document.DictionaryCacheState.Decodes);
    }

    [Fact]
    public void ADictionaryLargerThanTheCacheLimitIsDecodedEachTimeAndNotKept()
    {
        var document = DjvuDocument.Open(Convert.FromHexString(DjvuJb2Tests.DictionaryBundle));
        document.DictionaryCacheLimit = 3 * (9 + 64) - 1;

        var first = document.RenderPage(0).Pixels.ToArray();
        var second = document.RenderPage(0).Pixels.ToArray();

        Assert.Equal(first, second);
        Assert.Equal((2, 0L), document.DictionaryCacheState);
    }
}
