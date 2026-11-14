using System.Buffers.Binary;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// A chunk the reader does not know (a producer's own metadata) does not change the picture, and the reference viewer
// skips it, so a page that carries one still draws and converts, with a notice naming the chunk. A known picture layer
// the reader cannot draw (JPEG layers) still makes the page unsupported.
public sealed class DjvuUnrecognizedChunkTests
{
    private static readonly byte[] Producer = [1, 2, 3, 4, 5, 6, 7, 8];

    private static byte[] Jb2Page() => DjvuJb2Tests.Page(56, 32, 1,
        DjvuJb2Tests.Chunk("Sjbz", Convert.FromHexString("826C97EA17032BCDF26753F5D158491ECE4D21C50FA445251FF1C6B11C5102AAD66CC9ECF6202997")));

    private static byte[] SmmrPage() => DjvuJb2Tests.Page(3, 1, 1,
        DjvuJb2Tests.Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]));

    private static byte[] GrayIw44Page() => [.. "AT&T"u8, .. DjvuJb2Tests.Chunk("FORM", [.. "DJVU"u8,
        .. DjvuJb2Tests.Chunk("INFO", [0, 4, 0, 4, 24, 0, 100, 0, 10, 1]),
        .. DjvuJb2Tests.Chunk("BG44", Convert.FromHexString("000181020004000400FFFF"))])];

    public static TheoryData<string> Pages => new() { "jb2", "smmr", "gray", "color", "compound" };

    private static byte[] Page(string kind) => kind switch
    {
        "jb2" => Jb2Page(),
        "smmr" => SmmrPage(),
        "gray" => GrayIw44Page(),
        "color" => Convert.FromBase64String(DjvuIw44RealPageTests.OwnedC44Color),
        _ => Convert.FromHexString(DjvuCompoundTests.PaletteOverGrayHex)
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void AnUnrecognizedChunkIsSkippedWithANoticeAndThePageDrawsAsWithoutIt(string kind)
    {
        var plain = Page(kind);
        var extra = WithExtraChunk(plain, "XXXX", Producer);
        var expected = DjvuDocument.Open(plain).RenderPage(0);

        var document = DjvuDocument.Open(extra);
        var drawn = document.RenderPage(0);

        Assert.Equal(expected.Width, drawn.Width);
        Assert.Equal(expected.Pixels.ToArray(), drawn.Pixels.ToArray());
        var notice = Assert.Single(document.Diagnostics, d => d.Code == "UnrecognizedChunkSkipped");
        Assert.Contains("'XXXX'", notice.Message, StringComparison.Ordinal);
        var converted = new List<DjvuDiagnostic>();
        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(document.ConvertToPdf(converted))));
        Assert.DoesNotContain(converted, d => d.Code == "PageNotConverted");
    }

    // CIDa is the producer metadata LizardTech encoders and the Internet Archive put in their DjVu files, so most real
    // books carry it. It holds no picture or text, the reference viewer skips it silently, and so does this reader:
    // a page with one draws as without it and raises no notice.
    [Theory]
    [MemberData(nameof(Pages))]
    public void TheProducerMetadataChunkIsSkippedWithoutANotice(string kind)
    {
        var plain = Page(kind);
        var document = DjvuDocument.Open(WithExtraChunk(plain, "CIDa", Producer));

        Assert.Equal(DjvuDocument.Open(plain).RenderPage(0).Pixels.ToArray(), document.RenderPage(0).Pixels.ToArray());
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == "UnrecognizedChunkSkipped");
    }

    // A chunk inside a thumbnail component belongs to no page, so its notice must not claim a page was drawn without it.
    [Fact]
    public void AnUnrecognizedChunkInAThumbnailComponentDoesNotClaimAPageWasDrawnWithoutIt()
    {
        var bundle = DjvuTestStreams.Bundle(("page.djvu", 1, Jb2Page()[4..]),
            ("thumb.djvu", 2, DjvuTestStreams.Form("THUM", DjvuTestStreams.Chunk("XXXX", Producer))));

        var notice = Assert.Single(DjvuDocument.Open(bundle).Diagnostics, d => d.Code == "UnrecognizedChunkSkipped");

        Assert.Contains("'XXXX'", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("page was drawn", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognizedChunkInAPageStillSaysThePageWasDrawnWithoutIt()
    {
        var notice = Assert.Single(DjvuDocument.Open(WithExtraChunk(Jb2Page(), "XXXX", Producer)).Diagnostics,
            d => d.Code == "UnrecognizedChunkSkipped");

        Assert.Contains("the page was drawn without it", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EachUnrecognizedNameGetsOneNoticeAndTheNoticesAreBounded()
    {
        var page = Jb2Page();
        for (int i = 0; i < 40; i++)
        {
            page = WithExtraChunk(page, "X" + (char)('a' + i % 26) + "z" + (char)('A' + i / 26), Producer);
            page = WithExtraChunk(page, "X" + (char)('a' + i % 26) + "z" + (char)('A' + i / 26), Producer);
        }

        var notices = DjvuDocument.Open(page).Diagnostics.Where(d => d.Code == "UnrecognizedChunkSkipped").ToList();

        Assert.Equal(16, notices.Count);
        Assert.Equal(16, notices.Select(n => n.Message).Distinct().Count());
    }

    [Theory]
    [InlineData("jb2", "BGjp")]
    [InlineData("jb2", "FGjp")]
    [InlineData("gray", "BGjp")]
    [InlineData("gray", "FGjp")]
    [InlineData("smmr", "BGjp")]
    public void AKnownPictureLayerTheReaderCannotDrawStillMakesThePageUnsupported(string kind, string layer)
    {
        var document = DjvuDocument.Open(WithExtraChunk(Page(kind), layer, Producer));

        Assert.Throws<NotSupportedException>(() => document.RenderPage(0));
    }

    // The page's FORM with one more chunk at its end.
    private static byte[] WithExtraChunk(byte[] file, string id, byte[] payload)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(8));
        byte[] body = [.. file.AsSpan(12, length)];
        if (body.Length % 2 == 1) body = [.. body, 0];
        body = [.. body, .. DjvuJb2Tests.Chunk(id, payload)];
        var size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, body.Length);
        return [.. "AT&TFORM"u8, .. size, .. body];
    }
}
