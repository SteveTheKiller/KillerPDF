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
            PdfDocument parsed = Assert.IsType<PdfDocument>(document.ParsedDocument);
            Assert.Same(parsed, PdfPageRenderSession.OpenDocument(path));
            Assert.Null(document.ParsedDocument);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RegisteredWorkingParseDoesNotSurviveFileReplacement()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-cache-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
            using PdfWorkingDocument working = PdfWorkingDocument.Open(path);
            PdfDocument parsed = Assert.IsType<PdfDocument>(working.ParsedDocument);
            Assert.Same(parsed, PdfPageRenderSession.OpenDocument(path));
            Assert.Null(working.ParsedDocument);

            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            PdfDocument replacement = PdfPageRenderSession.OpenDocument(path);
            Assert.NotSame(parsed, replacement);
            Assert.Equal(2, PdfPageInformation.Read(replacement).Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RegisteredWorkingParseRendersSamePixelsAsFreshOpen()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(200, 100,
            new PdfContentStreamBuilder().SetFillRgb(0.8, 0.1, 0.3)
                .Rectangle(10, 10, 90, 50).Fill()).Build();
        string cachedPath = Path.Combine(Path.GetTempPath(), $"killerpdf-cached-{Guid.NewGuid():N}.pdf");
        string freshPath = Path.Combine(Path.GetTempPath(), $"killerpdf-fresh-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(cachedPath, source);
            File.WriteAllBytes(freshPath, source);
            using PdfWorkingDocument working = PdfWorkingDocument.Open(cachedPath);
            using var cached = PdfPageRenderSession.OpenEngineFirst(cachedPath, 256, 256);
            using var fresh = PdfPageRenderSession.OpenEngineFirst(freshPath, 256, 256);
            Assert.Equal(fresh.RenderPage(0).Pixels, cached.RenderPage(0).Pixels);
        }
        finally
        {
            if (File.Exists(cachedPath)) File.Delete(cachedPath);
            if (File.Exists(freshPath)) File.Delete(freshPath);
        }
    }
}
