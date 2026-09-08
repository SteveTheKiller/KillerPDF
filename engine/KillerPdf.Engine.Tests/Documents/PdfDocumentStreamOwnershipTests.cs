using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDocumentStreamOwnershipTests
{
    [Fact]
    public void Open_RecoveryScreensStreamsWithoutCopyingTheirPayloads()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder()).Build());
        byte[] payload = Enumerable.Repeat((byte)42, 2 * 1024 * 1024).ToArray();
        var update = new PdfIncrementalUpdateBuilder(source);
        var reference = update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Length"u8), new PdfInteger(payload.Length))]), payload));
        byte[] input = update.Build();
        int start = input.AsSpan().LastIndexOf("startxref"u8) + "startxref".Length;
        for (int i = start; i < input.Length && input[i] != '%'; i++)
            if (input[i] is >= (byte)'0' and <= (byte)'9') input[i] = (byte)'0';
        long before = GC.GetAllocatedBytesForCurrentThread();
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(input);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, input.Length, 4 * 1024 * 1024);
        Array.Clear(input);
        Assert.Equal(payload, Assert.IsType<PdfStream>(document.Resolve(reference)).EncodedData.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_StreamSharesOwnedSourceWithoutCopyingPayload(bool recovery)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder()).Build());
        byte[] payload = Enumerable.Repeat((byte)42, 2 * 1024 * 1024).ToArray();
        var update = new PdfIncrementalUpdateBuilder(source);
        var reference = update.AddObject(new PdfStream(new PdfDictionary([
            new(new PdfName("Length"u8), new PdfInteger(payload.Length))]), payload));
        byte[] input = update.Build();
        PdfDocument document = recovery ? PdfDocument.OpenWithCompatibilityRecovery(input) : PdfDocument.Open(input);
        Array.Clear(input);
        long before = GC.GetAllocatedBytesForCurrentThread();
        PdfStream stream = Assert.IsType<PdfStream>(document.Resolve(reference));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 128 * 1024);
        Assert.Equal(payload, stream.EncodedData.ToArray());
    }

    [Fact]
    public void PublicConstructionAndParsingStillCopyCallerOwnedPayloads()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("abc");
        var stream = new PdfStream(new PdfDictionary([]), bytes);
        Array.Clear(bytes);
        Assert.Equal("abc", Encoding.ASCII.GetString(stream.EncodedData.Span));
        byte[] input = Encoding.ASCII.GetBytes("1 0 obj << /Length 3 >> stream\nabc\nendstream\nendobj");
        var parsed = Assert.IsType<PdfStream>(new PdfObjectParser(input).ParseIndirectObject().Value);
        Array.Clear(input);
        Assert.Equal("abc", Encoding.ASCII.GetString(parsed.EncodedData.Span));
    }
}
