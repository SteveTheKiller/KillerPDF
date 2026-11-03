using System.Buffers.Binary;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed partial class EpubDocumentTests
{
    private readonly ITestOutputHelper _output;

    public EpubDocumentTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(5000)]
    [InlineData(60000)]
    public void RejectsLargeEmptyEntryDirectoryWithinAllocationBudget(int entryCount)
    {
        var entries = Enumerable.Range(0, entryCount).Select(i => ($"EPUB/empty{i}.txt", "")).ToArray();
        var bytes = Book(Package, entries);
        EpubDocument.Open(Book());
        long budget = bytes.Length + 16L * 1024 * 1024;
        var measurements = new long[2];
        for (int round = 0; round < measurements.Length; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<InvalidDataException>(() => EpubDocument.Open(bytes));
            measurements[round] = GC.GetAllocatedBytesForCurrentThread() - before;
            _output.WriteLine($"Entries: {entryCount}; round: {round}; input bytes: {bytes.Length}; allocated bytes: {measurements[round]}; fixed budget: {budget}.");
        }
        Assert.All(measurements, allocated => Assert.True(allocated <= budget,
            $"Open allocated {allocated} bytes; budget is {budget} bytes including one {bytes.Length}-byte input copy."));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void RejectsDishonestDirectoryCountWithinAllocationBudget(int declaredCount)
    {
        var entries = Enumerable.Range(0, 60000).Select(i => ($"EPUB/empty{i}.txt", "")).ToArray();
        var bytes = Book(Package, entries);
        int end = bytes.Length - 22;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 8), (ushort)declaredCount);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 10), (ushort)declaredCount);
        EpubDocument.Open(Book());
        long before = GC.GetAllocatedBytesForCurrentThread();
        var exception = Assert.Throws<InvalidDataException>(() => EpubDocument.Open(bytes));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long budget = bytes.Length + 16L * 1024 * 1024;
        _output.WriteLine($"Declared entries: {declaredCount}; input bytes: {bytes.Length}; allocated bytes: {allocated}; fixed budget: {budget}.");
        Assert.True(allocated <= budget, $"Dishonest count allocated {allocated} bytes; fixed budget is {budget}.");
        Assert.Equal("Invalid EPUB ZIP directory entry count.", exception.Message);
    }
    [Theory]
    [InlineData(20)]
    [InlineData(42)]
    public void DiagnosesResourceOffsetsAndLengthsOutsideLocalData(int centralField)
    {
        var bytes = Book();
        int directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + centralField), uint.MaxValue - 1);
        var exception = Assert.Throws<InvalidDataException>(() => EpubDocument.Open(bytes));
        Assert.Equal("Invalid EPUB ZIP resource bounds.", exception.Message);
    }
    [Theory]
    [InlineData("zip64-count")]
    [InlineData("zip64-size")]
    [InlineData("zip64-offset")]
    [InlineData("split-eocd")]
    [InlineData("split-entry")]
    public void DiagnosesUnsupportedZipContainerVariants(string variant)
    {
        var bytes = Book();
        int end = bytes.Length - 22;
        int directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        switch (variant)
        {
            case "zip64-count": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 10), ushort.MaxValue); break;
            case "zip64-size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12), uint.MaxValue); break;
            case "zip64-offset": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), uint.MaxValue); break;
            case "split-eocd": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 4), 1); break;
            default: BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(directory + 34), 1); break;
        }
        var exception = Assert.Throws<InvalidDataException>(() => EpubDocument.Open(bytes));
        Assert.Equal(variant.StartsWith("zip64", StringComparison.Ordinal) ? "ZIP64 EPUB containers are unsupported."
            : "Multi-volume EPUB ZIP containers are unsupported.", exception.Message);
    }
    [Theory]
    [InlineData("directory-size", "Invalid EPUB ZIP directory bounds.")]
    [InlineData("directory-offset", "Invalid EPUB ZIP directory bounds.")]
    [InlineData("record-length", "Invalid EPUB ZIP directory record length.")]
    [InlineData("signature", "Invalid EPUB ZIP directory header.")]
    [InlineData("on-disk-count", "Invalid EPUB ZIP directory entry count.")]
    [InlineData("local-offset", "Invalid EPUB ZIP resource bounds.")]
    [InlineData("payload-length", "Invalid EPUB ZIP resource bounds.")]
    [InlineData("local-extra", "Invalid EPUB ZIP resource bounds.")]
    public void RejectsMalformedDirectoryRangesBeforeMaterializingEntries(string scenario, string diagnosis)
    {
        var bytes = Book();
        int end = bytes.Length - 22;
        int directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        switch (scenario)
        {
            case "directory-size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 12), uint.MaxValue - 1); break;
            case "directory-offset": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(end + 16), uint.MaxValue - 1); break;
            case "record-length": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(directory + 28), ushort.MaxValue); break;
            case "signature": bytes[directory] = 0; break;
            case "on-disk-count": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 8), 0); break;
            case "local-offset": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 42), (uint)(directory - 29)); break;
            case "payload-length": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 20), (uint)(directory + 1)); break;
            default:
                int second = directory + 54;
                int local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(second + 42));
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 28), ushort.MaxValue);
                break;
        }
        Assert.Equal(diagnosis, Assert.Throws<InvalidDataException>(() => EpubDocument.Open(bytes)).Message);
    }

    [Fact]
    public void OpensOrdinaryZipAtEntryLimitAndWithArchiveComment()
    {
        var entries = Enumerable.Range(0, 4091).Select(i => ($"EPUB/empty{i}.txt", "")).ToArray();
        var bytes = Book(Package, entries);
        int end = bytes.Length - 22;
        Array.Resize(ref bytes, bytes.Length + 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(end + 20), 3);
        "abc"u8.CopyTo(bytes.AsSpan(end + 22));
        var book = EpubDocument.Open(bytes);
        Assert.Equal("Reading order", book.Metadata.Title);
        Assert.Empty(book.ReadResource("EPUB/empty4090.txt"));
    }
}
