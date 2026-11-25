using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

// Delete Pages, link removal and text-edit covers save incrementally, which leaves the removed content in
// an earlier revision anyone can recover by cutting the file at its first %%EOF (KP-SEC-04).
public sealed class PdfRevisionConsolidationTests
{
    [Fact]
    public void ADeletedPagesContentIsGoneFromTheConsolidatedBytes()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(200, 200, Encoding.ASCII.GetBytes("BT /F1 12 Tf (KEEPMARKER) Tj ET"))
            .AddPage(200, 200, Encoding.ASCII.GetBytes("BT /F1 12 Tf (GONEMARKER) Tj ET"))
            .Build();
        Assert.True(Count(source, "GONEMARKER") > 0, "The fixture must store page content uncompressed.");
        byte[] incremental = new PdfIncrementalPageEditor(PdfDocument.Open(source)).RemovePage(1).Build();
        Assert.True(Count(incremental, "GONEMARKER") > 0, "The incremental save keeps the old revision.");

        byte[] consolidated = PdfRevisionConsolidation.Consolidate(incremental);

        Assert.Equal(0, Count(consolidated, "GONEMARKER"));
        Assert.True(Count(consolidated, "KEEPMARKER") > 0);
        Assert.Equal(1, Count(consolidated, "%%EOF"));
        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(consolidated)));
    }

    [Fact]
    public void ASignedDocumentKeepsItsIncrementalSave()
    {
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build()),
            _ => [0x30, 0x00], new PdfSignatureOptions { ReservedSignatureSize = 16 });
        byte[] incremental = new PdfIncrementalPageEditor(PdfDocument.Open(signed)).RemovePage(1).Build();

        Assert.Same(incremental, PdfRevisionConsolidation.Consolidate(incremental));
    }

    private static int Count(byte[] data, string text)
    {
        byte[] needle = Encoding.ASCII.GetBytes(text);
        int count = 0;
        for (int start = 0; ;)
        {
            int at = data.AsSpan(start).IndexOf(needle);
            if (at < 0) return count;
            count++;
            start += at + needle.Length;
        }
    }
}
