using System.Buffers.Binary;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// The DjVu text layer is UTF-8 (DjVu v3, 8.3.5). Real books made in 1999 carry single bytes from older encodings
// (the djvu2spec reference edition has 0xB8 for a copyright sign, 0xDF for bullets, 0xBD and 0xF1 in tables). Each
// maximal ill-formed subsequence reads as one U+FFFD, the Unicode substitution of maximal subparts (Unicode 15, 3.9).
public sealed class DjvuLegacyEncodedTextTests
{
    // "é EDT \xB8 1999\n\xDF Image": a valid two-byte scalar, a lone 0xB8, and 0xDF followed by a space (a two-byte
    // lead without its continuation byte), with one word zone per word.
    private static readonly byte[] Text =
        [0xc3, 0xa9, 0x20, 0x45, 0x44, 0x54, 0x20, 0xb8, 0x20, 0x31, 0x39, 0x39, 0x39, 0x0a, 0xdf, 0x20, 0x49, 0x6d, 0x61, 0x67, 0x65];
    private const string Expected = "é EDT � 1999\n� Image";

    private static byte[][] Zones() =>
    [
        Zone(0, 0, 1, 1, 0, 2), Zone(1, 0, 1, 1, 3, 3), Zone(2, 0, 1, 1, 7, 1), Zone(0, 1, 1, 1, 9, 4),
        Zone(1, 1, 1, 1, 14, 1), Zone(2, 1, 1, 1, 16, 5)
    ];

    [Fact]
    public void TextThatIsNotUtf8ReadsWithReplacementCharactersAtItsZonesAndNamesThePage()
    {
        var document = DjvuDocument.Open(Page(Text, Zones()));

        var page = document.ReadTextPage(0);

        Assert.Equal(Expected, page.Text);
        Assert.Equal([(0, 1), (2, 3), (6, 1), (8, 4), (13, 1), (15, 5)], page.Zones.Select(z => (z.Start, z.Length)));
        var notice = Assert.Single(page.Diagnostics);
        Assert.Equal("TextEncodingInvalid", notice.Code);
        Assert.Contains("Page 1", notice.Message, StringComparison.Ordinal);
        Assert.Equal(0, notice.PageIndex);
        Assert.Equal(Expected, document.ReadText(0));
    }

    [Fact]
    public void TheViewerSearchesAndCopiesTheSameTextAtTheSameRegions()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page(Text, Zones()));

        var page = snapshot.ReadTextPage(0);

        Assert.Equal(Expected, page.Text);
        Assert.Equal(Expected, snapshot.ReadPageText(0));
        Assert.Equal([(0, 1), (2, 3), (6, 1), (8, 4), (13, 1), (15, 5)], page.Regions.Select(r => (r.Start, r.Length)));
    }

    // The viewer reads page text through the snapshot, so the page's notice comes with it, naming the page.
    [Fact]
    public void TheViewerTextPageCarriesTheNoticeNamingThePage()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page(Text, Zones()));

        var notice = Assert.Single(snapshot.ReadTextPage(0).Diagnostics);

        Assert.Equal("TextEncodingInvalid", notice.Code);
        Assert.Equal(0, notice.PageIndex);
        Assert.Empty(PdfReadSnapshot.OpenDjvu(Page("AB"u8.ToArray(), [Zone(0, 0, 1, 1, 0, 2)])).ReadTextPage(0).Diagnostics);
    }

    // INFO minor version 27 is newer than the November 2005 reference's 26; the Smmr mask adds its rendering notice.
    // Both are document notices, with no page.
    [Fact]
    public void TheSnapshotCarriesTheDocumentNoticesFoundAtOpen()
    {
        byte[] source = Page("AB"u8.ToArray(), [Zone(0, 0, 1, 1, 0, 2)]);
        source[28] = 27;

        var snapshot = PdfReadSnapshot.OpenDjvu(source);

        Assert.Equal(["SmmrRenderingLimited", "PageVersionUnsupported"], snapshot.DjvuDiagnostics.Select(d => d.Code));
        Assert.All(snapshot.DjvuDiagnostics, notice => Assert.Null(notice.PageIndex));
    }

    [Fact]
    public void ConversionKeepsThePageAndItsTextAndReportsThePage()
    {
        var document = DjvuDocument.Open(Page(Text, Zones()));
        var notices = new List<DjvuDiagnostic>();

        var pdf = PdfDocument.Open(document.ConvertToPdf(notices));

        Assert.Equal(Expected, string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        var notice = Assert.Single(notices);
        Assert.Equal("TextEncodingInvalid", notice.Code);
        Assert.Contains("Page 1", notice.Message, StringComparison.Ordinal);
        Assert.Equal(0, notice.PageIndex);
    }

    [Fact]
    public void ValidTextHasNoNotice()
    {
        var document = DjvuDocument.Open(Page("AB"u8.ToArray(), [Zone(0, 0, 1, 1, 0, 2)]));
        var notices = new List<DjvuDiagnostic>();

        document.ConvertToPdf(notices);

        Assert.Empty(document.ReadTextPage(0).Diagnostics);
        Assert.Empty(notices);
    }

    // A 3 x 2 Smmr page at 144 dpi, as DjvuPdfLocatedTextConversionTests builds it, with raw text bytes.
    private static byte[] Page(byte[] text, byte[][] zones)
        => [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 144, 0, 22, 1]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80]),
            .. Chunk("TXTa", [(byte)(text.Length >> 16), (byte)(text.Length >> 8), (byte)text.Length,
                .. text, 1, .. zones.SelectMany(zone => zone)])])];

    private static byte[] Zone(int left, int bottom, int width, int height, int start, int length)
    {
        var result = new byte[17];
        result[0] = 6;
        int[] values = [left, bottom, width, height, start];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1 + index * 2), checked((ushort)(values[index] + 0x8000)));
        result[11] = (byte)(length >> 16); result[12] = (byte)(length >> 8); result[13] = (byte)length;
        return result;
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        int length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16),
            (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
