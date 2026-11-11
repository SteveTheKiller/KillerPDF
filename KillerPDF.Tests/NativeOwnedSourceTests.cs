using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Opening a native book captures the file once, for the page views and for Convert to PDF to read later. The engine
// reads from that same captured array rather than copying it again, so a large file is held once, not twice or more.
public sealed class NativeOwnedSourceTests
{
    private const int PayloadBytes = 4 * 1024 * 1024;

    [Fact]
    public void OpeningADjvuDocumentCopiesTheFileOnlyOnce()
    {
        byte[] source = DjvuWithPayload();
        using (NativeWorkingDocument.OpenDjvu(NativeWorkingDocumentTests.DjvuPage())) { }
        using (NativeWorkingDocument.OpenDjvu(source)) { }

        long before = GC.GetAllocatedBytesForCurrentThread();
        using var owner = NativeWorkingDocument.OpenDjvu(source);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1, owner.PageCount);
        Assert.True(allocated < source.Length + 512 * 1024,
            $"Opening {source.Length} bytes allocated {allocated}; one captured copy is enough.");
    }
}
