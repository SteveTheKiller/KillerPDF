using System.Buffers.Binary;
using System.IO;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeTextRunServiceTests
{
    [Fact]
    public void NativeVisibleRangesUseAtomicSourceAreasAndExcludeUnlocatedSeparators()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("fi B",
            Zone(10, 20, 30, 40, 0, 2), Zone(50, 80, 30, 40, 3, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        Assert.Equal(new[] { (0, 1), (2, 3) }, VisibleTextGeometry.VisibleCarets(runs, 24, 48, 0,
            point => point, new System.Windows.Rect(0, 0, 24, 48)));
        Assert.Equal(new[] { (0, 1) }, VisibleTextGeometry.VisibleCarets(runs, 24, 48, 0,
            point => point, new System.Windows.Rect(2, 33, 8, 11)));
        Assert.Empty(VisibleTextGeometry.VisibleCarets(runs, 24, 48, 0,
            point => point, new System.Windows.Rect(10, 29, 1, 3)));
    }

    [Fact]
    public void CapturedEpubSelectionUsesOriginalLayoutAndTextWithoutPdfConversion()
    {
        using var resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        byte[] bytes = NativeSearchServiceTests.Book();
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenEpub(bytes,
            new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 }));
        Array.Clear(bytes);
        var runs = new TextRunService().GetPage(revision, 0)!;
        Assert.Equal("A A", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out int words));
        Assert.Equal(2, words);
        Assert.Equal("A A", PageReadingText.Build(runs).Text);
        Assert.Equal(60, runs.PdfWidth, 12);
        Assert.Equal(36, runs.PdfHeight, 12);
        var word = TextRunService.WordRangeFromPoint(runs, 4, 28)!.Value;
        Assert.Equal("A", TextRunService.TextForRange(runs, word.Start, word.End, out _));
        var bounds = Assert.Single(TextRunService.SelectionBounds(runs, word.Start, word.End));
        Assert.Equal(3, bounds.Left, 12);
        Assert.Equal(24, bounds.Bottom, 12);
        Assert.Equal(33, bounds.Top, 12);
        Assert.Equal(new[] { (0, 3) }, VisibleTextGeometry.VisibleCarets(runs, 60, 36, 0,
            point => point, new System.Windows.Rect(0, 0, 60, 36)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("A\r\nB")]
    public void NativeReadingKeepsEmptyAndUnlocatedTextWithoutInventingPageAreas(string text)
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page(text)));
        var runs = new TextRunService().GetPage(revision, 0)!;
        var reading = PageReadingText.Build(runs);
        Assert.Equal(text, reading.Text);
        Assert.Equal(text, TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
        Assert.Equal(text.Length + 1, reading.Carets.Length);
        Assert.Empty(TextRunService.SelectionBounds(runs, 0, runs.Chars.Count));
    }

    [Fact]
    public void NativeReadingAppendixHasNoPageGeometryAndPreservesBodyRanges()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("fi",
            Zone(10, 20, 30, 40, 0, 2))));
        var reading = PageReadingText.Build(new TextRunService().GetPage(revision, 0)!).WithAppendix(["Comment"]);
        Assert.Equal("fi\nComment", reading.Text);
        Assert.Equal(1, reading.CaretAt(3));
        Assert.Empty(reading.Segments(3, 10));
        Assert.Equal((0, 2), reading.SelectionOffsets(0, 1));
    }

    [Fact]
    public void NativeReadingFindsNestedCaretAtItsOriginalSourceOffset()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("ABCD",
            Zone(10, 20, 30, 40, 0, 4), Zone(50, 80, 30, 40, 1, 1))));
        var reading = PageReadingText.Build(new TextRunService().GetPage(revision, 0)!);
        Assert.Equal(1, reading.OffsetOf(1));
        Assert.Equal((1, 1), reading.SelectionOffsets(1, 1));
    }

    [Fact]
    public void NativeReadingRangesReachAllOverlappingAreasAndMapSelectionsBackToSource()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("ABCD",
            Zone(10, 20, 30, 40, 0, 4), Zone(50, 80, 30, 40, 1, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        var reading = PageReadingText.Build(runs);
        Assert.Equal("ABCD", reading.Text);
        Assert.Equal(new[] { (0, 2) }, reading.Segments(1, 2));
        Assert.Equal((1, 2), reading.SelectionOffsets(1, 2));
    }

    [Fact]
    public void NativeReadingPreservesSourceSeparatorsAndMapsOriginalOffsetsToAtomicCarets()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("fi \tB\r\n",
            Zone(10, 20, 30, 40, 0, 2), Zone(50, 80, 30, 40, 4, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        var reading = PageReadingText.Build(runs);
        Assert.Equal("fi \tB\r\n", reading.Text);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 3, 3, 4 }, reading.Carets);
        Assert.Equal(new[] { (0, 1), (2, 3) }, reading.Segments(0, reading.Text.Length));
    }

    [Fact]
    public void NativeSelectionCountsCopiedWordsWithoutDependingOnGeometry()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page(" one\t two\r\n")));
        var runs = new TextRunService().GetPage(revision, 0)!;
        Assert.Equal(" one\t two\r\n", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out int words));
        Assert.Equal(2, words);
    }

    [Fact]
    public void NativeSelectionReturnsActualDisjointBoxesWithoutSeparatorGeometry()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("fi B",
            Zone(10, 20, 30, 40, 0, 2), Zone(50, 80, 30, 40, 3, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        var areas = TextRunService.SelectionBounds(runs, 0, runs.Chars.Count);
        Assert.Equal(2, areas.Count);
        Assert.Equal(2.4, areas[0].Left, 12);
        Assert.Equal(4.8, areas[0].Bottom, 12);
        Assert.Equal(9.6, areas[0].Right, 12);
        Assert.Equal(14.4, areas[0].Top, 12);
        Assert.Equal(12, areas[1].Left, 12);
        Assert.Equal(19.2, areas[1].Bottom, 12);
        Assert.Equal(19.2, areas[1].Right, 12);
        Assert.Equal(28.8, areas[1].Top, 12);
        Assert.Empty(TextRunService.SelectionBounds(runs, 1, 2));
    }

    [Fact]
    public void NativeDoubleClickSelectsTheAtomicRegionWithoutAdjacentSeparators()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("fi B",
            Zone(10, 20, 30, 40, 0, 2), Zone(50, 80, 30, 40, 3, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        var first = TextRunService.WordRangeFromPoint(runs, 5, 10);
        Assert.Equal((0, 1), first!.Value);
        Assert.Equal("fi", TextRunService.TextForRange(runs, first.Value.Start, first.Value.End, out _));
        Assert.Equal((2, 3), TextRunService.WordRangeFromPoint(runs, 15, 24)!.Value);
        Assert.Null(TextRunService.WordRangeFromPoint(runs, 10.8, 16));
    }

    [Fact]
    public void NativeCaretsSnapToAtomicRegionBoundaries()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("fi B",
            Zone(10, 20, 30, 40, 0, 2), Zone(50, 80, 30, 40, 3, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        Assert.Equal(0, TextRunService.CaretFromPoint(runs, 3, 10));
        Assert.Equal(1, TextRunService.CaretFromPoint(runs, 9, 10));
        Assert.Equal(2, TextRunService.CaretFromPoint(runs, 13, 24));
        Assert.Equal(3, TextRunService.CaretFromPoint(runs, 18, 24));
        int start = TextRunService.CaretFromPoint(runs, 3, 10);
        int end = TextRunService.CaretFromPoint(runs, 9, 10);
        Assert.Equal("fi", TextRunService.TextForRange(runs, start, end, out _));
    }

    [Fact]
    public void NativeTextHitTestingUsesActualRegionsAndLeavesGapsUnselectable()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("fi B",
            Zone(10, 20, 30, 40, 0, 2), Zone(50, 80, 30, 40, 3, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        Assert.True(TextRunService.IsOverText(runs, 5, 10));
        Assert.True(TextRunService.IsOverText(runs, 15, 24));
        Assert.False(TextRunService.IsOverText(runs, 10.8, 16));
        Assert.False(TextRunService.IsOverText(runs, 22, 40));
    }

    [Fact]
    public void NativeSelectionHonorsCancellationAndRejectsRetiredOwners()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("A")));
        var service = new TextRunService();
        Assert.ThrowsAny<OperationCanceledException>(() => service.GetPage(revision, 0, new CancellationToken(true)));
        revision.Dispose();
        Assert.Throws<ObjectDisposedException>(() => service.GetPage(revision, 0));
    }

    [Fact]
    public void NativeSelectionUsesOriginalUtf16OffsetsForMultibyteRegions()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("\u00e9\U0001f600 X",
            Zone(10, 20, 30, 40, 0, 6), Zone(50, 80, 30, 40, 7, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        var located = runs.Chars.Where(item => !double.IsNaN(item.Left)).ToArray();
        Assert.Equal((0, 3), (located[0].SourceStart, located[0].SourceLength));
        Assert.Equal((4, 1), (located[1].SourceStart, located[1].SourceLength));
        Assert.Equal("\u00e9\U0001f600 X", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
        int second = runs.Chars.FindIndex(item => item.Value == "X");
        Assert.Equal("X", TextRunService.TextForRange(runs, second, second + 1, out _));
    }

    [Fact]
    public void OverlappingNativeRegionsCopyOriginalSourceOnlyOnce()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page("ABCD",
            Zone(10, 20, 30, 40, 0, 4), Zone(50, 80, 30, 40, 1, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        Assert.Equal(2, runs.Chars.Count(item => !double.IsNaN(item.Left)));
        Assert.Equal("ABCD", TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
        int nested = runs.Chars.FindIndex(item => item.Value == "B");
        Assert.Equal("B", TextRunService.TextForRange(runs, nested, nested + 1, out _));
    }

    [Fact]
    public void NativeSelectionKeepsCoarseRegionsAtomicAndCopiesTheirOriginalSeparators()
    {
        const string text = "fi \tB\r\n";
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(Page(text,
            Zone(10, 20, 30, 40, 0, 2), Zone(50, 80, 30, 40, 4, 1))));
        var runs = new TextRunService().GetPage(revision, 0)!;
        var located = runs.Chars.Where(item => !double.IsNaN(item.Left)).ToArray();
        Assert.Equal(new[] { "fi", "B" }, located.Select(item => item.Value));
        Assert.Equal(2.4, located[0].Left, 12);
        Assert.Equal(4.8, located[0].Bottom, 12);
        Assert.Equal(9.6, located[0].Right, 12);
        Assert.Equal(14.4, located[0].Top, 12);
        Assert.Equal(text, TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
        int first = runs.Chars.FindIndex(item => item.Value == "fi");
        Assert.Equal("fi", TextRunService.TextForRange(runs, first, first + 1, out _));
    }

    [Fact]
    public void CapturedNativeTextCopiesOriginalSeparatorsWithoutInventingGeometry()
    {
        const string text = " \tA\u00e9\U0001f600\r\nB  ";
        byte[] bytes = Page(text);
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(bytes));
        Array.Clear(bytes);
        var runs = new TextRunService().GetPage(revision, 0);
        Assert.NotNull(runs);
        Assert.Equal(text, TextRunService.TextForRange(runs, 0, runs.Chars.Count, out _));
        Assert.Equal(24, runs.PdfWidth, 12);
        Assert.Equal(48, runs.PdfHeight, 12);
        Assert.Empty(runs.Lines);
        Assert.All(runs.Chars, item =>
        {
            Assert.True(double.IsNaN(item.Left));
            Assert.True(double.IsNaN(item.Right));
            Assert.True(double.IsNaN(item.Bottom));
            Assert.True(double.IsNaN(item.Top));
        });
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
        var bytes = new byte[17];
        bytes[0] = 6;
        int[] values = [left, bottom, width, height, start];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1 + index * 2), checked((ushort)(values[index] + 0x8000)));
        bytes[11] = (byte)(length >> 16); bytes[12] = (byte)(length >> 8); bytes[13] = (byte)length;
        return bytes;
    }

    private static byte[] Chunk(string id, byte[] bytes) => [.. Encoding.ASCII.GetBytes(id),
        (byte)(bytes.Length >> 24), (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length,
        .. bytes, .. (bytes.Length % 2 == 1 ? new byte[1] : [])];
}
