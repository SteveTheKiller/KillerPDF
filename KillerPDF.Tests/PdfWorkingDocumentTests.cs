using System;
using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Syntax;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfWorkingDocumentTests
{
    [Fact]
    public void Open_AcceptsValidObjectsBeyondIncorrectTrailerSize()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        int marker = source.AsSpan().LastIndexOf("/Size "u8);
        Assert.True(marker >= 0);
        int digit = marker + "/Size ".Length;
        Assert.InRange(source[digit], (byte)'2', (byte)'9');
        source[digit++] = (byte)'1';
        while (digit < source.Length && source[digit] is >= (byte)'0' and <= (byte)'9')
            source[digit++] = (byte)' ';

        Assert.Throws<PdfSyntaxException>(() => PdfDocument.Open(source));

        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-trailer-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, source);
            using PdfWorkingDocument document = PdfWorkingDocument.Open(path);
            Assert.Equal(1, document.PageCount);
            Assert.False(document.IsReadOnly);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
