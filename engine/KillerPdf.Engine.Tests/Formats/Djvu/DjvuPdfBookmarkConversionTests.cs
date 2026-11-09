using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuPdfBookmarkConversionTests
{
    private const string NestedNavigation = "FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B";
    // Independently encoded with DjVuLibre bzz. The two 52-byte pages have IDs 2 and r\u00e9sum\u00e9+.
    private const string DirectoryMetadata = "FFFFEABF507BF481B02A66B21D64C05E891F0D1D1246CD";

    [Fact]
    public void ConversionPreservesNestedUnicodeLabelsAndLocalDestinations()
    {
        byte[] source = Bundle(Convert.FromHexString(NestedNavigation), 2);
        byte[] original = source.ToArray();
        var native = DjvuDocument.Open(source);
        float[] pixels = native.RenderPage(0).Pixels.ToArray();

        var pdf = PdfDocument.Open(native.ConvertToPdf());

        var catalog = Dictionary(pdf, pdf.Trailer[Name("Root")]);
        var outlines = Dictionary(pdf, catalog[Name("Outlines")]);
        var root = Dictionary(pdf, outlines[Name("First")]);
        var child = Dictionary(pdf, root[Name("First")]);
        var pages = Dictionary(pdf, catalog[Name("Pages")]);
        var kids = Assert.IsType<PdfArray>(pages[Name("Kids")]);
        Assert.Equal(2, kids.Count);
        Assert.Equal(2, Assert.IsType<PdfInteger>(outlines[Name("Count")]).Value);
        Assert.Equal("R\u00e9sum\u00e9", Title(root));
        Assert.Equal("\u05e4\u05e8\u05e7", Title(child));
        Assert.Equal(Assert.IsType<PdfIndirectReference>(outlines[Name("First")]).ObjectNumber,
            Assert.IsType<PdfIndirectReference>(child[Name("Parent")]).ObjectNumber);
        foreach (var bookmark in new[] { root, child })
            Assert.Equal(Assert.IsType<PdfIndirectReference>(kids[0]).ObjectNumber,
                Assert.IsType<PdfIndirectReference>(Assert.IsType<PdfArray>(bookmark[Name("Dest")])[0]).ObjectNumber);
        Assert.Equal(original, source);
        Assert.Equal(pixels, native.RenderPage(0).Pixels.ToArray());
        Assert.Equal("R\u00e9sum\u00e9", Assert.Single(native.ReadNavigation()).Label);
    }

    [Fact]
    public void ConversionPreservesOrderedSiblingsDeepNestingAndComponentIdPrecedence()
    {
        byte[] source = Bundle(Convert.FromHexString(
            "FFFFAEFE760CA9B0D215C07CD4C76F5B12E0461B48BA1F24D27033D34684F0E3DF3926DD2F6A467FBC8A55F95FB2B1143302970935CDF191BEA96DC08D27C9E657BCD5E47F"),
            2, Convert.FromHexString(DirectoryMetadata));
        var native = DjvuDocument.Open(source);
        Assert.Equal(new[] { "2", "r\u00e9sum\u00e9+" }, native.Pages.Select(page => page.ComponentId));

        var pdf = PdfDocument.Open(native.ConvertToPdf());

        var catalog = Dictionary(pdf, pdf.Trailer[Name("Root")]);
        var outlines = Dictionary(pdf, catalog[Name("Outlines")]);
        var parent = Dictionary(pdf, outlines[Name("First")]);
        var child = Dictionary(pdf, parent[Name("First")]);
        var detail = Dictionary(pdf, child[Name("First")]);
        var sibling = Dictionary(pdf, parent[Name("Next")]);
        var pages = Assert.IsType<PdfArray>(Dictionary(pdf, catalog[Name("Pages")])[Name("Kids")]);
        Assert.Equal(4, Assert.IsType<PdfInteger>(outlines[Name("Count")]).Value);
        Assert.Equal(2, Assert.IsType<PdfInteger>(parent[Name("Count")]).Value);
        Assert.Equal(1, Assert.IsType<PdfInteger>(child[Name("Count")]).Value);
        Assert.Equal(new[] { "R\u00e9sum\u00e9", "\u05e4\u05e8\u05e7", "Detail", "End" },
            new[] { parent, child, detail, sibling }.Select(Title));
        Assert.Equal(new[] { 1, 0, 1, 0 }.Select(index => Assert.IsType<PdfIndirectReference>(pages[index]).ObjectNumber),
            new[] { parent, child, detail, sibling }.Select(bookmark =>
                Assert.IsType<PdfIndirectReference>(Assert.IsType<PdfArray>(bookmark[Name("Dest")])[0]).ObjectNumber));
        Assert.Equal(Assert.IsType<PdfIndirectReference>(parent[Name("First")]).ObjectNumber,
            Assert.IsType<PdfIndirectReference>(detail[Name("Parent")]).ObjectNumber);
        Assert.Equal(Assert.IsType<PdfIndirectReference>(outlines[Name("Last")]).ObjectNumber,
            Assert.IsType<PdfIndirectReference>(parent[Name("Next")]).ObjectNumber);
        Assert.Equal(Assert.IsType<PdfIndirectReference>(outlines[Name("First")]).ObjectNumber,
            Assert.IsType<PdfIndirectReference>(sibling[Name("Prev")]).ObjectNumber);
    }

    [Theory]
    [InlineData("FFFFDFFE677686C4DEA2B8264CE221515962765A01EC79D046E5B9C8ACDD", "#3")]
    [InlineData("FFFFDDFF7181577E9398687984229201C1F9EA392D9DFD552BB303CFF3C994BA24EF", "#%GG")]
    [InlineData("FFFFBEFE710C28F93592410B5CF4B441265886801308C6F898CB4D8CED4B11FA0A0D2DA16A5F67125659A6D9239A8972A5E2C78FA840C4DF1377847F", "https://example.invalid/book.djvu#1")]
    [InlineData("FFFFCDFE7F76863FBC025ABA36A516DE0900FFA2E9F107C2A87E079BD69999AE5EEC498A99F8E284C32BE0BF30FEB2C1BF", "#1?djvuopts&zoom=200")]
    [InlineData("FFFFDEFE710C2AC6A2AA329492D5DE9D96F971884581345EF822EC098183D1F6", "#+1")]
    [InlineData("FFFFDCFE710C2AC6A2AA32949450349AC361D315FB62A8ACC55DD6C0F0FF6E77EF3B", "#%2B1")]
    public void ConversionLeavesOutAChildThatNamesNoPageInsteadOfFailing(string compressed, string target)
    {
        // A page that is not there, a malformed escape, an external link, a viewer command and a relative
        // target with no current page are each left out of the PDF; the rest of the outline still converts.
        var native = DjvuDocument.Open(Bundle(Convert.FromHexString(compressed), 2));
        Assert.Equal(target, Assert.Single(Assert.Single(native.ReadNavigation()).Children).Target);
        var pdf = PdfDocument.Open(native.ConvertToPdf());
        var outlines = Dictionary(pdf, Dictionary(pdf, pdf.Trailer[Name("Root")])[Name("Outlines")]);
        var root = Dictionary(pdf, outlines[Name("First")]);
        Assert.Equal(1, Assert.IsType<PdfInteger>(outlines[Name("Count")]).Value);
        Assert.Equal(Assert.Single(native.ReadNavigation()).Label, Title(root));
        Assert.False(root.ContainsKey(Name("First")));
        Assert.Equal(target, Assert.Single(Assert.Single(native.ReadNavigation()).Children).Target);
    }

    [Fact]
    public void ConversionRejectsAnUnrepresentableEmptyLabelRatherThanDroppingTheChild()
    {
        var native = DjvuDocument.Open(Bundle(Convert.FromHexString("FFFFE4FE7093E694E6EAA2BE1731ADED9E4A2B7EBF9A16"), 2));
        Assert.Equal(string.Empty, Assert.Single(Assert.Single(native.ReadNavigation()).Children).Label);
        Assert.Contains("bookmark title cannot be empty", Assert.Throws<ArgumentException>(() => native.ConvertToPdf()).Message);
        Assert.Single(Assert.Single(native.ReadNavigation()).Children);
    }

    [Fact]
    public void CancellationLeavesTheSourcePixelsAndNavigationAvailable()
    {
        byte[] source = Bundle(Convert.FromHexString(NestedNavigation), 2);
        byte[] original = source.ToArray();
        var native = DjvuDocument.Open(source);
        float[] pixels = native.RenderPage(1).Pixels.ToArray();
        Assert.Throws<OperationCanceledException>(() => native.ConvertToPdf(new CancellationToken(true)));
        Assert.Equal(original, source);
        Assert.Equal(pixels, native.RenderPage(1).Pixels.ToArray());
        Assert.Equal("\u05e4\u05e8\u05e7", Assert.Single(Assert.Single(native.ReadNavigation()).Children).Label);
    }

    private static string Title(PdfDictionary dictionary) =>
        Encoding.BigEndianUnicode.GetString(Assert.IsType<PdfString>(dictionary[Name("Title")]).Bytes.Span[2..]);

    private static PdfDictionary Dictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] Bundle(byte[] navigation, int pageCount, byte[]? metadata = null)
    {
        // NAVM reuses the independently encoded fixture from DjvuNavigationTests.
        byte[] navm = Chunk("NAVM", navigation);
        byte[] page = Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 144, 0, 22, 1]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])]);
        byte[] directory = new byte[3 + pageCount * 4 + (metadata?.Length ?? 0)];
        metadata?.CopyTo(directory, 3 + pageCount * 4);
        directory[0] = 0x81;
        BinaryPrimitives.WriteUInt16BigEndian(directory.AsSpan(1), (ushort)pageCount);
        int offset = 16 + Chunk("DIRM", directory).Length + navm.Length;
        for (int index = 0; index < pageCount; index++)
            BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(3 + index * 4), (uint)(offset + index * page.Length));
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVM"u8, .. Chunk("DIRM", directory),
            .. navm, .. Enumerable.Range(0, pageCount).SelectMany(_ => page)])];
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        int length = payload.Length;
        return [.. Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16),
            (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
