using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeSearchServiceTests
{
    [Fact]
    public void NativeSearchFinishesDenseDisjointTextWithinItsCancellationDeadline()
    {
        byte[][] zones = Enumerable.Range(0, 20_000)
            .Select(index => Zone(index % 100, index / 100, 1, 1, index, 1)).ToArray();
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page(new string('A', 20_000), zones)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "A", true, false, deadline.Token);
        Assert.Equal(20_000, result.TotalHits);
        Assert.Equal(20_000, result.PageRects[0].Count);
    }

    [Fact]
    public void NativeSearchRejectsExcessiveUnlocatedMatchesWithoutReturningPartialResults()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page(new string('A', 100_001))));
        var error = Assert.Throws<InvalidDataException>(() => SearchService.Search(
            new CurrentSearchSnapshot([], []) { NativeRevision = revision }, "A", true, false, CancellationToken.None));
        Assert.Contains("match limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeSearchRejectsExcessiveHighlightOutputWithoutReturningPartialMatches()
    {
        byte[][] zones = Enumerable.Range(0, 4096).Select(index => Zone(index % 100, index / 100, 1, 1, 0, 64)).ToArray();
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page(new string('A', 64), zones)));
        var error = Assert.Throws<InvalidDataException>(() => SearchService.Search(
            new CurrentSearchSnapshot([], []) { NativeRevision = revision }, "A", true, false, CancellationToken.None));
        Assert.Contains("area limit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableNativePagesAreReportedInsteadOfBeingTreatedAsAbsentText(bool allUnreadable)
    {
        byte[] bad = Page("A");
        bad[^2] = 2;
        byte[] bytes = allUnreadable ? bad : Bundle(Page("A", Zone(1, 2, 3, 4, 0, 1)), bad);
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(bytes));
        var source = new CurrentSearchSnapshot([], []) { NativeRevision = revision };
        if (allUnreadable)
        {
            var error = Assert.Throws<InvalidDataException>(() => SearchService.Search(source, "A", true, false, CancellationToken.None));
            Assert.Contains("No page", error.Message, StringComparison.Ordinal);
            return;
        }
        Assert.Equal(2, revision.PageCount);
        var result = SearchService.Search(source, "A", true, false, CancellationToken.None);
        Assert.Equal(1, result.TotalHits);
        Assert.Equal(new[] { 0 }, result.ResultPages);
        Assert.Equal(new[] { 1 }, result.UnreadablePages);
        Assert.Contains("version", result.UnreadableMessages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NativeSearchCancellationAndRetirementDoNotReturnFalseNoMatchResults()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page("A", Zone(1, 2, 3, 4, 0, 1)));
        using var retired = new PdfCapturedReadRevision(snapshot);
        using var current = new PdfCapturedReadRevision(snapshot);
        var oldSource = new CurrentSearchSnapshot([], []) { NativeRevision = retired };
        var currentSource = new CurrentSearchSnapshot([], []) { NativeRevision = current };
        Assert.ThrowsAny<OperationCanceledException>(() => SearchService.Search(currentSource, "A", true, false,
            new CancellationToken(true)));
        retired.Dispose();
        Assert.Throws<ObjectDisposedException>(() => SearchService.Search(oldSource, "A", true, false, CancellationToken.None));
        Assert.Equal(1, SearchService.Search(currentSource, "A", true, false, CancellationToken.None).TotalHits);
    }

    [Theory]
    [InlineData("a", true, false, 1)]
    [InlineData("a", false, false, 4)]
    [InlineData("a", false, true, 2)]
    [InlineData("fi", true, true, 1)]
    public void NativeSearchUsesExistingFoldingCaseAndWholeWordRules(string query, bool matchCase, bool wholeWord, int count)
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(
            Page("A a AA \ufb01", Zone(10, 20, 30, 40, 0, 10))));
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            query, matchCase, wholeWord, CancellationToken.None);
        Assert.Equal(count, result.TotalHits);
        if (query == "fi") Assert.Equal((7, 1), (Assert.Single(result.Hits).Start, result.Hits[0].Length));
    }

    [Fact]
    public void SearchesCapturedEpubLayoutWithoutPdfConversion()
    {
        using var resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        byte[] bytes = Book();
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenEpub(bytes,
            new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 }));
        Array.Clear(bytes);
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "A", true, true, CancellationToken.None);
        Assert.Equal(2, result.TotalHits);
        Assert.Equal(new[] { 0, 2 }, result.Hits.Select(hit => hit.Start));
        Assert.All(result.Hits, hit =>
        {
            Assert.True(hit.HasGeometry);
            Assert.False(hit.Source!.Editable);
            Assert.Single(hit.Areas);
            Assert.Equal(24, hit.bottom, 12);
            Assert.Equal(33, hit.top, 12);
        });
        Assert.Equal(3, result.Hits[0].left, 12);
    }

    [Fact]
    public void NativeSearchIdentitySeparatesOwnersOfTheSameSource()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page("A", Zone(1, 2, 3, 4, 0, 1)));
        using var first = new PdfCapturedReadRevision(snapshot);
        using var second = new PdfCapturedReadRevision(snapshot);
        var source = new CurrentSearchSnapshot([], []) { NativeRevision = first };
        var same = new CurrentSearchSnapshot([], []) { NativeRevision = first };
        var other = new CurrentSearchSnapshot([], []) { NativeRevision = second };
        Assert.True(source.SameContent(same));
        Assert.Equal(source.Fingerprint(), same.Fingerprint());
        Assert.False(source.SameContent(other));
        Assert.NotEqual(source.Fingerprint(), other.Fingerprint());
        Assert.Equal(first.Id, SearchService.Search(source, "A", true, false, CancellationToken.None).NativeRevisionId);
        Assert.Equal(second.Id, SearchService.Search(other, "A", true, false, CancellationToken.None).NativeRevisionId);
    }

    [Fact]
    public void NativePhraseUsesSeveralActualAreasForOneLogicalOccurrence()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A A",
            Zone(10, 20, 30, 40, 0, 1), Zone(50, 80, 30, 40, 2, 1))));
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "A A", true, false, CancellationToken.None);
        Assert.Equal(1, result.TotalHits);
        var hit = Assert.Single(result.Hits);
        Assert.Equal((0, 3), (hit.Start, hit.Length));
        Assert.Equal(2, hit.Areas.Count);
        Assert.Equal(2, result.PageRects[0].Count);
        Assert.Equal(2.4, hit.Areas[0].Left, 12);
        Assert.Equal(14.4, hit.Areas[0].Top, 12);
        Assert.Equal(12, hit.Areas[1].Left, 12);
        Assert.Equal(19.2, hit.Areas[1].Bottom, 12);
        Assert.Equal(28.8, hit.Areas[1].Top, 12);
    }

    [Fact]
    public void UnlocatedNativeTextMatchesWithoutInventingAHighlightRectangle()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        var result = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = revision },
            "A", true, false, CancellationToken.None);
        Assert.Equal(1, result.TotalHits);
        Assert.Equal(new[] { 0 }, result.ResultPages);
        Assert.False(Assert.Single(result.Hits).HasGeometry);
        Assert.Empty(result.PageRects);
    }

    [Fact]
    public void SearchesCapturedNativeUnicodeTextWithoutParsingPdfBytes()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A\u00e9\U0001f600",
            Zone(10, 20, 30, 40, 0, 7))));
        var source = new CurrentSearchSnapshot([], []) { NativeRevision = revision };
        var result = SearchService.Search(source, "\u00e9\U0001f600", true, false, CancellationToken.None);
        Assert.Equal(1, result.TotalHits);
        Assert.Equal(new[] { 0 }, result.ResultPages);
        var hit = Assert.Single(result.Hits);
        Assert.Equal((0, 1, 3), (hit.page, hit.Start, hit.Length));
        Assert.Equal(2.4, hit.left, 12);
        Assert.Equal(4.8, hit.bottom, 12);
        Assert.Equal(9.6, hit.right, 12);
        Assert.Equal(14.4, hit.top, 12);
        Assert.NotNull(hit.Source);
        Assert.False(hit.Source.Editable);
        Assert.Single(result.PageRects[0]);
    }

    private static byte[] Book()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-search</dc:identifier><dc:title>Native search</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-05T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native search</title></head><body><p>A A</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Native search</a></li></ol></nav></body></html>
                """);
            void Add(string path, string text, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }

    private static byte[] Bundle(byte[] firstPage, byte[] secondPage)
    {
        byte[] first = firstPage[4..], second = secondPage[4..];
        byte[] directory = [0x81, 0, 2, 0, 0, 0, 36, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32BigEndian(directory.AsSpan(7), 36 + first.Length);
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVM"u8, .. Chunk("DIRM", directory), .. first, .. second])];
    }

    private static byte[] Page(string text, params byte[][] zones)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]),
            .. Chunk("TXTa", [(byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length,
                .. bytes, 1, .. zones.SelectMany(zone => zone)])])];
    }

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

    private static byte[] Chunk(string id, byte[] data) => [.. Encoding.ASCII.GetBytes(id),
        (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length,
        .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];
}
