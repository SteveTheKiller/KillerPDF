using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuNavigationNameTests
{
    // BZZ streams for the decoded payloads described beside each constant. They come from a port of the DjVu.js BZZ
    // encoder that reproduces the DjVu.js bytes of two existing directory fixtures bit for bit.
    // Two 52-byte pages: "p1" (flags 0x01), then "p2" with a name "chapter-two" and a title "iv" (flags 0xC1).
    private const string Directory = "FFFFE1BF891FE990E8D42E4D632BF383A33C4B247CEAAB299F77DE6737059FFB";
    // One bookmark "Chapter two" -> "#iv".
    private const string TitleBookmark = "FFFFE7FF12DC176E824E98E53D4BBC3CD7E818E44E8B0CE5BB7F";
    // "Chapter two" -> "#iv", then "Missing" -> "#nowhere".
    private const string TitleThenMissing = "FFFFD1FF35C62B51A02FFB1559C860A552B6B8594CD1E0612BDA5194B2FDF6C1E86E0E73C986483E041F";
    // "Lost" -> "#nowhere" with the child "Kept child" -> "#chapter-two", then "Last" -> "#1".
    private const string MissingParentWithChild = "FFFFBFFE75D892D783DB0E18E8285F686120C354F3D514EB1932CCF0832D1EF19FD588058F8F5D359DE58FB6AFC4C7CCAD6956DBE985";

    [Theory]
    [InlineData("#p2", 1)]
    [InlineData("#chapter-two", 1)]
    [InlineData("#iv", 1)]
    [InlineData("#p1", 0)]
    [InlineData("#2", 1)]
    public void ATargetNamesAPageByIdNameTitleOrNumber(string target, int expected)
    {
        var native = DjvuDocument.Open(Bundle(TitleBookmark));
        Assert.Equal(expected, native.ResolveNavigationTarget(target));
    }

    [Fact]
    public void ABookmarkThatNamesAPageByTitleConvertsToAPdfBookmarkOnThatPage()
    {
        var pdf = PdfDocument.Open(DjvuDocument.Open(Bundle(TitleBookmark)).ConvertToPdf());
        var catalog = Dictionary(pdf, pdf.Trailer[Name("Root")]);
        var outlines = Dictionary(pdf, catalog[Name("Outlines")]);
        var bookmark = Dictionary(pdf, outlines[Name("First")]);
        var kids = Assert.IsType<PdfArray>(Dictionary(pdf, catalog[Name("Pages")])[Name("Kids")]);
        Assert.Equal("Chapter two", Title(bookmark));
        Assert.Equal(Assert.IsType<PdfIndirectReference>(kids[1]).ObjectNumber,
            Assert.IsType<PdfIndirectReference>(Assert.IsType<PdfArray>(bookmark[Name("Dest")])[0]).ObjectNumber);
    }

    [Fact]
    public void ABookmarkWhoseTargetIsNotAPageIsLeftOutInsteadOfFailingTheConversion()
    {
        var pdf = PdfDocument.Open(DjvuDocument.Open(Bundle(TitleThenMissing)).ConvertToPdf());
        var outlines = Dictionary(pdf, Dictionary(pdf, pdf.Trailer[Name("Root")])[Name("Outlines")]);
        Assert.Equal(1, Assert.IsType<PdfInteger>(outlines[Name("Count")]).Value);
        Assert.Equal("Chapter two", Title(Dictionary(pdf, outlines[Name("First")])));
    }

    [Fact]
    public void TheChildrenOfAnUnresolvableBookmarkTakeItsPlace()
    {
        var pdf = PdfDocument.Open(DjvuDocument.Open(Bundle(MissingParentWithChild)).ConvertToPdf());
        var outlines = Dictionary(pdf, Dictionary(pdf, pdf.Trailer[Name("Root")])[Name("Outlines")]);
        var first = Dictionary(pdf, outlines[Name("First")]);
        var second = Dictionary(pdf, first[Name("Next")]);
        Assert.Equal(2, Assert.IsType<PdfInteger>(outlines[Name("Count")]).Value);
        Assert.Equal(new[] { "Kept child", "Last" }, new[] { first, second }.Select(Title));
    }

    private static string Title(PdfDictionary dictionary) =>
        Encoding.BigEndianUnicode.GetString(Assert.IsType<PdfString>(dictionary[Name("Title")]).Bytes.Span[2..]);

    private static PdfDictionary Dictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] Bundle(string navigation)
    {
        byte[] navm = Chunk("NAVM", Convert.FromHexString(navigation));
        byte[] page = Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 144, 0, 22, 1]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])]);
        byte[] metadata = Convert.FromHexString(Directory);
        byte[] directory = new byte[3 + 2 * 4 + metadata.Length];
        metadata.CopyTo(directory, 3 + 2 * 4);
        directory[0] = 0x81;
        BinaryPrimitives.WriteUInt16BigEndian(directory.AsSpan(1), 2);
        int offset = 16 + Chunk("DIRM", directory).Length + navm.Length;
        for (int index = 0; index < 2; index++)
            BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(3 + index * 4), (uint)(offset + index * page.Length));
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVM"u8, .. Chunk("DIRM", directory), .. navm, .. page, .. page])];
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] result = new byte[8 + payload.Length + (payload.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(result, 8);
        return result;
    }
}
