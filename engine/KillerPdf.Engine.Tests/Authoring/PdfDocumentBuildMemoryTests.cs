using KillerPdf.Engine.Authoring;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfDocumentBuildMemoryTests(ITestOutputHelper output)
{
    private static PdfDocumentBuilder Book(int pages, int contentBytes)
    {
        var builder = new PdfDocumentBuilder();
        // Valid, uncompressed content: a long run of comment lines, as a text-heavy page is a long run of operators.
        byte[] content = System.Text.Encoding.ASCII.GetBytes(string.Concat(
            Enumerable.Repeat("% filler line of page content\n", contentBytes / 30)));
        for (int page = 0; page < pages; page++) builder.AddPage(300, 400, content);
        return builder;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(400)]
    public void BuildAllocatesNoMoreThanTwiceAndAHalfTheDocumentItWrites(int pages)
    {
        Book(1, 3000).Build();
        var builder = Book(pages, 60_000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] pdf = builder.Build();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Pages: {pages}; PDF bytes: {pdf.Length}; allocated bytes: {allocated}; factor: {(double)allocated / pdf.Length:F2}.");
        // The result array is one copy of the document and the serialized objects are at most one more; any further
        // copy (a doubling buffer, a per-object hashing copy) took it to 8.7 times before this was measured and fixed.
        Assert.True(allocated <= 2.5 * pdf.Length,
            $"Build allocated {allocated} bytes for a {pdf.Length}-byte PDF ({(double)allocated / pdf.Length:F2} times).");
    }
}
