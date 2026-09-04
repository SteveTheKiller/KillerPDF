using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfAttachmentMacroTests
{
    [Fact]
    public void AuditStepReportsUnsafeContentWithoutChangingTheDocument()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("notes.txt", new byte[] { (byte)'M', (byte)'Z', 0, 0 })
            .Build();
        PdfMacroStep step = PdfAttachmentMacro.AuditStep();

        PdfPreflightFinding finding = Assert.Single(
            PdfAttachmentMacro.Inspect(step, source));
        ReadOnlyMemory<byte> output = PdfAttachmentMacro.Execute(step, source);

        Assert.Equal(PdfMacroOperation.AuditAttachments, step.Operation);
        Assert.Equal("Attachment.ExecutableContent", finding.Code);
        Assert.Equal(source, output.ToArray());
    }

    [Fact]
    public void RemoveStepRemovesDocumentAndPagePlacedAttachments()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("private.txt", "private"u8.ToArray())
            .AddFileAttachmentAnnotation(0, 20, 20, 24, "private.txt")
            .Build();
        PdfMacroStep step = PdfAttachmentMacro.RemoveStep();

        PdfDocument sanitized = PdfDocument.Open(
            PdfAttachmentMacro.Execute(step, source));

        Assert.Equal(PdfMacroOperation.RemoveAttachments, step.Operation);
        Assert.Empty(PdfAttachmentReader.Read(sanitized));
        Assert.Empty(PdfAttachmentReader.ReadPageAnnotations(sanitized, 0));
        Assert.Throws<ArgumentException>(() => PdfAttachmentMacro.Execute(
            new PdfMacroStep(PdfMacroOperation.Save), source));
    }
}
